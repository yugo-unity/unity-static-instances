using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances
{
    [AddComponentMenu("Rendering/Static Instance Scatter")]
    [DisallowMultipleComponent]
    public sealed class StaticInstanceScatter : MonoBehaviour, IStaticInstanceBakeTarget
    {

        [SerializeField] private bool visible = true;
        [SerializeField] private bool castShadows = true;
        // Casting stops at the boundary that ends this level, while the
        // instances stay visible. -1 = every level casts.
        //
        // A value at the ladder's coarsest level stops nothing (`level > N` is
        // never true there) and the inspector does not offer it.
        //
        // A value below the window minimum is raised to it everywhere:
        // OnValidate clamps it and ShadowCastMaxLevelClamped floors every push.
        //
        // It is a level index and not a distance: a per-owner float would
        // duplicate URP's shadowDistance. It therefore inherits LodDistanceScale
        // through the ladder, and an owner with no ladder has no expressible
        // threshold and keeps casting while visible.
        [SerializeField] private int shadowCastMaxLevel = -1;
        [SerializeField, Min(0f)] private float minDrawDistance;
        [SerializeField, Min(0f)] private float cullDistance;
        // In Distance mode the draw-range limits dither through a
        // crossFadeWidth-wide band just inside each limit, the same width the LOD
        // boundaries use, so distance fades are a pure function of distance.

        // -- Per-instance mesh LOD ladder.

        // Default OFF: a ladder is an authored decision, so a freshly added
        // component draws level 0 only until someone turns it on.
        [SerializeField] private bool lodEnabled;
        [SerializeField, Range(0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel)]
        private int maxMeshLodLevel = 1;
        [SerializeField, Range(0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel)]
        private int minMeshLodLevel;
        // Ascending level boundaries in meters (camera -> instance AABB
        // surface). Window semantics: the Min/Max range selects which mesh LOD
        // levels the ladder spans, so index i is the boundary between level
        // (min + i) and (min + i + 1) and the ladder needs (max - min) entries;
        // sliding the window shifts the levels over the same authored distances.
        // Grows but never shrinks, like the group's array, so narrowing while
        // editing does not destroy authored values. NonReorderable because the
        // multi-selection branch is the only place the array itself is drawn, it
        // sits inside the LOD section's foldout header group, and Unity's
        // reorderable drawer would open a second one.
        [SerializeField, NonReorderable] private float[] lodDistances;
        // The authoring surface for the LOD metric.
        //
        // Defaults are Screen Size and Time. A missing serialized key adopts its
        // field initializer, so serialized data (fixtures, converted assets) must
        // author both modes explicitly.
        [SerializeField] private StaticInstanceLodMode lodMode = StaticInstanceLodMode.ScreenSize;

        // The ladder in Screen Size mode: screen-size ratios, a second array
        // beside `lodDistances` rather than a reinterpretation of it. Two arrays
        // because toggling the mode is where an author experiments: a mode
        // round-trip returns the numbers they typed, and no conversion has to
        // lie about a per-instance quantity. The mode picks which array is live.
        //
        // It is descending where `lodDistances` ascends: a ratio falls as
        // distance grows (1.0 near, 0.25 far), so every guard written against the
        // metric ladder inverts its comparison here (the `Min >= Max` push, the
        // hysteresis half-band clamp, the monotonic assumption and the
        // short-ladder warning).
        [SerializeField, NonReorderable] private float[] lodRatios;

        // Screen Size mode's own far cutoff, authored as a screen ratio like the
        // ladder above it (the ladder's ratio bar carries LOD segments and an
        // explicit Culled threshold). 0 = unauthored, i.e. no ratio cull.
        //
        // This is the owner's size-cull authoring in this mode, and the global
        // `Size Cull Distance Scale` is inert for such an owner. A ratio cutoff is
        // size-proportional culling expressed in the mode's own unit (the
        // per-instance radius fold makes smaller instances cull nearer), so
        // composing the global knob on top would author one cutoff twice.
        // Distance-mode owners keep the global.
        //
        // It is a separate float rather than an extra slot on `lodRatios`: the
        // array's length already means "how many LOD boundaries", an unset Culled
        // boundary needs an expressible "absent" (0), and overloading the tail
        // would make a short-ladder test ambiguous.
        [SerializeField, Range(0f, 1f)] private float cullRatio;

        /// <summary>The Screen Size far cutoff as a screen ratio; 0 = unauthored.</summary>
        public float CullRatio => cullRatio;


        // The FOV the authored metres of `lodDistances` are metres at.
        //
        // Default 50 (not Unity's 60, an uncommon FOV for this content), seeded
        // from `Camera.main` when a scene camera exists. "Seeded" means at
        // component add or Inspector Reset only: it is a starting value, never a
        // live binding, so a camera whose FOV changes later never rewrites
        // authored data.
        //
        // Serialized regardless of mode: an orthographic camera and the fallback
        // view (which has no Camera at all) evaluate a Screen Size owner as if
        // viewed at this reference, instead of at a factor of 1 that would be
        // wrong in that mode.
        [SerializeField, Range(
            StaticInstanceLodSystem.MinReferenceFovDegrees,
            StaticInstanceLodSystem.MaxReferenceFovDegrees)]
        private float referenceFovDegrees =
            StaticInstanceLodSystem.DefaultReferenceFovDegrees;

        /// <summary>Which unit this component's ladder is authored in.</summary>
        public StaticInstanceLodMode LodMode
        {
            get => lodMode;
            set
            {
                if (lodMode == value)
                    return;
                lodMode = value;
                PushLodParams();
            }
        }

        /// <summary>
        /// The Screen Size ladder's ratios, or null when the component has never
        /// been authored in that mode. Deliberately a separate accessor from
        /// <see cref="LodDistances"/> rather than one mode-dependent property: the
        /// converter writes metric distances, so a single accessor that returned
        /// the ratio array by mode would hand an existing caller the wrong unit.
        /// </summary>
        public float[] LodRatios => lodRatios;

        /// <summary>The reference FOV the authored metres belong to, and the no-FOV fallback in Screen Size mode.</summary>
        public float ReferenceFovDegrees => referenceFovDegrees;

        // The Camera.main seed. Unity calls Reset when the component is added and
        // when the Inspector's Reset is used, which is exactly when the seed
        // applies. A scene with no camera keeps the 50 default.
        private void Reset()
        {
            // The seed rule lives on StaticInstanceLodSystem so it can be tested
            // without a scene: Camera.main answers about whatever scene is open.
            referenceFovDegrees = StaticInstanceLodSystem.SeedReferenceFovFrom(Camera.main);
        }
        // Width in meters of the dither crossfade band below each boundary
        // (0 = hard switch, guarded by lodHysteresis below). The blend is a
        // pure function of distance, so it cannot oscillate and needs no
        // hysteresis while it is on.
        [SerializeField, Min(0f)] private float crossFadeWidth = 5f;
        [SerializeField] private float lodFadeDuration;
        [SerializeField]
        private StaticInstanceFadeMode fadeMode = StaticInstanceFadeMode.Time;
        [SerializeField, Min(0f)] private float lodHysteresis = 5f;
        // Debug overrides, mirroring the group's: Forced (-1 = auto) pins
        // every instance to one level and bypasses the bands; Offset shifts
        // the selected level (+1 = one step coarser), clamped to the ladder.
        [SerializeField] private int forcedLodLevel = -1;
        [SerializeField] private int lodLevelOffset;


        [SerializeField, HideInInspector] private StaticInstancePreparedMatrices bakedPreparedMatrices;
        // Stamped on build scene copies, including partial source stripping.
        [SerializeField, HideInInspector] private bool bakedSourcesStripped;
        [SerializeField, HideInInspector] private Bounds[] bakedBounds;
        // Per-instance shape factor, parallel to bakedBounds. Null means identity
        // (see CollectedData.ShapeFactors). Staleness is only checked in the
        // editor, so identity-on-null keeps a stale bake in a player correct
        // rather than merely detected.
        [SerializeField, HideInInspector] private float[] bakedShapeFactors;
        [SerializeField, HideInInspector] private BakedDrawGroup[] bakedDrawGroups;
        [SerializeField, HideInInspector] private int[] bakedInstanceIndices;
        [SerializeField, HideInInspector] private string bakedSourceHash;

        [SerializeField, HideInInspector] private Matrix4x4 bakedOwnerMatrix;

        /// <summary>
        /// Requests per-object motion vectors for shader vertex animation, default OFF.
        /// Read at initialization. Shaders supply current/previous deformed positions;
        /// instance transforms remain immutable. Groups drawing a wind-stripped
        /// SpeedTree clone are excluded; other shader groups remain eligible.
        /// </summary>
        [SerializeField, HideInInspector] private bool perObjectMotionVectors;

        // -- Runtime state (never serialized).

        private GraphicsBuffer instanceData;
        private StaticInstanceOwner owner;
        private MeshRenderer[] sourceRenderers;
        private bool[] sourceRendererSuppressed;
        private List<Mesh> registeredMeshes;
        private List<Material> registeredMaterials;
        private bool initialized;
        private bool retainResourcesOnDisable;
        private bool releasedBakedMatrices;

        private int instanceCount;
        private int drawGroupCount;
        private int uniqueMeshCount;
        private int uniqueMaterialCount;

        // -- Public API (runtime-changeable; setters push into the shared
        //    system without reallocating anything).

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                if (owner != null)
                    owner.Visible = value && isActiveAndEnabled;
            }
        }

        public bool CastShadows
        {
            get => castShadows;
            set
            {
                castShadows = value;
                if (owner != null)
                {
                    owner.CastShadows = value;
                    StaticInstanceRendererSystem.UpdateEnabledViewTypes();
                }
            }
        }

        // The one clamp every push goes through, so the runtime can never see a
        // value the Editor would have raised. -1 stays -1 (the feature off);
        // anything else is floored at the window minimum and ceilinged at its max,
        // the same expression OnValidate applies to the serialized field.
        private int ShadowCastMaxLevelClamped => shadowCastMaxLevel < 0
            ? -1
            : Mathf.Clamp(shadowCastMaxLevel, minMeshLodLevel, maxMeshLodLevel);

        // Clamped to the ladder the level index refers to; -1 disables the
        // option. A value at or past the coarsest level stops nothing (`level > N`
        // is never true there) and the inspector shows it as "Every level casts".
        public int ShadowCastMaxLevel
        {
            get => shadowCastMaxLevel;
            set
            {
                int clamped = value < 0
                    ? -1
                    : Mathf.Clamp(value, minMeshLodLevel, maxMeshLodLevel);
                if (shadowCastMaxLevel == clamped)
                    return;
                shadowCastMaxLevel = clamped;
                if (owner != null)
                {
                    owner.ShadowCastMaxLevel = (sbyte)clamped;
                    StaticInstanceRendererSystem.MarkCombinedDataDirty();
                }
            }
        }

        public float CullDistance
        {
            get => cullDistance;
            set
            {
                cullDistance = Mathf.Max(0f, value);
                PushDrawDistances();
            }
        }

        public float MinDrawDistance
        {
            get => minDrawDistance;
            set
            {
                minDrawDistance = Mathf.Max(0f, value);
                PushDrawDistances();
            }
        }


        public bool LodEnabled
        {
            get => lodEnabled;
            set
            {
                lodEnabled = value;
                PushLodParams();
            }
        }

        // The ladder depth. Editor-time only (the boundary array length
        // follows it); the inspector greys it out during Play like the group's.
        public int MaxMeshLodLevel
        {
            get => maxMeshLodLevel;
            set => maxMeshLodLevel = Mathf.Clamp(
                value, 0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel);
        }

        public int MinMeshLodLevel
        {
            get => minMeshLodLevel;
            set
            {
                minMeshLodLevel = Mathf.Clamp(
                    value, 0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel);
                PushLodParams();
            }
        }

        public float[] LodDistances => lodDistances;

        public float CrossFadeWidth
        {
            get => crossFadeWidth;
            set
            {
                crossFadeWidth = Mathf.Max(0f, value);
                PushLodParams();
            }
        }

        // Time is accepted: SIS has a per-instance time fade, so the field
        // reflects what the component does.
        public StaticInstanceFadeMode FadeMode
        {
            get => fadeMode;
            set
            {
                if (fadeMode == value)
                    return;
                fadeMode = value;
                PushLodParams();
                PushDrawDistances();
            }
        }

        public StaticInstanceFadeMode EffectiveFadeMode =>
            StaticInstanceLodSystem.ResolveFadeMode(fadeMode);

        public float EffectiveLodFadeDuration => lodFadeDuration > 0f
            ? lodFadeDuration
            : Mathf.Max(0f, StaticInstanceLodSystem.LodFadeDuration);

        public float LodFadeDuration
        {
            get => lodFadeDuration;
            set
            {
                float clamped = Mathf.Max(0f, value);
                if (Mathf.Approximately(lodFadeDuration, clamped))
                    return;
                lodFadeDuration = clamped;
                PushDrawDistances();
            }
        }

        private float PushedFadeDuration => EffectiveFadeMode != StaticInstanceFadeMode.Time
            ? 0f
            : (lodFadeDuration > 0f ? lodFadeDuration : -1f);

        public int ForcedLodLevel
        {
            get => forcedLodLevel;
            set
            {
                forcedLodLevel = Mathf.Clamp(
                    value, -1, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel);
                PushLodParams();
            }
        }

        public int LodLevelOffset
        {
            get => lodLevelOffset;
            set
            {
                lodLevelOffset = Mathf.Clamp(
                    value,
                    -StaticInstanceLodSystem.MaxAuthorableMeshLodLevel,
                    StaticInstanceLodSystem.MaxAuthorableMeshLodLevel);
                PushLodParams();
            }
        }

        public bool IsInitialized => initialized;
        public int InstanceCount => instanceCount;
        public int DrawGroupCount => drawGroupCount;
        public int UniqueMeshCount => uniqueMeshCount;
        public int UniqueMaterialCount => uniqueMaterialCount;

        // Union AABB of the baked instance bounds, for the inspector's read-only
        // display. Authoring bounds are unnecessary: every distance decision is
        // per instance.
        public Bounds ComputeBakedBounds()
        {
            var union = new Bounds();
            if (bakedBounds == null || bakedBounds.Length == 0)
                return union;

            union = bakedBounds[0];
            for (int i = 1; i < bakedBounds.Length; i++)
                union.Encapsulate(bakedBounds[i]);
            return union;
        }



        private bool HasPreparedMatrices => bakedPreparedMatrices != null && bakedPreparedMatrices.IsPresent;

        /// <summary>Storage presence, including corrupt data. False after CPU release.</summary>
        public bool HasStoredBakedMatrices => HasPreparedMatrices;

        public bool HasBakedData =>
            (releasedBakedMatrices && initialized ||
                (HasPreparedMatrices && bakedPreparedMatrices.HasValidShape)) &&
            bakedBounds != null && bakedDrawGroups != null &&
            bakedInstanceIndices != null;

        public int BakedInstanceCount => releasedBakedMatrices ? instanceCount :
            HasPreparedMatrices ? bakedPreparedMatrices.Count : 0;

        /// <summary>Validates stored matrices before a build discards source renderers.</summary>
        public bool ValidateBakedMatrices() => HasBakedData && !releasedBakedMatrices &&
            bakedPreparedMatrices.Validate() &&
            bakedBounds.Length == bakedPreparedMatrices.Count;

        public string BakedSourceHash => bakedSourceHash;

        public void ApplyBakedSourceHash(string hash)
        {
            bakedSourceHash = hash;
        }

        public bool BakeFromHierarchy()
        {
            CollectedData data = StaticInstanceBuilder.CollectFromHierarchy(
                transform, new List<MeshRenderer>());
            if (data == null)
                return false;

            var prepared = StaticInstancePreparedMatrices.Create(data.Matrices);
            bakedPreparedMatrices = prepared;
            bakedSourcesStripped = false;
            releasedBakedMatrices = false;
            bakedBounds = data.Bounds;
            bakedShapeFactors = data.ShapeFactors;
            bakedDrawGroups = data.DrawGroups;
            bakedInstanceIndices = data.InstanceIndices;
            bakedOwnerMatrix = transform.localToWorldMatrix;
#if UNITY_EDITOR
            StaticInstanceEditorBridge.AssignStaticMaterials?.Invoke(bakedDrawGroups);
#endif
            return true;
        }

        /// <summary>
        /// Explicit query: reconstructs a fresh matrix array. Returns false after
        /// a stripped Player uploads and releases CPU matrices. Other arrays remain.
        /// </summary>
        public bool GetBakedData(
            out Matrix4x4[] matrices,
            out Bounds[] bounds,
            out BakedDrawGroup[] drawGroups,
            out int[] instanceIndices)
        {
            matrices = HasPreparedMatrices && bakedPreparedMatrices.Validate()
                ? bakedPreparedMatrices.Decode()
                : null;
            bounds = bakedBounds;
            drawGroups = bakedDrawGroups;
            instanceIndices = bakedInstanceIndices;
            return matrices != null && HasBakedData;
        }

        /// <summary>Returns the baked group array without reconstructing matrices.</summary>
        public BakedDrawGroup[] GetBakedDrawGroups() => bakedDrawGroups;

        public void ApplyBakedData(
            Matrix4x4[] matrices,
            Bounds[] bounds,
            BakedDrawGroup[] drawGroups,
            int[] instanceIndices,
            // Optional; null means identity rather than "recompute", which is
            // right for callers that synthesize tiled boxes. The bake path always
            // supplies it.
            float[] shapeFactors = null)
        {
            var prepared = StaticInstancePreparedMatrices.Create(matrices);
            bakedPreparedMatrices = prepared;
            bakedSourcesStripped = false;
            releasedBakedMatrices = false;
            bakedBounds = bounds;
            bakedShapeFactors = shapeFactors;
            bakedDrawGroups = drawGroups;
            bakedInstanceIndices = instanceIndices;
            bakedOwnerMatrix = transform.localToWorldMatrix;
        }

        public void ClearBakedData()
        {
            bakedPreparedMatrices = null;
            bakedSourcesStripped = false;
            releasedBakedMatrices = false;
            bakedBounds = null;
            bakedShapeFactors = null;
            bakedDrawGroups = null;
            bakedInstanceIndices = null;
            bakedSourceHash = null;
            bakedOwnerMatrix = default;
        }

        public Matrix4x4 BakedOwnerMatrix => bakedOwnerMatrix;

        public void SetLodDistances(float[] distances)
        {
            lodDistances = distances;
            PushLodParams();
        }

        // Public writer for the Screen Size ladder. Same shape and guards as
        // `SetLodDistances`: the array is taken as given (the ladder is authored,
        // not derived) and the push is what makes it live on an initialized
        // owner. Without it a script could not reach the default (Screen Size)
        // mode's ladder through the public API.
        public void SetLodRatios(float[] ratios)
        {
            lodRatios = ratios;
            PushLodParams();
        }

        public void CollectSourceMeshes(Dictionary<Mesh, int> results)
        {
            if (HasBakedData)
            {
                for (int i = 0; i < bakedDrawGroups.Length; i++)
                {
                    Mesh mesh = bakedDrawGroups[i].Mesh;
                    if (mesh == null)
                        continue;
                    results.TryGetValue(mesh, out int count);
                    results[mesh] = count + bakedDrawGroups[i].InstanceCount;
                }

                return;
            }

            foreach (MeshRenderer renderer in
                StaticInstanceOwnership.OwnedComponents<MeshRenderer>(this, false))
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;
                results.TryGetValue(mesh, out int count);
                results[mesh] = count + 1;
            }
        }

        public void CollectReferencedMaterials(List<Material> results)
        {
            if (HasBakedData)
            {
                for (int i = 0; i < bakedDrawGroups.Length; i++)
                {
                    if (bakedDrawGroups[i].Material != null)
                        results.Add(bakedDrawGroups[i].Material);
                }

                return;
            }

            foreach (MeshRenderer renderer in
                StaticInstanceOwnership.OwnedComponents<MeshRenderer>(this))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null)
                        results.Add(materials[i]);
                }
            }
        }

        // -- Lifecycle.

        private void OnEnable()
        {
            if (Application.isPlaying)
                Initialize();
        }

        private void OnDisable()
        {
            if (initialized && retainResourcesOnDisable)
            {
                owner.Visible = false;
                return;
            }
            Dispose();
        }

        private void OnDestroy()
        {
            Dispose();
        }

        private void OnValidate()
        {
            maxMeshLodLevel = Mathf.Clamp(
                maxMeshLodLevel, 0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel);
            minMeshLodLevel = Mathf.Clamp(minMeshLodLevel, 0, maxMeshLodLevel);
            crossFadeWidth = Mathf.Max(0f, crossFadeWidth);
            forcedLodLevel = Mathf.Clamp(forcedLodLevel, -1, maxMeshLodLevel);
            // Same treatment as forcedLodLevel above: a ladder-relative authored
            // value has to survive a Level Range edit.
            shadowCastMaxLevel = shadowCastMaxLevel < 0
                ? -1
                : Mathf.Clamp(shadowCastMaxLevel, minMeshLodLevel, maxMeshLodLevel);
            // Beyond +/- the window depth the offset cannot move the result any
            // further (the evaluation clamps to the window).
            int windowDepth = Mathf.Max(1, maxMeshLodLevel - minMeshLodLevel);
            lodLevelOffset = Mathf.Clamp(lodLevelOffset, -windowDepth, windowDepth);


            if (!initialized)
                return;
            owner.CastShadows = castShadows;
            StaticInstanceRendererSystem.UpdateEnabledViewTypes();
            owner.ShadowCastMaxLevel = (sbyte)ShadowCastMaxLevelClamped;
            owner.Visible = visible && isActiveAndEnabled;
            PushDrawDistances();
            PushLodParams();
        }

        public void Initialize()
        {
            if (initialized)
            {
                owner.Visible = visible && isActiveAndEnabled;
                return;
            }

            bool useBaked = HasBakedData;
            if (HasPreparedMatrices && !ValidateBakedMatrices())
            {
                useBaked = false;
                Debug.LogWarning("StaticInstanceScatter prepared matrix data is invalid. " +
                    "Re-bake this owner; intact source renderers will be used if available.", this);
            }
#if UNITY_EDITOR
            if (useBaked && StaticInstanceEditorBridge.IsBakeStale != null &&
                StaticInstanceEditorBridge.IsBakeStale(this))
            {
                useBaked = false;
                Debug.LogWarning(
                    "StaticInstanceScatter baked data is stale (the hierarchy " +
                    "changed after the last bake) — ignored for this Play and " +
                    "collected at runtime. Builds re-bake it automatically; " +
                    "with Bake On Build off they fail instead, since a stale " +
                    "bake cannot be detected outside the editor.",
                    this);
            }
#endif

            bool relocated = useBaked &&
                !StaticInstanceBakeGuard.MatchesOwner(bakedOwnerMatrix, transform);
            if (relocated)
                useBaked = false;

            // A partial strip leaves some renderers, but they cannot reconstruct
            // the complete owner after corruption or world-space relocation.
            if (!useBaked && bakedSourcesStripped)
            {
                Debug.LogError("StaticInstanceScatter cannot rebuild this bake because its " +
                    "sources were stripped. Restore source content and re-bake the owner.", this);
                return;
            }

            CollectedData data;
            if (useBaked)
            {
                data = new CollectedData
                {
                    Matrices = bakedPreparedMatrices.MatchesProducer
                        ? null : bakedPreparedMatrices.Decode(),
                    PreparedMatrices = bakedPreparedMatrices.MatchesProducer
                        ? bakedPreparedMatrices : null,
                    Bounds = bakedBounds,
                    // Null when the baked data carries no shape factors; BuildRuntimeData reads null as identity.
                    ShapeFactors = bakedShapeFactors,
                    DrawGroups = bakedDrawGroups,
                    InstanceIndices = bakedInstanceIndices,
                    ValidRenderers =
                        StaticInstanceOwnership.OwnedComponents<MeshRenderer>(this),
                };
            }
            else
            {
                data = StaticInstanceBuilder.CollectFromHierarchy(
                    transform, new List<MeshRenderer>());
                if (data == null)
                {
                    // Branch on the collection result rather than on "was it
                    // stripped": a prefab saved with disabled sources collects zero
                    // too, and means the same thing.
                    Debug.LogError(
                        relocated
                            ? "StaticInstanceScatter was baked at a different transform and " +
                              "has no source MeshRenderers to fall back on, so it draws " +
                              "nothing. Re-bake it where it now sits, or restore its source " +
                              "renderers. (Baked instance matrices are world-space.)"
                            : "StaticInstanceScatter found no valid MeshRenderers.",
                        this);
                    return;
                }

                if (relocated)
                {
                    Debug.LogWarning(
                        "StaticInstanceScatter was baked at a different transform, so its " +
                        "baked world-space matrices no longer describe where it sits — " +
                        "collected at runtime instead. Re-bake it here to get the baked load " +
                        "path back.",
                        this);
                }
            }

            registeredMeshes = new List<Mesh>();
            registeredMaterials = new List<Material>();
            owner = StaticInstanceBuilder.BuildRuntimeData(
                data,
                registeredMeshes,
                registeredMaterials,
                out instanceData,
                out instanceCount,
                out drawGroupCount,
                out uniqueMeshCount,
                out uniqueMaterialCount,
                // The builder excludes groups drawing a static clone; other
                // vertex-animation shaders retain the owner's initialization opt-in.
                motionVectors: perObjectMotionVectors);
            owner.Visible = visible && isActiveAndEnabled;
            owner.CastShadows = castShadows;
            owner.ShadowCastMaxLevel = (sbyte)ShadowCastMaxLevelClamped;
            owner.CullDistance = cullDistance;
            owner.MinDrawDistance = minDrawDistance;
            owner.Root = gameObject;
            owner.PickingEntityId = gameObject.GetEntityId();
            ApplyLodParams(owner);
            if (HasLadderShortfall(
                    lodEnabled, minMeshLodLevel, maxMeshLodLevel, lodDistances,
                    lodMode, lodRatios))
            {
                Debug.LogWarning(
                    LadderShortfallWarning(
                        name, lodEnabled, minMeshLodLevel, maxMeshLodLevel, lodDistances,
                        lodMode, lodRatios),
                    this);
            }

            if (data.ValidRenderers != null)
            {
                sourceRenderers = data.ValidRenderers;
                sourceRendererSuppressed = new bool[sourceRenderers.Length];
                for (int i = 0; i < sourceRenderers.Length; i++)
                {
                    if (StaticInstanceBuilder.IsEligibleSource(sourceRenderers[i]))
                    {
                        sourceRendererSuppressed[i] = true;
                        sourceRenderers[i].enabled = false;
                    }
                }
            }

            initialized = true;
            StaticInstanceRendererSystem.RegisterOwner(owner);
            // Keep the Editor lifecycle. Surviving renderers cannot identify
            // partial stripping; use the stamp written by the build scene processor.
            retainResourcesOnDisable = bakedSourcesStripped && !Application.isEditor;
            if (retainResourcesOnDisable)
            {
                bakedPreparedMatrices = null;
                releasedBakedMatrices = true;
            }

            // Built inside the branch that consumes it, never before the decision.
            // The baked path (the healthy one, once per owner per Initialize) goes
            // through the [Conditional] wrapper, so a release build constructs
            // nothing here at all.
            if (useBaked)
            {
                StaticInstanceLog.Info(
                    InitializedMessage(
                        useBaked, instanceCount, drawGroupCount,
                        uniqueMeshCount, uniqueMaterialCount),
                    this);
            }
            else
            {
                Debug.LogWarning(
                    InitializedMessage(
                        useBaked, instanceCount, drawGroupCount,
                        uniqueMeshCount, uniqueMaterialCount) +
                    " No usable baked data — collected at " +
                    "runtime, costing a managed allocation and a load spike. " +
                    "Builds bake automatically — including Addressables " +
                    "content builds — unless Bake On Build is off in Project " +
                    "Settings > Graphics > Static Instances.",
                    this);
            }
        }

        // One definition of the line both branches open with, so the informative
        // half cannot drift from the warning half. It is a method rather than a
        // local because only one branch ever builds it, and inside the
        // [Conditional] wrapper's argument the call disappears with the rest of
        // the expression in a release build.
        private static string InitializedMessage(
            bool useBaked, int instanceCount, int drawGroupCount,
            int uniqueMeshCount, int uniqueMaterialCount)
        {
            return
                $"StaticInstanceScatter initialized ({(useBaked ? "baked" : "runtime collect")}): " +
                $"{instanceCount} instances, {drawGroupCount} draw groups, " +
                $"{uniqueMeshCount} meshes, {uniqueMaterialCount} materials.";
        }

        private void PushDrawDistances()
        {
            if (!initialized)
                return;
            if (StaticInstanceBuilder.PushDrawDistances(
                    owner, cullDistance, minDrawDistance, PushedFadeDuration))
            {
                StaticInstanceRendererSystem.MarkCombinedDataDirty();
            }
        }

        // Flattens the authored ladder into the owner's PerInstance parameters.
        // The policy itself is the gate: with the ladder disabled (or no
        // boundaries authored) the owner registers as PerOwner at level 0, and
        // the scene pays none of the per-instance level tax.
        private void ApplyLodParams(StaticInstanceOwner target)
        {
            int boundaryCount = maxMeshLodLevel - minMeshLodLevel;
            bool active = lodEnabled && boundaryCount > 0 && HasLadderFor(boundaryCount);
            target.LodPolicy = active
                ? LodSelectionPolicy.PerInstance
                : LodSelectionPolicy.PerOwner;
            target.MeshLodLevel = (byte)(lodEnabled ? minMeshLodLevel : 0);
            // None zeroes the width, which is how the band's absence is expressed
            // internally, so a hard-switching SIS keeps the authored width in the
            // component and simply does not hand it over.
            bool distance = EffectiveFadeMode == StaticInstanceFadeMode.Distance;
            target.LodCrossFadeWidth = distance ? crossFadeWidth : 0f;
            target.LodHysteresis = lodHysteresis;
            target.LodDistanceFade = distance;
            // The gate the per-instance time machine runs on. `None` answers it
            // the same way Time does, so `!LodDistanceFade` is not a substitute.
            target.LodTimeFade = EffectiveFadeMode == StaticInstanceFadeMode.Time;
            target.LodMinLevel = (byte)Mathf.Clamp(minMeshLodLevel, 0, maxMeshLodLevel);
            target.LodMaxLevel = (byte)maxMeshLodLevel;
            target.LodForcedLevel = (sbyte)Mathf.Clamp(forcedLodLevel, -1, maxMeshLodLevel);
            // Clamped to the same ladder the level index refers to.
            target.ShadowCastMaxLevel = (sbyte)ShadowCastMaxLevelClamped;
            target.LodLevelOffset = (sbyte)Mathf.Clamp(
                lodLevelOffset, -maxMeshLodLevel, maxMeshLodLevel);
            target.LodBoundaries = new Unity.Mathematics.float3(
                Boundary(0), Boundary(1), Boundary(2));
            // Pushed here so the rebuild can flatten it. Clamped on the consuming
            // side, not trusted: `[Range]` guards the Inspector, but a hand-edited
            // scene, a Preset or a script can carry any float, and a reference
            // outside (0, 180) would make `tan(ref/2)` meaningless.
            target.LodMode = lodMode;
            target.LodCullRatio = Mathf.Clamp01(cullRatio);
            target.LodReferenceFovDegrees = Mathf.Clamp(
                referenceFovDegrees,
                StaticInstanceLodSystem.MinReferenceFovDegrees,
                StaticInstanceLodSystem.MaxReferenceFovDegrees);
        }

        private bool HasLadderFor(int boundaryCount) =>
            HasLadderFor(boundaryCount, lodMode, lodDistances, lodRatios);

        // The decision lives once and is reachable without an initialized
        // component, the same shape `HasLadderShortfall` uses, so a test does not
        // have to stand a whole owner up to ask it.
        // Public, not internal: the Editor-side mirror
        // (`StaticInstanceLodGroupConverter.LadderLive`) has to call this
        // predicate rather than restate it, and `StaticInstances.Editor` holds no
        // `InternalsVisibleTo` grant (only `StaticInstances.Editor.Tests` does).
        // Making the function public opens nothing new, because every argument it
        // takes is already a public getter on these components.
        public static bool HasLadderFor(
            int boundaryCount, StaticInstanceLodMode mode,
            float[] distances, float[] ratios)
        {
            float[] ladder = mode == StaticInstanceLodMode.ScreenSize ? ratios : distances;
            return ladder != null && ladder.Length >= boundaryCount;
        }

        internal static bool HasLadderShortfall(
            bool lodEnabled, int minLevel, int maxLevel, float[] distances,
            StaticInstanceLodMode mode = StaticInstanceLodMode.Distance,
            float[] ratios = null)
        {
            int needed = maxLevel - minLevel;
            if (!lodEnabled || needed <= 0)
                return false;

            float[] ladder = mode == StaticInstanceLodMode.ScreenSize ? ratios : distances;
            int authored = ladder == null ? 0 : ladder.Length;
            return authored < needed;
        }

        internal static string LadderShortfallWarning(
            string ownerName, bool lodEnabled, int minLevel, int maxLevel, float[] distances,
            StaticInstanceLodMode mode = StaticInstanceLodMode.Distance,
            float[] ratios = null)
        {
            // The decision lives once, in the predicate above. What is left here
            // is the message's own arithmetic, which only runs when there is a
            // message to build.
            if (!HasLadderShortfall(lodEnabled, minLevel, maxLevel, distances, mode, ratios))
                return null;

            bool screenSize = mode == StaticInstanceLodMode.ScreenSize;
            float[] ladder = screenSize ? ratios : distances;
            int needed = maxLevel - minLevel;
            int authored = ladder == null ? 0 : ladder.Length;
            // The message names the array the live mode reads; naming "distances"
            // to an author who is editing ratios sends them to the wrong row.
            string what = screenSize ? "level size ratios" : "level distances";
            return $"StaticInstanceScatter '{ownerName}': LOD is enabled but only " +
                   $"{authored} of {needed} {what} are authored, so every " +
                   $"instance stays at mesh LOD {minLevel} (no per-instance ladder). " +
                   "Raise or re-author the LOD Level Range in the Inspector, which " +
                   "fills the missing boundaries.";
        }

        // What this returns depends on the mode, and the two are different
        // quantities in the same float3.
        //
        // Distance mode returns authored metres, unchanged. Screen Size mode
        // returns `1 / ratio`, the reciprocal, because the job's boundary test
        // multiplies this by the per-instance corrected radius and the camera's
        // `1 / tan(fov/2)` to get `d_j = r_i / (ratio_j * tan(fov/2))`. Putting the
        // reciprocal here keeps the division at rebuild frequency instead of per
        // instance per frame.
        //
        // A ratio of 0 means "never coarser than this level" and must not become
        // an infinite boundary by accident: it maps to `float.MaxValue`
        // explicitly, the same sentinel a short ladder uses.
        // explicitly, the same sentinel a short ladder uses.
        private float Boundary(int index)
        {
            if (lodMode == StaticInstanceLodMode.ScreenSize)
            {
                if (lodRatios == null || index >= lodRatios.Length ||
                    index >= maxMeshLodLevel - minMeshLodLevel)
                    return float.MaxValue;

                return StaticInstanceLodSystem.RatioToBoundary(lodRatios[index]);
            }

            if (lodDistances == null || index >= lodDistances.Length ||
                index >= maxMeshLodLevel - minMeshLodLevel)
                return float.MaxValue;
            return Mathf.Max(0f, lodDistances[index]);
        }

        private void PushLodParams()
        {
            if (!initialized)
                return;
            ApplyLodParams(owner);
            // The params are flattened into the combined arrays, so a change
            // requires the lazy rebuild; fine at editing frequency, same as the
            // draw distances.
            StaticInstanceRendererSystem.MarkCombinedDataDirty();
        }

        private void Dispose()
        {
            if (!retainResourcesOnDisable && sourceRenderers != null)
            {
                for (int i = 0; i < sourceRenderers.Length; i++)
                {
                    if (sourceRenderers[i] != null && sourceRendererSuppressed[i])
                        sourceRenderers[i].enabled = true;
                }
            }
            sourceRenderers = null;
            sourceRendererSuppressed = null;

            if (initialized)
            {
                // Release the refcounted mesh/material registrations while the
                // shared BRG is guaranteed alive (this owner is still registered),
                // then unregister; the system disposes the BRG with the last owner.
                if (registeredMeshes != null)
                {
                    for (int i = 0; i < registeredMeshes.Count; i++)
                        StaticInstanceRendererSystem.UnregisterMesh(registeredMeshes[i]);
                }

                if (registeredMaterials != null)
                {
                    for (int i = 0; i < registeredMaterials.Count; i++)
                        StaticInstanceRendererSystem.UnregisterMaterial(registeredMaterials[i]);
                }

                StaticInstanceRendererSystem.UnregisterOwner(owner);
            }

            registeredMeshes = null;
            registeredMaterials = null;
            instanceData?.Dispose();
            instanceData = null;
            StaticInstanceBuilder.DisposeOwner(ref owner);
            initialized = false;
            retainResourcesOnDisable = false;
        }
    }
}
