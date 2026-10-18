using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances
{
    [Serializable]
    public struct BakedDrawGroup
    {
        public Mesh Mesh;
        public Material Material;
        public int SubMesh;
        public int InstanceOffset;
        public int InstanceCount;
        // Per-renderer filter attributes: groups split on these so the culling
        // jobs can honor them per draw command. receiveShadows is deliberately
        // absent: URP ignores the renderer/filter flag entirely, so the material
        // keyword _RECEIVE_SHADOWS_OFF is the only control.
        public ShadowCastingMode ShadowMode;
        public byte Layer;
        public bool FlipWinding;
        public uint RenderingLayerMask;

        /// <summary>
        /// This group's material with its SpeedTree wind keywords disabled, drawn
        /// in place of <see cref="Material"/> whenever it is set. SIS does not
        /// simulate wind, so this is what keeps wind-enabled SpeedTree content
        /// still and free of wind ALU. Null on a group whose material enables no
        /// wind keyword, and null on one whose clone has not been generated yet
        /// (that group draws its original material).
        ///
        /// A serialized asset reference rather than a runtime `new Material(...)`:
        /// the wind keywords are `shader_feature`, so the stripped variant is only
        /// compiled into the build if a serialized asset asks for it. A clone
        /// created at runtime would find its variant missing in a player.
        /// </summary>
        public Material StaticMaterial;
    }

    internal readonly struct DrawKey : IEquatable<DrawKey>
    {
        public readonly Mesh Mesh;
        public readonly Material Material;
        public readonly int SubMesh;
        public readonly ShadowCastingMode ShadowMode;
        public readonly byte Layer;
        public readonly bool FlipWinding;
        public readonly uint RenderingLayerMask;

        public DrawKey(
            Mesh mesh, Material material, int subMesh,
            ShadowCastingMode shadowMode, byte layer, bool flipWinding,
            uint renderingLayerMask)
        {
            Mesh = mesh;
            Material = material;
            SubMesh = subMesh;
            ShadowMode = shadowMode;
            Layer = layer;
            FlipWinding = flipWinding;
            RenderingLayerMask = renderingLayerMask;
        }

        public bool Equals(DrawKey other)
        {
            return Mesh == other.Mesh && Material == other.Material &&
                   SubMesh == other.SubMesh && ShadowMode == other.ShadowMode &&
                   Layer == other.Layer && FlipWinding == other.FlipWinding &&
                   RenderingLayerMask == other.RenderingLayerMask;
        }

        public override bool Equals(object obj)
        {
            return obj is DrawKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                // EntityId does not convert to int, so its hash is used as the
                // instance ID.
                int hash = Mesh != null ? Mesh.GetEntityId().GetHashCode() : 0;
                hash = (hash * 397) ^ (Material != null ? Material.GetEntityId().GetHashCode() : 0);
                hash = (hash * 397) ^ SubMesh;
                hash = (hash * 397) ^ (int)ShadowMode;
                hash = (hash * 397) ^ Layer;
                hash = (hash * 397) ^ (FlipWinding ? 1 : 0);
                hash = (hash * 397) ^ (int)RenderingLayerMask;
                return hash;
            }
        }
    }

    // Hierarchy collection result in the same flat layout as the baked
    // arrays, so both initialization paths share BuildRuntimeData.
    internal sealed class CollectedData
    {
        public Matrix4x4[] Matrices;
        public StaticInstancePreparedMatrices PreparedMatrices;
        public int InstanceCount => PreparedMatrices != null ? PreparedMatrices.Count : Matrices.Length;
        public Bounds[] Bounds;
        public BakedDrawGroup[] DrawGroups;
        public int[] InstanceIndices;
        public MeshRenderer[] ValidRenderers;

        /// <summary>
        /// The per-instance shape factor that corrects the bounding-sphere size
        /// input, one per instance, parallel to <see cref="Bounds"/>. See
        /// <see cref="StaticInstanceBuilder.ShapeFactorOf"/> for the formula. It is
        /// computed here, at collection/bake, and not at initialization, because it
        /// needs the local extents: `Bounds` carries `Renderer.bounds`, the world
        /// AABB, which is rotation-inflated and would under-correct rotated thin
        /// geometry. May be null (from the public `ApplyBakedData` overload that
        /// does not supply it, or from data baked without it), and null means identity,
        /// not "recompute".
        /// </summary>
        public float[] ShapeFactors;
    }

    internal struct PackedMatrix
    {
        public float c0x;
        public float c0y;
        public float c0z;
        public float c1x;
        public float c1y;
        public float c1z;
        public float c2x;
        public float c2y;
        public float c2z;
        public float c3x;
        public float c3y;
        public float c3z;

        public PackedMatrix(Matrix4x4 matrix)
        {
            c0x = matrix.m00;
            c0y = matrix.m10;
            c0z = matrix.m20;
            c1x = matrix.m01;
            c1y = matrix.m11;
            c1z = matrix.m21;
            c2x = matrix.m02;
            c2y = matrix.m12;
            c2z = matrix.m22;
            c3x = matrix.m03;
            c3y = matrix.m13;
            c3z = matrix.m23;
        }
    }

    public interface IStaticInstanceBakeTarget
    {
        bool HasBakedData { get; }
        int BakedInstanceCount { get; }
        string BakedSourceHash { get; }
        // The relocated-bake guard value. On the interface because the editor
        // tooling checks it the same way for both components, and because it pairs
        // with BakedSourceHash: the hash detects the content changing, this detects
        // the owner moving under content that did not, which the hash cannot see
        // once the sources are stripped.
        Matrix4x4 BakedOwnerMatrix { get; }
        void ApplyBakedSourceHash(string hash);
        bool BakeFromHierarchy();
        void ClearBakedData();
        // Every material this owner can render with: the baked draw groups when
        // baked, the owned renderers otherwise. The DOTS variant stripper's
        // keep-list input, and deliberately conservative: inactive renderers are
        // included, because a stripped variant is a broken build and a kept one is
        // only a larger one.
        void CollectReferencedMaterials(List<Material> results);
    }

    // The bake stores world-space matrices, so it is only valid at the transform
    // it was taken at.
    //
    // In the Editor that is masked: moving the owner changes the source hash, the
    // stale check fires, and Play falls back to runtime collection. The stale
    // check is `#if UNITY_EDITOR` (the hash needs asset GUIDs), so a player build
    // has no such backstop: a baked prefab instantiated somewhere else draws at
    // its bake coordinates and looks correct in the Editor the whole time. This
    // guard exists for that case and is deliberately outside the `#if`.
    public static class StaticInstanceBakeGuard
    {
        // Relative, because world matrices carry position magnitudes: an
        // absolute epsilon that suits a prop at the origin rejects the same
        // prop 2 km out.
        private const float Tolerance = 1e-5f;

        public static bool MatchesOwner(Matrix4x4 bakedOwnerMatrix, Transform owner)
        {
            Matrix4x4 current = owner.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
            {
                float a = bakedOwnerMatrix[i];
                float b = current[i];
                float scale = Mathf.Max(1f, Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)));
                if (Mathf.Abs(a - b) > Tolerance * scale)
                    return false;
            }

            return true;
        }
    }

    // Who owns what, when Static Instance components are nested.
    //
    // A node belongs to the nearest SIS component at or above it. Nested SIS
    // is rejected, but ownership filtering prevents duplicate rendering while
    // authoring and validation report the invalid hierarchy.
    public static class StaticInstanceOwnership
    {
        public static bool IsOwner(GameObject candidate)
        {
            return candidate != null &&
                candidate.TryGetComponent(out StaticInstanceScatter _);
        }

        // `node == owner` is owned. A node carrying its own SIS component is owned
        // by itself, which is why the walk starts at `node` and not at its parent:
        // the nested owner's own MeshRenderer belongs to the nested owner.
        public static bool IsOwnedBy(Transform owner, Transform node)
        {
            for (Transform t = node; t != null && t != owner; t = t.parent)
            {
                if (IsOwner(t.gameObject))
                    return false;
            }

            return true;
        }

        // The components under `owner` that `owner` actually owns.
        //
        // Filters GetComponentsInChildren. Un-nested content gets the unfiltered
        // sequence in the same order, so the bake hash and draw-group order are
        // unaffected. Returns the original array untouched when nothing is
        // excluded, so un-nested scenes cost one extra pass.
        public static T[] OwnedComponents<T>(Component owner, bool includeInactive = true)
            where T : Component
        {
            T[] all = owner.GetComponentsInChildren<T>(includeInactive);
            Transform root = owner.transform;

            int excluded = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && !IsOwnedBy(root, all[i].transform))
                    excluded++;
            }

            if (excluded == 0)
                return all;

            var owned = new T[all.Length - excluded];
            int next = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && IsOwnedBy(root, all[i].transform))
                    owned[next++] = all[i];
            }

            return owned;
        }

        // SIS nesting is unsupported. Return the first nested owner so callers
        // can reject the hierarchy with a concrete object name.
        public static Component FindUnsupportedNesting(Component owner)
        {
            foreach (StaticInstanceScatter nested in
                owner.GetComponentsInChildren<StaticInstanceScatter>(true))
            {
                if (nested != null && nested.transform != owner.transform)
                    return nested;
            }

            return null;
        }
    }

