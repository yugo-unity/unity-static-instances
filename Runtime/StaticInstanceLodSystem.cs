using System.Collections.Generic;
using UnityEngine;

namespace StaticInstances
{
    public enum StaticInstanceFadeMode
    {
        None = 0,
        Distance = 1,
        Time = 2,
    }

    // Which quantity a component's LOD ladder is authored in. Per component, on
    // the `StaticInstanceFadeMode` precedent above, not a global, because one
    // scene mixes content whose authors want different units.
    public enum StaticInstanceLodMode
    {
        // Metres. `Distance` is enum value 0, so a serialized `lodMode: 0` maps to
        // it. A component serialized with no `lodMode:` key takes the field
        // initializer (`ScreenSize`), not enum zero, so serialized data must write
        // explicit keys.
        Distance = 0,

        // Per-instance screen-size ratios: a boundary is the fraction of screen
        // height the instance's bounding sphere covers, so the effective distance
        // is per instance and the ratio is the owner-uniform quantity the author
        // sees.
        //
        // This mode's semantics need the current FOV, so it is inherently
        // FOV-tracking and ignores the `FovTrackingEnabled` master, whose scope is
        // Distance mode alone.
        ScreenSize = 1,
    }

    public static class StaticInstanceLodSystem
    {
        public const int MaxAuthorableMeshLodLevel = 3;
        public static bool ShadowLodEnabled = true;


        public static bool CrossFadeEnabled = true;

        // Effective crossfade switch: CrossFadeEnabled AND'ed with the URP Asset's
        // LOD Cross Fade setting (URP mirrors that setting into
        // QualitySettings.enableLODCrossFade when the pipeline is created). With
        // the Asset setting off, builds strip the LOD_FADE_CROSSFADE shader
        // variants, so a fade command would draw both halves of a dual-slot pair
        // un-clipped (double draw). Degrading every dither to a hard switch keeps
        // Editor and build behaviour identical. Main-thread only (reads
        // QualitySettings).
        public static bool CrossFadeActive =>
            CrossFadeEnabled && UnityEngine.QualitySettings.enableLODCrossFade;

        public static bool CullingFastPathEnabled = true;
        public static long CullingFastPathViews;

        // Internal spatial chunking for PerInstance (SIS) owners: large owners are
        // split into Morton-contiguous clusters so the owner-AABB early rejection
        // works per cluster instead of degenerating on one scene-sized box.
        // Flipping it marks the combined data dirty, so the next culling request
        // rebuilds with or without clusters.
        public static bool CullingChunkingEnabled = true;
        // Diagnostics for the culling fast path: how many culling requests passed
        // the feature gate (no distance cull / PerInstance / crossfade,
        // non-editor view) and reached the straddle check, and, when that check
        // rejected, which owner straddled first, with its split masks packed as
        // (intersect << 8 | inside).
        public static long CullingFastPathEligibleViews;
        public static int CullingFastPathLastStraddleOwner = -1;
        public static int CullingFastPathLastStraddleMasks;

        // Layer-union early-return diagnostics. SkippedViews counts culling
        // requests that returned before scheduling anything because the request's
        // culling mask shares no bit with the union of the draw groups' layers.
        // The two mask mirrors record the last camera and light view masks.
        public static long CullingLayerSkippedViews;
        public static uint CullingLayerMaskLastCamera;
        public static uint CullingLayerMaskLastLight;

        // Diagnostic toggle: logs the next N culling requests' view type, packed
        // view ID and slice index. Counts itself down.
        public static int DiagnoseViewIds;

        // Diagnostic toggle: logs the emitted command shape (command/range
        // counts, the instance total, and the instance x split work) for the next
        // N culling requests. Counts itself down; costs one branch per culling
        // request when off.
        public static int DiagnoseEmission;

        // Diagnostic toggle: records the emitted command shape of light views into
        // the counters below. Leave off during timing measurements: reading the
        // shape forces the emission chain to complete on the main thread. A
        // camera view leaves the last light view's values standing.
        public static bool EmissionCountersEnabled;
        public static long LastLightCommands;
        public static long LastLightSplitWork;
        public static long LastLightInstanceDraws;
        public static int LastLightSplitCount;

        public static bool ReceiverSphereCullEnabled = true;

