using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances
{
    internal struct CullingBounds
    {
        public Vector3 Center;
        public Vector3 Extents;
    }

    // One cascade's receiver sphere, pre-rotated into light space so the
    // per-cluster test is a subtraction rather than a matrix multiply, plus the
    // blend factor that decides how much of it counts as the "core" a caster
    // can be dropped from the coarser splits by.
    internal struct ReceiverSplitSphere
    {
        public float4 SphereLightSpace;
        public float CascadeBlendCullingFactor;
    }

    // The per-view receiver-volume constants, carried by value inside the mask
    // job. The build step keeps the same data in Allocator.Temp for the cluster
    // pass, which is not valid inside a scheduled job, so the job gets a
    // fixed-capacity copy instead of an allocation.
    //
    // Capacity is 6 on both dimensions, but the bounds differ in kind. The split
    // bound is the API's: BRG clamps splitCount at 6. The plane bound is a
    // capacity this code chooses (`receiverPlaneCount` has no cap and
    // BuildReceiverVolumePlanes sizes its array dynamically), so the builder
    // falls back rather than clamping when either dimension overflows.
    //
    // Payload: 6*16 (Plane) + 6*20 (ReceiverSplitSphere) + 36 (float3x3) = 252 B.
    internal unsafe struct ReceiverVolumeConstants
    {
        public const int Capacity = 6;
        public const int PlaneSize = 16;
        public const int SphereSize = 20;

        public fixed byte PlaneBytes[Capacity * PlaneSize];
        public fixed byte SphereBytes[Capacity * SphereSize];
        public float3x3 WorldToLight;
        // Derived from WorldToLight once per view rather than per instance:
        // the direction a shadow travels in world space.
        public float3 ShadowDirection;
        public int PlaneCount;
        public int SplitCount;
    }

    // Burst direct-call entry for the main-thread part of culling: the work is
    // per owner rather than per instance, so it does not deserve a job, but
    // managed float4 math on the main thread is too slow.
    [BurstCompile]
    internal static class StaticInstanceCullingBurst
    {
        // One AABB-vs-split verdict per cluster per split. A cluster is the
        // early-rejection unit: a whole PerOwner owner, or one Morton-contiguous
        // slice of a large PerInstance owner (without slicing, a scene-sized
        // AABB straddles every split and the pre-test rejects nothing).
        // Writes the split bits the mask job consumes: Intersect is every split
        // the cluster reaches, Inside the subset it lies wholly within (those
        // need no per-instance test). A cluster of a non-rendered owner gets an
        // empty mask.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        public static unsafe void ClassifyClusters(
            [NoAlias] CullingBounds* clusterAabbs,
            [NoAlias] PlanePacket4* packets,
            [NoAlias] SplitPlaneRange* splits,
            [NoAlias] byte* ownerFlags,
            [NoAlias] int* clusterOwners,
            [NoAlias] byte* intersectMasks,
            [NoAlias] byte* insideMasks,
            // Counters: [0] clusters belonging to a rendered owner, [1] how many
            // of those reach no split at all, [2] the instances those hold.
            // Counted here because the verdict is already in hand. Only rendered
            // owners count, since a non-rendered owner's groups never pass the
            // emission gate.
            [NoAlias] int* sizingCounters,
            [NoAlias] int* clusterInstanceCounts,
            int clusterCount,
            int splitCount)
        {
            int renderedClusters = 0;
            int outsideClusters = 0;
            int outsideInstances = 0;
            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                byte intersectMask = 0;
                byte insideMask = 0;
                if (ownerFlags[clusterOwners[cluster]] != 0)
                {
                    renderedClusters++;
                    CullingBounds bounds = clusterAabbs[cluster];
                    var cx = new float4(bounds.Center.x);
                    var cy = new float4(bounds.Center.y);
                    var cz = new float4(bounds.Center.z);
                    var ex = new float4(bounds.Extents.x);
                    var ey = new float4(bounds.Extents.y);
                    var ez = new float4(bounds.Extents.z);

                    for (int splitIndex = 0; splitIndex < splitCount; splitIndex++)
                    {
                        SplitPlaneRange split = splits[splitIndex];
                        var isCulled = new bool4(false);
                        var isOutsideAny = new bool4(false);
                        int packetEnd = split.PlaneOffset + split.PlaneCount;
                        for (int i = split.PlaneOffset; i < packetEnd; i++)
                        {
                            PlanePacket4 p = packets[i];
                            float4 distances = p.nx * cx + p.ny * cy + p.nz * cz + p.d;
                            float4 radii = p.nxAbs * ex + p.nyAbs * ey + p.nzAbs * ez;
                            // Same multiply-adds as TestPlanePackets, read from
                            // both sides: `distance + radius < 0` culls on any
                            // plane, `distance - radius >= 0` must hold on every
                            // plane to be wholly inside. Packet padding repeats
                            // the split's last real plane, so a duplicated lane
                            // cannot change either verdict.
                            isCulled |= distances + radii < float4.zero;
                            isOutsideAny |= distances - radii < float4.zero;
                        }

                        if (math.any(isCulled))
                            continue;
                        intersectMask |= (byte)(1 << splitIndex);
                        if (!math.any(isOutsideAny))
                            insideMask |= (byte)(1 << splitIndex);
                    }

                    if (intersectMask == 0)
                    {
                        outsideClusters++;
                        outsideInstances += clusterInstanceCounts[cluster];
                    }
                }

                intersectMasks[cluster] = intersectMask;
                insideMasks[cluster] = insideMask;
            }

            sizingCounters[0] = renderedClusters;
            sizingCounters[1] = outsideClusters;
            sizingCounters[2] = outsideInstances;
        }

        // Shared by the cluster pass (ApplyReceiverSphereMask) and the
        // per-instance test in ComputeVisibilityMasksJob, so both use one
        // implementation of the shadow sweep and the reach + core-containment
        // test.
        //
        // How far the shadow has to run before it has left every light-facing
        // frustum plane: past that, it can reach no receiver anywhere in the
        // frustum. Recomputed per caster (per instance on the per-instance path,
        // not inherited from the cluster): the safe error direction is a longer
        // shadow, and a length measured from the cluster's centre with the
        // cluster's radius does not trivially bound every member instance's.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe float ComputeShadowLength(
            float3 casterCenterWorld,
            float casterRadius,
            float3 shadowDirection,
            [NoAlias] Plane* lightFacingPlanes,
            int lightFacingCount)
        {
            float shadowLength = float.PositiveInfinity;
            for (int i = 0; i < lightFacingCount; i++)
            {
                Plane plane = lightFacingPlanes[i];
                float3 n = plane.normal;
                // Glancing angles are clamped, or the division below
                // explodes; the clamp widens the result, which is the safe
                // direction (a longer shadow keeps more splits).
                float cosTheta = math.max(math.abs(math.dot(n, shadowDirection)), 0.001f);
                float height = math.dot(n, casterCenterWorld) + plane.distance;
                float sinTheta = math.sqrt(math.max(1f - cosTheta * cosTheta, 0f));
                float distance = height / cosTheta + casterRadius * sinTheta / cosTheta;
                shadowLength = math.min(shadowLength, distance);
            }

            return math.max(shadowLength, 0f);
        }

        // The split mask a caster can actually reach: the per-split reach test,
        // then the core-containment break. Callers AND this into whatever mask
        // they already hold — it never widens one.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe uint ComputeReceiverSphereMask(
            float3 casterCenterLight,
            float casterRadius,
            float shadowLength,
            [NoAlias] ReceiverSplitSphere* splitSpheres,
            int sphereSplitCount)
        {
            uint sphereMask = 0;
            for (int splitIndex = 0; splitIndex < sphereSplitCount; splitIndex++)
            {
                ReceiverSplitSphere split = splitSpheres[splitIndex];
                float3 receiverCenter = split.SphereLightSpace.xyz;
                float receiverRadius = split.SphereLightSpace.w;
                float3 receiverToCaster = casterCenterLight - receiverCenter;

                // Where the two spheres would just touch, measured along
                // the light's z: negative means they do not even overlap
                // as circles in xy, so no shadow can land on this split.
                float maxDistance = casterRadius + receiverRadius;
                float zSqAtIntersection =
                    maxDistance * maxDistance - math.lengthsq(receiverToCaster.xy);
                if (zSqAtIntersection < 0f)
                    continue;
                // Past the receiver sphere in the light direction: the
                // shadow travels away from it.
                if (receiverToCaster.z > 0f &&
                    receiverToCaster.z * receiverToCaster.z > zSqAtIntersection)
                    continue;

                sphereMask |= 1u << splitIndex;

                // Shaders sample the lowest-index cascade that covers a
                // pixel, so a capsule wholly inside this split's core
                // sphere can be dropped from every coarser split.
                float coreRadius = receiverRadius * split.CascadeBlendCullingFactor;
                float3 shadowEnd = receiverToCaster + new float3(0f, 0f, shadowLength);
                float capsuleMax = coreRadius - casterRadius;
                float capsuleDistanceSq = math.max(
                    math.lengthsq(receiverToCaster), math.lengthsq(shadowEnd));
                if (capsuleMax > 0f && capsuleDistanceSq < capsuleMax * capsuleMax)
                    break;
            }

            return sphereMask;
        }

        // The receiver-sphere test applied at the cluster stage. A caster whose
        // shadow cannot reach a cascade's receiver sphere is dropped from that
        // split: the split bits are cleared in the masks ClassifyClusters just
        // wrote, so every stage downstream sees one fewer split to test per
        // instance.
        //
        // Composed by AND with the plane-derived masks, and errors may only
        // widen the result: a falsely cleared bit is a missing shadow, a falsely
        // kept one only costs work. The cluster AABB is bounded by a sphere of
        // its extents' length, which over-covers it, and the glancing-angle clamp
        // in ComputeShadowLength lengthens the shadow rather than shortening it;
        // both widen.
        //
        // `insideMask` is cleared alongside `intersectMask`: it is a subset
        // meaning "wholly within this split, no per-instance test needed", so a
        // split the cluster casts nothing into must lose both bits.
        //
        // Counters (non-zero only when the mask is on): [0] clusters reaching a
        // split, [1]/[2] unused, [3]/[4] clusters/instances dropped from the view
        // outright, [5]/[6] instance*split work before and after. The mask job's
        // work is per instance per split, so a cluster that loses one bit and
        // keeps another is a partial win the cluster counts cannot express.
        //
        // `worldToLight` is a pointer because a Burst direct call cannot take
        // these by value: the Editor accepts it and only the AOT player build
        // fails.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        public static unsafe void ApplyReceiverSphereMask(
            [NoAlias] CullingBounds* clusterAabbs,
            [NoAlias] byte* ownerFlags,
            [NoAlias] int* clusterOwners,
            [NoAlias] byte* intersectMasks,
            [NoAlias] byte* insideMasks,
            [NoAlias] int* clusterInstanceCounts,
            [NoAlias] Plane* lightFacingPlanes,
            int lightFacingCount,
            [NoAlias] ReceiverSplitSphere* splitSpheres,
            int sphereSplitCount,
            [NoAlias] float3x3* worldToLight,
            [NoAlias] long* counters,
            int clusterCount)
        {
            float3x3 rotation = *worldToLight;
            // The direction a shadow travels, in world space: the light-space
            // +z axis brought back through the inverse rotation.
            float3 shadowDirection = math.transpose(rotation).c2;

            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                if (ownerFlags[clusterOwners[cluster]] == 0)
                    continue;
                byte intersectMask = intersectMasks[cluster];
                if (intersectMask == 0)
                    continue;

                counters[0]++;
                CullingBounds bounds = clusterAabbs[cluster];
                int instances = clusterInstanceCounts[cluster];

                // The caster is treated as a sphere, then swept along the
                // shadow direction into a capsule — the same shape URP's
                // receiver-sphere culler uses.
                float3 casterCenterWorld = bounds.Center;
                float3 casterCenterLight = math.mul(rotation, casterCenterWorld);
                float casterRadius = math.length((float3)bounds.Extents);

                float shadowLength = ComputeShadowLength(
                    casterCenterWorld, casterRadius, shadowDirection,
                    lightFacingPlanes, lightFacingCount);
                uint sphereMask = ComputeReceiverSphereMask(
                    casterCenterLight, casterRadius, shadowLength,
                    splitSpheres, sphereSplitCount);

                uint before = intersectMask;
                uint after = before & sphereMask;
                if (after == 0)
                {
                    counters[3]++;
                    counters[4] += instances;
                }

                counters[5] += (long)math.countbits(before) * instances;
                counters[6] += (long)math.countbits(after) * instances;

                intersectMasks[cluster] = (byte)after;
                insideMasks[cluster] = (byte)(insideMasks[cluster] & sphereMask);
            }
        }

        // Shared by the classifier and per-instance evaluation. The operation
        // order is identical at neutral bias, including the no-FOV fallback.
        internal static void ComposeEvaluationScales(float distanceScale, float lodBias,
            float refTan, bool screenSize, float screenRadius, float cameraFovTerm,
            bool fovApplies, bool hasFov, out float boundaryScale, out float cullScale)
        {
            float ownerFovFactor = math.select(1f, refTan * cameraFovTerm, fovApplies);
            boundaryScale = distanceScale * ownerFovFactor;
            cullScale = ownerFovFactor;
            if (screenSize)
            {
                float screenTerm = hasFov ? cameraFovTerm : 1f / math.max(refTan, 1e-6f);
                boundaryScale = distanceScale * screenRadius * screenTerm;
                cullScale = screenTerm;
            }
            if (lodBias != 1f)
            {
                boundaryScale *= lodBias;
                cullScale *= lodBias;
            }
        }

        // Classifies clusters for cluster-uniform LOD: does a whole cluster
        // resolve to one LOD level with no instance in any fade band?
        //
        // Conservative by construction. `clusterAabbs` is the union of the
        // slice's instance bounds, so every instance box is contained in it;
        // containment gives one interval that bounds both metrics the evaluation
        // uses (an instance's AABB-surface distance for LOD bands and its centre
        // distance for Draw Range bands): each is at least the cluster box's
        // surface distance and at most the distance to its farthest corner.
        // Using one interval for both is slightly pessimistic, which is the safe
        // direction: a false non-uniform costs a missed optimization, a false
        // uniform would be a visual bug.
        //
        // The level ladder must mirror DrivingViewEvalJob exactly (window
        // semantics, DistanceScale on the boundaries but not on the crossfade
        // width, the global floor applied after selection).
        //
        // Counters: [0] eligible clusters, [1] uniform under the full
        // definition, [2] uniform on the level band alone (level-band
        // membership by itself overstates the rate).
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        public static unsafe void ClassifyClusterUniformity(
            [NoAlias] CullingBounds* clusterAabbs,
            [NoAlias] int* clusterOwners,
            [NoAlias] float* clusterFarMin,
            [NoAlias] float* clusterNearMax,
            [NoAlias] OwnerLodParams* ownerParams,
            // Three floats rather than a float3: a Burst direct call cannot
            // take a vector type by value across the interop boundary
            // (BC1064/BC1067), which only shows up in an AOT player build —
            // the Editor accepts it. Same reason ClassifyClusters takes
            // nothing but pointers and primitives.
            float cameraX,
            float cameraY,
            float cameraZ,
            float distanceScale,
            float lodBias,
            // The per-camera FOV term. This function composes the owner's FOV
            // factor itself from `cameraFovTerm` and that owner's `RefTan` (the
            // reference is per component), applied to the Draw Range limits
            // exactly as the eval job applies it. It must compose it the same
            // way, or the classifier and the per-instance path disagree and a
            // false "uniform" becomes a visual bug.
            //
            // The LOD side needs no parameter of its own: the caller folds the
            // factor into distanceScale, because both multiply the same
            // boundaries. They are two scales rather than one because they do
            // not cover the same set: distanceScale is LOD-only by design and
            // never reaches the draw range, while the FOV policy is uniform over
            // both. 1f = feature off, byte-identical.
            float cameraFovTerm,
            byte fovApplies,
            int globalMinLevel,
            byte crossFadeEnabled,
            int clusterCount,
            [NoAlias] int* counters,
            // The cluster-uniform output: level + 1 for a uniform cluster, 0
            // otherwise. The +1 bias keeps "not uniform" distinguishable from
            // level 0 without a second array. May be null when only the counters
            // are wanted.
            [NoAlias] byte* uniformLevels)
        {
            var cameraPosition = new float3(cameraX, cameraY, cameraZ);
            bool writeLevels = uniformLevels != null;
            int eligible = 0;
            int uniform = 0;
            int levelUniform = 0;
            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                if (writeLevels)
                    uniformLevels[cluster] = 0;

                OwnerLodParams p = ownerParams[clusterOwners[cluster]];
                // The same compose the eval job performs, per owner.
                // `clusterOwners` is one owner per cluster, so the factor is
                // exact per cluster.
                ComposeEvaluationScales(distanceScale, lodBias, p.RefTan, false, 0f,
                    cameraFovTerm, fovApplies != 0, true,
                    out float boundaryScale, out float cullDistanceScale);
                if (p.Policy != (byte)LodSelectionPolicy.PerInstance ||
                    p.CrossFadeWidth <= 0f ||
                    p.ScreenSize != 0)
                {
                    continue;
                }

                eligible++;

                CullingBounds bounds = clusterAabbs[cluster];
                float3 offset = math.abs(cameraPosition - (float3)bounds.Center);
                float3 extents = bounds.Extents;
                float dMin = math.length(math.max(offset - extents, float3.zero));
                float dMax = math.length(offset + extents);

                int maxLevel = p.MaxLevel;
                int minLevel = math.min((int)p.MinLevel, maxLevel);
                int levelAtMin = ResolveLevel(p, dMin, boundaryScale, globalMinLevel, minLevel, maxLevel);
                int levelAtMax = ResolveLevel(p, dMax, boundaryScale, globalMinLevel, minLevel, maxLevel);
                if (levelAtMin != levelAtMax)
                    continue;

                levelUniform++;

                float width = crossFadeEnabled != 0 ? p.CrossFadeWidth : 0f;
                if (width > 0f && levelAtMin < maxLevel)
                {
                    // The band the eval job dithers in is [b - w, b] below the
                    // next boundary. Uniform requires the whole interval to
                    // miss it.
                    int boundaryIndex = levelAtMin - minLevel;
                    float boundary = (boundaryIndex == 0
                        ? p.Boundaries.x
                        : boundaryIndex == 1 ? p.Boundaries.y : p.Boundaries.z)
                        * boundaryScale;
                    if (!(dMax < boundary - width || dMin > boundary))
                        continue;
                }

                // Draw Range, on the same conservative interval. Far band
                // (far - w, far), near band (near, near + w); an interval
                // wholly beyond a limit is still uniform (every instance
                // agrees, they are all culled), which is what "clear of every
                // fade band" means.
                // The FOV factor scales both draw-range limits, mirroring the
                // eval job. float.MaxValue must survive the multiply as "no
                // limit" (it is the sentinel the test below compares against),
                // so it is guarded rather than scaled.
                float far = clusterFarMin[cluster];
                if (far != float.MaxValue)
                    far *= cullDistanceScale;
                if (far != float.MaxValue &&
                    !(dMax < far - width || dMin > far))
                {
                    continue;
                }

                float near = clusterNearMax[cluster] * cullDistanceScale;
                if (near > 0f && !(dMax < near || dMin > near + width))
                    continue;

                uniform++;
                if (writeLevels)
                    uniformLevels[cluster] = (byte)(levelAtMin + 1);
            }

            counters[0] = eligible;
            counters[1] = uniform;
            counters[2] = levelUniform;
        }

        // The ladder of DrivingViewEvalJob's auto path, without the
        // hysteresis branch (excluded by the eligibility predicate) and
        // without the per-owner offset, which shifts a level and its band
        // together and so cannot break uniformity.
        private static int ResolveLevel(
            in OwnerLodParams p,
            float distance,
            float distanceScale,
            int globalMinLevel,
            int minLevel,
            int maxLevel)
        {
            int boundaryCount = maxLevel - minLevel;
            int level = minLevel;
            if (boundaryCount > 0 && distance > p.Boundaries.x * distanceScale)
                level = minLevel + 1;
            if (boundaryCount > 1 && distance > p.Boundaries.y * distanceScale)
                level = minLevel + 2;
            if (boundaryCount > 2 && distance > p.Boundaries.z * distanceScale)
                level = minLevel + 3;
            return math.clamp(math.max(level, globalMinLevel), minLevel, maxLevel);
        }

        /// <summary>
        /// Command flags for the fully-resolved-view fast path. Separate from the job path's
        /// `CommandFlags` because the fast path has no fade state to consider, but it
        /// must agree on everything else (for example `HasMotion`).
        /// Keep this method above the fast path's comment block so the
        /// `[BurstCompile]` attribute stays attached to `TryEmitFullyResolved`.
        /// </summary>
        private static BatchDrawCommandFlags FastPathCommandFlags(in CullingDrawGroup drawGroup)
        {
            BatchDrawCommandFlags flags = (drawGroup.Flags & CullingDrawGroup.FlipWindingFlag) != 0
                ? BatchDrawCommandFlags.FlipWinding
                : BatchDrawCommandFlags.None;
            if ((drawGroup.Flags & CullingDrawGroup.MotionVectorsFlag) != 0)
                flags |= BatchDrawCommandFlags.HasMotion;
            return flags;
        }

        // Fully-resolved-view fast path. Callable only when the request has no
        // per-instance feature active (no draw ranges, no PerInstance owners, no
        // running crossfade): then, if no owner straddles any split, every
        // instance's visibility mask equals its owner's inside mask, and the job
        // chain would produce exactly one bin per draw group: one command with
        // the owner's mask and the group's index slice verbatim. Emit that
        // directly (a memcpy per group) and schedule nothing. Returns -1 on
        // success; otherwise the index of the first straddling owner
        // (diagnostics) and the caller falls through to the normal chain.
        //
        // drawRanges must already be allocated by the caller (per segment,
        // same as the job path); commands and visibleInstances are allocated
        // here exact-size with the same TempJob lifetime BRG expects.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        public static unsafe int TryEmitFullyResolved(
            [NoAlias] CullingDrawGroup* drawGroups,
            [NoAlias] int* instanceIndices,
            [NoAlias] int* segmentGroupStarts,
            [NoAlias] int* segmentGroupCounts,
            [NoAlias] BatchFilterSettings* segmentFilterSettings,
            int segmentCount,
            [NoAlias] byte* ownerFlags,
            [NoAlias] byte* intersectMasks,
            [NoAlias] byte* insideMasks,
            [NoAlias] byte* ownerMeshLods,
            int ownerCount,
            byte isLightView,
            uint cullingLayerMask,
            [NoAlias] BatchCullingOutputDrawCommands* output)
        {
            for (int owner = 0; owner < ownerCount; owner++)
            {
                if (intersectMasks[owner] != insideMasks[owner])
                    return owner;
            }

            int commandTotal = 0;
            int visibleTotal = 0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                int groupEnd = segmentGroupStarts[segment] + segmentGroupCounts[segment];
                for (int g = segmentGroupStarts[segment]; g < groupEnd; g++)
                {
                    CullingDrawGroup drawGroup = drawGroups[g];
                    if (insideMasks[drawGroup.OwnerIndex] == 0 ||
                        !Emitted(drawGroup, ownerFlags[drawGroup.OwnerIndex],
                            isLightView, cullingLayerMask))
                        continue;
                    commandTotal++;
                    visibleTotal += drawGroup.InstanceCount;
                }
            }

            output->drawCommandCount = commandTotal;
            output->visibleInstanceCount = visibleTotal;
            if (commandTotal == 0)
            {
                output->drawRangeCount = 0;
                return -1;
            }

            output->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawCommand>() * (long)commandTotal,
                UnsafeUtility.AlignOf<BatchDrawCommand>(),
                Allocator.TempJob);
            output->visibleInstances = (int*)UnsafeUtility.Malloc(
                sizeof(int) * (long)visibleTotal, sizeof(int), Allocator.TempJob);

            int commandIndex = 0;
            int visibleOffset = 0;
            int rangeIndex = 0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                int rangeBegin = commandIndex;
                int groupEnd = segmentGroupStarts[segment] + segmentGroupCounts[segment];
                for (int g = segmentGroupStarts[segment]; g < groupEnd; g++)
                {
                    CullingDrawGroup drawGroup = drawGroups[g];
                    byte mask = insideMasks[drawGroup.OwnerIndex];
                    if (mask == 0 ||
                        !Emitted(drawGroup, ownerFlags[drawGroup.OwnerIndex],
                            isLightView, cullingLayerMask))
                        continue;

                    UnsafeUtility.MemCpy(
                        output->visibleInstances + visibleOffset,
                        instanceIndices + drawGroup.InstanceOffset,
                        sizeof(int) * (long)drawGroup.InstanceCount);

                    byte ownerLod = ownerMeshLods[drawGroup.OwnerIndex];
                    var command = new BatchDrawCommand
                    {
                        activeMeshLod = ownerLod < drawGroup.MaxMeshLod
                            ? ownerLod
                            : drawGroup.MaxMeshLod,
                        visibleOffset = (uint)visibleOffset,
                        visibleCount = (uint)drawGroup.InstanceCount,
                        batchID = drawGroup.BatchID,
                        materialID = drawGroup.MaterialID,
                        meshID = drawGroup.MeshID,
                        submeshIndex = (ushort)drawGroup.SubMesh,
                        splitVisibilityMask = mask,
                        // Must set the same flags as the job path's
                        // `CommandFlags` (including `HasMotion`).
                        flags = FastPathCommandFlags(drawGroup),
                        sortingPosition = 0,
                    };

                    output->drawCommands[commandIndex++] = command;

                    visibleOffset += drawGroup.InstanceCount;
                }

                if (commandIndex > rangeBegin)
                {
                    output->drawRanges[rangeIndex++] = new BatchDrawRange
                    {
                        drawCommandsBegin = (uint)rangeBegin,
                        drawCommandsCount = (uint)(commandIndex - rangeBegin),
                        drawCommandsType = BatchDrawCommandType.Direct,
                        filterSettings = segmentFilterSettings[segment],
                    };
                }
            }

            // Coalescing wrote fewer commands than the count pass reserved;
            // the emitted count is the truth and the allocation stays an
            // upper bound. Unchanged when nothing merged.
            output->drawCommandCount = commandIndex;
            output->drawRangeCount = rangeIndex;
            return -1;
        }

        // The emission test shared by both emission paths. Mirror of
        // StaticInstanceRendererSystem.IsGroupEmitted (outside this
        // Burst-compiled class); the logic must stay in lockstep.
        private static bool Emitted(
            in CullingDrawGroup drawGroup,
            byte ownerFlag,
            byte isLightView,
            uint cullingLayerMask)
        {
            if (ownerFlag == 0)
                return false;
            if (isLightView != 0)
            {
                if (drawGroup.ShadowMode == (byte)ShadowCastingMode.Off)
                    return false;
            }
            else if (drawGroup.ShadowMode == (byte)ShadowCastingMode.ShadowsOnly)
            {
                return false;
            }

            return (cullingLayerMask & (1u << drawGroup.Layer)) != 0;
        }
    }

    internal struct CullingDrawGroup
    {
        public const byte FlipWindingFlag = 1;

        // Builder-stamped shader motion eligibility; jobs do not read owner state.
        public const byte MotionVectorsFlag = 2;

        public BatchMeshID MeshID;
        public BatchMaterialID MaterialID;
        public BatchID BatchID;
        public int SubMesh;
        public int OwnerIndex;
        public int InstanceBase;
        public int InstanceOffset;
        public int InstanceCount;
        // Per-renderer filter attributes baked into the group (draw groups
        // split on these): ShadowCastingMode gates the light/camera emission,
        // Layer / RenderingLayerMask key the draw range, FlipWinding flows
        // into the command flags.
        public byte ShadowMode;
        public byte Layer;
        public byte Flags;
        public uint RenderingLayerMask;
        // Highest mesh LOD level this group's mesh actually has
        // (mesh.lodCount - 1, so 0 when the mesh carries no LOD chain).
        // The owner-wide level is clamped to this per group, because Mesh LODs
        // are per-mesh and small meshes get no chain at all.
        public byte MaxMeshLod;
    }

    // Packet range of one culling split inside the packed plane array (the
    // engine's CullingSplit planes are repacked so jobs never reference
    // engine-owned memory); offset/count are in PlanePacket4 units.
    internal struct SplitPlaneRange
    {
        public int PlaneOffset;
        public int PlaneCount;
    }

    // SoA packet of four culling planes. The absolute normals are precomputed
    // so the per-instance test is pure multiply-add; a split's trailing packet
    // repeats its last plane, which keeps the test correct without a
    // remainder branch.
    internal struct PlanePacket4
    {
        public float4 nx;
        public float4 ny;
        public float4 nz;
        public float4 d;
        public float4 nxAbs;
        public float4 nyAbs;
        public float4 nzAbs;

        public PlanePacket4(NativeArray<Plane> planes, int offset, int limit)
        {
            Plane p0 = planes[Mathf.Min(offset + 0, limit)];
            Plane p1 = planes[Mathf.Min(offset + 1, limit)];
            Plane p2 = planes[Mathf.Min(offset + 2, limit)];
            Plane p3 = planes[Mathf.Min(offset + 3, limit)];
            nx = new float4(p0.normal.x, p1.normal.x, p2.normal.x, p3.normal.x);
            ny = new float4(p0.normal.y, p1.normal.y, p2.normal.y, p3.normal.y);
            nz = new float4(p0.normal.z, p1.normal.z, p2.normal.z, p3.normal.z);
            d = new float4(p0.distance, p1.distance, p2.distance, p3.distance);
            nxAbs = math.abs(nx);
            nyAbs = math.abs(ny);
            nzAbs = math.abs(nz);
        }
    }

    internal enum LodSelectionPolicy : byte
    {
        PerOwner = 0,
        PerInstance = 1,
    }

    // Per-owner parameters for the PerInstance LOD policy, flattened into a
    // native array on rebuild so the driving-view evaluation job can read
    // them. Boundaries are camera → instance-AABB-surface distances in meters
    // (same metric and unit as the per-owner ladder), ascending; entries past
    // MaxLevel - 1 are unused.
    internal struct OwnerLodParams
    {
        // The cluster index, by arithmetic rather than a per-instance stream:
        // clusters are fixed-stride Morton runs per owner, so
        // cluster = ClusterBase + (instanceIndex - InstanceBase) / ClusterStride.
        // The cluster index is computed arithmetically rather than streamed per
        // instance, which avoids 4 B/instance of memory traffic.
        //
        // ClusterStride is the owner's actual stride, ceil(N / clusterCount),
        // not SisClusterSize; the two differ whenever N is not a multiple of the
        // cluster size, and a SisClusterSize divisor would read the wrong
        // cluster's value.
        public int ClusterBase;
        public int ClusterStride;
        public int InstanceBase;
        // `tan(reference / 2)` for this owner: the per-owner half of the FOV
        // factor, flattened here at rebuild. The factor is per owner x per
        // camera, but `ownerLodParams` is rebuild-time and shared read-only by
        // every camera slice, so it is decomposed as
        // `factor = tan(ref/2) * (1 / tan(fov/2))`: the left half is per owner
        // and constant per rebuild (stored here), the right half is one
        // per-camera scalar on the job and the classifier.
        //
        // Stored pre-tangented rather than in degrees: it is read per instance,
        // and `tan` per instance would pay for a value that cannot change
        // without a rebuild.
        public float RefTan;
        // The authored Draw Range in Screen Size mode, as absolute metres
        // (0 = unauthored). Per owner rather than per instance because it is
        // owner-uniform (a hard ceiling and floor), so it costs one compare each
        // and no per-instance memory.
        //
        // Zero in Distance mode, where the authored range is composed into the
        // per-instance thresholds. The two modes never both apply, so one pair
        // of fields serves without a second flag.
        public float AbsoluteFar;
        public float AbsoluteNear;
        // 1 when this owner authors in Screen Size mode: evaluation scales
        // the per-instance ratio ladder by each instance's screen radius
        // (ComposeEvaluationScales).
        public byte ScreenSize;
        public float3 Boundaries;
        public float CrossFadeWidth;
        // Extra distance to come back in by before returning to a finer
        // level, applied per instance against the previous frame's level —
        // only in the hard-switch mode (CrossFadeWidth == 0): the crossfade
        // band is a pure function of distance that cannot oscillate, so it
        // replaces the hysteresis outright.
        public float Hysteresis;
        public byte DistanceFade;
        // 1 when this owner's effective fade mode is Time. It cannot be derived
        // from `DistanceFade`, which answers a different question: `None` pushes
        // 0 there too, and `ResolveFadeMode` collapses every mode to `None`
        // while the project's Cross Fade switch is off. Gating the per-instance
        // time machine on `DistanceFade == 0` would run it when it is not needed.
        public byte TimeFade;
        // No owner-level "derived cull" flag exists: the authored Max and the
        // derived far limit compose as a min and the winner varies per instance,
        // so that fact rides the sign bit of each instance's stored far threshold.
        public byte MinLevel;
        public byte MaxLevel;
        public byte Policy;
        // Per-owner debug overrides, mirroring the group's Forced LOD / LOD
        // Offset semantics: Forced (-1 = auto) bypasses the distance bands
        // (and the crossfade with them), Offset shifts the selected level and
        // its band together, clamped to the ladder.
        public sbyte ForcedLevel;
        public sbyte LevelOffset;
    }

    internal sealed class StaticInstanceOwner
    {
        public NativeArray<CullingDrawGroup> DrawGroups;
        public NativeArray<CullingBounds> Bounds;
        public NativeArray<float> ShapeFactors;
        public NativeArray<int> InstanceIndices;
        public BatchID BatchId;

        // Initialization-time opt-in; batch metadata aliases previous matrices to
        // current matrices. Per-group eligibility is frozen by the builder.
        public bool MotionVectors;

        // The buffer this owner's batch addresses. The owner disposes it on
        // teardown; stripped Players retain it until Destroy.
        public GraphicsBuffer InstanceData;
        // Kept for diagnostics and editor picking labels.
        public GameObject Root;
        public bool Visible = true;
        // Maximum camera distance for this owner's instances (0 = unlimited).
        // Flattened into a per-instance squared threshold when the combined
        // arrays are rebuilt, so changing it marks the combined data dirty.
        public float CullDistance;
        // Near limit (0 = none).
        // Flattened next to CullDistance so the culling job tests one band.
        public float MinDrawDistance;
        // Whether this owner has already been told its Draw Range is inverted.
        // Per owner and re-armed when the pair is fixed (see ResolveNearLimit).
        public bool WarnedInvertedDrawRange;
        // Seconds a fade at either limit takes to complete, as authored:
        // negative means "follow StaticInstanceLodSystem.LodFadeDuration", which
        // the animate pass reads fresh every frame so the global knob is live.
        public float FadeDuration = -1f;
        // Master shadow mask on top of the per-draw-group shadow modes.
        public bool CastShadows = true;
        // False when every draw group is ShadowCastingMode.Off — the owner
        // then never contributes to light views.
        public bool HasShadowCasters = true;
        // Editor picking/selection round every instance to this GameObject
        // (the group root).
        public EntityId PickingEntityId;
        // Mesh LOD level decided for this owner by the driving view (one level
        // per owner, so draw command count is unaffected). Clamped per draw
        // group against CullingDrawGroup.MaxMeshLod when commands are emitted.
        // PerOwner policy only.
        public byte MeshLodLevel;
        // LOD selection policy plus the PerInstance parameters (ignored for
        // PerOwner owners). Flattened into ownerLodParams on rebuild, so
        // changing them after registration requires MarkCombinedDataDirty.
        public LodSelectionPolicy LodPolicy;
        public float3 LodBoundaries;
        public float LodCrossFadeWidth;
        public float LodHysteresis;
        // The authored mode and reference FOV, pushed here at registration; the
        // rebuild turns them into the per-owner `ScreenSize` flag and `RefTan`.
        // Both are per component, so the FOV factor is per owner x per camera,
        // and the per-owner half has to live somewhere the rebuild can flatten
        // into OwnerLodParams.
        //
        // `LodReferenceFovDegrees` is carried in degrees rather than
        // pre-tangented, so it reads like the authored value in a debugger; the
        // rebuild is where it becomes `tan(ref/2)`.
        public StaticInstanceLodMode LodMode;
        // The Screen Size far cutoff as a ratio, 0 = unauthored.
        // Flattened into the per-instance thresholds at rebuild, like the
        // authored Draw Range it replaces in that mode.
        public float LodCullRatio;
        public float LodReferenceFovDegrees =
            StaticInstanceLodSystem.DefaultReferenceFovDegrees;
        // True flips this owner's draw-range fades from time-driven to a
        // stateless distance band (SIS). Flattened into OwnerLodParams.
        public bool LodDistanceFade;
        // The owner's effective mode IS Time.
        // Distinct from `!LodDistanceFade`, which is also true for `None` and
        // for every owner while the project's Cross Fade switch is off.
        public bool LodTimeFade;
        public byte LodMinLevel;
        public byte LodMaxLevel;
        public sbyte LodForcedLevel = -1;
        public sbyte LodLevelOffset;
        // Casting stops at the boundary that ends this level.
        // -1 = every level casts (the default).
        //
        // It lives here rather than in OwnerLodParams because its first
        // consumer is IsOwnerRendered, whose signature takes an owner and no
        // owner index. The emit jobs get it through a per-owner scratch region
        // instead, the same shape OwnerMeshLods and OwnerPolicies use.
        public sbyte ShadowCastMaxLevel = -1;
        public byte MeshLodCrossfadeFrom;
        public byte MeshLodCrossfadeK;

        // The same three values per driving camera, indexed by the
        // slice a view resolves to — so one index reads owner state and
        // instance state alike. Index 0 is the primary camera and mirrors the
        // scalars above, which remain the authority at K=1 and the source the
        // inspector and tooling read.
        public readonly byte[] MeshLodLevels =
            new byte[StaticInstanceLodSystem.MaxLodSlices];
        public readonly byte[] MeshLodCrossfadeFroms =
            new byte[StaticInstanceLodSystem.MaxLodSlices];
        public readonly byte[] MeshLodCrossfadeKs =
            new byte[StaticInstanceLodSystem.MaxLodSlices];

    }

    internal static class StaticInstanceRendererSystem
    {
        private struct MeshEntry
        {
            public BatchMeshID Id;
            public int RefCount;
        }

        private struct MaterialEntry
        {
            public BatchMaterialID Id;
            public int RefCount;
        }

        // BRG supports at most 6 culling splits; a byte visibility mask covers 8.
        private const int MaxSplitCount = 8;
        // Mesh LOD levels a PerInstance owner can spread across (levels 0..3,
        // matching MaxAuthorableMeshLodLevel). Two bits inside the bin key.
        private const int MaxLodLevels = 4;
        // Commands are binned per distinct split visibility mask.
        // The bin key is (level, fadeFlag, splitMask): PerOwner groups only use
        // the low (fadeFlag, splitMask) region — the level rides on the command
        // as activeMeshLod without splitting bins — while PerInstance groups
        // spread instances across the level dimension and pay the binning tax.
        private const int MaxConfigCount = MaxLodLevels * 2 * (1 << MaxSplitCount);

        internal const byte FadeOpaque = 127;

        private static readonly Unity.Profiling.ProfilerMarker CullingCallbackMarker =
            new Unity.Profiling.ProfilerMarker("SI.OnPerformCulling");
        private static readonly Unity.Profiling.ProfilerMarker CullingScheduleMarker =
            new Unity.Profiling.ProfilerMarker("SI.Schedule");
        private static readonly Unity.Profiling.ProfilerMarker CountAndEmitMarker =
            new Unity.Profiling.ProfilerMarker("SI.CountAndEmit");

        private static readonly List<StaticInstanceOwner> Owners =
            new List<StaticInstanceOwner>();
        private static readonly Dictionary<Mesh, MeshEntry> MeshEntries =
            new Dictionary<Mesh, MeshEntry>();
        private static readonly Dictionary<Material, MaterialEntry> MaterialEntries =
            new Dictionary<Material, MaterialEntry>();

        private static BatchRendererGroup rendererGroup;
        private static NativeArray<CullingDrawGroup> combinedDrawGroups;
        private static NativeArray<CullingBounds> combinedBounds;
        private static NativeArray<int> combinedInstanceIndices;
        // Draw ranges are emitted per segment: a contiguous run of draw groups
        // within one owner that shares (layer, shadow mode). The groups are
        // sorted on those attributes at build time, so with uniform content
        // this degenerates to one segment per owner.
        private static NativeArray<int> segmentGroupStarts;
        private static NativeArray<int> segmentGroupCounts;
        private static NativeArray<int> segmentOwners;
        // Filter settings depend only on the (static) segment data, so they
        // are built once per rebuild instead of once per culling request.
        private static NativeArray<BatchFilterSettings> segmentFilterSettings;
        // Union of every segment's layer bit, built with the segment data
        // above. IsGroupEmitted's last condition is a layer test with no view
        // type exception, so a request whose culling mask shares no bit with
        // this union cannot emit a single group — the callback returns before
        // scheduling anything (a UI or otherwise SI-free camera).
        private static uint combinedLayerMask;
        // Per-instance squared cull-distance thresholds (float.MaxValue when
        // the owner has no cull distance); anyCullDistance gates the whole
        // distance test so owners without the feature pay nothing.
        private static NativeArray<float> combinedCullThresholdsSq;
        // See the allocation for what it holds and why it is not
        // recomputed in the job from `Bounds`.
        private static NativeArray<float> combinedScreenRadii;
        // Near limit of the same band (0 when the owner has no near limit).
        private static NativeArray<float> combinedMinThresholdsSq;
        // Per-instance dither fade, 0..127, persistent across frames so the fade
        // can be driven by time rather than by distance: once a transition
        // starts it runs to completion even if the camera stops. Kept in float
        // rather than the packed byte: a byte truncates every step, which floors
        // the fade at one unit per frame and collapses any duration longer than
        // ~127 frames back to ~127 frames.
        //
        // One array per slice rather than sub-arrays of one big one: a job
        // writing a sub-array leaves the parent unreadable from the main thread
        // even after its handles are completed. Independent arrays also make
        // "these evaluations are independent" true for the job safety system.
        private static readonly NativeArray<float>[] instanceFadeSlices =
            new NativeArray<float>[MaxSlices];
        // Fade units per second for each instance, flattened from its owner.
        // Negative = follow the global duration, resolved per frame in the job.
        private static NativeArray<float> combinedFadeRates;
        private static NativeArray<CullingBounds> clusterAabbs;
        private static NativeArray<int> clusterOwners;
        // Per-cluster Draw Range aggregates: the smallest far threshold and
        // the largest near threshold among the cluster's instances. The
        // thresholds themselves are per instance (they fold each instance's
        // own bounding-sphere radius into the owner's authored limits), so a
        // cluster-level draw-range test needs the conservative extremes
        // rather than an owner scalar. Identity values (far = +inf, near = 0)
        // mean "no limit", which is what an owner without a draw range
        // leaves behind. Consumed by the uniformity classifier.
        private static NativeArray<float> clusterFarThresholdMin;
        private static NativeArray<float> clusterNearThresholdMax;
        // Instances per cluster. Lets the fully-outside counters report
        // instances, not just clusters, without walking the sub-run table:
        // every instance belongs to exactly one group, so summing a cluster's
        // instance count is the same total the count job actually skips.
        private static NativeArray<int> clusterInstanceCounts;
        // Per cluster, level + 1 when the whole cluster resolves to one
        // LOD level with no instance in any fade band, 0 when it does not.
        // Written by the once-per-frame pre-pass, read by the eval job, which
        // then stores the value per instance instead of recomputing it.
        private static readonly NativeArray<byte>[] clusterUniformSlices =
            new NativeArray<byte>[MaxSlices];
        private static NativeArray<int> combinedInstanceClusters;
        private static bool instanceClustersAliased;

        // One per driving camera plus the shared shadow slice.
        private const int MaxSlices = StaticInstanceLodSystem.MaxDrivingCameras + 1;

        // How many copies of the per-instance evaluation state exist.
        //
        // One per driving camera, because every one of those values is a
        // function of a camera position and the fades are stateful on top of
        // that. Plus one shared shadow slice only when K > 1: a shadow map is
        // rendered once for every camera, so with two cameras it needs a
        // conservative merge (finest level, most-visible fade), while at K = 1
        // it is the single camera's slice by definition and a copy would be
        // pure overhead.
        //
        // So sliceCount == 1 at K = 1: the arrays, the loop counts and the
        // scheduling are those of the single-camera case.
        private static int drivingSliceCount = 1;
        private static int drivingCameraCountSnapshot = 1;
        private static int drivingGenerationSnapshot = -1;
        private static int shadowSliceIndex = -1;
        // Rebuild-time snapshot of the chunking mask, so flipping the mask
        // marks the combined data dirty exactly once.
        private static bool lastChunkingEnabled = true;
        // The size-cull-distance knob is precomputed into the thresholds at
        // rebuild, so a change has to force one, including a change made by game
        // code writing the static directly. Same last-applied comparison the
        // chunking mask uses.
        private static float lastSizeCullDistanceScale;
        // Fallback for the derived-cull fade fraction, used only when the
        // settings container is absent (non-URP, or stripped by hand). The
        // authored value lives in StaticInstanceGlobalSettings. The band scales
        // with the derived distance, so lowering the scale knob does not narrow
        // it in proportional terms.
        private const float DefaultDerivedCullFadeFraction = 0.15f;
        // The fade-curve lookup table, 128 entries over the quantized dither range,
        // rebuilt only when the curve changes and carried to the write job as
        // a persistent pointer rather than a per-view NativeArray. Stays
        // uncreated while the curve is Linear, and the job's null check then
        // skips shaping entirely — the default costs literally nothing.
        private static NativeArray<byte> fadeCurveLut;
        private static StaticInstanceLodSystem.FadeCurveMode lastFadeCurve =
            StaticInstanceLodSystem.FadeCurveMode.Linear;

        // Ease-in-out sine, chosen because it is symmetric: shaped(1 - t) =
        // 1 - shaped(t), so the complementary pair stays complementary across
        // the whole band and the two halves of a dither pair keep covering
        // the surface exactly once.
        private static unsafe byte* ResolveFadeCurveLut()
        {
            StaticInstanceLodSystem.FadeCurveMode curve = StaticInstanceLodSystem.FadeCurve;
            if (curve == StaticInstanceLodSystem.FadeCurveMode.Linear)
                return null;

            if (!fadeCurveLut.IsCreated || lastFadeCurve != curve)
            {
                if (!fadeCurveLut.IsCreated)
                {
                    fadeCurveLut = new NativeArray<byte>(
                        FadeOpaque + 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }

                for (int i = 0; i <= FadeOpaque; i++)
                {
                    float t = i / (float)FadeOpaque;
                    float shaped = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t);
                    fadeCurveLut[i] = (byte)Mathf.Clamp(
                        Mathf.RoundToInt(shaped * FadeOpaque), 0, FadeOpaque);
                }

                lastFadeCurve = curve;
            }

            return (byte*)fadeCurveLut.GetUnsafeReadOnlyPtr();
        }
        // Instances per PerInstance cluster. Small enough that a cluster's AABB
        // stays a meaningful fraction of the scene, large enough that the
        // per-cluster overhead never shows.
        private const int SisClusterSize = 256;
        private static NativeArray<int> combinedInstanceOwners;
        private static NativeArray<OwnerLodParams> ownerLodParams;
        private static readonly NativeArray<byte>[] instanceLevelSlices =
            new NativeArray<byte>[MaxSlices];
        private static readonly NativeArray<byte>[] crossfadeSlices =
            new NativeArray<byte>[MaxSlices];
        private static readonly NativeArray<byte>[] meshFadeFromSlices =
            new NativeArray<byte>[MaxSlices];
        private static readonly NativeArray<float>[] meshFadeProgressSlices =
            new NativeArray<float>[MaxSlices];
        private static bool anyPerInstanceTimeFade;
        private static bool anyPerInstanceLod;
        private static bool anyDistanceCull;
        // Some registered owner authors ShadowCastMaxLevel >= 0.
        // Mirrors anyDistanceCull so an owner without the option pays no
        // branch; every test it gates sits under IsLightView, so a camera
        // view pays nothing in either state.
        private static bool anyShadowCastCull;
        private static int lastFadeAnimationFrame = -1;
        private static JobHandle fadeAnimationHandle;
        private static JobHandle lastCullingHandle;
        private static bool combinedDataDirty;
        private static bool lightViewsEnabled;
#if UNITY_EDITOR
        private static Material pickingMaterial;
        // Selection.entityIds allocates a fresh array on every read, so the
        // SelectionOutline view reads this cache kept by selectionChanged.
        private static EntityId[] cachedSelection;
#endif

        // The evaluation pipeline is the parallel 4-job chain with SIMD plane
        // tests and a combined count+allocate stage.

        // Editor-only Picking / SelectionOutline views are appended so BRG
        // content is clickable and outlined in the Scene view; they never
        // exist in a player build.
#if UNITY_EDITOR
        private static readonly BatchCullingViewType[] ViewTypesCameraAndLight =
        {
            BatchCullingViewType.Camera,
            BatchCullingViewType.Light,
            BatchCullingViewType.Picking,
            BatchCullingViewType.SelectionOutline,
        };

        private static readonly BatchCullingViewType[] ViewTypesCameraOnly =
        {
            BatchCullingViewType.Camera,
            BatchCullingViewType.Picking,
            BatchCullingViewType.SelectionOutline,
        };
#else
        private static readonly BatchCullingViewType[] ViewTypesCameraAndLight =
        {
            BatchCullingViewType.Camera,
            BatchCullingViewType.Light,
        };

        private static readonly BatchCullingViewType[] ViewTypesCameraOnly =
        {
            BatchCullingViewType.Camera,
        };
#endif

#if UNITY_EDITOR
        // Guard against stale statics when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            if (rendererGroup != null)
                DisposeAll();
            Owners.Clear();
            MeshEntries.Clear();
            MaterialEntries.Clear();
            combinedDataDirty = false;
        }