#if UNITY_EDITOR
    // Editor-assembly hook the runtime Initialize path consults: a stale bake
    // is only detectable with editor APIs (the hash needs asset GUIDs), so the
    // editor assembly assigns the check on load and Play-mode initialization
    // falls back to runtime collection instead of drawing an outdated cache.
    // Never serialized, reassigned after every domain reload.
    public static class StaticInstanceEditorBridge
    {
        public static System.Func<MonoBehaviour, bool> IsBakeStale;

        /// <summary>
        /// Re-attach existing wind-stripped clones to freshly collected draw
        /// groups (mutated in place). Collection writes `StaticMaterial = null`,
        /// so without this every re-bake, including the build-time one, would
        /// drop the clones. A pure lookup: it creates no asset, so it is safe
        /// inside scene processing. Installed on every domain reload, like
        /// <see cref="IsBakeStale"/>.
        /// </summary>
        public static System.Action<BakedDrawGroup[]> AssignStaticMaterials;
    }
#endif

    /// <summary>
    /// The SpeedTree9 graph's vertex-stage wind keywords. SIS does not simulate
    /// wind, so a draw group whose material enables any of them draws a
    /// wind-stripped static clone instead (see <see cref="BakedDrawGroup.StaticMaterial"/>).
    /// </summary>
    public static class StaticInstanceSpeedTreeWind
    {
        public static readonly string[] Keywords =
        {
            "_WIND_SHARED", "_WIND_BRANCH1", "_WIND_BRANCH2", "_WIND_RIPPLE", "_WIND_SHIMMER",
        };

        /// <summary>
        /// Does this material declare and enable at least one wind keyword?
        /// Looked up through the shader's keyword space: constructing a
        /// `LocalKeyword` for an undeclared name logs an error, while
        /// `FindKeyword` returns an invalid keyword quietly.
        /// </summary>
        public static bool MaterialEnablesWind(Material material)
        {
            if (material == null || material.shader == null)
                return false;

            LocalKeywordSpace space = material.shader.keywordSpace;
            foreach (string keyword in Keywords)
            {
                LocalKeyword local = space.FindKeyword(keyword);
                if (local.isValid && material.IsKeywordEnabled(local))
                    return true;
            }
            return false;
        }
    }

    // Shared pieces of the SIS hierarchy-collection / BRG-registration pipeline.
    internal static class StaticInstanceBuilder
    {
        private const int SizeOfPackedMatrix = sizeof(float) * 4 * 3;
        private const int ExtraBytes = sizeof(float) * 4 * 4 * 2;

        internal static CollectedData CollectFromHierarchy(
            Transform root, List<MeshRenderer> validRenderers)
        {
            // The group renders the static MeshRenderers in its own hierarchy;
            // attach this component to the root of the renderers to convert.
            // Ownership-filtered: a nested SIS component's subtree belongs to that
            // component, and collecting it here would register the same instances
            // twice.
            MeshRenderer[] renderers = StaticInstanceOwnership.OwnedComponents<MeshRenderer>(root);

            // Bake-time spatial sort: instances are laid out in Morton order of
            // their bounds centers, so the flat matrix/bounds arrays and every draw
            // group's index run stay spatially coherent. Free at runtime, and it
            // keeps the culling jobs cache-friendly and the PerInstance level bins
            // contiguous on wide scatters. Group discovery order follows it,
            // which is harmless (draw-group order is not semantic), and the bake
            // hash is unaffected (it walks the hierarchy independently).
            var spatialOrder = new List<MeshRenderer>(renderers.Length);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer candidate = renderers[i];
                if (!IsEligibleSource(candidate))
                    continue;

                spatialOrder.Add(candidate);
            }

            SortBySpatialLocality(spatialOrder);

            var matrices = new List<Matrix4x4>(spatialOrder.Count);
            var bounds = new List<Bounds>(spatialOrder.Count);
            // Parallel to `bounds`, and filled in the same iteration so the two
            // cannot drift apart.
            var shapeFactors = new List<float>(spatialOrder.Count);
            var groupedInstances = new Dictionary<DrawKey, List<int>>();
            var groupOrder = new List<DrawKey>();

            for (int rendererIndex = 0; rendererIndex < spatialOrder.Count; rendererIndex++)
            {
                MeshRenderer meshRenderer = spatialOrder[rendererIndex];
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                Mesh mesh = meshFilter.sharedMesh;

                int instanceIndex = matrices.Count;
                matrices.Add(meshRenderer.localToWorldMatrix);
                // `Renderer.bounds` is the world AABB, rotation-inflated, which the
                // shape factor below deliberately does not read (it takes the mesh's
                // local bounds under the same matrix's scale).
                bounds.Add(meshRenderer.bounds);
                shapeFactors.Add(ShapeFactorOf(mesh.bounds, meshRenderer.localToWorldMatrix));
                validRenderers.Add(meshRenderer);

                ShadowCastingMode shadowMode = meshRenderer.shadowCastingMode;
                byte layer = (byte)meshRenderer.gameObject.layer;
                bool flipWinding = meshRenderer.localToWorldMatrix.determinant < 0f;
                uint rendererRenderingLayers = meshRenderer.renderingLayerMask;

                Material[] materials = meshRenderer.sharedMaterials;
                int subMeshCount = Mathf.Min(mesh.subMeshCount, materials.Length);
                for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                {
                    Material material = materials[subMesh];
                    if (material == null)
                        continue;

                    var key = new DrawKey(
                        mesh, material, subMesh, shadowMode, layer, flipWinding,
                        rendererRenderingLayers);
                    if (!groupedInstances.TryGetValue(key, out List<int> instances))
                    {
                        instances = new List<int>();
                        groupedInstances.Add(key, instances);
                        groupOrder.Add(key);
                    }

                    instances.Add(instanceIndex);
                }
            }

            if (matrices.Count == 0 || groupedInstances.Count == 0)
                return null;

            // Stable sort by (layer, shadow mode): the system emits one draw
            // range per contiguous run of equal filter attributes, so keeping
            // equal keys adjacent yields the minimum range count.
            var sortedOrder = new List<DrawKey>(groupOrder);
            int[] originalIndex = new int[groupOrder.Count];
            for (int i = 0; i < groupOrder.Count; i++)
                originalIndex[i] = i;
            Array.Sort(originalIndex, (a, b) =>
            {
                DrawKey keyA = sortedOrder[a];
                DrawKey keyB = sortedOrder[b];
                int compare = keyA.Layer.CompareTo(keyB.Layer);
                if (compare != 0)
                    return compare;
                compare = ((int)keyA.ShadowMode).CompareTo((int)keyB.ShadowMode);
                if (compare != 0)
                    return compare;
                compare = keyA.RenderingLayerMask.CompareTo(keyB.RenderingLayerMask);
                if (compare != 0)
                    return compare;
                return a.CompareTo(b); // stable: keep collection order
            });
            for (int i = 0; i < originalIndex.Length; i++)
                groupOrder[i] = sortedOrder[originalIndex[i]];

            int totalGroupInstances = 0;
            foreach (KeyValuePair<DrawKey, List<int>> pair in groupedInstances)
                totalGroupInstances += pair.Value.Count;

            var drawGroups = new BakedDrawGroup[groupOrder.Count];
            var instanceIndices = new int[totalGroupInstances];
            int instanceOffset = 0;
            for (int groupIndex = 0; groupIndex < groupOrder.Count; groupIndex++)
            {
                DrawKey key = groupOrder[groupIndex];
                List<int> indices = groupedInstances[key];
                drawGroups[groupIndex] = new BakedDrawGroup
                {
                    Mesh = key.Mesh,
                    Material = key.Material,
                    SubMesh = key.SubMesh,
                    InstanceOffset = instanceOffset,
                    InstanceCount = indices.Count,
                    ShadowMode = key.ShadowMode,
                    Layer = key.Layer,
                    FlipWinding = key.FlipWinding,
                    RenderingLayerMask = key.RenderingLayerMask,
                };

                for (int i = 0; i < indices.Count; i++)
                    instanceIndices[instanceOffset + i] = indices[i];
                instanceOffset += indices.Count;
            }

            return new CollectedData
            {
                Matrices = matrices.ToArray(),
                Bounds = bounds.ToArray(),
                DrawGroups = drawGroups,
                InstanceIndices = instanceIndices,
                ValidRenderers = validRenderers.ToArray(),
                ShapeFactors = shapeFactors.ToArray(),
            };
        }

        internal static bool IsEligibleSource(MeshRenderer renderer)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return false;

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null;
        }

        internal static StaticInstanceOwner BuildRuntimeData(
            CollectedData data,
            List<Mesh> registeredMeshes,
            List<Material> registeredMaterials,
            out GraphicsBuffer instanceData,
            out int instanceCount,
            out int drawGroupCount,
            out int uniqueMeshCount,
            out int uniqueMaterialCount,
            // Alias `unity_MatrixPreviousM`/`MI` per instance so the MotionVectors pass
            // reads a real previous matrix. Per component, default off.
            bool motionVectors = false)
        {
            var meshIDs = new Dictionary<Mesh, BatchMeshID>();
            var materialIDs = new Dictionary<Material, BatchMaterialID>();

            BakedDrawGroup[] groups = data.DrawGroups;
            var localDrawGroups = new NativeArray<CullingDrawGroup>(
                groups.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            bool anyShadowCaster = false;
            int unresolvedStaticVariants = 0;
            for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                BakedDrawGroup group = groups[groupIndex];
                Material material = SelectDrawGroupMaterial(group);
                if (group.StaticMaterial == null &&
                    StaticInstanceSpeedTreeWind.MaterialEnablesWind(group.Material))
                    unresolvedStaticVariants++;
                BatchMeshID meshID = RegisterMeshOnce(group.Mesh, meshIDs, registeredMeshes);
                BatchMaterialID materialID = RegisterMaterialOnce(material, materialIDs, registeredMaterials);
                anyShadowCaster |= group.ShadowMode != ShadowCastingMode.Off;

                // BatchID / OwnerIndex / InstanceBase are patched by the system
                // when it concatenates the registered owners into its combined
                // arrays.
                localDrawGroups[groupIndex] = new CullingDrawGroup
                {
                    MeshID = meshID,
                    MaterialID = materialID,
                    SubMesh = group.SubMesh,
                    InstanceOffset = group.InstanceOffset,
                    InstanceCount = group.InstanceCount,
                    ShadowMode = (byte)group.ShadowMode,
                    Layer = group.Layer,
                    // A static clone has no vertex animation, so it raises no
                    // per-object motion vectors.
                    Flags = (byte)((group.FlipWinding ? CullingDrawGroup.FlipWindingFlag : 0) |
                        (motionVectors && group.StaticMaterial == null
                            ? CullingDrawGroup.MotionVectorsFlag : 0)),
                    RenderingLayerMask = group.RenderingLayerMask,
                    MaxMeshLod = MaxMeshLodOf(group.Mesh),
                };
            }

            // Said once per owner at Initialize, not per draw group. Such groups
            // read the zero-block wind record and stand still, but keep paying
            // the wind vertex ALU. The remedy is an editor action, so it is named.
            if (unresolvedStaticVariants > 0)
            {
                StaticInstanceLog.Info(
                    $"Static Instances: {unresolvedStaticVariants} draw group(s) use a " +
                    "wind-enabled SpeedTree material with no wind-stripped variant, so they " +
                    "keep their authored material and stand still. Re-bake to generate " +
                    "the variants (Window > Rendering > Static Instances).");
            }

            // One float per instance, consumed only by the rebuild-time threshold
            // composition and never read per frame, so it costs no per-frame memory
            // traffic in the eval job. A null source array (baked data without shape
            // factors, or the public ApplyBakedData overload that does not supply
            // one) means identity, so those owners keep an unscaled cull.
            var localShapeFactors = new NativeArray<float>(
                data.Bounds.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            bool haveShapeFactors =
                data.ShapeFactors != null && data.ShapeFactors.Length == data.Bounds.Length;
            // Null and wrong-length are different states and must not read the
            // same. Null is by design (data without shape factors, and the public ApplyBakedData
            // overload's synthesized-box callers) and stays silent. A non-null
            // array of the wrong length is a defect (a partial or corrupted bake),
            // and identity would hide it as "correction absent". Direct
            // Debug.LogWarning, not StaticInstanceLog: this is the fail-visible
            // tier, which is kept out of the release-stripped path on purpose, and
            // it constructs only on the failure branch.
            if (data.ShapeFactors != null && data.ShapeFactors.Length != data.Bounds.Length)
            {
                Debug.LogWarning(
                    "Static Instances: baked shape factors do not match the instance count " +
                    $"({data.ShapeFactors.Length} factors for {data.Bounds.Length} instances), " +
                    "so the shape-aware size correction is disabled for this owner. " +
                    "Re-bake it (the arrays are written together, so a mismatch means a " +
                    "partial or edited bake).");
            }
            for (int i = 0; i < data.Bounds.Length; i++)
                localShapeFactors[i] = haveShapeFactors ? data.ShapeFactors[i] : 1f;

            var localBounds = new NativeArray<CullingBounds>(
                data.Bounds.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < data.Bounds.Length; i++)
            {
                localBounds[i] = new CullingBounds
                {
                    Center = data.Bounds[i].center,
                    Extents = data.Bounds[i].extents,
                };
            }

            var localInstanceIndices = new NativeArray<int>(
                data.InstanceIndices.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            localInstanceIndices.CopyFrom(data.InstanceIndices);

            instanceCount = data.InstanceCount;
            drawGroupCount = groups.Length;
            uniqueMeshCount = meshIDs.Count;
            uniqueMaterialCount = materialIDs.Count;

            BatchID batchID = CreateMatrixBatch(
                data.Matrices, data.PreparedMatrices, motionVectors, out instanceData);

            // Visible/CullDistance/MinDrawDistance/FadeDuration/CastShadows/
            // PickingEntityId come from the owning component's serialized
            // properties, not from anything generic here — the caller sets
            // them on the returned owner right after this call.
            return new StaticInstanceOwner
            {
                DrawGroups = localDrawGroups,
                Bounds = localBounds,
                ShapeFactors = localShapeFactors,
                InstanceIndices = localInstanceIndices,
                BatchId = batchID,
                HasShadowCasters = anyShadowCaster,
                InstanceData = instanceData,
                MotionVectors = motionVectors,
            };
        }

        // The shape factor for one instance, in the cube-normalized form:
        //
        //     c = sqrt( (xy + yz + zx) / (x² + y² + z²) )
        //
        // where (x, y, z) are the instance's object-space half-extents scaled by
        // its transform's per-axis scale. Cube 1.00, thin plate (1, 1, 0.05) 0.74,
        // pole (0.1, 4, 0.1) 0.22, so it only ever shrinks the size input, with
        // equality at the cube.
        //
        // The normalization keeps compact props unchanged (a cube is 1.0), so an
        // authored `Size Cull Distance Scale` keeps its meaning; only the relative
        // separation between shapes matters. The absolute projected-area
        // interpretation is not used: the derived limit sits inside an
        // author-tuned arbitrary-unit scale.
        //
        // Rotation is deliberately excluded. The scale comes from the matrix's
        // column magnitudes, so a 45-degree-rotated plate is measured as the plate
        // it is, while the distance metric keeps using the world AABB. This is
        // why the factor cannot be recovered at initialization from the
        // serialized world bounds.
        //
        // Degenerate input (a zero or non-finite extent product) returns 1:
        // identity, never 0, so a broken mesh cannot cull an instance to nothing.
        internal static float ShapeFactorOf(in Bounds localBounds, in Matrix4x4 matrix)
        {
            Vector3 e = localBounds.extents;
            float sx = ((Vector3)matrix.GetColumn(0)).magnitude;
            float sy = ((Vector3)matrix.GetColumn(1)).magnitude;
            float sz = ((Vector3)matrix.GetColumn(2)).magnitude;

            float x = Mathf.Abs(e.x) * sx;
            float y = Mathf.Abs(e.y) * sy;
            float z = Mathf.Abs(e.z) * sz;

            float sumSquares = x * x + y * y + z * z;
            if (!(sumSquares > 0f) || float.IsNaN(sumSquares) || float.IsInfinity(sumSquares))
                return 1f;

            float faces = x * y + y * z + z * x;
            if (!(faces > 0f))
                return 1f;

            float c = Mathf.Sqrt(faces / sumSquares);
            // Clamped rather than trusted: the closed form cannot exceed 1 (AM-GM),
            // but a float round-trip at the cube can land at 1.0000001, and a factor
            // above 1 would extend a cull distance, which this feature must never do.
            return c > 1f ? 1f : c;
        }

        // World AABB of a local-space bounds under an arbitrary transform
        // (absolute-axis extent projection).
        internal static Bounds TransformBounds(in Bounds local, in Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(local.center);
            Vector3 extents = local.extents;
            Vector3 axisX = matrix.GetColumn(0);
            Vector3 axisY = matrix.GetColumn(1);
            Vector3 axisZ = matrix.GetColumn(2);
            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) * extents.x + Mathf.Abs(axisY.x) * extents.y +
                    Mathf.Abs(axisZ.x) * extents.z,
                Mathf.Abs(axisX.y) * extents.x + Mathf.Abs(axisY.y) * extents.y +
                    Mathf.Abs(axisZ.y) * extents.z,
                Mathf.Abs(axisX.z) * extents.x + Mathf.Abs(axisY.z) * extents.y +
                    Mathf.Abs(axisZ.z) * extents.z);
            return new Bounds(center, worldExtents * 2f);
        }

        // Mesh LOD levels are index ranges inside one mesh, so the level count is
        // a property of the mesh and differs per draw group. Captured at
        // initialization: the baked content is static, and lodCount only changes
        // on reimport (which re-initializes the group anyway).
        internal static byte MaxMeshLodOf(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int last = mesh.lodCount - 1;
            return last <= 0 ? (byte)0 : (byte)(last > 255 ? 255 : last);
        }

        internal static BatchMeshID RegisterMeshOnce(
            Mesh mesh, Dictionary<Mesh, BatchMeshID> cache, List<Mesh> registeredMeshes)
        {
            if (cache.TryGetValue(mesh, out BatchMeshID id))
                return id;

            id = StaticInstanceRendererSystem.RegisterMesh(mesh);
            cache.Add(mesh, id);
            registeredMeshes.Add(mesh);
            return id;
        }

        /// <summary>
        /// The material one draw group draws with: its wind-stripped static clone
        /// when one has been generated, else its authored material.
        /// </summary>
        internal static Material SelectDrawGroupMaterial(in BakedDrawGroup group) =>
            group.StaticMaterial != null ? group.StaticMaterial : group.Material;

        internal static BatchMaterialID RegisterMaterialOnce(
            Material material,
            Dictionary<Material, BatchMaterialID> cache,
            List<Material> registeredMaterials)
        {
            if (cache.TryGetValue(material, out BatchMaterialID id))
                return id;

            material.enableInstancing = true;
            if (material.HasProperty("_EmissionMap") &&
                material.GetTexture("_EmissionMap") != null)
            {
                material.EnableKeyword("_EMISSION");
            }

            id = StaticInstanceRendererSystem.RegisterMaterial(material);
            cache.Add(material, id);
            registeredMaterials.Add(material);
            return id;
        }

        internal static BatchID CreateMatrixBatch(Matrix4x4[] matrices, out GraphicsBuffer buffer)
        {
            return CreateMatrixBatch(matrices, false, out buffer);
        }

        /// <summary>
        /// Build one owner's instance-data buffer and the batch over it.
        /// </summary>
        /// <param name="motionVectors">
        /// Alias `unity_MatrixPreviousM`/`MI` onto the current matrices per instance
        /// instead of zero-supplying them. Zero-supplied history explodes motion the
        /// moment `HasMotion` is raised, because the shader then reads the origin as
        /// last frame's position for every instance.
        /// </param>
        internal static BatchID CreateMatrixBatch(
            Matrix4x4[] matrices,
            bool motionVectors,
            out GraphicsBuffer buffer)
        {
            return CreateMatrixBatch(matrices, null, motionVectors, out buffer);
        }

        // Prepared data has passed validation before any registration.
        internal static BatchID CreateMatrixBatch(
            Matrix4x4[] matrices,
            StaticInstancePreparedMatrices prepared,
            bool motionVectors,
            out GraphicsBuffer buffer)
        {
            int count = prepared != null ? prepared.Count : matrices.Length;
            // Sized from the layout helpers rather than re-derived, so the
            // allocation cannot disagree with the addresses.
            int totalBytes = TotalBufferBytes(count);
            buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (totalBytes + sizeof(int) - 1) / sizeof(int),
                sizeof(int));

            PackedMatrix[] objectToWorld = null;
            PackedMatrix[] worldToObject = null;
            if (prepared == null)
            {
                objectToWorld = new PackedMatrix[count];
                worldToObject = new PackedMatrix[count];
                for (int i = 0; i < count; i++)
                {
                    objectToWorld[i] = new PackedMatrix(matrices[i]);
                    worldToObject[i] = new PackedMatrix(matrices[i].inverse);
                }
            }

            // Layout constants live beside the writer's, so the two cannot disagree.
            uint byteAddressObjectToWorld = (uint)MatricesByteBase;
            uint byteAddressWorldToObject = byteAddressObjectToWorld + (uint)(SizeOfPackedMatrix * count);

            // The leading block stays zero: every non-per-instance metadata constant
            // below points here, so uniform loads of any declared size read zeros.
            buffer.SetData(new int[byteAddressObjectToWorld / sizeof(int)]);
            if (prepared != null)
            {
                buffer.SetData(prepared.Values, 0,
                    (int)byteAddressObjectToWorld / sizeof(float), 12 * count);
                buffer.SetData(prepared.Values, 12 * count,
                    (int)byteAddressWorldToObject / sizeof(float), 12 * count);
            }
            else
            {
                buffer.SetData(objectToWorld, 0,
                    (int)(byteAddressObjectToWorld / SizeOfPackedMatrix), count);
                buffer.SetData(worldToObject, 0,
                    (int)(byteAddressWorldToObject / SizeOfPackedMatrix), count);
            }

            // Every DOTS instanced property URP's UniversalDOTSInstancing.hlsl
            // declares gets a metadata entry; otherwise the graphics backend can log
            // a warning per batch/pass/constant. The defaulted entries carry
            // Value = 0 (no per-instance bit, offset 0): plain constants uniformly
            // read the zero block, as an unresolved constant would, and the
            // override-checked ones (unity_SHCoefficients / unity_EntityId) take the
            // same global fallback path, so behaviour is unchanged.
            var metadata = new NativeArray<MetadataValue>(
                2 + DefaultedMetadataNames.Length + 2 * WindParamCount, Allocator.Temp);
            metadata[0] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_ObjectToWorld"),
                Value = 0x80000000 | byteAddressObjectToWorld,
            };
            metadata[1] = new MetadataValue
            {
                NameID = Shader.PropertyToID("unity_WorldToObject"),
                Value = 0x80000000 | byteAddressWorldToObject,
            };
            for (int i = 0; i < DefaultedMetadataNames.Length; i++)
            {
                metadata[2 + i] = new MetadataValue
                {
                    NameID = Shader.PropertyToID(DefaultedMetadataNames[i]),
                    Value = 0,
                };
            }

            // The previous-frame matrices. Zero-supplied they read the origin
            // as every instance's last position, which is only harmless while nothing
            // raises HasMotion — so with motion vectors on they ALIAS the current
            // matrices, per-instance bit set. PreviousM == M exactly, which is right
            // for content that does not move, and it costs no extra memory.
            if (motionVectors)
            {
                SetMetadata(metadata, "unity_MatrixPreviousM", 0x80000000 | byteAddressObjectToWorld);
                SetMetadata(metadata, "unity_MatrixPreviousMI", 0x80000000 | byteAddressWorldToObject);
            }

            // The 22 wind properties the SpeedTree9 graph declares (11 current + 11
            // history), always supplied and always pointing at the zero block. SIS
            // does not simulate wind, so a SpeedTree group that still draws a
            // wind-enabled material (no static clone generated yet) reads a cleared
            // record and stands still instead of reading garbage. Supplied
            // unconditionally, like the defaulted names above: one entry per
            // declared property.
            int windMetadataBase = 2 + DefaultedMetadataNames.Length;
            for (int i = 0; i < WindParamCount; i++)
            {
                metadata[windMetadataBase + i] = new MetadataValue
                {
                    NameID = Shader.PropertyToID(WindParamNames[i]),
                    Value = 0,
                };
                metadata[windMetadataBase + WindParamCount + i] = new MetadataValue
                {
                    NameID = Shader.PropertyToID(WindHistoryParamNames[i]),
                    Value = 0,
                };
            }

            BatchID batchID = StaticInstanceRendererSystem.AddBatch(metadata, buffer.bufferHandle);

            metadata.Dispose();
            return batchID;
        }

        /// <summary>
        /// Re-point an already-present metadata entry. Reports rather than shrugging
        /// when the name is absent: the only caller is the motion-vector aliasing,
        /// which works solely because `unity_MatrixPreviousM`/`MI` sit in
        /// <see cref="DefaultedMetadataNames"/>, and a future metadata sweep could
        /// mistake those two for entries that are no longer "defaulted". Silently
        /// skipping would turn per-object motion vectors off with no message.
        /// </summary>
        private static void SetMetadata(NativeArray<MetadataValue> metadata, string name, uint value)
        {
            int nameID = Shader.PropertyToID(name);
            for (int i = 0; i < metadata.Length; i++)
            {
                if (metadata[i].NameID != nameID)
                    continue;
                metadata[i] = new MetadataValue { NameID = nameID, Value = value };
                return;
            }

            Debug.LogError(
                $"Static Instances: metadata property '{name}' is not supplied by this batch, " +
                "so it cannot be re-pointed. Per-object motion vectors will not work until it " +
                "is added back to the supplied set.");
        }

        // The non-per-instance DOTS instanced properties of URP 17.3's
        // UniversalDOTSInstancing.hlsl (everything it declares besides the two
        // matrices above). Mostly unused by this content: lightmaps/reflection
        // probes are off (APV), and Light/Decal Layers are unsupported
        // (unity_RenderingLayer would need a real per-instance upload).
        //
        // `unity_MatrixPreviousM`/`MI` are the exception. They are listed here
        // because being listed is what lets the motion-vector path re-point them
        // (see `SetMetadata`): with `motionVectors: true` they alias the current
        // matrices per instance rather than reading zero. Removing them from this
        // list is what `SetMetadata`'s error exists to catch.
        private static readonly string[] DefaultedMetadataNames =
        {
            "unity_SpecCube0_HDR",
            "unity_LightmapST",
            "unity_LightmapIndex",
            "unity_DynamicLightmapST",
            "unity_MatrixPreviousM",
            "unity_MatrixPreviousMI",
            "unity_SHCoefficients",
            "unity_EntityId",
            "unity_RendererUserValuesPropertyEntry",
        };

        // The SpeedTree9 graph declares 11 wind properties of each kind (22 in
        // total, per the shader's own UNITY_DOTS_INSTANCED_PROP block).
        private const int WindParamCount = 11;

        private static readonly string[] WindParamNames = BuildWindParamNames("DOTS_ST_WindParam");
        private static readonly string[] WindHistoryParamNames =
            BuildWindParamNames("DOTS_ST_WindHistoryParam");

        private static string[] BuildWindParamNames(string prefix)
        {
            var names = new string[WindParamCount];
            for (int i = 0; i < WindParamCount; i++)
                names[i] = prefix + i;
            return names;
        }

        // ---- the instance-data buffer layout, in one place --------------------
        // [zero block][objectToWorld][worldToObject][tail slack].

        /// <summary>Bytes of the leading zero block. Every defaulted metadata
        /// constant points inside it, so it must stay zero for the buffer's life.</summary>
        internal const int ZeroBlockBytes = SizeOfPackedMatrix * 2;

        /// `ExtraBytes` (128) covers both the leading zero block and this remainder.
        private const int TailSlackBytes = ExtraBytes - ZeroBlockBytes;

        /// <summary>
        /// Where the matrix arrays start. Must stay a multiple of
        /// <see cref="SizeOfPackedMatrix"/>: `GraphicsBuffer.SetData(Array, ...,
        /// graphicsBufferStartIndex, ...)` offsets the destination by
        /// `graphicsBufferStartIndex * elemSize` (48 for `PackedMatrix`), so an
        /// unaligned base would be silently truncated.
        /// </summary>
        internal const int MatricesByteBase = ZeroBlockBytes;

        /// <summary>Total bytes one owner's instance-data buffer needs.</summary>
        internal static int TotalBufferBytes(int instanceCount) =>
            MatricesByteBase +
            SizeOfPackedMatrix * 2 * instanceCount +
            TailSlackBytes;

        internal static bool PushDrawDistances(
            StaticInstanceOwner owner, float cullDistance, float minDrawDistance, float fadeDuration)
        {
            if (owner == null ||
                (owner.CullDistance == cullDistance &&
                 owner.MinDrawDistance == minDrawDistance &&
                 owner.FadeDuration == fadeDuration))
                return false;

            owner.CullDistance = cullDistance;
            owner.MinDrawDistance = minDrawDistance;
            owner.FadeDuration = fadeDuration;
            return true;
        }

        // Orders renderers along a 3D Morton curve of their bounds centers (10 bits
        // per axis inside the union bounds). Purely a memory-layout concern:
        // nothing downstream depends on this order; it makes neighbouring instances
        // neighbours in the flat arrays.
        private static void SortBySpatialLocality(List<MeshRenderer> renderers)
        {
            if (renderers.Count < 2)
                return;

            Bounds union = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++)
                union.Encapsulate(renderers[i].bounds);

            Vector3 min = union.min;
            Vector3 size = union.size;
            float scaleX = size.x > 1e-5f ? 1023f / size.x : 0f;
            float scaleY = size.y > 1e-5f ? 1023f / size.y : 0f;
            float scaleZ = size.z > 1e-5f ? 1023f / size.z : 0f;

            MeshRenderer[] items = renderers.ToArray();
            var keys = new uint[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                Vector3 center = items[i].bounds.center;
                var x = (uint)Mathf.Clamp((center.x - min.x) * scaleX, 0f, 1023f);
                var y = (uint)Mathf.Clamp((center.y - min.y) * scaleY, 0f, 1023f);
                var z = (uint)Mathf.Clamp((center.z - min.z) * scaleZ, 0f, 1023f);
                keys[i] = (Part1By2(x) << 2) | (Part1By2(y) << 1) | Part1By2(z);
            }

            Array.Sort(keys, items);
            renderers.Clear();
            renderers.AddRange(items);
        }

        // Spreads the low 10 bits of v so they occupy every third bit.
        private static uint Part1By2(uint v)
        {
            v &= 0x3FF;
            v = (v | (v << 16)) & 0x30000FF;
            v = (v | (v << 8)) & 0x300F00F;
            v = (v | (v << 4)) & 0x30C30C3;
            v = (v | (v << 2)) & 0x9249249;
            return v;
        }

        internal static void DisposeOwner(ref StaticInstanceOwner owner)
        {
            if (owner == null)
                return;

            if (owner.DrawGroups.IsCreated)
                owner.DrawGroups.Dispose();
            if (owner.Bounds.IsCreated)
                owner.Bounds.Dispose();
            if (owner.ShapeFactors.IsCreated)
                owner.ShapeFactors.Dispose();
            if (owner.InstanceIndices.IsCreated)
                owner.InstanceIndices.Dispose();
            owner = null;
        }
    }
}