        // Cluster and per-instance receiver-sphere culling are internal switches,
        // not settings surface. Both default ON, and ReceiverSphereCullEnabled
        // above is the master over both: with the master off nothing in this
        // feature runs.
        public static bool ReceiverSphereClusterCullEnabled = true;
        public static bool ReceiverSphereInstanceCullEnabled = true;

        // Diagnostic toggle. The per-instance test is normally gated on
        // `countbits(mask) > 1`, which is its zero-tax mechanism but means a
        // single-bit instance is never reach-tested. Turn this ON to run the test
        // on every instance. Default OFF.
        public static bool ReceiverSphereSingleBitCullEnabled;


        // Accumulated across views and frames, like the CullingFastPath* series:
        // clusters reaching at least one split (the population the receiver test
        // acts on), how many of those the receiver volume rejects, and the
        // instances they hold. Views whose receiver planes URP has already folded
        // into the split planes contribute nothing.
        public static long ReceiverReachingClusters;
        public static long ReceiverRejectedClusters;
        public static long ReceiverRejectedInstances;

        // The sphere half, which applies to the directional cascade view only.
        // The split-work pair measures the mask job's work, which is per instance
        // per split: a cluster that loses one split bit and keeps another is a
        // partial win the cluster counts cannot express.
        public static long ReceiverSphereRejectedClusters;
        public static long ReceiverSphereRejectedInstances;
        public static long ReceiverSphereSplitWorkBefore;
        public static long ReceiverSphereSplitWorkAfter;

        // There is deliberately no per-instance counterpart to the pair above: a
        // counter inside the mask job would have to be shared across concurrently
        // scheduled culling requests, which is a job-safety problem.
        //
        // Cap-and-fall-back, counted so it is not silent: views whose light-facing
        // plane count or split count exceeded the mask job's fixed capacity, so
        // the per-instance test did not run for them. The cluster pass is
        // unaffected (it runs on uncapped Temp arrays). The split dimension is
        // bounded at 6 by BRG; the plane dimension is not bounded by anything,
        // which is why this counter exists.
        public static long ReceiverVolumeCapacityFallbacks;

        // Diagnostic counters: how many of the clusters ClassifyClusters
        // classifies (those of rendered owners) reach no split at all. Their
        // instances are the ones the owner-AABB early rejection already spares the
        // mask job but that count and write still loop over one by one.
        // Accumulated per view, like CullingFastPathViews; a probe divides and
        // resets.
        public static long CullingClassifiedClusters;
        public static long CullingOutsideClusters;
        public static long CullingOutsideInstances;

        // Resolve a whole SIS cluster's LOD level once per frame when the cluster
        // sits inside one level band and clear of every fade band, and let the
        // eval job store that instead of recomputing it per instance. Default ON.
        public static bool LodClusterUniformEnabled = true;
        // Mechanism counters, accumulated per frame by the pre-pass.
        public static long LodUniformClusters;
        public static long LodEligibleClusters;

        // The reference-FOV seed: 50 degrees by default, or the scene camera's FOV
        // when there is one.
        //
        // A seam, not a convenience: `Reset` has to ask `Camera.main`, which
        // answers about whatever scene happens to be open, so a test that
        // constructed its own camera would measure the open scene instead.
        // Splitting the resolution from the source makes the rule testable without
        // a scene, which is also why `ResolveFovFactor` takes a Camera rather than
        // reaching for one.
        internal const float DefaultReferenceFovDegrees = 50f;
        internal const float MinReferenceFovDegrees = 1f;
        internal const float MaxReferenceFovDegrees = 179f;

        internal static float RatioToBoundary(float ratio)
        {
            if (!(ratio > 0f))
                return float.MaxValue;

            return 1f / ratio;
        }

        // The rebuild's answer to an out-of-range reference: the default, not a
        // clamp to the range's edge. The edges are the problem: a hand-edited
        // scene, a Preset or a script can carry 0, and clamping that to 1 degree
        // makes `tan(ref/2)` ~0.0087, so thresholds silently collapse to a few
        // percent of what the author wrote. The default is the only value that
        // means "this content never chose a reference".
        internal static float SanitizeReferenceFov(float referenceFovDegrees)
        {
            if (referenceFovDegrees < MinReferenceFovDegrees ||
                referenceFovDegrees > MaxReferenceFovDegrees ||
                float.IsNaN(referenceFovDegrees))
            {
                return DefaultReferenceFovDegrees;
            }

            return referenceFovDegrees;
        }