#endif

        internal static BatchMeshID RegisterMesh(Mesh mesh)
        {
            EnsureCreated();
            if (MeshEntries.TryGetValue(mesh, out MeshEntry entry))
            {
                entry.RefCount++;
                MeshEntries[mesh] = entry;
                return entry.Id;
            }

            BatchMeshID id = rendererGroup.RegisterMesh(mesh);
            MeshEntries.Add(mesh, new MeshEntry { Id = id, RefCount = 1 });
            return id;
        }

        internal static BatchMaterialID RegisterMaterial(Material material)
        {
            EnsureCreated();
            if (MaterialEntries.TryGetValue(material, out MaterialEntry entry))
            {
                entry.RefCount++;
                MaterialEntries[material] = entry;
                return entry.Id;
            }

            BatchMaterialID id = rendererGroup.RegisterMaterial(material);
            MaterialEntries.Add(material, new MaterialEntry { Id = id, RefCount = 1 });
            return id;
        }

        internal static void UnregisterMesh(Mesh mesh)
        {
            if (rendererGroup == null || mesh == null ||
                !MeshEntries.TryGetValue(mesh, out MeshEntry entry))
            {
                return;
            }

            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                MeshEntries[mesh] = entry;
                return;
            }

            MeshEntries.Remove(mesh);
            rendererGroup.UnregisterMesh(entry.Id);
        }

        internal static void UnregisterMaterial(Material material)
        {
            if (rendererGroup == null || material == null ||
                !MaterialEntries.TryGetValue(material, out MaterialEntry entry))
            {
                return;
            }

            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                MaterialEntries[material] = entry;
                return;
            }

            MaterialEntries.Remove(material);
            rendererGroup.UnregisterMaterial(entry.Id);
        }

        internal static BatchID AddBatch(NativeArray<MetadataValue> metadata, GraphicsBufferHandle buffer)
        {
            EnsureCreated();
            return rendererGroup.AddBatch(metadata, buffer);
        }

        internal static void RegisterOwner(StaticInstanceOwner owner)
        {
            Owners.Add(owner);
            combinedDataDirty = true;
            UpdateEnabledViewTypes();
        }

        private static void RemoveBatch(BatchID batchId)
        {
            if (rendererGroup == null)
                return;
            rendererGroup.RemoveBatch(batchId);
        }

        // BatchDrawRange carries these filter attributes. Motion-vector state
        // is included because Unity filters the range before command flags.
        private static bool SameFilter(in CullingDrawGroup a, in CullingDrawGroup b)
        {
            return a.Layer == b.Layer &&
                   a.ShadowMode == b.ShadowMode &&
                   a.RenderingLayerMask == b.RenderingLayerMask &&
                   (a.Flags & CullingDrawGroup.MotionVectorsFlag) ==
                   (b.Flags & CullingDrawGroup.MotionVectorsFlag);
        }

        // Called when an owner field that feeds the combined arrays changes
        // after registration (currently the cull distance); the rebuild runs
        // lazily on the next culling request.
        internal static void MarkCombinedDataDirty()
        {
            combinedDataDirty = true;
        }

        internal static void UnregisterOwner(StaticInstanceOwner owner)
        {
            if (!Owners.Remove(owner))
                return;

            lastCullingHandle.Complete();
            RemoveBatch(owner.BatchId);
            combinedDataDirty = true;

            if (Owners.Count == 0)
                DisposeAll();
            else
                UpdateEnabledViewTypes();
        }

        // Called when an owner's CastShadows changes; Light views are only
        // invoked while at least one owner casts shadows.
        internal static void UpdateEnabledViewTypes()
        {
            if (rendererGroup == null)
                return;

            bool anyShadows = false;
            for (int i = 0; i < Owners.Count; i++)
                anyShadows |= Owners[i].CastShadows && Owners[i].HasShadowCasters;

            if (anyShadows != lightViewsEnabled)
            {
                lightViewsEnabled = anyShadows;
                rendererGroup.SetEnabledViewTypes(
                    anyShadows ? ViewTypesCameraAndLight : ViewTypesCameraOnly);
            }
        }

        private static void EnsureCreated()
        {
            if (rendererGroup != null)
                return;

            rendererGroup = new BatchRendererGroup(new BatchRendererGroupCreateInfo
            {
                cullingCallback = OnPerformCulling,
                userContext = System.IntPtr.Zero,
            });
            rendererGroup.SetEnabledViewTypes(ViewTypesCameraAndLight);
            lightViewsEnabled = true;

#if UNITY_EDITOR
            // Scene view picking renders BRG content with this replacement
            // material (same wiring as GPU Resident Drawer).
            Shader pickingShader = Shader.Find("Hidden/Universal Render Pipeline/BRGPicking");
            if (pickingShader != null)
            {
                pickingMaterial = new Material(pickingShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                rendererGroup.SetPickingMaterial(pickingMaterial);
            }

            UnityEditor.Selection.selectionChanged += OnSelectionChanged;
            cachedSelection = UnityEditor.Selection.entityIds;
#endif
        }

#if UNITY_EDITOR
        private static void OnSelectionChanged()
        {
            cachedSelection = UnityEditor.Selection.entityIds;
        }
#endif

        private static void DisposeAll()
        {
            lastCullingHandle.Complete();
            DisposeCombinedData();
            if (fadeCurveLut.IsCreated)
                fadeCurveLut.Dispose();
            if (rendererGroup != null)
            {
                rendererGroup.Dispose();
                rendererGroup = null;
            }

#if UNITY_EDITOR
            if (pickingMaterial != null)
            {
                Object.DestroyImmediate(pickingMaterial);
                pickingMaterial = null;
            }

            UnityEditor.Selection.selectionChanged -= OnSelectionChanged;
            cachedSelection = null;
#endif

            MeshEntries.Clear();
            MaterialEntries.Clear();
            combinedDataDirty = false;
        }

        private static void DisposeCombinedData()
        {
            if (combinedDrawGroups.IsCreated)
                combinedDrawGroups.Dispose();
            if (combinedBounds.IsCreated)
                combinedBounds.Dispose();
            if (combinedInstanceIndices.IsCreated)
                combinedInstanceIndices.Dispose();
            if (segmentGroupStarts.IsCreated)
                segmentGroupStarts.Dispose();
            if (segmentGroupCounts.IsCreated)
                segmentGroupCounts.Dispose();
            if (segmentOwners.IsCreated)
                segmentOwners.Dispose();
            if (segmentFilterSettings.IsCreated)
                segmentFilterSettings.Dispose();
            if (combinedCullThresholdsSq.IsCreated)
                combinedCullThresholdsSq.Dispose();
            if (combinedScreenRadii.IsCreated)
                combinedScreenRadii.Dispose();
            if (combinedMinThresholdsSq.IsCreated)
                combinedMinThresholdsSq.Dispose();
            for (int i = 0; i < MaxSlices; i++)
            {
                if (instanceFadeSlices[i].IsCreated)
                    instanceFadeSlices[i].Dispose();
                instanceFadeSlices[i] = default;
            }

            if (combinedFadeRates.IsCreated)
                combinedFadeRates.Dispose();
            if (clusterAabbs.IsCreated)
                clusterAabbs.Dispose();
            if (clusterOwners.IsCreated)
                clusterOwners.Dispose();
            if (clusterFarThresholdMin.IsCreated)
                clusterFarThresholdMin.Dispose();
            if (clusterNearThresholdMax.IsCreated)
                clusterNearThresholdMax.Dispose();
            for (int i = 0; i < MaxSlices; i++)
            {
                if (clusterUniformSlices[i].IsCreated)
                    clusterUniformSlices[i].Dispose();
                clusterUniformSlices[i] = default;
            }

            if (clusterInstanceCounts.IsCreated)
                clusterInstanceCounts.Dispose();
            if (combinedInstanceClusters.IsCreated && !instanceClustersAliased)
                combinedInstanceClusters.Dispose();
            combinedInstanceClusters = default;
            if (combinedInstanceOwners.IsCreated)
                combinedInstanceOwners.Dispose();
            if (ownerLodParams.IsCreated)
                ownerLodParams.Dispose();
            for (int i = 0; i < MaxSlices; i++)
            {
                if (instanceLevelSlices[i].IsCreated)
                    instanceLevelSlices[i].Dispose();
                instanceLevelSlices[i] = default;
                if (crossfadeSlices[i].IsCreated)
                    crossfadeSlices[i].Dispose();
                crossfadeSlices[i] = default;
                // The per-slice time-fade state, freed with the slices it
                // shadows.
                if (meshFadeFromSlices[i].IsCreated)
                    meshFadeFromSlices[i].Dispose();
                meshFadeFromSlices[i] = default;
                if (meshFadeProgressSlices[i].IsCreated)
                    meshFadeProgressSlices[i].Dispose();
                meshFadeProgressSlices[i] = default;
            }
        }

        // The runtime guard behind the Inspector's push semantics. The Inspector
        // cannot author Min at or past a non-zero Max, but a YAML edit, a
        // script, or a Preset applied to the component can reach the fields
        // without passing through it.
        //
        // The thresholds fold the instance's bounding radius in
        // (`far = Max + r`, `near = Min - r`), so an inverted pair does not draw
        // nothing everywhere: it erases exactly the instances whose radius is
        // smaller than (Min - Max) / 2 and keeps the larger ones.
        //
        // The response is fail-visible: ignoring Min draws more than the author
        // asked for, never less, so the mistake shows up as a warning naming both
        // values rather than as content that quietly is not there. The warning is
        // logged once per owner and re-armed when the pair becomes valid again,
        // to avoid rebuild-rate log spam.
        internal static float ResolveNearLimit(StaticInstanceOwner owner)
        {
            float min = owner.MinDrawDistance;
            float max = owner.CullDistance;
            if (!IsDrawRangeInverted(min, max))
            {
                owner.WarnedInvertedDrawRange = false;
                return min;
            }

            if (!owner.WarnedInvertedDrawRange)
            {
                owner.WarnedInvertedDrawRange = true;
                Debug.LogWarning(
                    $"Static Instances: Draw Range Min ({min:0.###}) is at or past Max " +
                    $"({max:0.###}) on '{OwnerNameOf(owner)}', which would erase instances " +
                    "by size rather than by distance (the limits fold in each instance's " +
                    "own radius). Min is IGNORED for this owner — it draws from the camera " +
                    "out to Max — until the pair is authored the other way round.",
                    owner.Root);
            }

            return 0f;
        }

        // Both halves of the rule in one predicate, so the Inspector-side push
        // and the runtime guard cannot drift apart about what "inverted" means:
        // a Max of 0 is "unlimited" and constrains nothing, and equality is
        // degenerate rather than legal (a band of zero authored width).
        internal static bool IsDrawRangeInverted(float min, float max) =>
            max > 0f && min >= max;

        // -- Hysteresis on the draw-range limits -----------------------------------

        // The distance-driven (SIS) rule. A side with a band fades across it; a
        // side without one is a cut, guarded by the component's Hysteresis (the
        // same value that guards its LOD boundaries, because the margin tracks
        // camera jitter amplitude and jitter does not care which kind of
        // boundary is crossed). Without it, a camera resting on the threshold
        // would pop the instance in and out once per frame.
        //
        // The state it needs already exists: InstanceFades holds last frame's
        // value for this instance in this view, so "was it visible" costs a load
        // and no new array. Out re-enters only inside far - h (or outside near +
        // h); in leaves at the authored limit. History-dependence is therefore
        // confined to the h-wide margin: outside it a camera cut still resolves
        // in one frame.
        //
        // Content that authors a Distance fade is untouched: both widths are
        // non-zero there and neither term consults the previous fade.
        internal static float ResolveDistanceDrawRangeFade(
            float distance, float far, float near,
            float farWidth, float nearWidth,
            float hysteresis, float previousFade)
        {
            float margin = EffectiveHysteresis(far, near, hysteresis);
            // Binary in the case that matters (both widths zero, so the fade
            // is 0 or opaque); "> 0" rather than "settled" because a partial
            // fade here comes from the OTHER side's band, and an instance
            // dithering against its near band has not left through its far
            // limit.
            bool wasVisible = previousFade > 0f;
            float farTerm = farWidth > 0f
                ? (far - distance) / farWidth
                : (distance <= (wasVisible ? far : far - margin) ? 1f : 0f);
            float nearTerm = near > 0f
                ? (nearWidth > 0f
                    ? (distance - near) / nearWidth
                    : (distance >= (wasVisible ? near : near + margin) ? 1f : 0f))
                : 1f;
            return FadeOpaque * math.saturate(math.min(farTerm, nearTerm));
        }

        // The margin may never be wider than half the band it guards.
        //
        // Unclamped, a hysteresis larger than the draw range would turn boundary
        // flicker into permanent disappearance: with a margin of 5 m and an
        // authored `Max` of 5 m or less, the far re-entry threshold `far - h`
        // lands at or below 0, so an instance that crossed out once could never
        // come back. The near side would hide the mirrored `[near, near + h)`
        // shell.
        //
        // Half the band is exactly the largest margin for which the two
        // re-entry thresholds cannot cross (`near + h_eff <= far - h_eff`), so
        // the guarded region always keeps a non-empty interior and every
        // instance that can be drawn at all has a distance at which it
        // re-enters. Content whose band is comfortably wider than the margin is
        // unaffected, because the clamp is inert there.
        internal static float EffectiveHysteresis(float far, float near, float hysteresis)
        {
            float band = math.max(0f, far - near);
            return math.min(math.max(0f, hysteresis), band * 0.5f);
        }

        internal static float ResolveScreenSizeFarBound(
            float far, float absoluteFar, ref bool derivedLimit)
        {
            if (absoluteFar > 0f && absoluteFar < far)
            {
                derivedLimit = false;
                return absoluteFar;
            }

            return far;
        }

        /// <summary>The near side of the same bound — a hard floor, so max wins.</summary>
        internal static float ResolveScreenSizeNearBound(float near, float absoluteNear) =>
            absoluteNear > 0f ? math.max(near, absoluteNear) : near;

        internal static void ResolveScreenSizeTimeBounds(
            ref float farSq, ref float nearSq, float absoluteFar, float absoluteNear)
        {
            if (absoluteFar > 0f)
                farSq = math.min(farSq, absoluteFar * absoluteFar);

            if (absoluteNear > 0f)
                nearSq = math.max(nearSq, absoluteNear * absoluteNear);
        }

        internal static bool ResolveTimeDrawRangeInBand(
            float distanceSq, float farSq, float nearSq,
            float hysteresis, float previousFade)
        {
            bool inBand = distanceSq <= farSq && distanceSq >= nearSq;
            if (!inBand || hysteresis <= 0f || previousFade >= FadeOpaque)
                return inBand;

            float distance = math.sqrt(distanceSq);
            float farLimit = math.sqrt(farSq);
            float nearLimit = math.sqrt(nearSq);
            // Same clamp as the distance path (see EffectiveHysteresis): without
            // it an owner whose Max is under the margin has a re-entry threshold
            // at 0 and its instances leave for good the first time the camera
            // steps outside.
            float margin = EffectiveHysteresis(farLimit, nearLimit, hysteresis);
            return distance <= farLimit - margin &&
                   (nearLimit <= 0f || distance >= nearLimit + margin);
        }

        private static string OwnerNameOf(StaticInstanceOwner owner) =>
            owner.Root != null ? owner.Root.name : "<destroyed owner>";

        private static int ClusterCountOf(StaticInstanceOwner owner, bool chunking)
        {
            if (!chunking || owner.LodPolicy != LodSelectionPolicy.PerInstance)
                return 1;
            return Mathf.Max(1, (owner.Bounds.Length + SisClusterSize - 1) / SisClusterSize);
        }

        // The per-instance derived cull limit, lifted out of RebuildCombinedData
        // so tests can assert it directly.
        //
        // With the shape factor at its identity value the Distance leg must be
        // bit-for-bit `radius * 2 * sizeScale + radius`, so content that never
        // opts in gets the unscaled threshold. `x * 1f` is exact in IEEE 754, so
        // the identity holds by construction.
        //
        // `effectiveSizeScale` and `cullRatio` arrive already resolved per owner
        // (the caller needs both for its own guard, and neither varies per
        // instance): in Distance mode the scale is the global knob and the ratio
        // is 0; in Screen Size mode the scale is 0 and the ratio is the authored
        // Culled boundary.
        internal static float ResolveDerivedCullLimit(
            float radius, float shape, float effectiveSizeScale,
            float cullRatio, bool screenSizeMode)
        {
            if (screenSizeMode)
            {
                // No `+ radius` offset on this leg: the ratio already
                // describes the sphere's projection, so adding the geometric
                // offset would double-count it.
                return cullRatio > 0f ? radius * shape / cullRatio : float.MaxValue;
            }

            // The shape factor multiplies ONLY the `2 * radius * sizeScale`
            // term. The trailing `+ radius` is the geometric offset that puts
            // the limit on the instance's surface, not a size-perception term,
            // so scaling it would move the limit inside the geometry for thin
            // content.
            return effectiveSizeScale > 0f
                ? radius * 2f * effectiveSizeScale * shape + radius
                : float.MaxValue;
        }

        // Concatenates every registered owner's culling data into the combined
        // arrays. Runs lazily on the next culling request after registration
        // changes, so a burst of enables/disables costs one rebuild.
        private static void RebuildCombinedData()
        {
            lastCullingHandle.Complete();
            DisposeCombinedData();
            int instanceTotal = 0;
            bool anyScreenSize = false;
            int drawGroupTotal = 0;
            int indexTotal = 0;
            int clusterTotal = 0;
            bool perInstanceLod = false;
            // Does any PerInstance owner fade on Time? Learned in the same
            // counting pass that sizes the combined arrays, so a project with no
            // Time-mode Scatter allocates none of the state below.
            bool perInstanceTimeFade = false;
            bool chunking = StaticInstanceLodSystem.CullingChunkingEnabled;
            lastChunkingEnabled = chunking;
            lastSizeCullDistanceScale =
                Mathf.Max(0f, StaticInstanceLodSystem.SizeCullDistanceScale);
            for (int i = 0; i < Owners.Count; i++)
            {
                StaticInstanceOwner ownerData = Owners[i];
                instanceTotal += ownerData.Bounds.Length;
                drawGroupTotal += ownerData.DrawGroups.Length;
                indexTotal += ownerData.InstanceIndices.Length;
                perInstanceLod |= ownerData.LodPolicy == LodSelectionPolicy.PerInstance;
                perInstanceTimeFade |=
                    ownerData.LodPolicy == LodSelectionPolicy.PerInstance &&
                    ownerData.LodTimeFade;
                // Learned in the same counting pass, so the per-instance screen
                // radius below is allocated only for projects that use the mode
                // (the same shape `anyDistanceCull` and `anyShadowCastCull` use).
                anyScreenSize |= ownerData.LodMode == StaticInstanceLodMode.ScreenSize;
                clusterTotal += ClusterCountOf(ownerData, chunking);
            }

            combinedBounds = new NativeArray<CullingBounds>(
                instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedDrawGroups = new NativeArray<CullingDrawGroup>(
                drawGroupTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedInstanceIndices = new NativeArray<int>(
                indexTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedCullThresholdsSq = new NativeArray<float>(
                instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // The per-instance screen radius: the corrected radius each Screen
            // Size instance's ratio boundaries are computed from. Length 1 (a
            // dummy the job never indexes) when no owner authors the mode,
            // because a Burst job field must be bound even when unused;
            // `instanceTotal` only for projects that use it.
            //
            // A ratio boundary is a per-instance distance:
            // `d_j = (r_i / ratio_j) x (1 / tan(fov/2))`, where only
            // `1 / ratio_j` is per owner. `Bounds` is already streamed by the
            // job, but its `Extents` is the world AABB, which would discard the
            // shape correction, so the corrected radius is composed here at
            // rebuild.
            combinedScreenRadii = new NativeArray<float>(
                anyScreenSize ? instanceTotal : 1,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            combinedMinThresholdsSq = new NativeArray<float>(
                instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedFadeRates = new NativeArray<float>(
                instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            clusterAabbs = new NativeArray<CullingBounds>(
                clusterTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            clusterOwners = new NativeArray<int>(
                clusterTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            clusterFarThresholdMin = new NativeArray<float>(
                clusterTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            clusterNearThresholdMax = new NativeArray<float>(
                clusterTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < drivingSliceCount; i++)
            {
                clusterUniformSlices[i] = new NativeArray<byte>(
                    clusterTotal, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            clusterInstanceCounts = new NativeArray<int>(
                clusterTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedInstanceOwners = new NativeArray<int>(
                instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            instanceClustersAliased = clusterTotal == Owners.Count;
            combinedInstanceClusters = instanceClustersAliased
                ? combinedInstanceOwners
                : new NativeArray<int>(
                    instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // Starts fully visible: a rebuild must not make everything dither in
            // from nothing (owners are registered mid-scene all the time).
            for (int slice = 0; slice < drivingSliceCount; slice++)
            {
                var fades = new NativeArray<float>(
                    instanceTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < instanceTotal; i++)
                    fades[i] = FadeOpaque;
                instanceFadeSlices[slice] = fades;
            }

            anyPerInstanceLod = perInstanceLod;
            anyPerInstanceTimeFade = perInstanceTimeFade;
            ownerLodParams = new NativeArray<OwnerLodParams>(
                Owners.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int slice = 0; slice < drivingSliceCount; slice++)
            {
                instanceLevelSlices[slice] = new NativeArray<byte>(
                    perInstanceLod ? instanceTotal : 0,
                    Allocator.Persistent, NativeArrayOptions.ClearMemory);
                crossfadeSlices[slice] = new NativeArray<byte>(
                    perInstanceLod ? instanceTotal : 0,
                    Allocator.Persistent, NativeArrayOptions.ClearMemory);
                // Cleared, not uninitialized: zero progress is "idle", so a
                // rebuild starts every instance settled at its level instead of
                // resuming a fade the previous registration owned. The
                // allocation is initialization-time like every other slice, so
                // the no-realloc constraint holds.
                // The shadow slice gets a zero-length pair. This loop runs over
                // `drivingSliceCount`, which is K + 1 above K = 1, and the
                // shadow slice holds no time state by design (it inherits the
                // finest camera's byte and k at the merge). Zero-length, not
                // absent: a Burst job field must still be bound.
                bool cameraSlice = drivingSliceCount == 1 || slice != shadowSliceIndex;
                int timeStateLength = perInstanceTimeFade && cameraSlice ? instanceTotal : 0;
                meshFadeFromSlices[slice] = new NativeArray<byte>(
                    timeStateLength, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                meshFadeProgressSlices[slice] = new NativeArray<float>(
                    timeStateLength, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            anyDistanceCull = false;
            anyShadowCastCull = false;
            // The animation reruns for the new arrays on the next request.
            lastFadeAnimationFrame = -1;

            int instanceBase = 0;
            int drawGroupBase = 0;
            int indexBase = 0;
            int clusterBase = 0;
            var segmentStarts = new List<int>(Owners.Count * 2);
            var segmentCounts = new List<int>(Owners.Count * 2);
            var segmentOwnerIds = new List<int>(Owners.Count * 2);
            for (int owner = 0; owner < Owners.Count; owner++)
            {
                StaticInstanceOwner ownerData = Owners[owner];
                NativeArray<CullingBounds> bounds = ownerData.Bounds;
                NativeArray<float> shapeFactors = ownerData.ShapeFactors;
                NativeArray<CullingDrawGroup> drawGroups = ownerData.DrawGroups;
                NativeArray<int> indices = ownerData.InstanceIndices;

                NativeArray<CullingBounds>.Copy(bounds, 0, combinedBounds, instanceBase, bounds.Length);
                NativeArray<int>.Copy(indices, 0, combinedInstanceIndices, indexBase, indices.Length);

                // Early-rejection clusters plus the per-instance back
                // references the jobs use. A PerOwner owner is one cluster (a
                // union of everything); a large PerInstance owner is sliced into
                // Morton-contiguous runs (the bounds arrive in bake-time Morton
                // order, so a contiguous slice is a spatially compact box). An
                // owner with no instances keeps one degenerate cluster; it is
                // classified outside and skipped.
                int clusterCountForOwner = ClusterCountOf(ownerData, chunking);
                int clusterStride =
                    (bounds.Length + clusterCountForOwner - 1) / clusterCountForOwner;
                for (int cluster = 0; cluster < clusterCountForOwner; cluster++)
                {
                    int sliceStart = cluster * clusterStride;
                    int sliceEnd = Mathf.Min(sliceStart + clusterStride, bounds.Length);
                    var unionMin = new float3(float.MaxValue);
                    var unionMax = new float3(float.MinValue);
                    for (int i = sliceStart; i < sliceEnd; i++)
                    {
                        CullingBounds b = bounds[i];
                        unionMin = math.min(unionMin, (float3)(b.Center - b.Extents));
                        unionMax = math.max(unionMax, (float3)(b.Center + b.Extents));
                        combinedInstanceOwners[instanceBase + i] = owner;
                        if (!instanceClustersAliased)
                            combinedInstanceClusters[instanceBase + i] = clusterBase + cluster;
                    }

                    clusterAabbs[clusterBase + cluster] = sliceEnd > sliceStart
                        ? new CullingBounds
                        {
                            Center = (unionMin + unionMax) * 0.5f,
                            Extents = (unionMax - unionMin) * 0.5f,
                        }
                        : default;
                    clusterOwners[clusterBase + cluster] = owner;
                    // Identity values; the threshold loop below narrows them
                    // where the owner actually authors a draw range.
                    clusterFarThresholdMin[clusterBase + cluster] = float.MaxValue;
                    clusterNearThresholdMax[clusterBase + cluster] = 0f;
                    clusterInstanceCounts[clusterBase + cluster] = sliceEnd - sliceStart;
                }

                // The knob applies whether or not a Max is authored. The two
                // limits compose as a min per instance in the loop below: an
                // authored Max does not suppress the size-derived limit, which
                // would otherwise let a small instance in an owner with a large
                // Max draw out to the authored distance.
                float cullDistance = ownerData.CullDistance;
                float minDrawDistance = ResolveNearLimit(ownerData);
                float sizeScale = Mathf.Max(0f, StaticInstanceLodSystem.SizeCullDistanceScale);

                // The zero-tax gate. Set inside the owner walk exactly like
                // anyDistanceCull, so a project that authors the option nowhere
                // never reaches any of the tests it gates.
                if (ownerData.ShadowCastMaxLevel >= 0)
                    anyShadowCastCull = true;

                ownerLodParams[owner] = new OwnerLodParams
                {
                    ClusterBase = clusterBase,
                    ClusterStride = clusterStride,
                    InstanceBase = instanceBase,
                    // Absolute metres, Screen Size only; 0 in Distance mode,
                    // where the authored range is composed per instance above.
                    AbsoluteFar = ownerData.LodMode == StaticInstanceLodMode.ScreenSize
                        ? Mathf.Max(0f, ownerData.CullDistance)
                        : 0f,
                    AbsoluteNear = ownerData.LodMode == StaticInstanceLodMode.ScreenSize
                        ? Mathf.Max(0f, ResolveNearLimit(ownerData))
                        : 0f,
                    // The per-owner half of the FOV factor, pre-tangented here
                    // because it cannot change without a rebuild. Sanitized again
                    // at this consuming site: the components clamp on push, but
                    // this value feeds arithmetic, so it does not inherit trust.
                    RefTan = Mathf.Tan(
                        0.5f * Mathf.Deg2Rad *
                        StaticInstanceLodSystem.SanitizeReferenceFov(
                            ownerData.LodReferenceFovDegrees)),
                    ScreenSize = ownerData.LodMode == StaticInstanceLodMode.ScreenSize
                        ? (byte)1
                        : (byte)0,
                    Boundaries = ownerData.LodBoundaries,
                    CrossFadeWidth = Mathf.Max(0f, ownerData.LodCrossFadeWidth),
                    Hysteresis = Mathf.Max(0f, ownerData.LodHysteresis),
                    DistanceFade = ownerData.LodDistanceFade ? (byte)1 : (byte)0,
                    TimeFade = ownerData.LodTimeFade ? (byte)1 : (byte)0,
                    MinLevel = ownerData.LodMinLevel,
                    MaxLevel = (byte)Mathf.Min(ownerData.LodMaxLevel, MaxLodLevels - 1),
                    Policy = (byte)ownerData.LodPolicy,
                    ForcedLevel = ownerData.LodForcedLevel,
                    LevelOffset = ownerData.LodLevelOffset,
                };

                if (anyScreenSize &&
                    ownerData.LodMode == StaticInstanceLodMode.ScreenSize)
                {
                    for (int i = 0; i < bounds.Length; i++)
                    {
                        float shape = shapeFactors.IsCreated ? shapeFactors[i] : 1f;
                        combinedScreenRadii[instanceBase + i] =
                            math.length((float3)bounds[i].Extents) * shape;
                    }
                }

                // The draw-distance test is a band around the instance's bounding
                // sphere, flattened to two squared centre-distance thresholds per
                // instance. Both sides stay conservative — the far one keeps
                // drawing until the whole sphere is out, the near one until the
                // whole sphere is in — so nothing pops while still half visible.
                // Negative duration stays negative here: it is the sentinel the
                // job turns into the live global rate.
                float fadeRate = ownerData.FadeDuration < 0f
                    ? -1f
                    : (ownerData.FadeDuration > 0f
                        ? FadeOpaque / ownerData.FadeDuration
                        : float.MaxValue);
                // The authored Max and the size-derived limit are both
                // candidates and the tighter one wins, per instance. Which one
                // won also decides the fade width, and that answer varies within
                // an owner: a small tree takes the derived limit while a large
                // rock in the same owner takes the authored one.
                //
                // The winner is folded into the sign bit of the stored squared
                // threshold rather than into a per-instance array or a re-test in
                // the job. Deciding it here is exact: `authored` and `derived` are
                // both in hand and un-squared, so there is no sqrt(x*x)
                // round-trip to be wrong about at the crossover. It costs no
                // memory and no per-frame work; the two job-side readers take
                // math.abs. Negative means "the derived limit won".
                //
                // Screen Size mode composes a different far leg, and the global
                // knob is inert there. The owner's far cutoff is its authored
                // ratio (`cullRatio`): the stored per-instance threshold becomes
                // `r_i / cullRatio`, which the job then scales by the camera
                // term. The global `SizeCullDistanceScale` is not composed for
                // such an owner: the ratio boundary is that owner's size-cull
                // authoring in the mode's own unit, so adding the global would
                // author one cutoff twice. Distance-mode owners are untouched.
                //
                // The authored Draw Range does not enter the composition at all
                // in this mode: it is an absolute metric bound, so it rides
                // `OwnerLodParams.AbsoluteFar` / `.AbsoluteNear` as owner-uniform
                // compares instead. That is why `screenSizeMode` suppresses both
                // authored terms below.
                bool screenSizeMode = ownerData.LodMode == StaticInstanceLodMode.ScreenSize;
                float cullRatio = screenSizeMode
                    ? Mathf.Clamp01(ownerData.LodCullRatio)
                    : 0f;
                float effectiveSizeScale = screenSizeMode ? 0f : sizeScale;

                if (cullDistance > 0f || minDrawDistance > 0f ||
                    effectiveSizeScale > 0f || cullRatio > 0f)
                {
                    anyDistanceCull = true;
                    for (int i = 0; i < bounds.Length; i++)
                    {
                        float radius = bounds[i].Extents.magnitude;

                        // The shape factor corrects the size input and nothing
                        // else. It multiplies only the `2 * radius * sizeScale`
                        // term: the trailing `+ radius` is the geometric offset
                        // that puts the limit on the instance's surface, not a
                        // size-perception term, so scaling it would move the
                        // limit inside the geometry for thin content.
                        // Identity (1) leaves the expression bit-identical to the
                        // unscaled one: a stale or absent factor array reads as 1.
                        float shape = shapeFactors.IsCreated ? shapeFactors[i] : 1f;

                        // "Instance world size" is the bounding-sphere
                        // diameter, 2 x Extents.magnitude: twice the radius the
                        // band below folds in. Deliberately not the LOD Group
                        // conversion's input, which must match LOD Group
                        // semantics instead; the two are never unified.
                        // Screen Size: the ratio-derived limit, `r_i / ratio`,
                        // using the shape-corrected radius for the same reason
                        // the ladder does: the mode asks how much screen the
                        // instance covers, which is what the shape factor
                        // corrects. No `+ radius` offset on this leg: the ratio
                        // already describes the sphere's projection, so adding
                        // the geometric offset would double-count it.
                        float derived = ResolveDerivedCullLimit(
                            radius, shape, effectiveSizeScale, cullRatio,
                            screenSizeMode);
                        // Suppressed in Screen Size mode: the authored range
                        // is absolute there and is compared per owner in the
                        // job, not composed per instance here.
                        float authored = !screenSizeMode && cullDistance > 0f
                            ? cullDistance + radius
                            : float.MaxValue;

                        // Strict, so a tie goes to the authored width. With no
                        // authored Max, `authored` is MaxValue and the derived
                        // limit wins whenever the knob is on.
                        bool derivedWon = derived < authored;
                        float far = derivedWon ? derived : authored;

                        // Same suppression on the near side, for the same
                        // reason: in Screen Size mode the authored Min is an
                        // absolute owner bound, not a per-instance threshold.
                        float near = !screenSizeMode && minDrawDistance > 0f
                            ? Mathf.Max(0f, minDrawDistance - radius)
                            : 0f;
                        // The MaxValue sentinel never carries the flag: there
                        // is no limit, so there is no width to choose.
                        combinedCullThresholdsSq[instanceBase + i] =
                            far == float.MaxValue
                                ? float.MaxValue
                                : (derivedWon ? -(far * far) : far * far);
                        combinedMinThresholdsSq[instanceBase + i] = near * near;
                        combinedFadeRates[instanceBase + i] = fadeRate;

                        // Conservative per-cluster extremes of the same
                        // thresholds. The slice is the fixed-stride Morton run
                        // the cluster loop above walked, so the cluster index
                        // is arithmetic — no lookup table.
                        int clusterOfInstance = clusterBase +
                            Mathf.Min(i / clusterStride, clusterCountForOwner - 1);
                        clusterFarThresholdMin[clusterOfInstance] = Mathf.Min(
                            clusterFarThresholdMin[clusterOfInstance], far);
                        clusterNearThresholdMax[clusterOfInstance] = Mathf.Max(
                            clusterNearThresholdMax[clusterOfInstance], near);
                    }
                }
                else
                {
                    for (int i = 0; i < bounds.Length; i++)
                    {
                        combinedCullThresholdsSq[instanceBase + i] = float.MaxValue;
                        combinedMinThresholdsSq[instanceBase + i] = 0f;
                        combinedFadeRates[instanceBase + i] = fadeRate;
                    }
                }

                // Run-length segments over (layer, shadow mode). Correct for
                // any group order; the build-time sort just minimizes runs.
                int segmentBegin = 0;
                for (int i = 0; i < drawGroups.Length; i++)
                {
                    CullingDrawGroup entry = drawGroups[i];
                    entry.BatchID = ownerData.BatchId;
                    entry.OwnerIndex = owner;
                    entry.InstanceBase = instanceBase;
                    entry.InstanceOffset += indexBase;
                    combinedDrawGroups[drawGroupBase + i] = entry;

                    // Motion-vector-ness joins the segment key, because the
                    // engine's MotionVectors pass filters at the range level: a
                    // range whose `motionMode` is Camera is skipped wholesale,
                    // before any per-command flag is read. A segment must
                    // therefore be uniformly MV or non-MV for its `motionMode`
                    // to be right.
                    //
                    // Compare the final adjacent entries that define the emitted ranges.
                    if (i > 0 &&
                        !SameFilter(entry, combinedDrawGroups[drawGroupBase + i - 1]))
                    {
                        segmentStarts.Add(drawGroupBase + segmentBegin);
                        segmentCounts.Add(i - segmentBegin);
                        segmentOwnerIds.Add(owner);
                        segmentBegin = i;
                    }
                }

                if (drawGroups.Length > 0)
                {
                    segmentStarts.Add(drawGroupBase + segmentBegin);
                    segmentCounts.Add(drawGroups.Length - segmentBegin);
                    segmentOwnerIds.Add(owner);
                }

                instanceBase += bounds.Length;
                drawGroupBase += drawGroups.Length;
                indexBase += indices.Length;
                clusterBase += clusterCountForOwner;
            }


            segmentGroupStarts = new NativeArray<int>(
                segmentStarts.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            segmentGroupCounts = new NativeArray<int>(
                segmentStarts.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            segmentOwners = new NativeArray<int>(
                segmentStarts.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            segmentFilterSettings = new NativeArray<BatchFilterSettings>(
                segmentStarts.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            combinedLayerMask = 0u;
            for (int i = 0; i < segmentStarts.Count; i++)
            {
                segmentGroupStarts[i] = segmentStarts[i];
                segmentGroupCounts[i] = segmentCounts[i];
                segmentOwners[i] = segmentOwnerIds[i];

                CullingDrawGroup firstGroup = combinedDrawGroups[segmentStarts[i]];
                // A segment is a run of groups sharing (layer, shadow mode,
                // rendering layer mask), so the segments' layers are exactly
                // the set of draw group layers.
                combinedLayerMask |= 1u << firstGroup.Layer;
                segmentFilterSettings[i] = new BatchFilterSettings
                {
                    renderingLayerMask = firstGroup.RenderingLayerMask,
                    layer = firstGroup.Layer,
                    // `Object` on a segment whose groups carry motion vectors,
                    // `Camera` everywhere else. This is not cosmetic: the engine
                    // skips a range whose mode is Camera in the MotionVectors
                    // pass before it reads `HasMotion` per command, so the
                    // per-command flag alone does nothing. Camera stays the
                    // default so no other content gains MV-pass draws.
                    motionMode = (firstGroup.Flags & CullingDrawGroup.MotionVectorsFlag) != 0
                        ? MotionVectorGenerationMode.Object
                        : MotionVectorGenerationMode.Camera,
                    // The per-renderer mode reaches the engine (TwoSided shadow
                    // state); the Off/ShadowsOnly view exclusion is enforced by
                    // the emission gate in the jobs.
                    shadowCastingMode = (ShadowCastingMode)firstGroup.ShadowMode,
                    // Inert in URP: neither this flag nor Renderer.receiveShadows
                    // is consumed; only the material keyword _RECEIVE_SHADOWS_OFF
                    // controls receiving.
                    receiveShadows = true,
                    staticShadowCaster = false,
                    allDepthSorted = false,
                };
            }

            combinedDataDirty = false;
        }

        // Diagnostic query: the share of SIS clusters that resolve to a single
        // LOD level with no instance in any fade band, for one camera pose. On
        // demand rather than wired into the frame, since the rate is a property
        // of (content, pose) and nothing should be paid for it in the hot path.
        public static unsafe void MeasureClusterUniformity(
            Vector3 cameraPosition,
            out int eligibleClusters,
            out int uniformClusters,
            out int levelUniformClusters)
        {
            eligibleClusters = 0;
            uniformClusters = 0;
            levelUniformClusters = 0;
            if (Owners.Count == 0)
                return;
            if (combinedDataDirty)
                RebuildCombinedData();

            int clusterCount = clusterOwners.IsCreated ? clusterOwners.Length : 0;
            if (clusterCount == 0)
                return;

            int* counters = stackalloc int[3];
            StaticInstanceCullingBurst.ClassifyClusterUniformity(
                (CullingBounds*)clusterAabbs.GetUnsafeReadOnlyPtr(),
                (int*)clusterOwners.GetUnsafeReadOnlyPtr(),
                (float*)clusterFarThresholdMin.GetUnsafeReadOnlyPtr(),
                (float*)clusterNearThresholdMax.GetUnsafeReadOnlyPtr(),
                (OwnerLodParams*)ownerLodParams.GetUnsafeReadOnlyPtr(),
                cameraPosition.x,
                cameraPosition.y,
                cameraPosition.z,
                // Passes FOV factor 1 deliberately: the camera here is a
                // query argument rather than a driving camera, and a result
                // that moved with somebody's zoom would not be comparable
                // across calls.
                Mathf.Max(0f, StaticInstanceLodSystem.LodDistanceScale),
                Mathf.Max(0f, StaticInstanceLodSystem.LodBias),
                // The camera term, with the gate off: fovApplies = 0 keeps the
                // "factor 1" property, since the compose inside selects 1f per
                // owner and never reads RefTan.
                1f,
                (byte)0,
                Mathf.Clamp(StaticInstanceLodSystem.MinMeshLodLevel, 0, MaxLodLevels - 1),
                StaticInstanceLodSystem.CrossFadeActive ? (byte)1 : (byte)0,
                clusterCount,
                counters,
                null);

            eligibleClusters = counters[0];
            uniformClusters = counters[1];
            levelUniformClusters = counters[2];
        }

        // Diagnostic query: how long is a (cluster x draw group) sub-run? The
        // value of skipping clusters outside the view rests on that length
        // (skipping a run of one instance cannot pay for the table entry that
        // describes it), and the length is a property of the bake's ordering,
        // not of the camera. Also reports monotonicity violations, the property
        // sub-run contiguity depends on (each group's index list ascending in
        // Morton collection order).
        public static void MeasureClusterSubRuns(
            out int drawGroups,
            out int totalInstances,
            out int subRuns,
            out int singleInstanceRuns,
            out int monotonicityViolations)
        {
            drawGroups = 0;
            totalInstances = 0;
            subRuns = 0;
            singleInstanceRuns = 0;
            monotonicityViolations = 0;
            if (Owners.Count == 0)
                return;
            if (combinedDataDirty)
                RebuildCombinedData();
            if (!combinedDrawGroups.IsCreated || !combinedInstanceClusters.IsCreated)
                return;

            drawGroups = combinedDrawGroups.Length;
            for (int g = 0; g < combinedDrawGroups.Length; g++)
            {
                CullingDrawGroup drawGroup = combinedDrawGroups[g];
                int end = drawGroup.InstanceOffset + drawGroup.InstanceCount;
                int previousCluster = -1;
                int runLength = 0;
                for (int i = drawGroup.InstanceOffset; i < end; i++)
                {
                    int cluster = combinedInstanceClusters[
                        drawGroup.InstanceBase + combinedInstanceIndices[i]];
                    totalInstances++;
                    if (cluster == previousCluster)
                    {
                        runLength++;
                        continue;
                    }

                    if (previousCluster >= 0)
                    {
                        if (cluster < previousCluster)
                            monotonicityViolations++;
                        if (runLength == 1)
                            singleInstanceRuns++;
                    }

                    subRuns++;
                    previousCluster = cluster;
                    runLength = 1;
                }

                if (previousCluster >= 0 && runLength == 1)
                    singleInstanceRuns++;
            }
        }

        private static unsafe T* MallocTempJob<T>(int count) where T : unmanaged
        {
            return (T*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<T>() * (long)count,
                UnsafeUtility.AlignOf<T>(),
                Allocator.TempJob);
        }

        // AABB-vs-split plane test over PlanePacket4 (split offset/count are
        // in packet units). No early-out: the four-lane multiply-adds are
        // cheaper than the branch for the small per-split packet counts.
        private static unsafe bool TestPlanePackets(
            in CullingBounds bounds,
            PlanePacket4* packets,
            in SplitPlaneRange split)
        {
            var cx = new float4(bounds.Center.x);
            var cy = new float4(bounds.Center.y);
            var cz = new float4(bounds.Center.z);
            var ex = new float4(bounds.Extents.x);
            var ey = new float4(bounds.Extents.y);
            var ez = new float4(bounds.Extents.z);

            var isCulled = new bool4(false);
            int packetEnd = split.PlaneOffset + split.PlaneCount;
            for (int i = split.PlaneOffset; i < packetEnd; i++)
            {
                PlanePacket4 p = packets[i];
                float4 distances = p.nx * cx + p.ny * cy + p.nz * cz + p.d;
                float4 radii = p.nxAbs * ex + p.nyAbs * ey + p.nzAbs * ez;
                isCulled |= distances + radii < float4.zero;
            }

            return !math.any(isCulled);
        }

        // Per-request scratch memory carved out of one TempJob allocation so the
        // culling callback performs a single Malloc instead of several NativeArray
        // creations. Freed by FreeScratchBufferJob at the end of the job chain.
        private unsafe struct CullingScratchBuffer
        {
            public void* Buffer;
            public PlanePacket4* PlanePackets;
            public SplitPlaneRange* Splits;
            public EntityId* OwnerPickingIds;
            public int* GroupConfigCounts;
            public byte* OwnerFlags;
            public byte* OwnerMeshLods;
            public byte* OwnerPolicies;
            // Per-cluster split bits from the AABB pre-test (a cluster is a
            // whole PerOwner owner or one slice of a chunked PerInstance
            // owner): Intersect is every split the cluster reaches at
            // all, Inside the subset it is wholly within. Intersect == 0
            // skips the cluster's instances; the bits in Inside are taken
            // without a per-instance test.
            public byte* ClusterIntersectMasks;
            public byte* ClusterInsideMasks;
            public byte* VisibilityMasks;
            public byte* OwnerCrossfadeFrom;
            public byte* OwnerCrossfadeK;
            // The owner-authored Shadow Cast Max Level, as a byte with 0xFF
            // standing for "-1, every level casts". Allocated only when
            // anyShadowCastCull, so a project that authors the option nowhere
            // does not pay the region.
            public byte* OwnerShadowCastMaxLevel;
        }

        // The Shadow Cast Max Level rule, in one place because the count and
        // write halves must apply the same expression: a predicate in one and
        // not the other writes past a cursor or leaves a hole. Every emission
        // site calls this; none restates it.
        //
        // The rule: a light view emits the half whose level is at or below the
        // owner's Shadow Cast Max Level, and drops the half above it. Applied
        // per copy, so it is direction-agnostic by construction: a crossfade
        // pair's `from` may be above or below its `to` (ApplyLodLevel starts a
        // fade on any level change, either way), and neither half needs to know
        // which end it is.
        //
        // `n` is the owner's threshold as the scratch region carries it:
        // 0xFF stands for "-1, every level casts".
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShadowCastEmits(int level, byte n) =>
            n == 0xFF || level <= n;

        // The owner-level half of the same rule, extracted so it can be driven
        // by a test without a live owner and view slice.
        //
        // Refuse only in the rule's "emits nothing" cases: no pair and past the
        // threshold, or a pair wholly past it. Everything else falls through to
        // the emit path, which drops the half above the threshold.
        //
        // `from` and `to` are not ordered: ApplyLodLevel starts a fade on any
        // level change, in either direction, so an approaching camera gives
        // from > to. Taking the minimum keeps this direction-agnostic; refusing
        // on `to` alone would delete a fading-in owner for the whole band and
        // then pop it on.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool ShadowCastOwnerRefused(int from, int to, int k, int n)
        {
            if (n < 0)
                return false;
            int lowest = k != 0 ? math.min(from, to) : to;
            return lowest > n;
        }

        // Per-draw-group emission gate shared by every culling job: the owner
        // must be rendered, the per-renderer shadow mode must match the view
        // (Off never reaches light views, ShadowsOnly never reaches camera-like
        // views) and the group's layer must be in the request's culling mask.
        private static bool IsGroupEmitted(
            in CullingDrawGroup drawGroup,
            byte ownerFlag,
            bool isLightView,
            uint cullingLayerMask)
        {
            if (ownerFlag == 0)
                return false;
            if (isLightView)
            {
                if (drawGroup.ShadowMode == (byte)ShadowCastingMode.Off)
                    return false;
            }
            else if (drawGroup.ShadowMode == (byte)ShadowCastingMode.ShadowsOnly)
            {
                return false;
            }

            return (cullingLayerMask & (1u << drawGroup.Layer)) != 0;
        }

        // The once-per-frame driving-view evaluation: every distance-dependent
        // per-instance decision is made here, from Camera.main, and every view
        // in the frame just reads the results.
        // Two independent parts, each gated so absent features cost nothing:
        // - Draw-range fades (DistanceCull): steps every instance's dither fade
        //   towards where the draw distances say it should be. Time driven, so
        //   a transition that starts always finishes: stopping the camera
        //   mid-band does not freeze a half-dithered mesh.
        // - PerInstance mesh LOD (EvaluateLod): level selection from the
        //   camera → AABB-surface distance, plus the distance-driven
        //   crossfade band below each boundary. While the band is on there is
        //   no hysteresis — it is a pure function of distance, so it cannot
        //   oscillate, and stopping inside it holds a stable blend of two
        //   levels (deliberate contrast with the time-driven draw-range fade,
        //   which blends against nothing). At width 0 (hard switches) a
        //   per-instance hysteresis guards the way back in instead, judged
        //   against the level held last frame.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private unsafe struct DrivingViewEvalJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<CullingBounds> Bounds;
            [ReadOnly] public NativeArray<float> CullThresholdsSq;
            // The per-instance corrected radius a Screen Size owner's ratio
            // boundaries are measured from. Indexed only when `p.ScreenSize != 0`;
            // it is a length-1 dummy otherwise, because a Burst job field must be
            // bound even when the project uses no Screen Size owner.
            [ReadOnly] public NativeArray<float> ScreenRadii;
            [ReadOnly] public NativeArray<float> MinThresholdsSq;
            [ReadOnly] public NativeArray<float> FadeRates;
            public NativeArray<float> InstanceFades;
            [ReadOnly] public NativeArray<int> InstanceOwners;
            [ReadOnly] public NativeArray<OwnerLodParams> OwnerParams;
            [NativeDisableParallelForRestriction] public NativeArray<byte> InstanceLevels;
            [NativeDisableParallelForRestriction] public NativeArray<byte> CrossfadeValues;
            // The per-slice time-fade state. Zero-length when no owner fades on
            // time; the fields must still be bound (a Burst job cannot carry an
            // unassigned NativeArray), the same dummy-binding shape ScreenRadii
            // uses.
            [NativeDisableParallelForRestriction] public NativeArray<byte> MeshFadeFrom;
            [NativeDisableParallelForRestriction] public NativeArray<float> MeshFadeProgress;
            // Gate: some registered PerInstance owner is in Time mode. False
            // costs one branch for the whole job.
            public bool AnyTimeFade;

            public float3 CameraPosition;
            public float DeltaTime;
            // Rate for owners that did not author their own duration; read fresh
            // every frame so changing the global knob takes effect immediately
            // and without a rebuild (a rebuild would reset every fade to opaque,
            // which reads as a pop).
            public float GlobalRate;
            public bool DistanceCull;
            public bool EvaluateLod;
            // Globals resolved on the main thread once per frame: forced level
            // (-1 = auto), the global level floor, and the distance scale that
            // multiplies every authored boundary.
            public int ForcedLevel;
            public int GlobalMinLevel;
            // The global `LodDistanceScale`, alone. The per-camera FOV factor is
            // not folded into it: the reference is per component, so the factor
            // is per owner x per camera and is composed inside Execute from this
            // scalar, `CameraFovTerm` and the owner's `RefTan`. LodDistanceScale
            // must never reach the draw range, which is why the compose produces
            // two values rather than one.
            public float DistanceScale;
            public float LodBias;

            // The camera half of the FOV factor: `1 / tan(fov/2)`, one scalar
            // per view (StaticInstanceLodSystem.ResolveCameraFovTerm).
            public float CameraFovTerm;

            // Whether the factor applies to a Distance-mode owner in this view:
            // the `FovTrackingEnabled` master and a camera that actually has a
            // FOV (not null, not orthographic). False means every Distance owner
            // takes exactly 1f, so "off" is byte-identical: the identity is
            // selected, not multiplied through.
            public bool FovApplies;

            // Whether this view has a usable FOV at all: false for an
            // orthographic camera and for the fallback slice, which has no
            // Camera. It is separate from `FovApplies` because the two modes
            // answer it differently: a Distance owner takes factor 1 (exactly
            // neutral, authored metres as written), while a Screen Size owner
            // cannot, since its stored quantity is a ratio and 1 would be wrong
            // rather than neutral. Such a view evaluates it as if seen at the
            // owner's own reference, i.e. `1 / tan(ref/2)` = `1f / p.RefTan`.
            public bool HasFov;
            // Mask (StaticInstanceLodSystem.CrossFadeActive):
            // false skips the band, hard-switching at each boundary.
            public bool CrossFadeEnabled;
            public bool SnapTimeFades;
            // The fade-band width as a fraction of the derived limit,
            // snapshotted on the main thread: a Burst job cannot read the
            // managed static the settings container writes.
            public float DerivedCullFadeFraction;
            // Level + 1 per cluster when the whole cluster resolves to one
            // level with no instance in any fade band; null when the
            // optimization is off. The loop and its stores are kept either way
            // (only the band-search arithmetic is skipped), so every downstream
            // consumer reads the arrays the same way and no cluster-value
            // fallback logic has to exist in three jobs.
            [NativeDisableUnsafePtrRestriction] public byte* ClusterUniformLevels;
            public void Execute(int instanceIndex)
            {
                OwnerLodParams p = OwnerParams[InstanceOwners[instanceIndex]];
                // The FOV factor is composed per owner here, because the owner's
                // reference and the view's FOV live in different places.
                // `ComposeEvaluationScales` uses `math.select` rather than a
                // branch: the identity case must stay free, and a per-owner
                // branch in this loop is what the design avoids.
                StaticInstanceCullingBurst.ComposeEvaluationScales(DistanceScale, LodBias,
                    p.RefTan, p.ScreenSize != 0,
                    p.ScreenSize != 0 ? ScreenRadii[instanceIndex] : 0f,
                    CameraFovTerm, FovApplies, HasFov,
                    out float boundaryScale, out float cullScale);
                if (DistanceCull)
                {
                    if (p.DistanceFade != 0)
                    {
                        // Distance-driven (SIS): the fade is a pure function
                        // of the centre distance — a CrossFadeWidth-wide
                        // dither band just inside each draw limit, sharing
                        // the LOD band's width so SIS has one band concept.
                        // Stateless: stopping inside the band holds a stable
                        // dither, and a camera cut resolves instantly (no
                        // fade-in animation after teleports).
                        float d = math.length(
                            (float3)Bounds[instanceIndex].Center - CameraPosition);
                        // The stored square is NEGATED when the
                        // size-derived limit won this instance. abs recovers
                        // the value; the sign is read below. Tested with
                        // asuint, not `< 0f`, because a zero-radius instance
                        // can derive a limit of exactly 0 and -0.0f < 0f is
                        // false.
                        float farSq = CullThresholdsSq[instanceIndex];
                        bool derivedLimit = (math.asuint(farSq) >> 31) != 0;
                        // The uniform factor, applied to the
                        // composed limits. Scaling after the sqrt rather than
                        // squaring the factor keeps this one multiply per side
                        // and avoids a second rounding step.
                        float far = math.sqrt(math.abs(farSq)) * cullScale;
                        float near = math.sqrt(MinThresholdsSq[instanceIndex])
                            * cullScale;
                        // In Screen Size mode an authored Draw Range is an
                        // absolute metric bound, not scaled by FOV.
                        //
                        // It cannot ride the stored threshold: that leg is
                        // ratio-derived and gets the camera term, while these are
                        // metres the author wrote and must stay put. So they are
                        // owner-uniform compares (one min and one max per
                        // instance, no per-instance data), and a one-float
                        // per-cluster bound stays exact because the owner
                        // constant applies after the scaled reduction.
                        //
                        // Owner-uniform means no per-instance `+radius`
                        // conservatism: this is a ceiling for an author who
                        // writes metres into a ratio world, not a second
                        // sphere-aware limit.
                        //
                        // It fades at the bound by design, and "hard ceiling"
                        // bounds the value against FOV scaling, never the
                        // transition's shape. Fading is per fade mode, not
                        // universal: `fadeMode` picks the transition mechanism at
                        // every boundary kind, so a `None` owner hard-cuts here
                        // (hysteresis-guarded) and only `Distance` and `Time`
                        // fade. The authored width is 0 in `None`, so it never
                        // makes a `None` owner fade.
                        //
                        // The clamp decides the winner: the width below follows
                        // `derivedLimit` (the stored sign bit), which cannot know
                        // a clamp happened after it. Re-deciding the winner here
                        // keeps the fade width uniform: the absolute bound is an
                        // authored limit, so it carries the owner's authored
                        // crossfade width in all cases.
                        if (p.ScreenSize != 0)
                        {
                            far = ResolveScreenSizeFarBound(
                                far, p.AbsoluteFar, ref derivedLimit);
                            near = ResolveScreenSizeNearBound(near, p.AbsoluteNear);
                        }
                        // A derived limit carries its own fade width, a
                        // fraction of the limit itself (authored in Graphics
                        // Settings), independent of the owner's authored
                        // crossfade width; otherwise an owner in hysteresis mode
                        // (width 0) would hard-cut at a derived distance the
                        // author never set. With CrossFadeActive off, dithering
                        // is impossible by construction (variants stripped), so
                        // that configuration hard-cuts by design.
                        // The two sides carry different widths. A derived far
                        // limit fades over a fraction of itself, but a near limit
                        // is never derived: it is always authored, so it fades
                        // over the authored Fade Width. Sharing one width would
                        // give an owner with an authored Min and no Max the far
                        // limit's derived width on its near side (a large mesh
                        // with a large size scale derives a far limit of hundreds
                        // of metres, so a 5 m near limit would dither out over
                        // that same width).
                        // Since the two limits compose, both can occur inside one
                        // owner and only the instance knows which it got, so the
                        // width follows the limit's origin per instance.
                        float farWidth = derivedLimit
                            ? far * DerivedCullFadeFraction
                            : p.CrossFadeWidth;
                        float nearWidth = p.CrossFadeWidth;
                        if (!CrossFadeEnabled)
                        {
                            farWidth = 0f;
                            nearWidth = 0f;
                        }

                        // A zero width on either side is that side's hard cut:
                        // the plain "in the band or not" test. No near limit (0)
                        // means no near band at all; without the guard,
                        // instances close to the camera would dither against a
                        // limit that does not exist.
                        //
                        // Where there is no band, Hysteresis guards the cut. The
                        // arithmetic (bands where they exist, hysteresis where
                        // they do not) is one static function so that a test can
                        // drive the rule itself; this job is Burst-compiled over
                        // every instance and the semantics are the only part of
                        // it that can be wrong.
                        InstanceFades[instanceIndex] = ResolveDistanceDrawRangeFade(
                            d, far, near, farWidth, nearWidth,
                            p.Hysteresis, InstanceFades[instanceIndex]);
                    }
                    else
                    {
                        float distanceSq = math.lengthsq(
                            (float3)Bounds[instanceIndex].Center - CameraPosition);
                        // abs: the far square is stored negated when the
                        // size-derived limit won. This path does not care which
                        // limit it was, only how far it is.
                        // The time-driven half of the hysteresis; see
                        // ResolveTimeDrawRangeInBand for what it guards against
                        // and why the state it reads is "settled visible" rather
                        // than "not zero".
                        // This path compares squares, so the uniform factor
                        // enters squared.
                        float cullScaleSq = cullScale * cullScale;
                        float timeFarSq =
                            math.abs(CullThresholdsSq[instanceIndex]) * cullScaleSq;
                        float timeNearSq =
                            MinThresholdsSq[instanceIndex] * cullScaleSq;
                        if (p.ScreenSize != 0)
                        {
                            ResolveScreenSizeTimeBounds(
                                ref timeFarSq, ref timeNearSq,
                                p.AbsoluteFar, p.AbsoluteNear);
                        }

                        bool inBand = ResolveTimeDrawRangeInBand(
                            distanceSq,
                            timeFarSq,
                            timeNearSq,
                            p.Hysteresis,
                            InstanceFades[instanceIndex]);

                        float target = inBand ? FadeOpaque : 0f;
                        if (SnapTimeFades)
                        {
                            InstanceFades[instanceIndex] = target;
                        }
                        else
                        {
                            float rate = FadeRates[instanceIndex];
                            float step = (rate < 0f ? GlobalRate : rate) * DeltaTime;
                            float current = InstanceFades[instanceIndex];
                            InstanceFades[instanceIndex] = current < target
                                ? math.min(target, current + step)
                                : math.max(target, current - step);
                        }
                    }
                }

                if (!EvaluateLod)
                    return;
                if (p.Policy != (byte)LodSelectionPolicy.PerInstance)
                    return;

                // If this instance's cluster already resolved to a
                // single level, store that and skip the ladder entirely — the
                // sqrt, the band compares and the crossfade band test. The
                // cluster index is arithmetic (fixed-stride Morton runs), so
                // no per-instance cluster stream is read. A uniform cluster is
                // by definition clear of every fade band, so the crossfade
                // byte is 0 and the level carries no pair bit.
                if (ClusterUniformLevels != null)
                {
                    int cluster = p.ClusterBase +
                        (instanceIndex - p.InstanceBase) / p.ClusterStride;
                    byte resolved = ClusterUniformLevels[cluster];
                    if (resolved != 0)
                    {
                        InstanceLevels[instanceIndex] = (byte)(resolved - 1);
                        CrossfadeValues[instanceIndex] = 0;
                        return;
                    }
                }

                // Camera → AABB surface distance, the same metric the per-owner
                // ladder uses (zero while the camera is inside the box).
                CullingBounds bounds = Bounds[instanceIndex];
                float3 delta = math.abs(CameraPosition - (float3)bounds.Center)
                    - (float3)bounds.Extents;
                float distance = math.length(math.max(delta, float3.zero));

                int maxLevel = p.MaxLevel;
                int minLevel = math.min((int)p.MinLevel, maxLevel);
                int level;
                byte crossfade = 0;
                // The pair's partner. -1 = "whatever the distance band
                // implies", i.e. level + 1; the time path names it outright.
                int pairLevel = -1;
                if (ForcedLevel >= 0)
                {
                    // Global force wins over everything.
                    level = math.clamp(ForcedLevel, minLevel, maxLevel);
                }
                else if (p.ForcedLevel >= 0)
                {
                    // Per-owner force bypasses the bands (and their crossfade),
                    // same semantics as the group's Forced LOD dropdown.
                    level = math.clamp(p.ForcedLevel, minLevel, maxLevel);
                }
                else
                {
                    // Window semantics: the Min/Max range selects which mesh LOD
                    // levels the ladder spans, and boundary i is the transition
                    // (min + i) -> (min + i + 1); sliding the window shifts the
                    // levels over the same authored distances.
                    int boundaryCount = maxLevel - minLevel;
                    level = minLevel;
                    if (boundaryCount > 0 && distance > p.Boundaries.x * boundaryScale)
                        level = minLevel + 1;
                    if (boundaryCount > 1 && distance > p.Boundaries.y * boundaryScale)
                        level = minLevel + 2;
                    if (boundaryCount > 2 && distance > p.Boundaries.z * boundaryScale)
                        level = minLevel + 3;

                    // Hard-switch mode only (the crossfade band replaces the
                    // hysteresis): coming back to a finer level additionally
                    // needs the instance inside the previous boundary by the
                    // margin, judged against the level this instance held
                    // last frame (InstanceLevels persists across frames; a
                    // rebuild clears it to 0, which starts fine — the margin
                    // only ever *holds* a coarser level). Same asymmetry as
                    // the per-owner state machine: the margin is not scaled
                    // by DistanceScale (it tracks camera jitter amplitude,
                    // not scene size).
                    if (p.Hysteresis > 0f && p.CrossFadeWidth <= 0f)
                    {
                        int held = math.clamp(
                            InstanceLevels[instanceIndex] & 0x03, minLevel, maxLevel);
                        while (held > level)
                        {
                            int boundaryIndex = held - 1 - minLevel;
                            float boundary = (boundaryIndex == 0
                                ? p.Boundaries.x
                                : boundaryIndex == 1 ? p.Boundaries.y : p.Boundaries.z)
                                * boundaryScale;
                            if (distance < boundary - p.Hysteresis)
                                held--;
                            else
                                break;
                        }

                        level = math.max(level, held);
                    }
                    // The global quality floor is runtime-only and
                    // non-structural: it lifts the resolved level and never
                    // touches the window or its boundaries.
                    level = math.clamp(math.max(level, GlobalMinLevel), minLevel, maxLevel);

                    // Crossfade band [b - w, b] below the next boundary: k is
                    // the fraction of pixels the outgoing level keeps (127 → 1
                    // as the instance approaches the boundary). The degenerate
                    // ends (k = 127 or 0) fall back to a hard single level, so
                    // the pair only exists while it actually blends.
                    if (CrossFadeEnabled && level < maxLevel && p.CrossFadeWidth > 0f)
                    {
                        int boundaryIndex = level - minLevel;
                        float boundary = (boundaryIndex == 0
                            ? p.Boundaries.x
                            : boundaryIndex == 1 ? p.Boundaries.y : p.Boundaries.z)
                            * boundaryScale;
                        float t = (distance - (boundary - p.CrossFadeWidth)) / p.CrossFadeWidth;
                        if (t > 0f)
                        {
                            int keepOld = (int)math.round((1f - t) * FadeOpaque);
                            if (keepOld >= 1 && keepOld < FadeOpaque)
                                crossfade = (byte)keepOld;
                        }
                    }

                    // The offset shifts the selected level and its band
                    // together (the pair becomes shifted → shifted + 1); a
                    // band whose incoming level would leave the window
                    // degenerates to the hard clamped level.
                    if (p.LevelOffset != 0)
                    {
                        level = math.clamp(level + p.LevelOffset, minLevel, maxLevel);
                        if (crossfade != 0 && level >= maxLevel)
                            crossfade = 0;
                    }
                }

                if (AnyTimeFade && p.TimeFade != 0)
                {
                    byte fadeState = MeshFadeFrom[instanceIndex];
                    float progress = MeshFadeProgress[instanceIndex];
                    float rate = FadeRates[instanceIndex];
                    // The state machine is `StepTimeFade`, a pure static below,
                    // so the arithmetic can be unit-tested frame by frame (a
                    // private nested Burst job is not reachable from a test);
                    // Burst inlines it.
                    crossfade = StepTimeFade(
                        level,
                        InstanceLevels[instanceIndex] & 0x03,
                        !CrossFadeEnabled || SnapTimeFades ||
                            ForcedLevel >= 0 || p.ForcedLevel >= 0,
                        (rate < 0f ? GlobalRate : rate) * DeltaTime,
                        ref fadeState,
                        ref progress,
                        out int fadePartner);
                    if (crossfade != 0)
                        pairLevel = fadePartner;
                    MeshFadeProgress[instanceIndex] = progress;
                    MeshFadeFrom[instanceIndex] = fadeState;
                }

                // The pair's partner level is explicit in bits 2-3. A time fade
                // may blend any two levels (a fast zoom can move the selection
                // two boundaries in one frame), so the partner is carried rather
                // than assumed to be `level + 1`. The distance path leaves
                // `pairLevel` at -1 and writes `level + 1`.
                InstanceLevels[instanceIndex] = (byte)(
                    crossfade != 0
                        ? level | ((pairLevel >= 0 ? pairLevel : level + 1) << 2) | 0x80
                        : level);
                CrossfadeValues[instanceIndex] = crossfade;
            }
        }

        // The finest level an instance's level byte actually draws. With no
        // pair it is bits 0-1. With a pair it is the finer of the two halves,
        // because the pair draws both and "finest across cameras" is a claim
        // about what is on screen, not about which half the encoding puts first.
        //
        // The pair-bit guard is load-bearing: a settled instance leaves bits 2-3
        // zero, so an unguarded min() would read every settled level as 0 and
        // hand the shadow slice to whichever camera asked first. On the distance
        // path (partner = level + 1) this returns bits 0-1 unchanged.
        internal static int FinestLevel(byte levelByte)
        {
            int level = levelByte & 0x03;
            return (levelByte & 0x80) != 0
                ? math.min(level, (levelByte >> 2) & 0x03)
                : level;
        }

        internal static byte StepTimeFade(
            int level,
            int heldLevel,
            bool snap,
            float step,
            ref byte state,
            ref float progress,
            out int partner)
        {
            int from = state & 0x03;
            bool seen = (state & 0x80) != 0;

            if (snap || !seen)
            {
                // A forced level, the cross-fade mask and the URP Asset's
                // Cross Fade switch each mean "no pair may be emitted", and the
                // progress is cleared with them rather than frozen: a fade
                // resumed later, from a pose the camera has long left, would be
                // wrong.
                progress = 0f;
            }
            else if (level != heldLevel)
            {
                from = heldLevel;
                progress = FadeOpaque;
            }
            else if (progress > 0f)
            {
                progress = math.max(0f, progress - step);
            }

            if (progress > 0f && from != level)
            {
                partner = from;
                state = (byte)(from | 0x80);
                return (byte)math.clamp(
                    (int)math.round(FadeOpaque - progress), 1, FadeOpaque - 1);
            }

            progress = 0f;
            partner = level;
            state = (byte)(level | 0x80);
            return 0;
        }

        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private unsafe struct ComputeVisibilityMasksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<CullingBounds> Bounds;
            [ReadOnly] public NativeArray<float> InstanceFades;
            [ReadOnly] public NativeArray<int> InstanceClusters;
            [NativeDisableUnsafePtrRestriction] public PlanePacket4* PlanePackets;
            [NativeDisableUnsafePtrRestriction] public SplitPlaneRange* Splits;
            [NativeDisableUnsafePtrRestriction] public byte* ClusterIntersectMasks;
            [NativeDisableUnsafePtrRestriction] public byte* ClusterInsideMasks;
            [NativeDisableUnsafePtrRestriction] public byte* VisibilityMasks;

            public int SplitCount;
            // False while no owner sets a draw distance, so the common case never
            // touches the fade array.
            public bool DistanceCull;
            // Bit above the split bits marking "part way through a fade", so the
            // binning separates fading instances into their own draw commands
            // (the crossfade keyword is per command, not per instance).
            public byte FadeBit;

            // The per-instance cascade caster cull. The cluster pass applies the
            // same receiver-sphere test per cluster, but the mask below starts at
            // the cluster's insideMask and only ORs, so it cannot clear a bit the
            // cluster kept. This is the per-instance application of the same
            // test, on the same constants.
            public bool ReceiverInstanceCull;
            public bool SingleBitCull;
            public ReceiverVolumeConstants ReceiverConstants;

            public void Execute(int instanceIndex)
            {
                // The cluster's AABB contains every one of its instances, so a
                // split it misses entirely cannot contain them either. This also
                // covers owners that are not rendered in this view (their
                // clusters are classified with an empty mask), which the
                // draw-group gate drops anyway.
                int cluster = InstanceClusters[instanceIndex];
                byte intersectMask = ClusterIntersectMasks[cluster];
                if (intersectMask == 0)
                {
                    VisibilityMasks[instanceIndex] = 0;
                    return;
                }

                byte fadeFlag = 0;
                if (DistanceCull)
                {
                    // Visibility follows the animated fade rather than the raw
                    // distance test, so an instance keeps drawing while it
                    // dithers out — in every view, so its shadow fades with it.
                    float fade = InstanceFades[instanceIndex];
                    if (fade < 1f)
                    {
                        VisibilityMasks[instanceIndex] = 0;
                        return;
                    }

                    if (fade < FadeOpaque)
                        fadeFlag = FadeBit;
                }

                // Splits the cluster sits wholly inside are taken without a test;
                // only the ones it straddles still need the instance's own AABB.
                byte insideMask = ClusterInsideMasks[cluster];
                byte mask = insideMask;
                int straddling = intersectMask & ~insideMask;
                if (straddling != 0)
                {
                    CullingBounds bounds = Bounds[instanceIndex];
                    for (int splitIndex = 0; splitIndex < SplitCount; splitIndex++)
                    {
                        if ((straddling & (1 << splitIndex)) == 0)
                            continue;
                        if (TestPlanePackets(bounds, PlanePackets, Splits[splitIndex]))
                            mask |= (byte)(1 << splitIndex);
                    }
                }

                // The per-instance cascade caster cull, gated on the
                // instance's assembled mask carrying two or more split bits.
                //
                // countbits(mask) > 1 is a deliberate scope trade, not an
                // exactness argument. The core-containment break only ever
                // strips larger-index splits, so it cannot touch a single-bit
                // instance and is free there. But the reach test can remove any
                // bit including a lone one, so this gate forgoes per-instance
                // reach-culling of single-bit instances (they keep the cluster
                // verdict).
                // `SingleBitCull` is a diagnostic toggle: with it on the test
                // also runs on single-bit instances, which can cull one to
                // mask 0.
                if (ReceiverInstanceCull &&
                    (SingleBitCull || math.countbits((uint)mask) > 1))
                {
                    unsafe
                    {
                        fixed (byte* planeBytes = ReceiverConstants.PlaneBytes)
                        fixed (byte* sphereBytes = ReceiverConstants.SphereBytes)
                        {
                            CullingBounds casterBounds = Bounds[instanceIndex];
                            float3 casterCenterWorld = casterBounds.Center;
                            float casterRadius = math.length((float3)casterBounds.Extents);
                            // Recomputed per instance, never inherited from the
                            // cluster: the cluster's length is measured from the
                            // cluster's centre with the cluster's radius, and
                            // that it bounds every member's is not trivially
                            // true. The safe error direction is a longer shadow.
                            float shadowLength = StaticInstanceCullingBurst.ComputeShadowLength(
                                casterCenterWorld,
                                casterRadius,
                                ReceiverConstants.ShadowDirection,
                                (Plane*)planeBytes,
                                ReceiverConstants.PlaneCount);
                            uint sphereMask = StaticInstanceCullingBurst.ComputeReceiverSphereMask(
                                math.mul(ReceiverConstants.WorldToLight, casterCenterWorld),
                                casterRadius,
                                shadowLength,
                                (ReceiverSplitSphere*)sphereBytes,
                                ReceiverConstants.SplitCount);

                            mask = (byte)(mask & sphereMask);
                        }
                    }
                }

                // The fade bit alone is not visibility: an instance outside every
                // split still bins to 0 and is dropped.
                VisibilityMasks[instanceIndex] = mask != 0 ? (byte)(mask | fadeFlag) : (byte)0;
            }
        }

        // Counts the visible instances per (draw group x split visibility
        // mask) and, in the same serial job, allocates the exact-size output
        // arrays, writes commands segment-major (one BatchDrawRange per
        // contiguous run of equal filter attributes within an owner) and turns
        // GroupConfigCounts into write offsets for the scatter job. Binning on
        // the distinct mask value means an instance visible in two cascades is
        // emitted once with a multi-bit splitVisibilityMask instead of being
        // duplicated per split. Counting serially instead of in a parallel
        // stage saves one scheduled job per view, and the mask-read count loop
        // is too cheap for the lost parallelism to matter.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private unsafe struct CountAndAllocateJob : IJob
        {
            [ReadOnly] public NativeArray<CullingDrawGroup> DrawGroups;
            [ReadOnly] public NativeArray<int> InstanceIndices;
            [ReadOnly] public NativeArray<int> SegmentGroupStarts;
            [ReadOnly] public NativeArray<int> SegmentGroupCounts;
            [ReadOnly] public NativeArray<int> SegmentOwners;
            [ReadOnly] public NativeArray<BatchFilterSettings> SegmentFilterSettings;
            [ReadOnly] public NativeArray<byte> InstanceLevels;
            [NativeDisableUnsafePtrRestriction] public EntityId* OwnerPickingIds;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerFlags;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerMeshLods;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerPolicies;
            // ShadowCastCull is the uniform gate: false makes every
            // ShadowCastEmits call unreachable, so a project that authors the
            // option nowhere pays one hoisted branch per dispatch.
            [NativeDisableUnsafePtrRestriction] public byte* OwnerShadowCastMaxLevel;
            public bool ShadowCastCull;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerCrossfadeFrom;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerCrossfadeK;
            [NativeDisableUnsafePtrRestriction] public byte* VisibilityMasks;
            [NativeDisableUnsafePtrRestriction] public int* GroupConfigCounts;

            public int SplitCount;
            public bool WritePickingIds;
            public bool IsLightView;
            public uint CullingLayerMask;
            // Bin space is one slot per split mask, doubled while any owner
            // fades: the high bin half holds the fading instances. PerInstance
            // groups always carry the fade dimension (the crossfade pair needs
            // it) plus the level dimension above it:
            //   config = (level * 2 + fadeFlag) * splitBins + splitMask
            // PerOwner groups use the low region, which is the same
            // formula with level = 0.
            public byte FadeBit;
            public bool AnyPerInstanceLod;
            public NativeArray<BatchCullingOutputDrawCommands> CullingOutput;
            // SI.CountAndEmit, passed in rather than read from the static: a
            // Burst job cannot touch managed statics (the same rule the fade
            // curve and every other job-carried value follow).
            public Unity.Profiling.ProfilerMarker CountAndEmitMarker;

            // Stride between groups in GroupConfigCounts: the widest bin space
            // any group in this request needs. Each group only touches its own
            // GroupConfigCount(...) slots inside that stride.
            private int GroupConfigStride(int splitBins)
            {
                return AnyPerInstanceLod
                    ? splitBins * 2 * MaxLodLevels
                    : (FadeBit != 0 ? splitBins << 1 : splitBins);
            }

            private int GroupConfigCount(int splitBins, bool perInstance)
            {
                return perInstance
                    ? splitBins * 2 * MaxLodLevels
                    : (FadeBit != 0 ? splitBins << 1 : splitBins);
            }

            public void Execute()
            {
                using (CountAndEmitMarker.Auto())
                    Run();
            }

            private void Run()
            {
                int splitBins = 1 << SplitCount;
                int stride = GroupConfigStride(splitBins);
                int groupCount = DrawGroups.Length;
                int commandTotal = 0;
                int visibleTotal = 0;
                int* counts = stackalloc int[MaxConfigCount];
                for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
                {
                    CullingDrawGroup drawGroup = DrawGroups[groupIndex];
                    bool perInstance = OwnerPolicies[drawGroup.OwnerIndex] != 0;
                    // A PerOwner owner mid time-driven crossfade bins
                    // per-instance-style for this request, so the pair rides
                    // the same (level, fade, split) key SIS uses.
                    byte ownerK = OwnerCrossfadeK[drawGroup.OwnerIndex];
                    bool binPerInstance = perInstance || ownerK != 0;
                    int configCount = GroupConfigCount(splitBins, binPerInstance);
                    int baseIndex = groupIndex * stride;
                    if (!IsGroupEmitted(drawGroup, OwnerFlags[drawGroup.OwnerIndex], IsLightView, CullingLayerMask))
                    {
                        for (int c = 0; c < configCount; c++)
                            GroupConfigCounts[baseIndex + c] = 0;
                        continue;
                    }

                    for (int c = 0; c < configCount; c++)
                        counts[c] = 0;

                    int instanceEnd = drawGroup.InstanceOffset + drawGroup.InstanceCount;
                    {
                        int runStart = drawGroup.InstanceOffset;
                        int runEnd = instanceEnd;

                        if (!binPerInstance)
                        {
                            // Culled instances (mask 0) land in bin 0, which is never
                            // emitted — cheaper than a branch per instance.
                            for (int i = runStart; i < runEnd; i++)
                                counts[VisibilityMasks[drawGroup.InstanceBase + InstanceIndices[i]]]++;
                        }
                        else if (!perInstance)
                        {
                            int from = OwnerCrossfadeFrom[drawGroup.OwnerIndex];
                            int to = OwnerMeshLods[drawGroup.OwnerIndex];
                            // Shadow Cast Max Level, per copy (see ShadowCastEmits).
                            byte shadowMax = ShadowCastCull
                                ? OwnerShadowCastMaxLevel[drawGroup.OwnerIndex]
                                : (byte)0xFF;
                            bool emitFrom = ShadowCastEmits(from, shadowMax);
                            bool emitTo = ShadowCastEmits(to, shadowMax);
                            for (int i = runStart; i < runEnd; i++)
                            {
                                byte mask = VisibilityMasks[drawGroup.InstanceBase + InstanceIndices[i]];
                                if (mask == 0)
                                    continue;
                                int splitPart = mask & (splitBins - 1);
                                if ((mask & FadeBit) == 0 && emitFrom)
                                    counts[((from << 1) | 1) * splitBins + splitPart]++;
                                if (emitTo)
                                    counts[((to << 1) | 1) * splitBins + splitPart]++;
                            }
                        }
                        else
                        {
                            byte shadowMax = ShadowCastCull
                                ? OwnerShadowCastMaxLevel[drawGroup.OwnerIndex]
                                : (byte)0xFF;
                            for (int i = runStart; i < runEnd; i++)
                            {
                                int globalIndex = drawGroup.InstanceBase + InstanceIndices[i];
                                byte mask = VisibilityMasks[globalIndex];
                                if (mask == 0)
                                    continue;
                                int splitPart = mask & (splitBins - 1);
                                bool drawRangeFade = (mask & FadeBit) != 0;
                                byte lv = InstanceLevels[globalIndex];
                                int level = lv & 0x03;
                                if (!drawRangeFade && (lv & 0x80) != 0)
                                {
                                    // Crossfade pair: the instance is counted once
                                    // for the outgoing level and once for the
                                    // incoming one, both in fade bins so their
                                    // commands carry LOD_FADE_CROSSFADE.
                                    // Each half is tested (see ShadowCastEmits).
                                    // The partner rides bits 2-3 of the level byte.
                                    int partner = (lv >> 2) & 0x03;
                                    if (ShadowCastEmits(level, shadowMax))
                                        counts[((level << 1) | 1) * splitBins + splitPart]++;
                                    if (ShadowCastEmits(partner, shadowMax))
                                        counts[((partner << 1) | 1) * splitBins + splitPart]++;
                                }
                                else
                                {
                                    // A draw-range fade wins over the crossfade:
                                    // one copy at the current level with the
                                    // time-driven value.
                                    int fadeFlag = drawRangeFade ? 1 : 0;
                                    if (ShadowCastEmits(level, shadowMax))
                                        counts[((level << 1) | fadeFlag) * splitBins + splitPart]++;
                                }
                            }
                        }
                    }

                    GroupConfigCounts[baseIndex] = 0;
                    for (int c = 1; c < configCount; c++)
                    {
                        int count = counts[c];
                        GroupConfigCounts[baseIndex + c] = count;
                        if (count == 0)
                            continue;
                        commandTotal++;
                        visibleTotal += count;
                    }
                }

                BatchCullingOutputDrawCommands output = CullingOutput[0];
                output.drawCommandCount = commandTotal;
                output.visibleInstanceCount = visibleTotal;
                if (commandTotal == 0)
                {
                    output.drawRangeCount = 0;
                    CullingOutput[0] = output;
                    return;
                }

                output.drawCommands = MallocTempJob<BatchDrawCommand>(commandTotal);
                output.visibleInstances = MallocTempJob<int>(visibleTotal);
                EntityId* pickingIds = null;
                if (WritePickingIds)
                {
                    pickingIds = MallocTempJob<EntityId>(commandTotal);
                    output.drawCommandPickingEntityIds = pickingIds;
                }

                int commandIndex = 0;
                int visibleOffset = 0;
                int rangeIndex = 0;
                for (int segment = 0; segment < SegmentGroupStarts.Length; segment++)
                {
                    int groupStart = SegmentGroupStarts[segment];
                    int groupEnd = groupStart + SegmentGroupCounts[segment];
                    int ownerIndex = SegmentOwners[segment];
                    int rangeBegin = commandIndex;
                    for (int groupIndex = groupStart; groupIndex < groupEnd; groupIndex++)
                    {
                        CullingDrawGroup drawGroup = DrawGroups[groupIndex];
                        bool binPerInstance = OwnerPolicies[drawGroup.OwnerIndex] != 0 ||
                            OwnerCrossfadeK[drawGroup.OwnerIndex] != 0;
                        int configCount = GroupConfigCount(splitBins, binPerInstance);
                        int baseIndex = groupIndex * stride;
                        for (int config = 1; config < configCount; config++)
                        {
                            int slot = baseIndex + config;
                            int visibleCount = GroupConfigCounts[slot];
                            GroupConfigCounts[slot] = visibleOffset;
                            if (visibleCount == 0)
                                continue;

                            byte binLod = binPerInstance
                                ? (byte)(config / (splitBins * 2))
                                : OwnerMeshLods[drawGroup.OwnerIndex];
                            byte activeLod = binLod < drawGroup.MaxMeshLod
                                ? binLod
                                : drawGroup.MaxMeshLod;
                            bool fading = binPerInstance
                                ? ((config / splitBins) & 1) != 0
                                : config >= splitBins;
                            var command = new BatchDrawCommand
                            {
                                activeMeshLod = activeLod,
                                visibleOffset = (uint)visibleOffset,
                                visibleCount = (uint)visibleCount,
                                batchID = drawGroup.BatchID,
                                materialID = drawGroup.MaterialID,
                                meshID = drawGroup.MeshID,
                                submeshIndex = (ushort)drawGroup.SubMesh,
                                splitVisibilityMask = (ushort)(config & (splitBins - 1)),
                                flags = CommandFlags(drawGroup, fading),
                                sortingPosition = 0,
                            };

                            if (WritePickingIds)
                                pickingIds[commandIndex] = OwnerPickingIds[ownerIndex];
                            output.drawCommands[commandIndex++] = command;

                            visibleOffset += visibleCount;
                        }
                    }

                    if (commandIndex > rangeBegin)
                    {
                        output.drawRanges[rangeIndex++] = new BatchDrawRange
                        {
                            drawCommandsBegin = (uint)rangeBegin,
                            drawCommandsCount = (uint)(commandIndex - rangeBegin),
                            drawCommandsType = BatchDrawCommandType.Direct,
                            filterSettings = SegmentFilterSettings[segment],
                        };
                    }
                }

                // Coalescing means fewer commands were written than the count
                // stage reserved, so the emitted count is the truth. The
                // allocation stays an upper bound, which is correct and is
                // freed whole either way.
                output.drawCommandCount = commandIndex;
                output.drawRangeCount = rangeIndex;
                CullingOutput[0] = output;
            }

            // LODCrossFade turns on the LOD_FADE_CROSSFADE keyword for this
            // command and tells BRG the fade value is packed into the top 8 bits
            // of each visible instance index. No per-instance buffer and no GPU
            // upload: the index array is written every frame anyway. BRG carries
            // the flag into the shadow path too, so shadows fade with the mesh
            // instead of cutting.
            private static BatchDrawCommandFlags CommandFlags(
                in CullingDrawGroup drawGroup, bool fading)
            {
                BatchDrawCommandFlags flags = (drawGroup.Flags & CullingDrawGroup.FlipWindingFlag) != 0
                    ? BatchDrawCommandFlags.FlipWinding
                    : BatchDrawCommandFlags.None;
                // Build-time eligibility pairs HasMotion with valid previous-matrix
                // metadata for shader deformation on immutable instance transforms.
                if ((drawGroup.Flags & CullingDrawGroup.MotionVectorsFlag) != 0)
                    flags |= BatchDrawCommandFlags.HasMotion;
                return fading ? flags | BatchDrawCommandFlags.LODCrossFade : flags;
            }
        }

        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private unsafe struct WriteVisibleInstancesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<CullingDrawGroup> DrawGroups;
            [ReadOnly] public NativeArray<int> InstanceIndices;
            [NativeDisableUnsafePtrRestriction] public byte* VisibilityMasks;
            [ReadOnly] public NativeArray<float> InstanceFades;
            [ReadOnly] public NativeArray<byte> InstanceLevels;
            [ReadOnly] public NativeArray<byte> CrossfadeValues;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerFlags;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerPolicies;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerMeshLods;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerShadowCastMaxLevel;
            public bool ShadowCastCull;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerCrossfadeFrom;
            [NativeDisableUnsafePtrRestriction] public byte* OwnerCrossfadeK;
            [NativeDisableUnsafePtrRestriction] public int* GroupConfigOffsets;
            [ReadOnly] public NativeArray<BatchCullingOutputDrawCommands> CullingOutput;

            public int SplitCount;
            public bool IsLightView;
            public uint CullingLayerMask;
            public byte FadeBit;
            public bool AnyPerInstanceLod;

            // 128-entry remap of the already-quantized 0..127 dither value, or
            // null for Linear, which is the default and makes the feature
            // byte-identical to not having it. A table rather than a math.sin
            // call because this job runs per culling request, so a
            // transcendental would cost per view per in-band output instance;
            // the input is already quantized, so the table replaces the curve
            // exactly rather than approximating it.
            [NativeDisableUnsafePtrRestriction] public byte* FadeCurveLut;

            private int Shape(int value)
            {
                return FadeCurveLut != null ? FadeCurveLut[value] : value;
            }

            public void Execute(int groupIndex)
            {
                // visibleInstances is allocated by AllocateOutputJob, so the pointer
                // must be read here inside Execute, never captured at schedule time.
                int* visibleInstances = CullingOutput[0].visibleInstances;
                if (visibleInstances == null)
                    return;

                CullingDrawGroup drawGroup = DrawGroups[groupIndex];
                if (!IsGroupEmitted(drawGroup, OwnerFlags[drawGroup.OwnerIndex], IsLightView, CullingLayerMask))
                    return;

                // One cursor per bin: each instance is written exactly once
                // (crossfading instances exactly twice, once per pair half),
                // into the command for its own (level, fading, split mask)
                // combination. The stride/config math must mirror
                // CountAndAllocateJob exactly.
                int splitBins = 1 << SplitCount;
                bool perInstance = OwnerPolicies[drawGroup.OwnerIndex] != 0;
                byte ownerK = OwnerCrossfadeK[drawGroup.OwnerIndex];
                bool binPerInstance = perInstance || ownerK != 0;
                int stride = AnyPerInstanceLod
                    ? splitBins * 2 * MaxLodLevels
                    : (FadeBit != 0 ? splitBins << 1 : splitBins);
                int configCount = binPerInstance
                    ? splitBins * 2 * MaxLodLevels
                    : (FadeBit != 0 ? splitBins << 1 : splitBins);
                int* cursors = stackalloc int[MaxConfigCount];
                int baseIndex = groupIndex * stride;
                for (int c = 1; c < configCount; c++)
                    cursors[c] = GroupConfigOffsets[baseIndex + c];

                int ownerFrom = OwnerCrossfadeFrom[drawGroup.OwnerIndex];
                int ownerTo = OwnerMeshLods[drawGroup.OwnerIndex];
                // Per copy: the same ShadowCastEmits the count half calls, on
                // the same inputs, which keeps the two predicates identical.
                byte shadowMax = ShadowCastCull
                    ? OwnerShadowCastMaxLevel[drawGroup.OwnerIndex]
                    : (byte)0xFF;
                int instanceEnd = drawGroup.InstanceOffset + drawGroup.InstanceCount;
                for (int i = drawGroup.InstanceOffset; i < instanceEnd; i++)
                {
                    int localIndexInOwner = InstanceIndices[i];
                    int localInstanceIndex = localIndexInOwner;
                    int globalIndex = drawGroup.InstanceBase + localIndexInOwner;
                    byte mask = VisibilityMasks[globalIndex];
                    if (mask == 0)
                        continue;

                    // Fading instances land in a command that carries the
                    // crossfade flag, so their index is packed: low 24 bits are
                    // the index, the top 8 are the SNORM fade the shader reads
                    // back as unity_LODFade (UnityDOTSInstancing.hlsl:327 sign
                    // extends it, so the value must fit a signed byte).
                    if (!binPerInstance)
                    {
                        int value = (mask & FadeBit) != 0
                            ? (localInstanceIndex & 0x00FFFFFF) |
                              (Shape((int)InstanceFades[globalIndex]) << 24)
                            : localInstanceIndex;
                        visibleInstances[cursors[mask]++] = value;
                        continue;
                    }

                    int splitPart = mask & (splitBins - 1);
                    bool drawRangeFade = (mask & FadeBit) != 0;
                    if (!perInstance)
                    {
                        // Owner-level pair, mirroring CountAndAllocateJob: +k
                        // keeps k/127 of the outgoing level's pixels, the
                        // sign-flipped copy the complement for the current
                        // level; a draw-range fade wins per instance.
                        int packedIndex = localInstanceIndex & 0x00FFFFFF;
                        if (drawRangeFade)
                        {
                            if (ShadowCastEmits(ownerTo, shadowMax))
                                visibleInstances[
                                    cursors[((ownerTo << 1) | 1) * splitBins + splitPart]++] =
                                    packedIndex | (Shape((int)InstanceFades[globalIndex]) << 24);
                        }
                        else
                        {
                            // Complementary-pair rule: shape ONCE and
                            // derive the complement from the shaped value.
                            // Running the curve over each half independently
                            // breaks shaped(k) + shaped(256 - k) = 256 and
                            // leaves double-draw or holes inside the band.
                            int shapedK = Shape(ownerK);
                            if (ShadowCastEmits(ownerFrom, shadowMax))
                                visibleInstances[
                                    cursors[((ownerFrom << 1) | 1) * splitBins + splitPart]++] =
                                    packedIndex | (shapedK << 24);
                            if (ShadowCastEmits(ownerTo, shadowMax))
                                visibleInstances[
                                    cursors[((ownerTo << 1) | 1) * splitBins + splitPart]++] =
                                    packedIndex | (((0x100 - shapedK) & 0xFF) << 24);
                        }

                        continue;
                    }
                    byte lv = InstanceLevels[globalIndex];
                    int level = lv & 0x03;
                    if (!drawRangeFade && (lv & 0x80) != 0)
                    {
                        // Complementary pair: +k keeps k/127 of the outgoing
                        // level's pixels, the sign-flipped copy keeps the
                        // complement for the incoming level, so together they
                        // cover the surface exactly once.
                        int k = Shape(CrossfadeValues[globalIndex]);
                        int packed = localInstanceIndex & 0x00FFFFFF;
                        // `partner` is carried in bits 2-3 rather than
                        // assumed to be `level + 1`, so a time fade between
                        // non-adjacent levels emits the same complementary pair.
                        int partner = (lv >> 2) & 0x03;
                        if (ShadowCastEmits(level, shadowMax))
                            visibleInstances[cursors[((level << 1) | 1) * splitBins + splitPart]++] =
                                packed | (k << 24);
                        if (ShadowCastEmits(partner, shadowMax))
                            visibleInstances[cursors[((partner << 1) | 1) * splitBins + splitPart]++] =
                                packed | (((0x100 - k) & 0xFF) << 24);
                    }
                    else
                    {
                        int fadeFlag = drawRangeFade ? 1 : 0;
                        int value = drawRangeFade
                            ? (localInstanceIndex & 0x00FFFFFF) |
                              (Shape((int)InstanceFades[globalIndex]) << 24)
                            : localInstanceIndex;
                        if (ShadowCastEmits(level, shadowMax))
                            visibleInstances[cursors[((level << 1) | fadeFlag) * splitBins + splitPart]++] =
                                value;
                    }
                }
            }
        }

        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private unsafe struct FreeScratchBufferJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public void* Buffer;

            public void Execute()
            {
                UnsafeUtility.Free(Buffer, Allocator.TempJob);
            }
        }

        // Perspective camera-type views drop the near/far planes (indices 4/5
        // of the standard frustum order) before packing: the four side planes
        // of a perspective frustum meet at the eye and already reject
        // everything behind the camera, and geometry beyond the far plane is
        // clipped by the GPU — the cull distance is the intended substitute
        // there. Light views keep every plane (their far plane is the light
        // range) and so do orthographic views (parallel side planes do not
        // reject anything behind the camera). The identification is guarded:
        // exactly the engine's whole-frustum split layout, and near/far must
        // be antiparallel.
        private static bool ShouldSkipNearFarPlanes(in BatchCullingContext cullingContext)
        {
            if (cullingContext.viewType == BatchCullingViewType.Light)
                return false;
            if (cullingContext.projectionType != BatchCullingProjectionType.Perspective)
                return false;

            NativeArray<Plane> planes = cullingContext.cullingPlanes;
            if (planes.Length < 6)
                return false;
            if (cullingContext.cullingSplits.Length > 1)
                return false;
            if (cullingContext.cullingSplits.Length == 1)
            {
                CullingSplit split = cullingContext.cullingSplits[0];
                if (split.cullingPlaneOffset != 0 || split.cullingPlaneCount != planes.Length)
                    return false;
            }

            return Vector3.Dot(planes[4].normal, planes[5].normal) < -0.99f;
        }

        private static unsafe CullingScratchBuffer AllocateScratchBuffer(
            in BatchCullingContext cullingContext,
            int splitCount,
            bool anyOwnerCrossfade,
            // The shadow-cast region is a light-view cost, and a camera view
            // must pay nothing in either state, so the allocator has to know
            // which kind of view it is sizing for.
            bool isLightView)
        {
            int ownerCount = Owners.Count;
            int planeCount = cullingContext.cullingPlanes.Length;
            // The bin space doubles while anything fades: fading instances need
            // their own commands, so the counting array has to hold both halves
            // up front (the culling path never reallocates after initialization).
            // With a PerInstance owner registered, or a PerOwner owner mid
            // time-driven crossfade this request, it widens further to the full
            // (level, fade, split) key; this is the stride, and PerOwner groups
            // only use their low region inside it.
            int configCount = anyPerInstanceLod || anyOwnerCrossfade
                ? (1 << splitCount) * 2 * MaxLodLevels
                : (1 << splitCount) << (anyDistanceCull ? 1 : 0);

            NativeArray<Plane> planes = cullingContext.cullingPlanes;
            bool skipNearFar = ShouldSkipNearFarPlanes(cullingContext);
            if (skipNearFar)
            {
                var filtered = new NativeArray<Plane>(
                    planeCount - 2, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < 4; i++)
                    filtered[i] = planes[i];
                for (int i = 6; i < planeCount; i++)
                    filtered[i - 2] = planes[i];
                planes = filtered;
                planeCount -= 2;
            }

            // The plane region holds PlanePacket4s; each split rounds up to
            // whole packets (the last packet repeats the split's final
            // plane), so the total is summed per split. A near/far-skipped
            // view is always the whole-frustum single-split layout, so it
            // takes the splitless path below.
            bool useSplits = cullingContext.cullingSplits.Length > 0 && !skipNearFar;
            int totalPacketCount = 0;
            if (useSplits)
            {
                for (int i = 0; i < splitCount; i++)
                    totalPacketCount += (cullingContext.cullingSplits[i].cullingPlaneCount + 3) / 4;
            }
            else
            {
                totalPacketCount = (planeCount + 3) / 4;
            }

            long planeBytes = UnsafeUtility.SizeOf<PlanePacket4>() * (long)totalPacketCount;
            long splitBytes = UnsafeUtility.SizeOf<SplitPlaneRange>() * (long)splitCount;
            long pickingBytes = UnsafeUtility.SizeOf<EntityId>() * (long)ownerCount;
            long countBytes = sizeof(int) * (long)(combinedDrawGroups.Length * configCount);
            int clusterCount = clusterOwners.Length;
            long flagBytes = ownerCount;
            long lodBytes = ownerCount;
            long policyBytes = ownerCount;
            long intersectBytes = clusterCount;
            long insideBytes = clusterCount;
            long maskBytes = combinedBounds.Length;
            long crossfadeBytes = ownerCount * 2L;
            // `isLightView` as well as the flag: a camera view must pay nothing
            // in either state, and without this the region enlarges the
            // per-request malloc for every view. Every read is behind
            // ShadowCastCull, which carries isLightView too.
            long shadowBytes = anyShadowCastCull && isLightView ? ownerCount : 0L;
            byte* buffer = (byte*)UnsafeUtility.Malloc(
                planeBytes + splitBytes + pickingBytes + countBytes +
                    flagBytes + lodBytes + policyBytes + intersectBytes + insideBytes +
                    maskBytes + crossfadeBytes + shadowBytes,
                16,
                Allocator.TempJob);

            long byteArrayBase = planeBytes + splitBytes + pickingBytes + countBytes;
            var scratch = new CullingScratchBuffer
            {
                Buffer = buffer,
                PlanePackets = (PlanePacket4*)buffer,
                Splits = (SplitPlaneRange*)(buffer + planeBytes),
                OwnerPickingIds = (EntityId*)(buffer + planeBytes + splitBytes),
                GroupConfigCounts = (int*)(buffer + planeBytes + splitBytes + pickingBytes),
                OwnerFlags = buffer + byteArrayBase,
                OwnerMeshLods = buffer + byteArrayBase + flagBytes,
                OwnerPolicies = buffer + byteArrayBase + flagBytes + lodBytes,
                ClusterIntersectMasks =
                    buffer + byteArrayBase + flagBytes + lodBytes + policyBytes,
                ClusterInsideMasks =
                    buffer + byteArrayBase + flagBytes + lodBytes + policyBytes + intersectBytes,
                VisibilityMasks = buffer + byteArrayBase + flagBytes + lodBytes +
                    policyBytes + intersectBytes + insideBytes,
                OwnerCrossfadeFrom = buffer + byteArrayBase + flagBytes + lodBytes +
                    policyBytes + intersectBytes + insideBytes + maskBytes,
                OwnerCrossfadeK = buffer + byteArrayBase + flagBytes + lodBytes +
                    policyBytes + intersectBytes + insideBytes + maskBytes + ownerCount,
                OwnerShadowCastMaxLevel = buffer + byteArrayBase + flagBytes + lodBytes +
                    policyBytes + intersectBytes + insideBytes + maskBytes + crossfadeBytes,
            };

            if (useSplits)
            {
                int packetBase = 0;
                for (int i = 0; i < splitCount; i++)
                {
                    CullingSplit split = cullingContext.cullingSplits[i];
                    int packetCount = (split.cullingPlaneCount + 3) / 4;
                    int limit = split.cullingPlaneOffset + split.cullingPlaneCount - 1;
                    for (int p = 0; p < packetCount; p++)
                    {
                        scratch.PlanePackets[packetBase + p] = new PlanePacket4(
                            planes, split.cullingPlaneOffset + p * 4, limit);
                    }

                    scratch.Splits[i] = new SplitPlaneRange
                    {
                        PlaneOffset = packetBase,
                        PlaneCount = packetCount,
                    };
                    packetBase += packetCount;
                }
            }
            else
            {
                for (int p = 0; p < totalPacketCount; p++)
                    scratch.PlanePackets[p] = new PlanePacket4(planes, p * 4, planeCount - 1);
                scratch.Splits[0] = new SplitPlaneRange
                {
                    PlaneOffset = 0,
                    PlaneCount = totalPacketCount,
                };
            }

            if (skipNearFar)
                planes.Dispose();

            return scratch;
        }

        // -- Receiver-volume driver ------------------------------------------------

        private static bool ShouldReceiverSphereCull(in BatchCullingContext cullingContext)
        {
            return StaticInstanceLodSystem.ReceiverSphereCullEnabled &&
                anyPerInstanceLod &&
                cullingContext.viewType == BatchCullingViewType.Light &&
                HasReceiverSpheres(cullingContext);
        }

        // Builds the per-view constants (the light-facing frustum planes that
        // bound how far a shadow can usefully travel, and each cascade's
        // receiver sphere pre-rotated into light space); the cluster masks are
        // then narrowed with them.
        // Building the receiver-volume constants and applying them are
        // separately gated, because the consumers have different conditions;
        // fusing build and apply would let a change to one gate silently
        // re-gate the others.
        //
        // Returns false only when there is nothing to build. It deliberately
        // does not apply the fixed-capacity cap: that cap belongs to the job
        // struct, which is the per-instance consumer's concern alone. On
        // overflow the fall-back is to skip the per-instance test for that
        // view; returning false here would also stop the cluster pass, which
        // has no capacity limit and keeps running on these unbounded Temp
        // arrays.
        //
        // The caller owns `planes` and `spheres` and disposes both.
        private static unsafe bool BuildReceiverVolume(
            in BatchCullingContext cullingContext,
            out NativeArray<Plane> planes,
            out int lightFacingCount,
            out NativeArray<ReceiverSplitSphere> spheres,
            out float3x3 worldToLight)
        {
            spheres = default;
            worldToLight = default;

            planes = BuildReceiverVolumePlanes(cullingContext, out lightFacingCount);
            if (!planes.IsCreated || planes.Length == 0)
                return false;

            int splitCount = cullingContext.cullingSplits.Length;
            worldToLight = math.transpose(
                (float3x3)(float4x4)cullingContext.localToWorldMatrix);
            spheres = new NativeArray<ReceiverSplitSphere>(
                splitCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < splitCount; i++)
            {
                CullingSplit split = cullingContext.cullingSplits[i];
                spheres[i] = new ReceiverSplitSphere
                {
                    SphereLightSpace = new float4(
                        math.mul(worldToLight, (float3)split.sphereCenter),
                        split.sphereRadius),
                    CascadeBlendCullingFactor = split.cascadeBlendCullingFactor,
                };
            }

            return true;
        }

        // Packs the built volume into the by-value struct the mask job carries.
        // This is where the capacity cap and fall-back live, and they reach the
        // per-instance test only: false here leaves the cluster pass running.
        // Skipping keeps more splits, which is the safe direction.
        private static unsafe bool TryPackReceiverConstants(
            in NativeArray<Plane> planes,
            int lightFacingCount,
            in NativeArray<ReceiverSplitSphere> spheres,
            in float3x3 worldToLight,
            out ReceiverVolumeConstants constants)
        {
            constants = default;
            if (lightFacingCount > ReceiverVolumeConstants.Capacity ||
                spheres.Length > ReceiverVolumeConstants.Capacity)
            {
                StaticInstanceLodSystem.ReceiverVolumeCapacityFallbacks++;
                return false;
            }

            ReceiverVolumeConstants result = default;
            result.WorldToLight = worldToLight;
            // The direction a shadow travels, in world space: the light-space
            // +z axis brought back through the inverse rotation. Derived once
            // per view here rather than once per instance in the job.
            result.ShadowDirection = math.transpose(worldToLight).c2;
            result.PlaneCount = lightFacingCount;
            result.SplitCount = spheres.Length;

            // `result` is a local, so its fixed buffers are already fixed —
            // a `fixed` statement over them is a compile error, not a nicety.
            var planeDst = (Plane*)result.PlaneBytes;
            for (int i = 0; i < lightFacingCount; i++)
                planeDst[i] = planes[i];

            var sphereDst = (ReceiverSplitSphere*)result.SphereBytes;
            for (int i = 0; i < spheres.Length; i++)
                sphereDst[i] = spheres[i];

            constants = result;
            return true;
        }

        // The cluster pass, applied from an already-built volume. It takes the
        // uncapped arrays; see BuildReceiverVolume.
        private static unsafe void RunReceiverSphereClusterPass(
            in NativeArray<Plane> planes,
            int lightFacingCount,
            in NativeArray<ReceiverSplitSphere> spheres,
            float3x3 worldToLight,
            in CullingScratchBuffer scratch,
            int clusterCount)
        {
            var counters = stackalloc long[7];
            for (int i = 0; i < 7; i++)
                counters[i] = 0;
            StaticInstanceCullingBurst.ApplyReceiverSphereMask(
                (CullingBounds*)clusterAabbs.GetUnsafeReadOnlyPtr(),
                scratch.OwnerFlags,
                (int*)clusterOwners.GetUnsafeReadOnlyPtr(),
                scratch.ClusterIntersectMasks,
                scratch.ClusterInsideMasks,
                (int*)clusterInstanceCounts.GetUnsafeReadOnlyPtr(),
                (Plane*)planes.GetUnsafeReadOnlyPtr(),
                lightFacingCount,
                (ReceiverSplitSphere*)spheres.GetUnsafeReadOnlyPtr(),
                spheres.Length,
                &worldToLight,
                counters,
                clusterCount);

            AccumulateReceiverCounters(counters);
        }

        private static unsafe void AccumulateReceiverCounters(long* counters)
        {
            StaticInstanceLodSystem.ReceiverReachingClusters += counters[0];
            StaticInstanceLodSystem.ReceiverRejectedClusters += counters[1];
            StaticInstanceLodSystem.ReceiverRejectedInstances += counters[2];
            StaticInstanceLodSystem.ReceiverSphereRejectedClusters += counters[3];
            StaticInstanceLodSystem.ReceiverSphereRejectedInstances += counters[4];
            StaticInstanceLodSystem.ReceiverSphereSplitWorkBefore += counters[5];
            StaticInstanceLodSystem.ReceiverSphereSplitWorkAfter += counters[6];
        }

        // -- The shadow-receiver volume's planes -----------------------------------

        // URP's own precondition for building a receiver-sphere culler, and
        // therefore the condition under which it treats the receiver planes as
        // already folded into the split culling planes: more than one split,
        // and every split carrying a valid sphere. In practice this holds for
        // the directional cascade view and nothing else: additional-light views
        // report `sphereRadius = -Infinity`.
        private static bool HasReceiverSpheres(in BatchCullingContext cullingContext)
        {
            int splitCount = cullingContext.cullingSplits.Length;
            if (splitCount <= 1)
                return false;
            for (int i = 0; i < splitCount; i++)
            {
                // Written so that a NaN radius is not valid.
                if (!(cullingContext.cullingSplits[i].sphereRadius > 0f))
                    return false;
            }

            return true;
        }

        // Builds the planes bounding the region a shadow can land in, from the
        // receiver planes a light view is handed (`receiverPlaneOffset/Count`,
        // the camera frustum expressed for this light). A caster outside this
        // volume cannot cast into anything the camera sees, so it can be
        // rejected before any per-instance work. The split packets come from
        // each split's own `cullingPlaneOffset/Count` range, and the receiver
        // planes sit outside every split's range, so they are not otherwise
        // used.
        //
        // Only the light-facing subset is built: the input planes whose front
        // side the light is on. The others cannot bound the volume, since
        // geometry behind them is between the light and the camera frustum and
        // still casts in. The silhouette planes that would close the volume are
        // not built, as nothing consumes them.
        //
        // Adapted from `ReceiverPlanes.Create` in com.unity.render-pipelines.core's
        // Runtime/GPUDriven/Utilities/FrustumPlanes.cs (the GPU Resident Drawer's
        // own culler); the API hands us the raw planes and no public helper
        // turns them into this volume.
        //
        // Returns Temp-allocated planes; the caller disposes. Empty for
        // non-light views, and for any view supplying no receiver planes.
        private static NativeArray<Plane> BuildReceiverVolumePlanes(
            in BatchCullingContext cullingContext,
            out int lightFacingCount)
        {
            lightFacingCount = 0;
            int count = cullingContext.receiverPlaneCount;
            if (cullingContext.viewType != BatchCullingViewType.Light || count == 0)
                return default;

            // Orthographic light ⇒ the sweep direction is constant; punctual
            // ⇒ it converges on the light position. Read from the split's
            // culling matrix exactly as URP does, rather than from the light,
            // because this callback never sees the light.
            bool isOrthographic = false;
            if (cullingContext.cullingSplits.Length > 0)
            {
                Matrix4x4 m = cullingContext.cullingSplits[0].cullingMatrix;
                isOrthographic = m[15] == 1f && m[11] == 0f && m[7] == 0f && m[3] == 0f;
            }

            Vector3 lightDir = -(Vector3)cullingContext.localToWorldMatrix.GetColumn(2);
            Vector3 lightPos = cullingContext.localToWorldMatrix.GetPosition();

            // Worst case: every input plane faces the light.
            var result = new NativeArray<Plane>(
                count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                Plane plane = cullingContext.cullingPlanes[cullingContext.receiverPlaneOffset + i];
                float facing = isOrthographic
                    ? Vector3.Dot(plane.normal, lightDir)
                    : plane.GetDistanceToPoint(lightPos);
                // Sign bit rather than `< 0`, so a negative zero counts as
                // facing away — the same tie-break URP takes, and the
                // conservative direction here is to add fewer planes.
                if ((math.asuint(facing) >> 31) == 0)
                    result[written++] = plane;
            }

            lightFacingCount = written;
            return result.GetSubArray(0, written);
        }

        private static unsafe JobHandle OnPerformCulling(
            BatchRendererGroup group,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            System.IntPtr userContext)
        {
            using var callbackScope = CullingCallbackMarker.Auto();
            var output =
                (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();
            *output = default;

            if (Owners.Count == 0)
                return default;

            // Changing the chunking mask restructures the cluster arrays, which
            // only exist at rebuild time.
            if (lastChunkingEnabled != StaticInstanceLodSystem.CullingChunkingEnabled ||
                lastSizeCullDistanceScale !=
                    Mathf.Max(0f, StaticInstanceLodSystem.SizeCullDistanceScale))
            {
                combinedDataDirty = true;
            }

            RefreshDrivingSlices();
            if (combinedDataDirty)
                RebuildCombinedData();
            if (combinedBounds.Length == 0)
                return default;

            // Layer-union early-return. The union is rebuild-time data, so this
            // must sit after the rebuild above. It is placed ahead of the
            // per-owner IsOwnerRendered sweep because it subsumes it: no group
            // can be emitted (IsGroupEmitted's layer test), so no owner can
            // contribute either. The last observed masks are recorded per view
            // type for diagnostics.
            uint cullingLayerMask = cullingContext.cullingLayerMask;
            bool isLightView = cullingContext.viewType == BatchCullingViewType.Light;
            if (isLightView)
                StaticInstanceLodSystem.CullingLayerMaskLastLight = cullingLayerMask;
            else
                StaticInstanceLodSystem.CullingLayerMaskLastCamera = cullingLayerMask;
            if (StaticInstanceLodSystem.DiagnoseViewIds > 0)
            {
                StaticInstanceLodSystem.DiagnoseViewIds--;
                StaticInstanceLog.Info(
                    $"[SI-VIEWID] type={cullingContext.viewType} " +
#if UNITY_6000_4_OR_NEWER
                    // 6000.4 dropped the slice index (GetSliceIndex returns 0).
                    $"id={cullingContext.viewID.GetEntityId()} " +
#else
                    $"id={cullingContext.viewID.GetInstanceID()} " +
                    $"slice={cullingContext.viewID.GetSliceIndex()} " +
#endif
                    $"splits={cullingContext.cullingSplits.Length} " +
                    $"camPos={(float3)cullingContext.lodParameters.cameraPosition}");
            }

            if ((cullingLayerMask & combinedLayerMask) == 0)
            {
                StaticInstanceLodSystem.CullingLayerSkippedViews++;
                return default;
            }

            bool isPickingView = false;
#if UNITY_EDITOR
            isPickingView = cullingContext.viewType == BatchCullingViewType.Picking;
            bool isOutlineView = cullingContext.viewType == BatchCullingViewType.SelectionOutline;
            // SelectionOutline must draw only the selected owners; picking
            // rounds every instance to its owner root, so compare against the
            // selection cache here on the main thread (Selection.entityIds
            // itself allocates a fresh array on every read).
            EntityId[] selection = isOutlineView ? cachedSelection : null;
#endif

            // Which camera's evaluation this view reads. Resolved once
            // per culling request — never per owner or per instance — so at
            // K=1 it is one compare and everything downstream indexes 0.
            int viewSlice = GetSliceForView(cullingContext, isLightView);

            bool anyRendered = false;
            for (int i = 0; i < Owners.Count; i++)
            {
                if (IsOwnerRendered(Owners[i], isLightView, viewSlice
#if UNITY_EDITOR
                    , isOutlineView, selection
#endif
                    ))
                {
                    anyRendered = true;
                    break;
                }
            }

            if (!anyRendered)
                return default;

            int splitCount = Mathf.Clamp(cullingContext.cullingSplits.Length, 1, MaxSplitCount);
            int ownerCount = Owners.Count;
            int segmentCount = segmentGroupStarts.Length;

            // One draw range per segment is allocated up front; drawCommands
            // and visibleInstances are allocated exact-size inside CountAndAllocateJob.
            output->drawRanges = MallocTempJob<BatchDrawRange>(segmentCount);

            // A PerOwner owner mid time-driven crossfade needs the wide
            // (level, fade, split) bin space this request. A global force
            // snaps every fade (the pair would fight the forced level).
            int globalForced = StaticInstanceLodSystem.ForcedMeshLod;
            bool anyOwnerCrossfade = false;
            if (globalForced < 0)
            {
                for (int i = 0; i < ownerCount; i++)
                    anyOwnerCrossfade |= Owners[i].MeshLodCrossfadeKs[viewSlice] != 0;
            }

            // The cluster consumer is gated on LOD. The instance receiver test
            // also applies to ordinary no-LOD/no-draw-range shadow views;
            // split-plane inclusion alone cannot establish that a caster
            // reaches a cascade's receiver sphere.
            bool clusterSphereCull =
                ShouldReceiverSphereCull(cullingContext) &&
                StaticInstanceLodSystem.ReceiverSphereClusterCullEnabled;
            bool instanceSphereCull =
                StaticInstanceLodSystem.ReceiverSphereCullEnabled &&
                StaticInstanceLodSystem.ReceiverSphereInstanceCullEnabled &&
                cullingContext.viewType == BatchCullingViewType.Light &&
                HasReceiverSpheres(cullingContext);

            CullingScratchBuffer scratch =
                AllocateScratchBuffer(
                    cullingContext, splitCount, anyOwnerCrossfade, isLightView);
            for (int i = 0; i < ownerCount; i++)
            {
                StaticInstanceOwner owner = Owners[i];
                bool rendered = IsOwnerRendered(owner, isLightView, viewSlice
#if UNITY_EDITOR
                    , isOutlineView, selection
#endif
                    );
                scratch.OwnerFlags[i] = rendered ? (byte)1 : (byte)0;
                scratch.OwnerPickingIds[i] = owner.PickingEntityId;
                scratch.OwnerMeshLods[i] = globalForced >= 0
                    ? (byte)(globalForced > 255 ? 255 : globalForced)
                    : owner.MeshLodLevels[viewSlice];
                scratch.OwnerPolicies[i] = (byte)owner.LodPolicy;
                // 0xFF stands for "-1, every level casts", so the job
                // side compares against a byte and never sees a sign.
                if (anyShadowCastCull && isLightView)
                {
                    scratch.OwnerShadowCastMaxLevel[i] = owner.ShadowCastMaxLevel >= 0
                        ? (byte)owner.ShadowCastMaxLevel
                        : (byte)0xFF;
                }
                scratch.OwnerCrossfadeFrom[i] = owner.MeshLodCrossfadeFroms[viewSlice];
                scratch.OwnerCrossfadeK[i] = globalForced < 0
                    ? owner.MeshLodCrossfadeKs[viewSlice]
                    : (byte)0;
            }

            // Cluster AABB vs the same plane packets the instances will use,
            // done on the main thread rather than in its own job: the work is
            // per cluster, not per instance, and the job chain's schedule cost
            // is the dominant main-thread term, so an extra job would cost more
            // than these tests do. Burst direct call, because managed float4
            // math here is too slow.
            int clusterCount = clusterOwners.Length;
            int* clusterSizing = stackalloc int[3];
            StaticInstanceCullingBurst.ClassifyClusters(
                (CullingBounds*)clusterAabbs.GetUnsafeReadOnlyPtr(),
                scratch.PlanePackets,
                scratch.Splits,
                scratch.OwnerFlags,
                (int*)clusterOwners.GetUnsafeReadOnlyPtr(),
                scratch.ClusterIntersectMasks,
                scratch.ClusterInsideMasks,
                clusterSizing,
                (int*)clusterInstanceCounts.GetUnsafeReadOnlyPtr(),
                clusterCount,
                splitCount);
            StaticInstanceLodSystem.CullingClassifiedClusters += clusterSizing[0];
            StaticInstanceLodSystem.CullingOutsideClusters += clusterSizing[1];
            StaticInstanceLodSystem.CullingOutsideInstances += clusterSizing[2];

            // Narrow the split masks by the receiver-sphere test before
            // anything downstream reads them.
            //
            // The build is gated on the union of both consumers' effective
            // conditions: the light-facing planes, the split spheres and
            // worldToLight are built in one place only, so gating the build on
            // one consumer would starve the other (for example cluster switch
            // on with the per-instance switch off).
            ReceiverVolumeConstants receiverConstants = default;
            bool haveReceiverConstants = false;
            if (clusterSphereCull || instanceSphereCull)
            {
                if (BuildReceiverVolume(
                        cullingContext, out NativeArray<Plane> receiverPlanes,
                        out int lightFacingCount,
                        out NativeArray<ReceiverSplitSphere> receiverSpheres,
                        out float3x3 receiverWorldToLight))
                {
                    // The cluster pass runs on the uncapped arrays, so the
                    // per-instance capacity cap below cannot switch it off; a
                    // capacity fall-back is the per-instance consumer's alone.
                    if (clusterSphereCull)
                    {
                        RunReceiverSphereClusterPass(
                            receiverPlanes, lightFacingCount, receiverSpheres,
                            receiverWorldToLight, scratch, clusterCount);
                    }

                    if (instanceSphereCull)
                    {
                        haveReceiverConstants = TryPackReceiverConstants(
                            receiverPlanes, lightFacingCount, receiverSpheres,
                            receiverWorldToLight, out receiverConstants);
                    }

                    if (receiverSpheres.IsCreated)
                        receiverSpheres.Dispose();
                    receiverPlanes.Dispose();
                }
            }

            if (StaticInstanceLodSystem.CullingFastPathEnabled &&
                // Fully-inside split masks still need the instance receiver test.
                // Unavailable constants retain the conservative fast-path fallback.
                !haveReceiverConstants &&
                !anyDistanceCull && !anyPerInstanceLod && !anyOwnerCrossfade &&
                !isPickingView
#if UNITY_EDITOR
                && !isOutlineView
#endif
                )
            {
                // Under the eligibility gate every owner is PerOwner (no
                // PerInstance owner registered), so clusters and owners are
                // the same list in the same order — the emission may index
                // the cluster masks by drawGroup.OwnerIndex. The equality
                // check makes that invariant explicit rather than trusted.
                StaticInstanceLodSystem.CullingFastPathEligibleViews++;
                int straddleOwner = clusterCount == ownerCount
                    ? StaticInstanceCullingBurst.TryEmitFullyResolved(
                        (CullingDrawGroup*)combinedDrawGroups.GetUnsafeReadOnlyPtr(),
                        (int*)combinedInstanceIndices.GetUnsafeReadOnlyPtr(),
                        (int*)segmentGroupStarts.GetUnsafeReadOnlyPtr(),
                        (int*)segmentGroupCounts.GetUnsafeReadOnlyPtr(),
                        (BatchFilterSettings*)segmentFilterSettings.GetUnsafeReadOnlyPtr(),
                        segmentCount,
                        scratch.OwnerFlags,
                        scratch.ClusterIntersectMasks,
                        scratch.ClusterInsideMasks,
                        scratch.OwnerMeshLods,
                        ownerCount,
                        isLightView ? (byte)1 : (byte)0,
                        cullingLayerMask,
                        output)
                    : 0;
                if (straddleOwner < 0)
                {
                    StaticInstanceLodSystem.CullingFastPathViews++;
                    UnsafeUtility.Free(scratch.Buffer, Allocator.TempJob);
                    return default;
                }

                StaticInstanceLodSystem.CullingFastPathLastStraddleOwner = straddleOwner;
                StaticInstanceLodSystem.CullingFastPathLastStraddleMasks =
                    (scratch.ClusterIntersectMasks[straddleOwner] << 8) |
                    scratch.ClusterInsideMasks[straddleOwner];
            }

            var freeScratchJob = new FreeScratchBufferJob
            {
                Buffer = scratch.Buffer,
            };

            // splitCount is clamped to 6 by BRG, so the fade bit never collides
            // with a split bit inside the byte mask.
            byte fadeBit = anyDistanceCull ? (byte)(1 << splitCount) : (byte)0;
            // Which camera's evaluation this view reads. Resolved once per
            // culling request, never per instance; at K=1 everything indexes 0.
            NativeArray<float> viewFades = instanceFadeSlices[viewSlice];
            NativeArray<byte> viewLevels = instanceLevelSlices[viewSlice];
            NativeArray<byte> viewCrossfades = crossfadeSlices[viewSlice];
            var maskJob = new ComputeVisibilityMasksJob
            {
                Bounds = combinedBounds,
                InstanceFades = viewFades,
                InstanceClusters = combinedInstanceClusters,
                PlanePackets = scratch.PlanePackets,
                Splits = scratch.Splits,
                ClusterIntersectMasks = scratch.ClusterIntersectMasks,
                ClusterInsideMasks = scratch.ClusterInsideMasks,
                VisibilityMasks = scratch.VisibilityMasks,
                SplitCount = splitCount,
                DistanceCull = anyDistanceCull,
                FadeBit = fadeBit,
                // The constants must have actually been built: a capacity
                // fall-back leaves the test off for the whole view rather than
                // running it against a zeroed struct.
                ReceiverInstanceCull = instanceSphereCull && haveReceiverConstants,
                SingleBitCull = StaticInstanceLodSystem.ReceiverSphereSingleBitCullEnabled,
                ReceiverConstants = receiverConstants,
            };
            var countAndAllocateJob = new CountAndAllocateJob
            {
                DrawGroups = combinedDrawGroups,
                InstanceIndices = combinedInstanceIndices,
                SegmentGroupStarts = segmentGroupStarts,
                SegmentGroupCounts = segmentGroupCounts,
                SegmentOwners = segmentOwners,
                SegmentFilterSettings = segmentFilterSettings,
                OwnerPickingIds = scratch.OwnerPickingIds,
                OwnerFlags = scratch.OwnerFlags,
                OwnerMeshLods = scratch.OwnerMeshLods,
                OwnerPolicies = scratch.OwnerPolicies,
                OwnerShadowCastMaxLevel = scratch.OwnerShadowCastMaxLevel,
                ShadowCastCull = anyShadowCastCull && isLightView,
                OwnerCrossfadeFrom = scratch.OwnerCrossfadeFrom,
                OwnerCrossfadeK = scratch.OwnerCrossfadeK,
                InstanceLevels = viewLevels,
                VisibilityMasks = scratch.VisibilityMasks,
                GroupConfigCounts = scratch.GroupConfigCounts,
                SplitCount = splitCount,
                WritePickingIds = isPickingView,
                IsLightView = isLightView,
                CullingLayerMask = cullingLayerMask,
                FadeBit = fadeBit,
                AnyPerInstanceLod = anyPerInstanceLod || anyOwnerCrossfade,
                CullingOutput = cullingOutput.drawCommands,
                CountAndEmitMarker = CountAndEmitMarker,
            };
            var writeJob = new WriteVisibleInstancesJob
            {
                DrawGroups = combinedDrawGroups,
                InstanceIndices = combinedInstanceIndices,
                VisibilityMasks = scratch.VisibilityMasks,
                InstanceFades = viewFades,
                InstanceLevels = viewLevels,
                CrossfadeValues = viewCrossfades,
                OwnerFlags = scratch.OwnerFlags,
                OwnerPolicies = scratch.OwnerPolicies,
                OwnerMeshLods = scratch.OwnerMeshLods,
                OwnerShadowCastMaxLevel = scratch.OwnerShadowCastMaxLevel,
                ShadowCastCull = anyShadowCastCull && isLightView,
                OwnerCrossfadeFrom = scratch.OwnerCrossfadeFrom,
                OwnerCrossfadeK = scratch.OwnerCrossfadeK,
                GroupConfigOffsets = scratch.GroupConfigCounts,
                CullingOutput = cullingOutput.drawCommands,
                SplitCount = splitCount,
                IsLightView = isLightView,
                CullingLayerMask = cullingLayerMask,
                FadeBit = fadeBit,
                AnyPerInstanceLod = anyPerInstanceLod || anyOwnerCrossfade,
                // Snapshotted on the main thread: a Burst job cannot read a
                // managed static, so the curve reaches the job as a field.
                FadeCurveLut = ResolveFadeCurveLut(),
            };

            using var scheduleScope = CullingScheduleMarker.Auto();
            JobHandle fadeHandle = ScheduleDrivingViewEval(cullingContext);
            JobHandle maskHandle = maskJob.Schedule(combinedBounds.Length, 128, fadeHandle);
            JobHandle allocateHandle = countAndAllocateJob.Schedule(maskHandle);
            JobHandle writeHandle = writeJob.Schedule(combinedDrawGroups.Length, 8, allocateHandle);
            // Diagnostic emission-shape counters (DiagnoseViewIds shape). Stalls
            // the chain on purpose: this reports a structural count, never a
            // timing, so the stall cannot corrupt what it reads.
            bool diagnoseEmission = StaticInstanceLodSystem.DiagnoseEmission > 0;
            if (diagnoseEmission || StaticInstanceLodSystem.EmissionCountersEnabled)
            {
                if (diagnoseEmission)
                    StaticInstanceLodSystem.DiagnoseEmission--;
                writeHandle.Complete();
                MeasureEmissionShape(
                    cullingContext, cullingOutput.drawCommands[0], splitCount, isLightView,
                    diagnoseEmission);
            }
            JobHandle handle = freeScratchJob.Schedule(writeHandle);

            // The accumulator only guards rebuild/dispose, so once everything
            // in it has completed it can be replaced instead of growing the
            // dependency chain forever.
            lastCullingHandle = lastCullingHandle.IsCompleted
                ? handle
                : JobHandle.CombineDependencies(lastCullingHandle, handle);
            return handle;
        }

        // Diagnostic (see StaticInstanceLodSystem.DiagnoseEmission). Walks the
        // finished command array and reports what the emission actually
        // produced:
        //   commands / ranges   - the submission granularity
        //   instanceDraws       - sum of visibleCount, the instances the view
        //                         submits at all
        //   splitWork           - sum of popcount(mask) x visibleCount, the
        //                         (instance x split) pairs the GPU is asked to
        //                         process
        //   perMask             - the same two, per distinct split mask
        // splitWork / instanceDraws is the average number of splits an emitted
        // instance is drawn into; it is 1.0 exactly when no instance is
        // submitted to a split it is not visible in.
        private static unsafe void MeasureEmissionShape(
            in BatchCullingContext cullingContext,
            in BatchCullingOutputDrawCommands output,
            int splitCount,
            bool isLightView,
            bool log)
        {
            const int MaskSlots = 16;
            long* maskInstances = stackalloc long[MaskSlots];
            int* maskCommands = stackalloc int[MaskSlots];
            for (int i = 0; i < MaskSlots; i++)
            {
                maskInstances[i] = 0;
                maskCommands[i] = 0;
            }

            int commandCount = output.drawCommandCount;
            long instanceDraws = 0;
            long splitWork = 0;
            long overflowInstances = 0;
            for (int i = 0; i < commandCount; i++)
            {
                BatchDrawCommand command = output.drawCommands[i];
                uint mask = command.splitVisibilityMask;
                long visible = command.visibleCount;
                instanceDraws += visible;
                splitWork += (long)math.countbits(mask) * visible;
                if (mask < MaskSlots)
                {
                    maskInstances[mask] += visible;
                    maskCommands[mask]++;
                }
                else
                {
                    overflowInstances += visible;
                }
            }

            // Recorded for diagnostics; light views only, so a camera view
            // leaves the shadow submission's counts standing.
            if (isLightView)
            {
                StaticInstanceLodSystem.LastLightCommands = commandCount;
                StaticInstanceLodSystem.LastLightSplitWork = splitWork;
                StaticInstanceLodSystem.LastLightInstanceDraws = instanceDraws;
                StaticInstanceLodSystem.LastLightSplitCount = splitCount;
            }

            if (!log)
                return;

            var histogram = new System.Text.StringBuilder();
            for (int m = 0; m < MaskSlots; m++)
            {
                if (maskCommands[m] == 0)
                    continue;
                histogram.Append($" mask{m}={maskCommands[m]}c/{maskInstances[m]}i");
            }
            if (overflowInstances != 0)
                histogram.Append($" maskOver={overflowInstances}i");

            StaticInstanceLog.Info(
                $"[SI-EMIT] type={cullingContext.viewType} light={isLightView} " +
                $"splits={splitCount} commands={commandCount} " +
                $"ranges={output.drawRangeCount} instanceDraws={instanceDraws} " +
                $"splitWork={splitWork} " +
                $"splitsPerInstance={(instanceDraws == 0 ? 0d : (double)splitWork / instanceDraws):F4}" +
                histogram);
        }

        // The driving-view evaluation (draw-range fades + PerInstance LOD) runs
        // once per frame, not once per view: every view has to see the same
        // values or a shadow would dither out of step with its mesh. The first
        // culling request of the frame schedules it and keeps the handle; every
        // later view in the same frame depends on that handle instead of
        // re-running it, so the arrays are never read mid-write and the main
        // thread never stalls.
        private static unsafe JobHandle ScheduleDrivingViewEval(in BatchCullingContext cullingContext)
        {
            if (!anyDistanceCull && !anyPerInstanceLod)
                return default;

            int frame = Time.frameCount;
            if (frame == lastFadeAnimationFrame)
                return fadeAnimationHandle;

            // Driven by the registered driving cameras (`Camera.main` alone
            // unless a project registers others), the same set the per-owner
            // ladder uses, and not by whichever view happens to request culling
            // first. This matters in the editor: the SceneView camera would
            // otherwise drive fades, cull what it cannot see, and never restore
            // it, because moving the Game camera does not move it.
            int drivingCount = StaticInstanceLodSystem.ResolveDrivingCameras();
            float3 fallbackPosition = (float3)cullingContext.lodParameters.cameraPosition;

            lastFadeAnimationFrame = frame;
            float globalDuration = Mathf.Max(0f, StaticInstanceLodSystem.LodFadeDuration);
            int globalMinLevel = Mathf.Clamp(
                StaticInstanceLodSystem.MinMeshLodLevel, 0, MaxLodLevels - 1);
            float distanceScale = Mathf.Max(0f, StaticInstanceLodSystem.LodDistanceScale);
            float lodBias = Mathf.Max(0f, StaticInstanceLodSystem.LodBias);
            // Snapshot rule: read the mask once and hand the same value to the
            // pre-pass and the eval job. Reading it independently lets a
            // mid-frame Debug-band toggle desynchronize them for one frame, and
            // one direction is a visual bug: the pre-pass seeing "no bands =>
            // uniform" while the job still applies them would give in-band
            // instances the cluster's out-of-band value.
            bool crossFadeActive = StaticInstanceLodSystem.CrossFadeActive;

            // The cluster-uniform front stage: resolve whole clusters once per
            // frame on the main thread (the ClassifyClusters shape: per cluster,
            // not per instance, so a job would cost more than the work). The
            // eval job then stores the resolved value per instance instead of
            // recomputing it; every downstream consumer reads the same arrays.
            bool clusterUniform = StaticInstanceLodSystem.LodClusterUniformEnabled &&
                anyPerInstanceLod && clusterUniformSlices[0].IsCreated;
            int clusterTotal = clusterOwners.IsCreated ? clusterOwners.Length : 0;
            int instanceCount = combinedBounds.Length;

            // One evaluation per driving camera, each writing its own slice.
            // They are independent by construction (disjoint slices, shared
            // read-only inputs), so they are scheduled as siblings and joined
            // once.
            JobHandle evalHandle = default;
            for (int k = 0; k < drivingCameraCountSnapshot; k++)
            {
            float3 cameraPosition = k < drivingCount
                ? (float3)StaticInstanceLodSystem.GetDrivingCameraPosition(k)
                : fallbackPosition;
            // Only the camera half of the FOV factor is resolved here:
            // `1 / tan(fov/2)`. The owner half (`RefTan`) rides `OwnerLodParams`,
            // because the reference is per component. Both the eval job and the
            // classifier compose the two.
            //
            // Resolved inside this loop because two driving cameras at
            // different FOVs must get different thresholds, and the per-camera
            // slices are what make that expressible.
            //
            // `hasFov` is false for the fallback branch (no Camera at all; the
            // culling context's position is all it has) and for an orthographic
            // camera. In Distance mode that means factor 1, which is exactly
            // neutral. In Screen Size mode 1 would be wrong rather than neutral,
            // so such a view uses the owner's own reference:
            // ComposeEvaluationScales takes 1 / RefTan there.
            bool hasFov = false;
            float cameraFovTerm = k < drivingCount
                ? StaticInstanceLodSystem.ResolveCameraFovTerm(
                    StaticInstanceLodSystem.GetDrivingCamera(k), out hasFov)
                : 1f;
            // The gate for Distance-mode owners: the master and a real FOV.
            // Screen Size owners ignore the master (ComposeEvaluationScales).
            bool fovApplies = StaticInstanceLodSystem.FovTrackingEnabled && hasFov;
            byte* uniformSlice = null;
            if (clusterUniform)
            {
                // The cluster-uniform pre-pass is a function of camera position,
                // so it too is per camera: a byte per cluster, which is why it is
                // sliced rather than disabled above K=1.
                uniformSlice = (byte*)clusterUniformSlices[k].GetUnsafePtr();
                int* uniformCounters = stackalloc int[3];
                StaticInstanceCullingBurst.ClassifyClusterUniformity(
                    (CullingBounds*)clusterAabbs.GetUnsafeReadOnlyPtr(),
                    (int*)clusterOwners.GetUnsafeReadOnlyPtr(),
                    (float*)clusterFarThresholdMin.GetUnsafeReadOnlyPtr(),
                    (float*)clusterNearThresholdMax.GetUnsafeReadOnlyPtr(),
                    (OwnerLodParams*)ownerLodParams.GetUnsafeReadOnlyPtr(),
                    cameraPosition.x,
                    cameraPosition.y,
                    cameraPosition.z,
                    distanceScale,
                    lodBias,
                    cameraFovTerm,
                    fovApplies ? (byte)1 : (byte)0,
                    globalMinLevel,
                    crossFadeActive ? (byte)1 : (byte)0,
                    clusterTotal,
                    uniformCounters,
                    uniformSlice);
                StaticInstanceLodSystem.LodUniformClusters += uniformCounters[1];
                StaticInstanceLodSystem.LodEligibleClusters += uniformCounters[0];
            }
            var evalJob = new DrivingViewEvalJob
            {
                Bounds = combinedBounds,
                CullThresholdsSq = combinedCullThresholdsSq,
                MinThresholdsSq = combinedMinThresholdsSq,
                FadeRates = combinedFadeRates,
                InstanceFades = instanceFadeSlices[k],
                InstanceOwners = combinedInstanceOwners,
                OwnerParams = ownerLodParams,
                InstanceLevels = instanceLevelSlices[k],
                CrossfadeValues = crossfadeSlices[k],
                // This camera's own fade state.
                MeshFadeFrom = meshFadeFromSlices[k],
                MeshFadeProgress = meshFadeProgressSlices[k],
                AnyTimeFade = anyPerInstanceTimeFade,
                CameraPosition = cameraPosition,
                DeltaTime = Time.deltaTime,
                GlobalRate = globalDuration > 0f
                    ? FadeOpaque / globalDuration
                    : float.MaxValue,
                DistanceCull = anyDistanceCull,
                EvaluateLod = anyPerInstanceLod,
                // The global Mesh LOD kill switch forces full detail rather
                // than freezing whatever levels were last written.
                ForcedLevel = StaticInstanceLodSystem.ForcedMeshLod,
                GlobalMinLevel = globalMinLevel,
                DistanceScale = distanceScale,
                LodBias = lodBias,
                CameraFovTerm = cameraFovTerm,
                FovApplies = fovApplies,
                HasFov = hasFov,
                ScreenRadii = combinedScreenRadii,
                CrossFadeEnabled = crossFadeActive,
                SnapTimeFades = !QualitySettings.enableLODCrossFade |
                    (k < drivingCount && StaticInstanceLodSystem.ConsumeFadeSnap(
                        StaticInstanceLodSystem.GetDrivingCamera(k), frame)),
                // The static carries the authored value; the const is only
                // the fallback for a project without the settings container.
                DerivedCullFadeFraction = StaticInstanceLodSystem.DerivedCullFadeFraction >= 0f
                    ? StaticInstanceLodSystem.DerivedCullFadeFraction
                    : DefaultDerivedCullFadeFraction,
                ClusterUniformLevels = uniformSlice,
            };
            JobHandle handle = evalJob.Schedule(instanceCount, 128);
            evalHandle = k == 0 ? handle : JobHandle.CombineDependencies(evalHandle, handle);
            }

            // The shared shadow slice, K>1 only (see drivingSliceCount). One
            // pass per camera, folding into the accumulator: at most four
            // small chained jobs, and each carries exactly one source and one
            // target rather than a fan-in of every slice at once.
            if (drivingSliceCount > 1)
            {
                for (int k = 0; k < drivingCameraCountSnapshot; k++)
                {
                    var mergeJob = new MergeShadowSliceJob
                    {
                        SourceLevels = instanceLevelSlices[k],
                        SourceCrossfades = crossfadeSlices[k],
                        SourceFades = instanceFadeSlices[k],
                        TargetLevels = instanceLevelSlices[shadowSliceIndex],
                        TargetCrossfades = crossfadeSlices[shadowSliceIndex],
                        TargetFades = instanceFadeSlices[shadowSliceIndex],
                        EvaluateLod = anyPerInstanceLod,
                        DistanceCull = anyDistanceCull,
                        Initialize = k == 0,
                    };
                    evalHandle = mergeJob.Schedule(instanceCount, 128, evalHandle);
                }
            }

            fadeAnimationHandle = evalHandle;
            return fadeAnimationHandle;
        }

        // Merges the per-camera slices into the one a shadow view reads.
        //
        // A shadow map is rendered once and sampled by every camera, so it
        // cannot belong to any single one. The merge is conservative in both
        // directions: the **finest** level across cameras (a shadow cast by a
        // coarser mesh than the receiver sees is the visible error) and the
        // **most visible** draw-range fade (a shadow that fades out while a
        // camera still sees the caster is the visible error).
        //
        // The crossfade pair is taken wholesale from the camera that supplied
        // the finest level — level byte and k together, never mixed — because
        // the pair's two halves are complementary by construction
        // (shaped(k) + shaped(256-k) = 256) and pairing a level from one camera
        // with a k from another breaks that into a double draw or a hole.
        [BurstCompile(DisableSafetyChecks = true, OptimizeFor = OptimizeFor.Performance)]
        private struct MergeShadowSliceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> SourceLevels;
            [ReadOnly] public NativeArray<byte> SourceCrossfades;
            [ReadOnly] public NativeArray<float> SourceFades;
            public NativeArray<byte> TargetLevels;
            public NativeArray<byte> TargetCrossfades;
            public NativeArray<float> TargetFades;
            public bool EvaluateLod;
            public bool DistanceCull;
            // The first camera seeds the accumulator instead of merging into
            // whatever last frame left there.
            public bool Initialize;

            public void Execute(int index)
            {
                if (DistanceCull)
                {
                    float fade = SourceFades[index];
                    TargetFades[index] = Initialize
                        ? fade
                        : math.max(TargetFades[index], fade);
                }

                if (!EvaluateLod)
                    return;

                byte candidate = SourceLevels[index];
                if (Initialize)
                {
                    TargetLevels[index] = candidate;
                    TargetCrossfades[index] = SourceCrossfades[index];
                    return;
                }

                // Strictly finer wins, so equal levels keep the earlier
                // camera's pair: the tie has to break the same way every frame
                // or a shadow would flicker between two identical-level cameras
                // whose fades differ.
                // The test is `FinestLevel`, not bits 0-1. On the distance path
                // bits 0-1 are min(level, partner) by construction, but on the
                // time path bits 0-1 are the incoming level and the partner is
                // the outgoing one, so an instance fading 0 -> 1 presents 1
                // while still drawing 126/127 of level 0, and the shadow could
                // be taken from a camera drawing coarser than another camera
                // sees.
                if (FinestLevel(candidate) >= FinestLevel(TargetLevels[index]))
                    return;

                TargetLevels[index] = candidate;
                TargetCrossfades[index] = SourceCrossfades[index];
            }
        }

        // Keep the per-camera state count in step with the registry.
        //
        // Pull, not push: registration bumps a generation counter and this
        // compares it, so registration order and initialization order cannot
        // matter, and a registration made before the system exists is picked
        // up on the first culling request rather than lost. A change resizes
        // the per-instance arrays through the existing rebuild path, which is
        // also the cost of registering mid-play (one rebuild hitch plus a fade snap).
        //
        // Two int compares per culling request in the steady state.
        private static void RefreshDrivingSlices()
        {
            int generation = StaticInstanceLodSystem.DrivingCameraGeneration;
            if (generation == drivingGenerationSnapshot)
                return;

            drivingGenerationSnapshot = generation;
            int cameras = Mathf.Max(1, StaticInstanceLodSystem.ResolveDrivingCameras());
            // The shadow slice exists only above K=1 — see drivingSliceCount.
            int slices = cameras == 1 ? 1 : cameras + 1;
            if (slices == drivingSliceCount && cameras == drivingCameraCountSnapshot)
                return;

            drivingCameraCountSnapshot = cameras;
            drivingSliceCount = slices;
            shadowSliceIndex = cameras == 1 ? 0 : cameras;
            combinedDataDirty = true;
        }

        // Which per-camera slice a culling request reads.
        //
        // Attribution is exact rather than positional: the view carries the id
        // of the camera it belongs to, so two cameras at one position are still
        // told apart, and `lodParameters.cameraPosition` (identical for every
        // view of a frame, light views included) is not consulted at all.
        //
        // Views that match no registered camera (SceneView, Preview, Picking,
        // reflection probes) read slice 0, the primary camera's.
        private static int GetSliceForView(in BatchCullingContext cullingContext, bool isLightView)
        {
            if (drivingSliceCount == 1)
                return 0;

            // A shadow map is shared by every camera, so it gets the merged
            // slice rather than any one camera's.
            if (isLightView)
                return shadowSliceIndex;

#if UNITY_6000_4_OR_NEWER
            EntityId viewId = cullingContext.viewID.GetEntityId();
#else
            EntityId viewId = cullingContext.viewID.GetInstanceID();
#endif
            for (int i = 0; i < drivingCameraCountSnapshot; i++)
            {
                Camera camera = StaticInstanceLodSystem.GetDrivingCamera(i);
                if (camera != null && camera.GetEntityId() == viewId)
                    return i;
            }

            return 0;
        }

        private static bool IsOwnerRendered(
            StaticInstanceOwner owner,
            bool isLightView,
            int viewSlice
#if UNITY_EDITOR
            , bool isOutlineView, EntityId[] selection
#endif
            )
        {
            if (!owner.Visible)
                return false;
            if (isLightView)
            {
                if (!owner.CastShadows || !owner.HasShadowCasters)
                    return false;

                // The owner-level half of the Shadow Cast Max Level rule. Refuse
                // only in the rule's "emits nothing" cases; every other state
                // falls through to the emit path, which drops the half above the
                // threshold.
                //
                // `from` and `to` are not ordered. ApplyLodLevel starts a fade on
                // any level change, in either direction, so an approaching camera
                // gives from > to. Refusing on the published level alone would
                // delete a fading-in owner for the whole band and then pop it on,
                // which is the pop this option's Fade Mode rule exists to prevent.
                if (anyShadowCastCull && owner.ShadowCastMaxLevel >= 0)
                {
                    // The same masked level the emit path bins by: the global
                    // ForcedMeshLod overrides the owner's published level there
                    // (`scratch.OwnerMeshLods`), and reading the unmasked one here
                    // would let a ladder-inactive owner pass the refusal and then
                    // emit at the forced level, casting past its own threshold.
                    int globalForced = StaticInstanceLodSystem.ForcedMeshLod;
                    int to;
                    if (globalForced < 0)
                    {
                        to = owner.MeshLodLevels[viewSlice];
                    }
                    else
                    {
                        to = owner.LodPolicy == LodSelectionPolicy.PerInstance
                            ? math.clamp(globalForced, owner.LodMinLevel, owner.LodMaxLevel)
                            : globalForced;
                    }
                    // A global force snaps every fade, so the pair is not live
                    // under it; the emit path zeroes OwnerCrossfadeK the same way.
                    int k = globalForced >= 0 ? 0 : owner.MeshLodCrossfadeKs[viewSlice];
                    if (ShadowCastOwnerRefused(
                            owner.MeshLodCrossfadeFroms[viewSlice], to, k,
                            owner.ShadowCastMaxLevel))
                    {
                        return false;
                    }
                }
            }
#if UNITY_EDITOR
            if (isOutlineView)
            {
                if (selection == null)
                    return false;
                for (int i = 0; i < selection.Length; i++)
                {
                    if (selection[i] == owner.PickingEntityId)
                        return true;
                }

                return false;
            }
#endif
            return true;
        }
    }
}