        internal static float SeedReferenceFovFrom(Camera camera)
        {
            // An orthographic camera has no FOV worth seeding from, so the default
            // stands.
            if (camera == null || camera.orthographic)
                return DefaultReferenceFovDegrees;

            return Mathf.Clamp(
                camera.fieldOfView, MinReferenceFovDegrees, MaxReferenceFovDegrees);
        }


        public static int ForcedMeshLod = -1;

        // Global floor on the mesh LOD level every group resolves to; the group's
        // own Min Mesh LOD Level is folded in with a max(), so whichever asks for
        // the coarser level wins. Meant for a game-wide quality switch (hold the
        // near stages at level 1 in gameplay, release to 0 for a cutscene)
        // without touching per-group authored data. Changing it takes effect on
        // the next Update: the stage count and thresholds are unaffected, so
        // nothing is reallocated.
        public static int MinMeshLodLevel;

        // Multiplies every group's authored LOD distances. The knob a quality tier
        // turns. It deliberately does not read QualitySettings.lodBias, which
        // belongs to LOD Group (even GPU Resident Drawer leaves the mesh LOD path
        // out of it).
        public static float LodDistanceScale = 1f;

        // SI-only bias on LOD and scalable Draw Range distances. 1 is neutral.
        // Runtime override; GlobalSettings.ApplyToSystem restores the project value.
        public static float LodBias = 1f;

        // Runtime FOV tracking for the distance thresholds.
        //
        // Policy: uniform. The factor scales the LOD boundaries, the size-derived
        // cull limit and the authored Draw Range Max. Uniformity is what makes the
        // feature nearly free: the authored Max and the derived limit are composed
        // as a per-instance min at rebuild (StaticInstanceRendererSystem, the
        // combinedCullThresholdsSq walk), and min(a*f, d*f) == f*min(a, d), so a
        // uniform factor needs no change to the baked data: it applies to the
        // stored threshold at evaluation. A derived-only factor could not, since
        // min(a, d*f) is unrecoverable from a stored min whose loser was
        // discarded, and would cost a second per-instance float plus a per-frame
        // min.
        //
        // Default OFF, and off is identical to not having the feature
        // (ResolveFovFactor returns exactly 1f and every call site multiplies by
        // it).
        //
        // Enabling it costs draw calls and triangles: zooming in no longer buys
        // the frustum's triangle reduction, because the policy holds world detail
        // constant under zoom. At f = 1 the feature is inert.
        public static bool FovTrackingEnabled;


        public static float ResolveFovFactorForDiagnostics(
            Camera camera, float referenceFovDegrees) =>
            ResolveFovFactor(camera, referenceFovDegrees);

        internal static float ResolveFovFactor(Camera camera, float referenceFovDegrees)
        {
            if (!FovTrackingEnabled)
                return 1f;

            float cameraTerm = ResolveCameraFovTerm(camera, out bool hasFov);
            if (!hasFov)
                return 1f;

            // A degenerate reference returns exactly 1f, not a clamped one: at a
            // clamp's floor `tan(0.5 deg)` is 0.0087, so a reference of 0 would
            // collapse every threshold to ~1.5% of its authored value. A caller
            // with a nonsense reference has no opinion about scaling, and 1f is the
            // only safe way to say that.
            //
            // The rebuild path deliberately answers the same bad input differently
            // (`SanitizeReferenceFov` maps it to the default 50) because there a
            // usable reference is required to compute `tan(ref/2)` at all, and
            // identity is not expressible as a reference.
            if (referenceFovDegrees <= 0f || referenceFovDegrees >= 180f)
                return 1f;

            return Mathf.Tan(referenceFovDegrees * 0.5f * Mathf.Deg2Rad) * cameraTerm;
        }

        // The camera half of the decomposition: `1 / tan(fov/2)`.
        //
        // `factor = tan(ref/2) * (1 / tan(fov/2))` splits into a per-owner
        // constant and this per-camera scalar, which is what lets a per-owner
        // reference reach the jobs at all: owner params are rebuild-time and shared
        // across camera slices, so the two halves cannot live in the same place.
        //
        // `hasFov` is false for a null camera and for an orthographic one. In
        // Distance mode "no FOV" means factor 1, which is neutral (authored metres
        // as written). In Screen Size mode a factor of 1 would be wrong, because
        // the stored threshold is a ratio-derived quantity; such a view evaluates a
        // Screen Size owner as if seen at its own reference, i.e.
        // `1 / tan(ref/2)`.
        internal static float ResolveCameraFovTerm(Camera camera, out bool hasFov)
        {
            hasFov = false;
            if (camera == null || camera.orthographic)
                return 1f;

            float fov = camera.fieldOfView;
            if (fov <= 0f || fov >= 180f)
                return 1f;

            float current = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            if (current <= 0f)
                return 1f;

            hasFov = true;
            return 1f / current;
        }

        public enum FadeCurveMode
        {
            Linear = 0,
            SineEase = 1,
        }

        public static FadeCurveMode FadeCurve = FadeCurveMode.Linear;

        // Metres of draw distance per metre of instance size. 0 = off (the
        // default, and identical to not having the feature). Non-zero derives a
        // cull distance for every instance that has no authored Draw Range Max
        // (authored always wins) from its own bake-time size, so a fence post
        // disappears long before a barn does without either being authored
        // individually.
        //
        // Deliberately FOV-free: the distance is metric and does not track camera
        // FOV, like the rest of this system's distances. A quality tier turns this
        // knob the same way it turns LodDistanceScale.
        //
        // Unlike the other knobs, changing this one marks the combined data dirty:
        // the thresholds are precomputed per instance at rebuild, and the 0 <-> non-0
        // flip changes the fade bin dimension. Authoring-time knob.
        public static float SizeCullDistanceScale;

        // How much of a derived cull distance is spent fading out, as a fraction
        // of that distance. Authored rather than fixed because no universal value
        // exists: the band is a distance, so how long it reads depends on camera
        // speed, and lowering SizeCullDistanceScale cannot substitute (the band
        // scales with the limit, so the ratio is unchanged). 0 hard-cuts.
        public static float DerivedCullFadeFraction = 0.15f;

        public static float LodFadeDuration = 0.5f;

        // Resolves a component's authored mode against the project-wide switch:
        // with Cross Fade disabled (or the URP Asset's own switch off, where
        // the dither variants are stripped) every mode collapses to None.
        public static StaticInstanceFadeMode ResolveFadeMode(StaticInstanceFadeMode authored)
        {
            return CrossFadeActive ? authored : StaticInstanceFadeMode.None;
        }

        public const int MaxDrivingCameras = 4;

        // Per-camera state slots plus the shared shadow slot. One index space
        // for per-instance arrays and per-owner state alike, so the slice a
        // view resolves to indexes both.
        internal const int MaxLodSlices = MaxDrivingCameras + 1;

        private static readonly List<Camera> RegisteredDrivingCameras =
            new List<Camera>(MaxDrivingCameras);
        private static readonly Camera[] ResolvedDrivingCameras =
            new Camera[MaxDrivingCameras];
        private static readonly Vector3[] ResolvedDrivingPositions =
            new Vector3[MaxDrivingCameras];
        private static int resolvedDrivingCount;
        private static int resolvedDrivingFrame = int.MinValue;

        // Bumped by every registration change. Consumers that size per-camera
        // state (the renderer's combined arrays) compare against their own
        // copy rather than being pushed to, so registration order and
        // initialization order cannot matter.
        public static int DrivingCameraGeneration { get; private set; }

        // True while nothing is registered, i.e. the Camera.main default is in
        // force. The K=1 fast paths test this rather than the resolved count,
        // so a project that registers exactly one camera still takes them.
        public static bool DrivingCamerasAreDefault => RegisteredDrivingCameras.Count == 0;

        private static readonly Camera[] PendingFadeSnaps = new Camera[MaxDrivingCameras];
        private static readonly int[] PendingFadeSnapFrames = new int[MaxDrivingCameras];

        /// <summary>
        /// Requests completion of this driving camera's LOD and Draw Range Time fades.
        /// Main thread only. True means accepted, not synchronously completed.
        /// Requests after the camera snapshot apply no earlier than the next frame;
        /// already submitted views are never reevaluated. Distance/None are unchanged.
        /// </summary>
        public static bool SnapFades(Camera camera)
        {
            if (!IsCurrentDrivingCamera(camera))
                return false;
            int earliest = Time.frameCount + (resolvedDrivingFrame == Time.frameCount ? 1 : 0);
            int empty = -1;
            for (int i = 0; i < PendingFadeSnaps.Length; i++)
            {
                if (!IsCurrentDrivingCamera(PendingFadeSnaps[i])) PendingFadeSnaps[i] = null;
                if (ReferenceEquals(PendingFadeSnaps[i], camera))
                {
                    PendingFadeSnapFrames[i] = earliest;
                    return true;
                }
                if (PendingFadeSnaps[i] == null && empty < 0) empty = i;
            }
            if (empty < 0) return false;
            PendingFadeSnaps[empty] = camera;
            PendingFadeSnapFrames[empty] = earliest;
            return true;
        }

        private static bool IsCurrentDrivingCamera(Camera camera)
        {
            if (camera == null) return false;
            bool anyLive = false;
            for (int i = 0; i < RegisteredDrivingCameras.Count; i++)
            {
                Camera registered = RegisteredDrivingCameras[i];
                if (registered == null) continue;
                anyLive = true;
                if (ReferenceEquals(registered, camera)) return true;
            }
            return !anyLive && camera == Camera.main;
        }

        internal static bool ConsumeFadeSnap(Camera camera, int frame)
        {
            if (!IsCurrentDrivingCamera(camera)) return false;
            for (int i = 0; i < PendingFadeSnaps.Length; i++)
            {
                if (!ReferenceEquals(PendingFadeSnaps[i], camera) || PendingFadeSnapFrames[i] > frame) continue;
                PendingFadeSnaps[i] = null;
                return true;
            }
            return false;
        }

        private static void ClearFadeSnap(Camera camera)
        {
            for (int i = 0; i < PendingFadeSnaps.Length; i++)
                if (ReferenceEquals(PendingFadeSnaps[i], camera)) PendingFadeSnaps[i] = null;
        }
        public static bool RegisterDrivingCamera(Camera camera)
        {
            if (camera == null)
            {
                Debug.LogError("[SI] RegisterDrivingCamera(null) ignored.");
                return false;
            }

            if (RegisteredDrivingCameras.Contains(camera))
                return true;

            if (RegisteredDrivingCameras.Count >= MaxDrivingCameras)
            {
                // Rejected rather than silently accepted: per-camera state is
                // linear in the count, and a registry that grows without bound is
                // a leak. The rejected camera degrades to the documented
                // unsupported state (it renders, from another camera's LOD
                // decisions).
                Debug.LogError(
                    $"[SI] RegisterDrivingCamera('{camera.name}') rejected — " +
                    $"{MaxDrivingCameras} driving cameras are already registered. " +
                    "The limit is a misuse guard, not a hard constraint: cost is " +
                    "linear in the count, so raise MaxDrivingCameras if a project " +
                    "genuinely needs more.",
                    camera);
                return false;
            }

            RegisteredDrivingCameras.Add(camera);
            for (int i = 0; i < PendingFadeSnaps.Length; i++)
                if (!IsCurrentDrivingCamera(PendingFadeSnaps[i])) PendingFadeSnaps[i] = null;
            InvalidateDrivingCameras();
            return true;
        }

        public static bool UnregisterDrivingCamera(Camera camera)
        {
            if (camera == null || !RegisteredDrivingCameras.Remove(camera))
                return false;

            ClearFadeSnap(camera);
            InvalidateDrivingCameras();
            return true;
        }

        private static void InvalidateDrivingCameras()
        {
            resolvedDrivingFrame = int.MinValue;
            DrivingCameraGeneration++;
        }

        // Back to the Camera.main default. Scene teardown does not unregister for
        // you: a registration is a statement about the game's cameras, and the
        // statics outlive any one scene.
        public static void ClearDrivingCameras()
        {
            System.Array.Clear(PendingFadeSnaps, 0, PendingFadeSnaps.Length);
            if (RegisteredDrivingCameras.Count == 0)
                return;

            RegisteredDrivingCameras.Clear();
            InvalidateDrivingCameras();
        }

        // Registrations must not outlive the Play session that made them. Statics
        // survive both play-mode exit and, with domain reload disabled, play-mode
        // entry, so a camera registered while testing would still be driving LOD
        // in edit mode, against a Camera object that may no longer exist. Both
        // hooks are needed, one for each direction, and neither costs anything at
        // runtime.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDrivingCamerasOnPlay()
        {
            ClearDrivingCameras();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookDrivingCameraReset()
        {
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state == UnityEditor.PlayModeStateChange.EnteredEditMode)
                    ClearDrivingCameras();
            };
        }
#endif

        // Resolves the driving set for this frame and returns how many cameras it
        // holds. Destroyed cameras are swept out here rather than in a callback: a
        // camera can be destroyed without anyone unregistering it, and a stale
        // entry would pin the minimum distance forever.
        //
        // The resolution is cached for the frame, so every decision in a frame
        // agrees. A camera destroyed mid-frame stays in the resolved set until the
        // next frame, so this array can hold a destroyed Camera; positions are
        // snapshotted here for that reason, and nothing downstream dereferences
        // the reference.
        internal static int ResolveDrivingCameras()
        {
            int frame = Time.frameCount;
            if (frame == resolvedDrivingFrame)
                return resolvedDrivingCount;

            resolvedDrivingFrame = frame;
            resolvedDrivingCount = 0;

            // Sweep first so the last destroyed registration falls back to main
            // in this resolution, while already-resolved frames keep their snapshot.
            for (int i = RegisteredDrivingCameras.Count - 1; i >= 0; i--)
            {
                if (RegisteredDrivingCameras[i] == null)
                {
                    ClearFadeSnap(RegisteredDrivingCameras[i]);
                    RegisteredDrivingCameras.RemoveAt(i);
                    DrivingCameraGeneration++;
                }
            }

            if (RegisteredDrivingCameras.Count == 0)
            {
                Camera main = Camera.main;
                if (main != null)
                {
                    ResolvedDrivingCameras[0] = main;
                    ResolvedDrivingPositions[0] = main.transform.position;
                    resolvedDrivingCount = 1;
                }

                return resolvedDrivingCount;
            }

            for (int i = 0; i < RegisteredDrivingCameras.Count; i++)
            {
                Camera camera = RegisteredDrivingCameras[i];
                ResolvedDrivingCameras[resolvedDrivingCount] = camera;
                ResolvedDrivingPositions[resolvedDrivingCount] = camera.transform.position;
                resolvedDrivingCount++;
            }

            return resolvedDrivingCount;
        }

        internal static Vector3 GetDrivingCameraPosition(int index)
        {
            return ResolvedDrivingPositions[index];
        }

        internal static Camera GetDrivingCamera(int index)
        {
            return ResolvedDrivingCameras[index];
        }

        public static int DrivingCameraCount => ResolveDrivingCameras();

        // Distance from the nearest driving camera to the surface of a
        // world-space AABB: the one distance metric every per-owner decision uses
        // (an AABB surface, not a bounding sphere, because the sphere of a large
        // owner bulges far above and below it and would make a switch fire much
        // too late when looking down).
        //
        // Nearest, not first: a representation has to be good enough for the most
        // demanding camera looking at it, and taking the minimum makes "lights are
        // on whenever any driving camera sees the source" hold without a separate
        // rule. Returns +inf when no camera drives, which every caller treats as
        // "do nothing this frame".
        internal static float MinSurfaceDistance(Bounds bounds)
        {
            int count = ResolveDrivingCameras();
            if (count == 0)
                return float.PositiveInfinity;

            Vector3 extents = bounds.extents;
            Vector3 center = bounds.center;
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Vector3 delta = ResolvedDrivingPositions[i] - center;
                float x = Mathf.Max(0f, Mathf.Abs(delta.x) - extents.x);
                float y = Mathf.Max(0f, Mathf.Abs(delta.y) - extents.y);
                float z = Mathf.Max(0f, Mathf.Abs(delta.z) - extents.z);
                float distance = Mathf.Sqrt(x * x + y * y + z * z);
                if (distance < best)
                    best = distance;
            }

            return best;
        }

    }
}
