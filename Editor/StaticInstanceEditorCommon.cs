using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StaticInstances.Editor
{
    // Reusable inspector building blocks for StaticInstanceScatter.
    internal static class StaticInstanceEditorCommon
    {
        // -- BRG Variants warning ------------------------------------------------

        // Shown here rather than in the Graphics settings section so a project that merely installs
        // the package (without using SIS) never sees the warning; a build with that content also
        // fails fast on this via the shader stripper's validation.
        internal static void DrawBrgVariantsWarning()
        {
            if (UnityEditor.Rendering.EditorGraphicsSettings.batchRendererGroupShaderStrippingMode ==
                UnityEditor.Rendering.BatchRendererGroupStrippingMode.KeepAll)
                return;

            EditorGUILayout.HelpBox(
                "Project Settings > Graphics > Shader Stripping > " +
                "BatchRendererGroup Variants must be 'Keep All' — without " +
                "it the Static Instance Group content will not render in " +
                "builds.",
                MessageType.Error);
            if (GUILayout.Button(new GUIContent("Open Project Settings > Graphics ...", "Opens the page holding the BatchRendererGroup Variants setting.")))
                SettingsService.OpenProjectSettings("Project/Graphics");
        }

        // -- Bake status ----------------------------------------------------------

        // One-line bake status: the bake/strip pipeline itself runs from
        // Tools/Static Instances/... (StaticInstanceBakeUtility) — this is
        // read-only status, not a control surface. The cache
        // (cachedStatus/cachedType) is owned by the
        // caller: the hash check walks the whole hierarchy, so each editor
        // instance caches per selection and invalidates on selection change /
        // undo (OnUndoRedoPerformed clearing cachedStatus to null).
        // Above this many selected objects the per-target walk is abandoned
        // and the line reports what the first slice found. A precise "M of N"
        // is worth a bounded amount of work per repaint, not an unbounded one.
        private const int BakeStatusScanLimit = 64;

        internal static void DrawBakeStatus(
            MonoBehaviour target,
            Object[] selection,
            bool multiEditing,
            string componentNoun,
            ref string cachedStatus,
            ref MessageType cachedType)
        {
            if (cachedStatus == null)
            {
                if (multiEditing)
                    RefreshMultiBakeStatus(selection, componentNoun, ref cachedStatus, ref cachedType);
                else
                    RefreshBakeStatus(target, ref cachedStatus, ref cachedType);
            }

            EditorGUILayout.HelpBox(cachedStatus, cachedType);
        }

        // The status line is the single source of truth about whether a bake will happen, so it
        // consults Validate: a promise of automatic baking would be false whenever validation
        // blocks it.
        private static void RefreshBakeStatus(
            MonoBehaviour target, ref string cachedStatus, ref MessageType cachedType)
        {
            int blocking = StaticInstanceBakeUtility.CountBlockingErrors(target);
            if (blocking > 0)
            {
                cachedStatus =
                    $"Bake blocked by {blocking} validation error(s): Play Mode falls back to " +
                    "runtime collection and a build fails outright. Run Validate " +
                    "(Project Settings > Graphics > Static Instances) for the list.";
                cachedType = MessageType.Error;
                return;
            }

            var bakeTarget = (IStaticInstanceBakeTarget)target;
            if (!bakeTarget.HasBakedData)
            {
                cachedStatus = "Not baked — entering Play Mode or building bakes automatically.";
                cachedType = MessageType.None;
                return;
            }

            int instances = bakeTarget.BakedInstanceCount;
            bool stale = !Application.isPlaying && StaticInstanceBakeUtility.IsBakeStale(target);
            if (stale)
            {
                cachedStatus = $"Baked {instances:N0} instances — stale, re-baked automatically at the next Play or build.";
                cachedType = MessageType.Info;
            }
            else
            {
                cachedStatus = $"Baked {instances:N0} instances (cache fresh).";
                cachedType = MessageType.None;
            }
        }

        // The multi-selection form of the same truth. Bake counts stay a single-selection affair
        // (they are per target, with no useful aggregate), but "will these bake at all" is the
        // question a multi-selection needs answered.
        private static void RefreshMultiBakeStatus(
            Object[] selection,
            string componentNoun,
            ref string cachedStatus,
            ref MessageType cachedType)
        {
            int blocked = 0;
            int scanned = 0;
            foreach (Object candidate in selection)
            {
                if (scanned >= BakeStatusScanLimit)
                    break;
                if (!(candidate is MonoBehaviour behaviour) || behaviour == null)
                    continue;

                scanned++;
                if (StaticInstanceBakeUtility.CountBlockingErrors(behaviour) > 0)
                    blocked++;
            }

            bool truncated = selection.Length > scanned;
            string consequence =
                " Play Mode falls back to runtime collection and a build fails outright.";

            if (blocked > 0)
            {
                cachedStatus = truncated
                    ? $"At least {blocked} of the first {scanned} {componentNoun}s inspected are " +
                      $"blocked by validation errors ({selection.Length} selected)." + consequence
                    : $"{blocked} of {selection.Length} selected {componentNoun}s are blocked by " +
                      "validation errors." + consequence;
                cachedType = MessageType.Error;
                return;
            }

            cachedStatus = truncated
                ? $"{selection.Length} {componentNoun}s selected — none of the first {scanned} " +
                  "inspected is blocked; select fewer for an exact count."
                : $"{selection.Length} {componentNoun}s selected, none blocked — bake counts are " +
                  "shown for a single selection.";
            cachedType = MessageType.None;
        }

        // Spread ratio = owner AABB diagonal divided by average instance
        // bounding-sphere diameter. Compact content shows the diagnostic rows.
        internal const float SpreadRatioCompactThreshold = 50f;

        // The ratio walks every baked instance bound (or, unbaked, every
        // child renderer), so it is cached per selection like the bake
        // status and refreshed when the bake contents change.
        internal sealed class SpreadRatioCache
        {
            private Object cachedTarget;
            private int cachedForBakeState = int.MinValue;

            public float Ratio { get; private set; }
            public float Diagonal { get; private set; }
            public float AverageDiameter { get; private set; }

            public void Invalidate()
            {
                cachedTarget = null;
                cachedForBakeState = int.MinValue;
            }

            public void Refresh(MonoBehaviour target, int bakedInstanceCount)
            {
                if (cachedTarget == target && cachedForBakeState == bakedInstanceCount)
                    return;

                cachedTarget = target;
                cachedForBakeState = bakedInstanceCount;

                var union = new Bounds();
                double diameterSum = 0;
                int count = 0;
                var serialized = new SerializedObject(target);
                SerializedProperty baked = serialized.FindProperty("bakedBounds");
                if (baked != null && baked.arraySize > 0)
                {
                    count = baked.arraySize;
                    for (int i = 0; i < count; i++)
                    {
                        Bounds bounds = baked.GetArrayElementAtIndex(i).boundsValue;
                        if (i == 0)
                            union = bounds;
                        else
                            union.Encapsulate(bounds);
                        diameterSum += 2.0 * bounds.extents.magnitude;
                    }
                }
                else
                {
                    // Same source filter as the bake, so the unbaked preview
                    // measures the instances a bake would actually collect —
                    // including the ownership boundary, which is also what
                    // gives nesting its remedy value: moving a spread-out
                    // region into a nested Scatter takes it out of this ratio.
                    foreach (MeshRenderer renderer in
                        StaticInstanceOwnership.OwnedComponents<MeshRenderer>(target))
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null ||
                            !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                            continue;

                        Bounds bounds = renderer.bounds;
                        if (count == 0)
                            union = bounds;
                        else
                            union.Encapsulate(bounds);
                        diameterSum += 2.0 * bounds.extents.magnitude;
                        count++;
                    }
                }

                // A single instance (or none) has no spread to measure.
                if (count < 2 || diameterSum <= 0)
                {
                    Ratio = 0f;
                    Diagonal = 0f;
                    AverageDiameter = 0f;
                    return;
                }

                Diagonal = union.size.magnitude;
                AverageDiameter = (float)(diameterSum / count);
                Ratio = AverageDiameter > 0f ? Diagonal / AverageDiameter : 0f;
            }
        }

        // Shows neutral spread measurements for compact SIS content.
        internal static void DrawSpreadRatioRows(
            SpreadRatioCache cache, MonoBehaviour target, int bakedInstanceCount)
        {
            cache.Refresh(target, bakedInstanceCount);
            if (cache.Ratio <= 0f || cache.Ratio > SpreadRatioCompactThreshold)
                return;

            EditorGUILayout.LabelField(
                new GUIContent(
                    "Spread Diagonal",
                    "World-space union AABB diagonal in metres, used as the " +
                    "spread ratio numerator."),
                new GUIContent($"{cache.Diagonal:F1} m"));
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Mean Instance Size",
                    "Mean instance bounding-sphere diameter in metres. The " +
                    "displayed ratio is Spread Diagonal divided by this value."),
                new GUIContent(
                    $"{cache.AverageDiameter:F1} m  (ratio {cache.Ratio:F0}x)"));
        }

        // -- Missing-mesh-LOD-level cache and warning ------------------------------

        // Which source meshes fall short of a required mesh LOD level. Walking every source mesh is
        // too expensive per repaint, so results are cached per selection and invalidated when the
        // required level or the bake contents change (see RefreshMissingLevelCache).
        internal sealed class MissingLevelCache
        {
            private Object cachedTarget;
            private int cachedForLevel = int.MinValue;
            private int cachedForBakeState = int.MinValue;

            public int MissingMeshes { get; private set; }
            public int TotalMeshes { get; private set; }
            public int MissingInstances { get; private set; }
            public int TotalInstances { get; private set; }
            public string List { get; private set; } = string.Empty;

            // View state of the foldout list, not cached data: the scroll
            // position when the list is taller than the box cap. Lives here
            // because the cache is already the per-editor-instance state the
            // shared drawer receives.
            public Vector2 ListScroll;

            public void Invalidate()
            {
                cachedTarget = null;
                cachedForLevel = int.MinValue;
                cachedForBakeState = int.MinValue;
            }

            public void Refresh(
                Object target,
                int requiredLevel,
                int bakedInstanceCount,
                System.Action<Dictionary<Mesh, int>> collectSourceMeshes)
            {
                if (cachedTarget == target &&
                    cachedForLevel == requiredLevel &&
                    cachedForBakeState == bakedInstanceCount)
                    return;

                cachedTarget = target;
                cachedForLevel = requiredLevel;
                cachedForBakeState = bakedInstanceCount;

                var meshes = new Dictionary<Mesh, int>();
                collectSourceMeshes(meshes);

                MissingMeshes = 0;
                TotalMeshes = meshes.Count;
                MissingInstances = 0;
                TotalInstances = 0;
                var names = new StringBuilder();
                foreach (var pair in meshes)
                {
                    TotalInstances += pair.Value;
                    if (requiredLevel <= 0 || pair.Key.lodCount - 1 >= requiredLevel)
                        continue;

                    MissingMeshes++;
                    MissingInstances += pair.Value;
                    names.Append(pair.Key.name)
                        .Append("  (lodCount ").Append(pair.Key.lodCount)
                        .Append(", ").Append(pair.Value).Append(" instances)\n");
                }

                // No trailing newline: the box is sized at 14 px per line
                // plus a little slack (DrawMissingLevelWarning), and a
                // phantom empty last line would eat exactly that slack,
                // leaving the text flush against the bottom edge.
                List = names.ToString().TrimEnd('\n');
            }
        }

        // Summary HelpBox plus a foldout list of offending meshes. In large scenes nearly
        // every mesh lacks the level, and a bare name list would just be
        // scrolled past.
        internal static void DrawMissingLevelWarning(
            MissingLevelCache cache, int requiredLevel, string foldoutKey)
        {
            if (cache.MissingMeshes <= 0)
                return;

            EditorGUILayout.HelpBox(
                $"Level {requiredLevel} is missing from {cache.MissingMeshes} of " +
                $"{cache.TotalMeshes} source meshes ({cache.MissingInstances:N0} of " +
                $"{cache.TotalInstances:N0} instances). Those are drawn at the " +
                "coarsest level they have — enable Mesh LODs on the model " +
                "importer, or accept that small meshes get no chain.",
                MessageType.Info);
            if (Foldout(foldoutKey, new GUIContent("Meshes without the level", "Every source mesh whose LOD chain is too short, with its level count and instance count.")))
            {
                EditorGUI.indentLevel++;
                // Sized to the content up to a cap. A list taller than the cap scrolls instead of
                // clipping, since a fixed-height label cuts the text at the bottom border.
                float contentHeight = 14f * cache.MissingMeshes + 8f;
                if (contentHeight <= 160f)
                {
                    EditorGUILayout.SelectableLabel(
                        cache.List,
                        EditorStyles.textArea,
                        GUILayout.Height(contentHeight));
                }
                else
                {
                    cache.ListScroll = EditorGUILayout.BeginScrollView(
                        cache.ListScroll, GUILayout.Height(160f));
                    EditorGUILayout.SelectableLabel(
                        cache.List,
                        EditorStyles.textArea,
                        GUILayout.Height(contentHeight));
                    EditorGUILayout.EndScrollView();
                }

                EditorGUI.indentLevel--;
            }
        }

        // -- Draw Range row ---------------------------------------------------------

        internal static readonly GUIContent DrawRangeLabel = new GUIContent(
            "Draw Range",
            "Near/far limits in metres, independent of mesh LOD; they also " +
            "control shadow visibility. Distance uses sphere-aware thresholds, " +
            "while Screen Size uses absolute center distances.");

        internal static readonly GUIContent DrawRangeMinLabel = new GUIContent(
            "Min",
            "Nearest drawing distance in metres (0 = no near limit), not a " +
            "near-clip plane. Raising Min past a nonzero Max also raises Max.");

        internal static readonly GUIContent DrawRangeMaxLabel = new GUIContent(
            "Max",
            "Farthest drawing distance in metres (0 = no authored limit); " +
            "lowering a nonzero Max below Min also lowers Min. Perspective SIS " +
            "uses this instead of near/far clip planes, so set Max when a " +
            "distance cap is required.");

        // The two ends of one band (the culling job tests them as one), so they
        // share a row. They stay two float properties rather than a Vector2 so
        // each keeps its own [Min(0f)] clamp and its own tooltip — "0" means
        // "no near limit" on one end and "unlimited" on the other, which a
        // single shared tooltip cannot carry.
        internal static void DrawRangeRow(SerializedProperty min, SerializedProperty max)
        {
            Rect rect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(), DrawRangeLabel);

            // PrefixLabel has already consumed the indent and reserved the
            // label column; the sub-fields must not re-apply either.
            int previousIndent = EditorGUI.indentLevel;
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUI.indentLevel = 0;
            EditorGUIUtility.labelWidth = 30f;

            const float spacing = 4f;
            float fieldWidth = (rect.width - spacing) * 0.5f;
            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(
                new Rect(rect.x, rect.y, fieldWidth, rect.height),
                min, DrawRangeMinLabel);
            bool minEdited = EditorGUI.EndChangeCheck();

            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(
                new Rect(rect.x + fieldWidth + spacing, rect.y, fieldWidth, rect.height),
                max, DrawRangeMaxLabel);
            bool maxEdited = EditorGUI.EndChangeCheck();

            EditorGUIUtility.labelWidth = previousLabelWidth;
            EditorGUI.indentLevel = previousIndent;

            if (minEdited || maxEdited)
                PushDrawRangePartner(min, max, minEdited);
        }

        // Min at or past a non-zero Max is unauthorable, resolved with LOD-Group boundary-push
        // semantics: the edited field wins and its partner follows. Typed values are never
        // discarded (a clamp would delete what the user just typed).
        //
        // The 0 sentinel is exempt at both ends. Max = 0 means unlimited, so no Min can pass it;
        // Min = 0 means no near limit and cannot be past anything.
        //
        // Per target, not per selection: each component resolves against its own partner, so a
        // selection whose Maxes differ gets each one pushed to its own new value. That needs the
        // typed value landed first (hence the ApplyModifiedProperties before the walk) and one
        // SerializedObject per target, the only way to read a partner that has multiple different
        // values.
        //
        // The separation is the same 0.1 m the ladder bar's drag uses between neighbouring
        // boundaries; pushing to exactly equal would author the degenerate pair the runtime has to
        // warn about.
        private const float DrawRangeMinSeparation = 0.1f;

        // internal so its test drives this exact walk, including the per-target half.
        internal static void PushDrawRangePartner(
            SerializedProperty min, SerializedProperty max, bool minEdited)
        {
            SerializedObject serialized = min.serializedObject;
            string minPath = min.propertyPath;
            string maxPath = max.propertyPath;
            serialized.ApplyModifiedProperties();

            foreach (Object target in serialized.targetObjects)
            {
                if (target == null)
                    continue;

                var one = new SerializedObject(target);
                SerializedProperty targetMin = one.FindProperty(minPath);
                SerializedProperty targetMax = one.FindProperty(maxPath);
                if (targetMin == null || targetMax == null)
                    continue;

                float minValue = targetMin.floatValue;
                float maxValue = targetMax.floatValue;
                if (maxValue <= 0f || minValue < maxValue)
                    continue;

                if (minEdited)
                    targetMax.floatValue = minValue + DrawRangeMinSeparation;
                else
                    targetMin.floatValue = Mathf.Max(0f, maxValue - DrawRangeMinSeparation);

                one.ApplyModifiedProperties();
            }

            serialized.Update();
        }

        // -- LOD Level Range row --------------------------------------------------

        internal static readonly GUIContent LevelRangeLabel = new GUIContent(
            "LOD Level Range",
            "Mesh levels used by the ladder; moving the range retains authored " +
            "boundaries. Locked in Play Mode; use the project Min Level for a " +
            "live quality floor.");

        // Min/Max mesh LOD level as one MinMaxSlider row (float drag,
        // snapped to int) with int readouts at each end. Window semantics:
        // both ends are structural (together they decide the boundary count
        // and which levels the segments map to), so while playing the whole
        // row is shown disabled with the same layout. The runtime quality
        // floor is the global StaticInstanceLodSystem.MinMeshLodLevel only.
        // Only ever writes through min/max; the caller's own post-edit handling
        // (lodDistances regrow on every DrawLodDistances call, not on a change
        // callback — see GrowDistances) is unaffected.
        // `distances` is grown here, on the user's edit, and nowhere else. Growing it on every
        // repaint would make the Inspector write serialized data with no user input; that write is
        // invisible to Undo, so an undo landing on a short array would be re-applied by the next
        // repaint. Raising Max is the only thing that can create the shortfall, so it is the only
        // place that needs to fix it.
        internal static void DrawLevelRange(
            SerializedProperty min, SerializedProperty max, int maxAuthorable, bool locked,
            SerializedProperty distances, SerializedProperty ratios = null)
        {
            bool windowChanged = false;
            Rect rect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(), LevelRangeLabel);

            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            const float fieldWidth = 28f;
            const float spacing = 4f;

            using (new EditorGUI.DisabledScope(locked))
            {
                var minFieldRect = new Rect(rect.x, rect.y, fieldWidth, rect.height);
                var sliderRect = new Rect(
                    rect.x + fieldWidth + spacing, rect.y,
                    rect.width - (fieldWidth + spacing) * 2f, rect.height);
                var maxFieldRect = new Rect(rect.xMax - fieldWidth, rect.y, fieldWidth, rect.height);

                float minValue = min.intValue;
                float maxValue = max.intValue;
                EditorGUI.BeginChangeCheck();
                EditorGUI.MinMaxSlider(sliderRect, ref minValue, ref maxValue, 0, maxAuthorable);
                if (EditorGUI.EndChangeCheck())
                {
                    // Max is 1..maxAuthorable, never 0: a 0-0 window is a single
                    // level 0, which is exactly what LOD Enabled = false already
                    // says — offering it as a range only duplicates the checkbox.
                    int snappedMin = Mathf.Clamp(Mathf.RoundToInt(minValue), 0, maxAuthorable);
                    int snappedMax = Mathf.Clamp(Mathf.RoundToInt(maxValue), 1, maxAuthorable);
                    if (snappedMin > snappedMax)
                        snappedMin = snappedMax;
                    min.intValue = snappedMin;
                    max.intValue = snappedMax;
                    windowChanged = true;
                }

                // Direct int entry at each end, independent of the slider drag
                // above — only one of the two ever consumes a given GUI event,
                // so there is no ordering conflict between them.
                EditorGUI.BeginChangeCheck();
                int fieldMin = EditorGUI.DelayedIntField(minFieldRect, min.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    min.intValue = Mathf.Clamp(fieldMin, 0, max.intValue);
                    windowChanged = true;
                }

                EditorGUI.BeginChangeCheck();
                int fieldMax = EditorGUI.DelayedIntField(maxFieldRect, max.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    // Same floor of 1 as the slider snap above.
                    max.intValue = Mathf.Clamp(
                        fieldMax, Mathf.Max(1, min.intValue), maxAuthorable);
                    windowChanged = true;
                }
            }

            EditorGUI.indentLevel = previousIndent;

            // The window just moved, so the ladder may need boundaries it does
            // not have. Growing here gives the everyday path (raise Max, get a
            // boundary) while leaving repaints read-only.
            // BOTH ladders grow here. The window's boundary
            // count is mode-independent -- switching Mode must not land the
            // component on a short ladder -- and this is the one user edit
            // that changes that count, so it is the one place either array may
            // grow. Growing the inactive mode's ladder costs an unread array
            // entry; NOT growing it costs a component that silently pins to
            // level 0 the moment its author switches mode.
            if (windowChanged)
            {
                GrowDistancesForWindow(min, max, distances);
                GrowRatiosForWindow(min, max, ratios);
            }
        }

        // Grow to the window's boundary count, never shrink: lowering the
        // count is often a step in the middle of an edit and truncating would
        // throw the authored value away for good. The runtime needs at least
        // `boundaryCount` entries and ignores extras.
        internal static void GrowDistancesForWindow(
            SerializedProperty min, SerializedProperty max, SerializedProperty distances)
        {
            if (distances == null || min.hasMultipleDifferentValues ||
                max.hasMultipleDifferentValues)
            {
                return;
            }

            int boundaryCount = Mathf.Max(0, max.intValue - min.intValue);
            if (distances.arraySize < boundaryCount)
                GrowDistances(distances, boundaryCount);
        }

        // GrowDistancesForWindow's ratio twin: the only writers of the array are user edits (the
        // window edit and the shortfall button). Growing from the ladder's draw call would make
        // every repaint a write and truncate the array whenever the window narrowed.
        internal static void GrowRatiosForWindow(
            SerializedProperty min, SerializedProperty max, SerializedProperty ratios)
        {
            if (ratios == null || min.hasMultipleDifferentValues ||
                max.hasMultipleDifferentValues)
            {
                return;
            }

            int boundaryCount = Mathf.Max(0, max.intValue - min.intValue);
            if (ratios.arraySize < boundaryCount)
                GrowRatios(ratios, boundaryCount);
        }

        // -- Fade Duration field ------------------------------------------------

        // Shared labels: both components describe the same concepts, so
        // they must describe them in the same words.
        internal static readonly GUIContent FadeModeLabel = new GUIContent(
            "Fade Mode",
            "Controls LOD and Draw Range transitions: None hard-switches, " +
            "Distance blends across Fade Width, and Time blends over Fade " +
            "Duration. Requires project fades and URP LOD Cross Fade support.");

        // "Fade Width" is used because the value governs the Draw Range limits' bands as
        // well as the LOD boundaries' (the eval job takes both the near and far draw-range terms
        // from it in Distance mode), and "Cross Fade" names only the level-to-level half. It also
        // lines up with Fade Mode / Fade Duration / Fade Curve. The serialized field is named
        // crossFadeWidth, as is the public CrossFadeWidth the eligibility predicate reads.
        internal static readonly GUIContent CrossFadeWidthLabel = new GUIContent(
            "Fade Width",
            "Distance-mode band width in metres (0 = hard boundary with " +
            "hysteresis). Authored limits use this width; size-derived far " +
            "limits use Derived Cull Fade Fraction.");

        // One value, both boundary families: every hard boundary is guarded by a band or by
        // hysteresis, and the margin tracks camera jitter amplitude, not scene size, whichever
        // boundary kind is crossed. So the LOD boundaries and the Draw Range limits share one
        // per-component value.
        internal static readonly GUIContent HysteresisLabel = new GUIContent(
            "Hysteresis",
            "Return margin in metres to reduce jitter at LOD and Draw Range " +
            "boundaries. Disabled when an active Distance fade band governs the " +
            "switch.");

        // The row itself, drawn in the pre-LOD strip by both inspectors (outside the LOD section
        // because that section hides itself whenever the ladder is inactive, while the draw-range
        // limits this value guards keep running).
        //
        // Greying: disabled only while a band governs BOTH sides, which — with
        // one value and one Fade Mode per component — is exactly the Distance
        // band case. In Time mode the row stays live, and deliberately: the
        // LOD side has no distance extent to blend over, and on the draw-range
        // side jitter at the limit can otherwise pump a full fade-out/fade-in
        // cycle repeatedly. A mixed selection greys nothing, matching
        // DrawFadeRows — with no single mode there is no single answer.
        //
        // `effectiveMode` must be the component's EffectiveFadeMode, so the
        // project-wide Cross Fade switch and the URP Asset gate are already
        // folded in: with either off there is no band, and the value governs
        // every boundary the component has.
        internal static void DrawHysteresisRow(
            SerializedProperty hysteresis,
            StaticInstanceFadeMode effectiveMode,
            SerializedProperty crossFadeWidth,
            SerializedProperty fadeMode)
        {
            bool mixed = fadeMode.hasMultipleDifferentValues ||
                crossFadeWidth.hasMultipleDifferentValues;
            bool bandGovernsBothSides = !mixed &&
                effectiveMode == StaticInstanceFadeMode.Distance &&
                crossFadeWidth.floatValue > 0f;

            using (new EditorGUI.DisabledScope(bandGovernsBothSides))
            {
                EditorGUILayout.PropertyField(hysteresis, HysteresisLabel);
            }
        }

        internal static readonly GUIContent FadeDurationLabel = new GUIContent(
            "Fade Duration",
            "Seconds for Time fades to finish, even when the camera stops. 0 " +
            "follows the project Fade > Duration; use None to disable fading.");

        // -- Fade rows (shared, drawn under the Draw Range row) -------------------

        // Shared by both inspectors and drawn under the Draw Range row rather than in the LOD
        // section: the LOD section's rows live behind `LodStagingActive`, so a knob that also
        // governs the always-running draw-range fade would be hidden along with it.
        //
        // `duration` is non-null for both components; the parameter stays nullable for callers that
        // draw the fade rows without one.
        internal static void DrawFadeRows(
            SerializedProperty fadeMode,
            SerializedProperty crossFadeWidth,
            SerializedProperty duration)
        {
            EditorGUILayout.PropertyField(fadeMode, FadeModeLabel);

            // The project-wide Cross Fade greys rather than hides, so the authored
            // values stay visible and come back when it is lifted. A mixed
            // selection greys nothing: with no single mode there is no single
            // answer about which row applies.
            bool crossFadeOn = StaticInstanceLodSystem.CrossFadeActive;
            bool mixed = fadeMode.hasMultipleDifferentValues;
            var authored = (StaticInstanceFadeMode)fadeMode.enumValueIndex;

            using (new EditorGUI.DisabledScope(
                !crossFadeOn || (!mixed && authored != StaticInstanceFadeMode.Distance)))
            {
                EditorGUILayout.PropertyField(crossFadeWidth, CrossFadeWidthLabel);
            }

            if (duration == null)
                return;

            using (new EditorGUI.DisabledScope(
                !crossFadeOn || (!mixed && authored != StaticInstanceFadeMode.Time)))
            {
                DrawFadeDuration(duration);
            }
        }

        // One row, 0 = follow the project default.
        internal static void DrawFadeDuration(SerializedProperty lodFadeDuration)
        {
            EditorGUILayout.Slider(lodFadeDuration, 0f, 1f, FadeDurationLabel);
        }

        // The state where a window spans more boundaries than the array authors. It is reachable by
        // a hand-edited scene, a Preset, a script that raises the window, and by Undo. The runtime
        // handles it deliberately: it pins the owner to level 0 and warns at Initialize naming both
        // counts, so the Inspector says the same thing and offers the fix as an action rather than
        // performing it on repaint.
        //
        // Returns how many boundaries the bar may actually draw.
        internal static int DrawLadderShortfall(
            SerializedProperty min, SerializedProperty max,
            SerializedProperty distances, int boundaryCount)
        {
            int authored = Mathf.Clamp(distances.arraySize, 0, boundaryCount);
            if (authored >= boundaryCount)
                return authored;

            EditorGUILayout.HelpBox(
                $"This ladder spans {boundaryCount} boundaries but only {authored} " +
                (authored == 1 ? "is" : "are") + " authored, so at runtime the " +
                "component stays at its finest level — no intermediate LOD, and no " +
                "combined mesh on a Group. Raising the LOD Level Range's Max adds " +
                "the missing boundaries; the button does it without touching the " +
                "window.",
                MessageType.Warning);

            if (IndentedButton(new GUIContent(
                    $"Author the missing {boundaryCount - authored} boundaries",
                    "Adds missing boundaries at twice the previous value. " +
                    "Existing boundaries are preserved.")))
            {
                GrowDistances(distances, boundaryCount);
                return boundaryCount;
            }

            return authored;
        }

        // The Screen Size ladder: the ratio bar, the numeric rows beneath it, and the Draw Range
        // hint.
        //
        // The axis is linear in ratio, from 1.0 at the left edge to 0.0 at the right (|LOD 0|LOD
        // 1|Culled| over 1.0 / 0.5 / 0.25 / 0.0). A ratio axis is used because a distance axis in
        // this mode would need one representative radius, and per-instance radii make that
        // impossible. Near is on the left on both bars (the camera is the left edge either way),
        // which is why the ratio axis runs down where the distance axis runs up.
        //
        // Unlike the distance bar, the axis does not rescale to its content: a ratio is bounded, so
        // there is a true full extent to draw, and rescaling would make two components' bars
        // incomparable and reintroduce the runaway drag the distance bar needs a clamp for.
        //
        // Not drawn here, because a quantity in metres has no single position on a ratio axis: the
        // Draw Range's Culled bands (the hint line under the bar says the Max is not inert) and the
        // fade / hysteresis band overlay.
        //
        // Also unlike the distance bar, the Culled boundary here is draggable: on the distance bar
        // Culled is the Draw Range, edited on its own row, whereas here it is this mode's own far
        // cutoff and an authored member of the ladder, so it carries a handle like every other
        // boundary.
        internal static void DrawScreenSizeLadder(
            SerializedProperty ratios,
            SerializedProperty cullRatio,
            SerializedProperty minDrawDistance,
            SerializedProperty cullDistance,
            int boundaryCount,
            System.Func<int, string> segmentName,
            System.Func<int, bool> isHighlightedSegment = null,
            System.Func<int, bool> isLiftedSegment = null)
        {
            if (boundaryCount <= 0)
            {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        RatiosLabel.text,
                        "Raise the LOD Level Range's Max to get a second level to switch to."),
                    new GUIContent("One level only (nothing to switch between)"));
                return;
            }

            // Multi-edit, decided exactly as the distance bar decides it: draw
            // the real bar whenever the selection agrees on what it would
            // draw, and substitute the mixed placeholder only for a genuine
            // disagreement. Every write below goes through SerializedProperty,
            // so a drag lands on the whole selection.
            if (ratios.serializedObject.isEditingMultipleObjects &&
                !LodRatiosAgree(ratios, cullRatio, boundaryCount))
            {
                Rect mixedRect = EditorGUILayout.GetControlRect(true, 34f);
                mixedRect = EditorGUI.PrefixLabel(mixedRect, new GUIContent(
                    RatiosLabel.text,
                    "Selected components have different ratio ladders. Select " +
                    "one to edit its boundaries."));
                DrawLodBarMixed(mixedRect, Mathf.Max(1, boundaryCount), segmentName);
                DrawMixedLadderNotice("Ratios differ across the selection");
                return;
            }

            // The same discipline on this axis: a window spanning more
            // boundaries than the array authors is REPORTED and offered as an
            // action, never fixed by a repaint.
            boundaryCount = DrawRatioLadderShortfall(ratios, boundaryCount);
            if (boundaryCount == 0)
                return;

            Rect barRect = EditorGUILayout.GetControlRect(true, 34f);
            barRect = EditorGUI.PrefixLabel(barRect, RatiosLabel);
            DrawRatioBar(
                barRect, ratios, cullRatio, boundaryCount, segmentName,
                isHighlightedSegment, isLiftedSegment);

            // The numeric rows stay beneath the bar: the drag GUI cannot express fine adjustments,
            // so they are the only way to make them.
            DrawScreenSizeLadderRows(ratios, cullRatio, boundaryCount, segmentName);
            DrawScreenSizeDrawRangeHint(minDrawDistance, cullDistance);
        }

        // "Ratios", to sit beside the distance ladder's "Distances": the two
        // are one control in two units, and naming this one after the mode
        // ("Screen Size") would only repeat the Mode row directly above it.
        internal static readonly GUIContent RatiosLabel = new GUIContent(
            "Ratios",
            "Screen-height ratio decreases from 1 to 0; drag LOD or Culled " +
            "boundaries. Metre-based Fade Width, Hysteresis and Draw Range are " +
            "not plotted.");

        // The ratio axis, as a pair of seams rather than arithmetic inside the draw. The bar draws
        // with one and drags with the other; if the two ever stop being inverses the handle slides
        // out from under the cursor, with no exception, log or failing frame. They are trivial on
        // purpose: the value is that the round trip is pinned by tests.
        // Fraction 0 is the bar's left edge and ratio 1.0 (near, filling the
        // view); fraction 1 is the right edge and ratio 0.0. That is the
        // 1.0 / 0.5 / 0.25 / 0.0 layout read left to right.
        internal static float RatioBarFraction(float ratio) =>
            1f - Mathf.Clamp01(ratio);

        // The percent display: the rows and the bar's readouts show 100 / 50 / 25 (LOD Group's
        // vocabulary) while storage stays a ratio float, and every clamp, comparison and runtime
        // read is in ratios. The conversion is display-layer only and lives in these two seams so
        // it cannot leak into storage (a value written back 100 times too large or too small would not
        // be caught by any clamp, since both ends are legal ratios).
        //
        // Both seams go through double and round the percent to 4 decimals: `percent * 0.01f` in
        // float turns a typed 10 into 0.099999994, shown as 9.999999, whereas dividing by an
        // exactly representable 100 in double and casting once yields the float nearest the true
        // ratio, and the reverse trip lands back on the typed constant.
        internal static float RatioToPercent(float ratio) =>
            (float)System.Math.Round((double)ratio * 100.0, 4);

        internal static float PercentToRatio(float percent) =>
            (float)(System.Math.Round((double)percent, 4) / 100.0);

        internal static float RatioAtBarFraction(float fraction) =>
            1f - Mathf.Clamp01(fraction);

        // The ratio bar itself. Geometry notes are on DrawScreenSizeLadder above.
        //
        // One rule for "too narrow to label", shared by every label the bars draw: the threshold is
        // named once and every site asks it, so the ratio bar's boundary readouts hide the same way
        // the `LOD [n]` labels do instead of drifting apart as two copies of a number.
        //
        // 30 px is the floor for a `LOD 0`-width miniLabel plus its padding; below it the text
        // either clips or collides with its neighbour.
        internal const float BarLabelMinWidth = 30f;

        internal static bool BarLabelFits(float width) => width > BarLabelMinWidth;

        internal static void DrawRatioBar(
            Rect rect,
            SerializedProperty ratios,
            SerializedProperty cullRatio,
            int boundaryCount,
            System.Func<int, string> segmentName,
            System.Func<int, bool> isHighlightedSegment = null,
            System.Func<int, bool> isLiftedSegment = null)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));

            // Ratio-linear over the full unit: 1.0 at the left edge, 0.0 at
            // the right. Unlike the distance bar there is no axisMax to pass —
            // the axis IS the unit.
            float PositionOf(float ratio) =>
                rect.x + rect.width * RatioBarFraction(ratio);

            // GUI.Label, never EditorGUI.LabelField — see DrawLodBar's header:
            // LabelField applies EditorGUI.indentLevel to an explicit rect, and
            // both inspectors draw this inside an indented section, so
            // "centered" text lands visibly right of center.
            var readoutStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };
            // To the left of the boundary line, ending just before it, so the number reads as this
            // boundary's ratio rather than floating between two segments (the distance bar's
            // convention). Shown as a percent, not a ratio: this ladder's display axis is percent,
            // and readouts in ratio above rows in percent would be two units for one number.
            //
            // Hidden when its segment is narrow, so two readouts never overlap; the `LOD [n]
            // Transition` rows below carry the same numbers. The readout belongs to the segment
            // ending at its line, so that segment's width decides, via `BarLabelFits`, the same
            // predicate the `LOD [n]` segment labels use.
            //
            // The distance bar (`DrawLodBar`) does not hide readouts: it has no Transition rows
            // below it, so hiding there would remove the only place the number appears.
            void DrawBoundaryReadout(float lineX, float ratio, float owningSegmentWidth)
            {
                if (!BarLabelFits(owningSegmentWidth))
                    return;
                GUI.Label(
                    new Rect(lineX - 51f, rect.yMax - 14f, 48f, 13f),
                    RatioToPercent(ratio).ToString("0.##") + "%", readoutStyle);
            }

            // The visible width of the segment that ENDS at `rightX` — visible
            // meaning the part the Culled band leaves uncovered, exactly as the
            // segment label's own rect is computed, so the readout and the
            // `LOD [n]` label appear and disappear together instead of at two
            // different widths.
            float OwningSegmentWidth(int boundaryIndex, float rightX)
            {
                float leftX = boundaryIndex > 0
                    ? PositionOf(ratios.GetArrayElementAtIndex(boundaryIndex - 1).floatValue)
                    : rect.x;
                float cullX = cullRatio != null && !cullRatio.hasMultipleDifferentValues &&
                              cullRatio.floatValue > 0f
                    ? PositionOf(Mathf.Clamp01(cullRatio.floatValue))
                    : rect.xMax;
                return Mathf.Max(0f, Mathf.Min(rightX, cullX) - leftX);
            }

            float cull = cullRatio != null && !cullRatio.hasMultipleDifferentValues
                ? Mathf.Clamp01(cullRatio.floatValue)
                : 0f;

            float previousX = rect.x;
            for (int level = 0; level <= boundaryCount; level++)
            {
                float nextX = level < boundaryCount
                    ? PositionOf(ratios.GetArrayElementAtIndex(level).floatValue)
                    : rect.xMax;
                var segment = new Rect(
                    previousX, rect.y, Mathf.Max(0f, nextX - previousX), rect.height);
                // Window semantics, as on the distance bar: `level` indexes the
                // window and the caller translates it to an absolute mesh LOD
                // level inside segmentName / isHighlightedSegment.
                bool isHighlighted =
                    isHighlightedSegment != null && isHighlightedSegment(level);
                bool isLifted = isLiftedSegment != null && isLiftedSegment(level);
                var fillRect = new Rect(
                    segment.x + 1f, segment.y + 1f,
                    Mathf.Max(0f, segment.width - 2f), segment.height - 2f);
                // The same palette as the distance bar, walked in the same
                // direction: level 0 is the near end on both, so the colours
                // agree between the two modes and a mode switch does not look
                // like a different control.
                Color fill = isHighlighted
                    ? new Color(0.72f, 0.36f, 0.12f)
                    : Color.Lerp(
                        new Color(0.24f, 0.52f, 0.30f),
                        new Color(0.30f, 0.36f, 0.52f),
                        boundaryCount > 0 ? level / (float)boundaryCount : 0f);
                if (isLifted)
                    fill = Color.Lerp(fill, new Color(0.30f, 0.30f, 0.30f), 0.75f);
                EditorGUI.DrawRect(fillRect, fill);

                // No transition-band overlay here: Fade Width and Hysteresis are in metres, and a
                // width in metres has no single position on a ratio axis.

                // The label centers in the segment's VISIBLE part — the part
                // the Culled band leaves uncovered — not in the full segment:
                // the band paints over the segments, so
                // a label centered on the full width sits visually inside the
                // dark band instead of over its level.
                float visibleRight = cull > 0f
                    ? Mathf.Min(segment.xMax, PositionOf(cull))
                    : segment.xMax;
                var labelRect = new Rect(
                    segment.x, segment.y,
                    Mathf.Max(0f, visibleRight - segment.x), segment.height);
                if (BarLabelFits(labelRect.width))
                {
                    var label = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.white },
                    };
                    GUI.Label(labelRect, segmentName(level), label);
                }

                previousX = nextX;
            }

            // Painted over the segments, not shortening them: a boundary that lands inside the band
            // is a level that can never draw, and seeing the handle inside the band makes that
            // authoring mistake visible. The clamps stop an edit from producing that state, but a
            // hand-edited scene, a Preset or a script still can.
            //
            // Drawn before the handles so the handles stay on top and legible.
            if (cull > 0f)
                DrawCulledBand(rect, PositionOf(cull), rect.xMax, "Culled");

            // The neighbours the clamps read, snapshotted BEFORE any handle
            // writes, so a boundary is judged against its neighbours' current
            // values rather than half-applied ones. Only one handle moves per
            // event and the clamp reads indices i-1 / i+1, never i, so the
            // snapshot is exactly right.
            var live = new float[ratios.arraySize];
            for (int i = 0; i < live.Length; i++)
                live[i] = ratios.GetArrayElementAtIndex(i).floatValue;

            for (int i = 0; i < boundaryCount; i++)
            {
                SerializedProperty element = ratios.GetArrayElementAtIndex(i);
                float x = PositionOf(element.floatValue);
                DrawRatioHandle(rect, x, Color.white);
                DrawBoundaryReadout(x, element.floatValue, OwningSegmentWidth(i, x));

                int controlId = GUIUtility.GetControlID(FocusType.Passive);
                Event current = Event.current;
                var handle = new Rect(x - 3f, rect.y, 6f, rect.height);
                switch (current.GetTypeForControl(controlId))
                {
                    case EventType.MouseDown when handle.Contains(current.mousePosition):
                        GUIUtility.hotControl = controlId;
                        current.Use();
                        break;
                    case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    {
                        float fraction = Mathf.Clamp01(
                            (current.mousePosition.x - rect.x) / Mathf.Max(1f, rect.width));
                        // The axis descends left to right, so the mouse fraction is the ratio's
                        // complement. Every guard lives in the clamp helpers: a single flipped
                        // comparison here would let a boundary pass its neighbour and give a level
                        // zero extent.
                        element.floatValue = ClampRatioBoundary(
                            live, i, boundaryCount, RatioAtBarFraction(fraction),
                            cull);
                        current.Use();
                        break;
                    }

                    case EventType.MouseUp when GUIUtility.hotControl == controlId:
                        GUIUtility.hotControl = 0;
                        current.Use();
                        break;
                }
            }

            // The Culled boundary's own handle, drawn last so it sits above the band it bounds.
            //
            // The handle is always present: parked at the bar's right edge when unauthored, and
            // dragging it out of the edge authors the cull (LOD Group's own idiom). The
            // accidental-authoring risk is accepted, mitigated by Undo and by the Culled band
            // appearing the moment the value exists.
            //
            // It must be allocated unconditionally: `GetControlID` hands out ids in draw order, so
            // a handle allocated only `if (cull > 0f)` would drop out of the id sequence when a
            // drag drives the value to 0, shifting every control drawn after this bar (the numeric
            // rows below). `GUIUtility.hotControl` would then name a different control than the one
            // under the mouse, and that control would follow the drag.
            //
            // The clamp is stated and tested too: `ClampCullRatio` holds the cull strictly below
            // the coarsest authored boundary, so the handle stops at the last LOD boundary and
            // never displaces it (the distance ladder's boundary discipline, read on the descending
            // axis).
            if (cullRatio != null && !cullRatio.hasMultipleDifferentValues)
            {
                float cullX = PositionOf(cull);
                // Dimmer while unauthored: parked at the edge it is an
                // affordance, not a boundary the ladder currently has.
                DrawRatioHandle(
                    rect, cullX,
                    cull > 0f
                        ? new Color(0.85f, 0.85f, 0.85f)
                        : new Color(0.55f, 0.55f, 0.55f));
                // The Culled boundary's readout follows the same rule. Its owning segment is the
                // last ladder boundary to the cull line, so it is passed `boundaryCount` (one past
                // the last ladder index, which `OwningSegmentWidth` reads as "the segment after the
                // last boundary"). `DrawCulledBand` hides its own "Culled" text at the same width,
                // so the band and its number disappear together.
                if (cull > 0f)
                    DrawBoundaryReadout(
                        cullX, cull, OwningSegmentWidth(boundaryCount, cullX));

                int controlId = GUIUtility.GetControlID(FocusType.Passive);
                Event current = Event.current;
                // Widened inward at the parked position only: a 6 px handle centred on the bar's
                // right edge is half outside the bar, so the grab target for "drag it out" would be
                // 3 px wide.
                var handle = cull > 0f
                    ? new Rect(cullX - 3f, rect.y, 6f, rect.height)
                    : new Rect(cullX - 6f, rect.y, 6f, rect.height);
                switch (current.GetTypeForControl(controlId))
                {
                    case EventType.MouseDown when handle.Contains(current.mousePosition):
                        GUIUtility.hotControl = controlId;
                        current.Use();
                        break;
                    case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    {
                        float fraction = Mathf.Clamp01(
                            (current.mousePosition.x - rect.x) / Mathf.Max(1f, rect.width));
                        cullRatio.floatValue = ClampCullRatio(
                            live, boundaryCount, RatioAtBarFraction(fraction));
                        current.Use();
                        break;
                    }

                    case EventType.MouseUp when GUIUtility.hotControl == controlId:
                        GUIUtility.hotControl = 0;
                        current.Use();
                        break;
                }
            }
        }

        // One boundary line plus its resize cursor. Extracted only because the
        // ratio bar draws two kinds of boundary and they must look and behave
        // alike.
        private static void DrawRatioHandle(Rect bar, float x, Color color)
        {
            EditorGUIUtility.AddCursorRect(
                new Rect(x - 3f, bar.y, 6f, bar.height), MouseCursor.ResizeHorizontal);
            EditorGUI.DrawRect(new Rect(x - 1f, bar.y, 2f, bar.height), color);
        }

        // DrawLadderShortfall's ratio twin: the same rule in this mode's unit, reachable by the
        // same routes (a hand-edited scene, a Preset, a script that raises the window, and Undo)
        // and answered the same way: say what the state is and offer the fix as an action. A
        // repaint that writes is a repaint Undo cannot survive.
        //
        // Returns how many boundaries the bar may actually draw.
        internal static int DrawRatioLadderShortfall(
            SerializedProperty ratios, int boundaryCount)
        {
            int authored = Mathf.Clamp(ratios.arraySize, 0, boundaryCount);
            if (authored >= boundaryCount)
                return authored;

            EditorGUILayout.HelpBox(
                $"This ladder spans {boundaryCount} boundaries but only {authored} " +
                (authored == 1 ? "is" : "are") + " authored, so at runtime the " +
                "component stays at its finest level — no intermediate LOD, and no " +
                "combined mesh on a Group. Raising the LOD Level Range's Max adds " +
                "the missing boundaries; the button does it without touching the " +
                "window.",
                MessageType.Warning);

            if (IndentedButton(new GUIContent(
                    $"Author the missing {boundaryCount - authored} boundaries",
                    "Adds missing boundaries at half the previous value. " +
                    "Existing boundaries are preserved.")))
            {
                GrowRatios(ratios, boundaryCount);
                return boundaryCount;
            }

            return authored;
        }

        // The ratio ladder's agreement test, judged on what the bar actually reads. It includes the
        // Culled ratio where LodDistancesAgree does not include the Draw Range, deliberately: on
        // the distance bar a mixed limit simply draws no band (BarCullDistance answers 0), while
        // here the Culled boundary is a ladder member with a handle and a mixed value has no
        // position to put it at.
        internal static bool LodRatiosAgree(
            SerializedProperty ratios, SerializedProperty cullRatio, int boundaryCount)
        {
            if (cullRatio != null && cullRatio.hasMultipleDifferentValues)
                return false;

            SerializedProperty size = ratios.FindPropertyRelative("Array.size");
            if (size != null && size.hasMultipleDifferentValues)
                return false;

            int drawn = Mathf.Min(boundaryCount, ratios.arraySize);
            for (int i = 0; i < drawn; i++)
            {
                if (ratios.GetArrayElementAtIndex(i).hasMultipleDifferentValues)
                    return false;
            }

            return true;
        }

        // The numeric rows, beneath the bar. They are this mode's fine-adjustment surface (see
        // DrawScreenSizeLadder), and each passes through its own clamp: the bar's drag handles and
        // these rows are two ways into the same guards, not two guard sets.
        //
        // The axis, restated where the numbers are typed: a ratio is the fraction of screen height
        // the instance's sphere covers, so 1.0 is near and small values are far; the ladder
        // descends. Every clamp called here is the inverse of its `lodDistances` counterpart.
        //
        // These rows do not grow the array: growing belongs to the window edit and the shortfall
        // button, as on the distance side. Growing from a repaint would make every repaint a write
        // and truncate the ladder whenever the window narrows.
        internal static void DrawScreenSizeLadderRows(
            SerializedProperty ratios,
            SerializedProperty cullRatio,
            int boundaryCount,
            System.Func<int, string> segmentName)
        {
            // A snapshot the clamps read, so a boundary is judged against its
            // neighbours' CURRENT values rather than half-applied ones.
            var current = new float[ratios.arraySize];
            for (int i = 0; i < current.Length; i++)
                current[i] = ratios.GetArrayElementAtIndex(i).floatValue;

            int rows = Mathf.Min(boundaryCount, ratios.arraySize);
            for (int i = 0; i < rows; i++)
            {
                SerializedProperty element = ratios.GetArrayElementAtIndex(i);
                // LOD Group's vocabulary: the row names the level the transition leaves, not the
                // pair it sits between.
                string label = segmentName != null
                    ? segmentName(i) + " Transition (% Screen Size)"
                    : "Boundary " + i + " (% Screen Size)";

                EditorGUI.BeginChangeCheck();
                float authoredPercent = EditorGUILayout.Slider(
                    new GUIContent(
                        label,
                        "Screen-height percentage covered by the instance's " +
                        "bounding sphere at this transition. 100 fills the " +
                        "view; boundaries descend and cannot cross neighbours."),
                    RatioToPercent(element.floatValue),
                    RatioToPercent(MinAuthorableRatio),
                    100f);
                if (EditorGUI.EndChangeCheck())
                {
                    // Back to a ratio before the clamp: every guard in this file speaks ratios, and
                    // clamping a percentage against ratio neighbours would compare 50 with 0.25.
                    element.floatValue = ClampRatioBoundary(
                        current, i, boundaryCount,
                        PercentToRatio(authoredPercent), cullRatio.floatValue);
                }
            }

            EditorGUI.BeginChangeCheck();
            float culledPercent = EditorGUILayout.Slider(
                new GUIContent(
                    "Culled (% Screen Size)",
                    "Screen-height percentage below which instances stop " +
                    "drawing (0 = no ratio culling). Draw Range Max remains an " +
                    "absolute cap; the project size-cull scale is inactive in " +
                    "this metric."),
                RatioToPercent(cullRatio.floatValue),
                0f,
                100f);
            if (EditorGUI.EndChangeCheck())
            {
                cullRatio.floatValue = ClampCullRatio(
                    current, boundaryCount, PercentToRatio(culledPercent));
            }
        }

        // In Screen Size mode an authored Draw Range Max still applies, as an absolute metric
        // bound, but it stays off the ratio graph, so without this line the omission reads as the
        // Max being inert. It also points at expressing the far cutoff as the Culled ratio instead,
        // which keeps one cutoff in one unit rather than the same intent authored twice.
        //
        // An authored Draw Range Min is invisible on this axis for the same reason, so it is named
        // too.
        internal static void DrawScreenSizeDrawRangeHint(
            SerializedProperty minDrawDistance, SerializedProperty cullDistance)
        {
            bool maxAuthored = cullDistance != null &&
                !cullDistance.hasMultipleDifferentValues && cullDistance.floatValue > 0f;
            bool minAuthored = minDrawDistance != null &&
                !minDrawDistance.hasMultipleDifferentValues && minDrawDistance.floatValue > 0f;
            if (!maxAuthored && !minAuthored)
                return;

            string which = maxAuthored && minAuthored
                ? "Draw Range Min and Max"
                : maxAuthored ? "Draw Range Max" : "Draw Range Min";
            string message =
                $"The authored {which} also applies in this mode, in absolute " +
                "metres, and is not shown on a ratio axis.";
            if (maxAuthored)
            {
                message +=
                    " The effective far cutoff is whichever of the Culled ratio " +
                    "and the Draw Range Max is reached first — authoring the " +
                    "cutoff as a ratio instead keeps one cutoff in one unit.";
            }

            EditorGUILayout.HelpBox(message, MessageType.None);
        }

        // The mode row and the reference FOV that goes with it.
        //
        // Reference FOV is hidden in Screen Size mode and shown in Distance mode (hidden rather
        // than greyed, deliberately overriding the usual "grey, never hide" convention). The field
        // is not inert in Screen Size mode: it is the reference FOV such an owner is evaluated at
        // in a view with no usable FOV, so the tooltip keeps describing it.
        internal static void DrawLodModeRows(
            SerializedProperty lodMode, SerializedProperty referenceFov)
        {
            // "Transition Metric" shares vocabulary
            // with the `LOD n Transition (% Screen Size)` rows below.
            EditorGUILayout.PropertyField(
                lodMode,
                new GUIContent(
                    "Transition Metric",
                    "Selects mesh LOD by projected Screen Size or " +
                    "camera-to-AABB surface Distance in metres. Fade Mode " +
                    "controls the blend independently."));

            // Mixed selections keep the field: `hasMultipleDifferentValues` means the selection
            // spans both metrics, and hiding would make the row appear and disappear as the
            // selection changed and strand the Distance-mode members' value. Hide only when the
            // component is unambiguously in Screen Size mode.
            bool screenSize = !lodMode.hasMultipleDifferentValues &&
                (StaticInstanceLodMode)lodMode.enumValueIndex ==
                    StaticInstanceLodMode.ScreenSize;
            if (screenSize)
                return;

            EditorGUILayout.PropertyField(
                referenceFov,
                new GUIContent(
                    "Reference FOV",
                    "Reference vertical FOV in degrees for Distance with FOV " +
                    "Tracking, and Screen Size views without usable FOV. Seeded " +
                    "from a perspective Camera.main on add/reset, never tracked " +
                    "live."));
        }

        // The ratio ladder's authoring arithmetic, as seams before any UI calls them: the rules
        // live here, tested, and the bar calls them. The ratio ladder is descending where every
        // distance-ladder guard assumes ascending, so every comparison below is the inverse of its
        // `lodDistances` counterpart, and a single flipped operator would let a boundary pass its
        // neighbour and give a level zero extent that the state machine can never settle in.
        //
        // The unit: a ratio is the fraction of screen height the instance's sphere covers, so 1.0
        // is near and small values are far. Range `(0, 1]`.
        internal const float MinAuthorableRatio = 0.0001f;

        /// <summary>Is this ratio ladder strictly descending, i.e. valid?</summary>
        internal static bool IsRatioLadderDescending(
            System.Collections.Generic.IReadOnlyList<float> ratios, int boundaryCount)
        {
            if (ratios == null)
                return boundaryCount <= 0;

            int count = System.Math.Min(boundaryCount, ratios.Count);
            for (int i = 1; i < count; i++)
            {
                // STRICTLY: equal neighbours mean a level with zero extent,
                // which is the state the ascending guard also refuses.
                if (!(ratios[i] < ratios[i - 1]))
                    return false;
            }

            return true;
        }

        // The descending twin of `GrowDistances`. New entries halve the previous ratio, the same
        // doubling-per-level spacing on the ratio axis (half the screen coverage is roughly twice
        // the distance). Grown, never shrunk, for `GrowDistances`' reason: lowering the level count
        // mid-edit must not destroy authored values.
        //
        // The first entry defaults to 0.5 rather than 1.0: a boundary at 1.0 would mean "leave
        // level 0 the moment the instance stops filling the screen", which no author wants as a
        // starting point.
        internal static void GrowRatios(SerializedProperty ratios, int boundaryCount)
        {
            int previous = ratios.arraySize;
            // Grown, never shrunk (the contract in this method's header), enforced here rather than
            // only at the call sites: narrowing the LOD Level Range must not truncate the array.
            if (boundaryCount <= previous)
                return;

            ratios.arraySize = boundaryCount;
            for (int i = previous; i < boundaryCount; i++)
            {
                float previousValue = i > 0
                    ? ratios.GetArrayElementAtIndex(i - 1).floatValue
                    : 1f;
                ratios.GetArrayElementAtIndex(i).floatValue = Mathf.Max(
                    MinAuthorableRatio, previousValue * 0.5f);
            }
        }

        // The descending boundary-push clamp, the inverse of the ascending clamp the distance bar
        // applies while dragging.
        //
        // Boundary `index` may move freely between its neighbours but never past either, and never
        // past the authored ratio Culled boundary (the far end of this axis, so a lower bound here
        // where the distance axis has an upper one). A `cullRatio` of 0 means unauthored and
        // constrains nothing.
        internal static float ClampRatioBoundary(
            System.Collections.Generic.IReadOnlyList<float> ratios,
            int index, int boundaryCount, float value, float cullRatio)
        {
            // The neighbour ABOVE this boundary in ratio terms sits at index-1
            // (nearer the camera, larger ratio); the one BELOW sits at index+1.
            float upper = index > 0 && ratios != null && index - 1 < ratios.Count
                ? ratios[index - 1] - MinAuthorableRatio
                : 1f;
            float lower = index + 1 < boundaryCount && ratios != null && index + 1 < ratios.Count
                ? ratios[index + 1] + MinAuthorableRatio
                : MinAuthorableRatio;

            // The Culled boundary is the axis's far end: no LOD boundary may
            // sit at or beyond it, or the level it opens can never be seen.
            if (cullRatio > 0f)
                lower = Mathf.Max(lower, cullRatio + MinAuthorableRatio);

            // A window this tight is degenerate (neighbours already adjacent or a Culled boundary
            // crowding them): hold the value rather than snapping it somewhere the author did not
            // ask for.
            if (upper < lower)
                return Mathf.Clamp(value, MinAuthorableRatio, 1f);

            return Mathf.Clamp(value, lower, upper);
        }

        // The Culled ratio's own clamp — it must stay strictly BELOW the
        // coarsest authored LOD boundary, since culling before the last level
        // is reachable would make that level unreachable.
        internal static float ClampCullRatio(
            System.Collections.Generic.IReadOnlyList<float> ratios,
            int boundaryCount, float value)
        {
            if (value <= 0f)
                return 0f;

            float coarsest = 1f;
            if (ratios != null)
            {
                int count = System.Math.Min(boundaryCount, ratios.Count);
                for (int i = 0; i < count; i++)
                    coarsest = Mathf.Min(coarsest, ratios[i]);
            }

            float ceiling = Mathf.Max(
                MinAuthorableRatio, coarsest - MinAuthorableRatio);
            return Mathf.Clamp(value, MinAuthorableRatio, ceiling);
        }

        // Grown to match the boundary count (so raising the level count cannot
        // silently pin the component to level 0 at runtime) but never shrunk:
        // lowering the level count is often a step in the middle of an edit,
        // and truncating would throw away the authored value for good. The
        // runtime only requires at least boundaryCount entries and ignores
        // any extras.
        internal static void GrowDistances(SerializedProperty distances, int boundaryCount)
        {
            int previous = distances.arraySize;
            // The same in-method grow-never-shrink guard `GrowRatios` carries: `arraySize = count`
            // shrinks as readily as it grows, and the header above promises it never does.
            if (boundaryCount <= previous)
                return;

            distances.arraySize = boundaryCount;
            // New entries default to twice the previous boundary, the same
            // doubling-per-level spacing SetLodDistances lays out.
            for (int i = previous; i < boundaryCount; i++)
            {
                float previousValue = i > 0
                    ? distances.GetArrayElementAtIndex(i - 1).floatValue
                    : 50f;
                distances.GetArrayElementAtIndex(i).floatValue = previousValue * 2f;
            }
        }

        // -- Cross-fade band helpers ----------------------------------------------

        // Smallest gap the ladder has to work with: from 0 to the first
        // boundary, and between each consecutive pair after that.
        internal static float MinBoundarySpacing(SerializedProperty distances, int boundaryCount)
        {
            float min = float.MaxValue;
            float previous = 0f;
            for (int i = 0; i < boundaryCount; i++)
            {
                float current = distances.GetArrayElementAtIndex(i).floatValue;
                min = Mathf.Min(min, current - previous);
                previous = current;
            }

            return min;
        }

        // Advisory only: OnValidate clamps negative widths but does not know
        // about boundary spacing, so an overlap is legal — this just flags it
        // rather than silently clamping the authored value. Single-selection
        // only (the spacing is per component); callers gate on that.
        internal static void DrawBandOverlapWarning(
            SerializedProperty distances, int boundaryCount, float width)
        {
            if (width <= 0f || boundaryCount <= 0 ||
                distances.arraySize < boundaryCount)
                return;

            float minSpacing = MinBoundarySpacing(distances, boundaryCount);
            if (width > minSpacing)
            {
                EditorGUILayout.HelpBox(
                    $"Fade Width ({width:F1} m) exceeds the smallest " +
                    $"gap between boundaries ({minSpacing:F1} m) — adjacent " +
                    "fade bands will overlap. Not clamped; only flagged here.",
                    MessageType.Warning);
            }
        }

        // -- Global quality floor, as the inspectors should display it ------------

        // The mesh LOD floor the ladder visuals fold in. While playing the
        // statics are the live truth (game code may have driven them); in
        // edit mode the statics still hold their compile-time defaults, so
        // the authored value in the URP Global Settings container — what the
        // next Play will seed — is the honest preview.
        internal static int CurrentGlobalMinMeshLodLevel =>
            Application.isPlaying
                ? StaticInstanceLodSystem.MinMeshLodLevel
                : UnityEngine.Rendering.GraphicsSettings.TryGetRenderPipelineSettings(
                      out StaticInstanceGlobalSettings settings)
                    ? settings.minMeshLodLevel
                    : 0;

        // -- LOD bar core ------------------------------------------------------------

        // The LOD Group walk is per selection state, like the bake status and
        // the spread ratio: two walks per target (count and convertibility
        // come from one pass), refreshed on selection change, undo and any
        // hierarchy change — the conversion itself is a hierarchy change, so
        // the band clears as soon as it has done its job.
        internal sealed class LodGroupConversionCache
        {
            private bool valid;

            // Targets whose own subtree still holds at least one LOD Group.
            public int NeedCount { get; private set; }
            // ...of which at least one group the converter would actually take.
            public int ConvertibleCount { get; private set; }
            public int Total { get; private set; }
            // The GameObjects of the NeedCount targets, so Convert analyses
            // exactly the scope the band is talking about.
            public readonly List<GameObject> Roots = new List<GameObject>();

            public void Invalidate()
            {
                valid = false;
            }

            public void Refresh(Object[] selection)
            {
                if (valid)
                    return;

                valid = true;
                Total = 0;
                NeedCount = 0;
                ConvertibleCount = 0;
                Roots.Clear();

                foreach (Object candidate in selection)
                {
                    if (!(candidate is Component component) || component == null)
                        continue;

                    Total++;
                    StaticInstanceLodGroupConverter.InspectSubtree(
                        component, out int lodGroups, out int convertible);
                    if (lodGroups == 0)
                        continue;

                    NeedCount++;
                    if (convertible > 0)
                        ConvertibleCount++;
                    Roots.Add(component.gameObject);
                }
            }
        }

        // Nested SIS owners would claim overlapping renderer hierarchies, so
        // validation and builds reject them explicitly.
        internal static void DrawNestingWarning(Object[] selection)
        {
            foreach (Object candidate in selection)
            {
                if (!(candidate is Component owner) || owner == null)
                    continue;

                Component nested = StaticInstanceOwnership.FindUnsupportedNesting(owner);
                if (nested == null)
                    continue;

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    $"'{nested.name}' is a nested StaticInstanceScatter. Nested SIS " +
                    "owners are not supported; the bake and build refuse " +
                    "them. Split the content into separate, non-nested hierarchies.",
                    MessageType.Error);

                if (GUILayout.Button(new GUIContent(
                        "Select the nested component",
                        "Selects the inner component so it can be removed or replaced.")))
                {
                    Selection.activeObject = nested.gameObject;
                    EditorGUIUtility.PingObject(nested.gameObject);
                }

                return;
            }
        }

        // Baked data sitting in a prefab, reported where everyday
        // editing looks — a prefab-stage selection, or the asset selected in
        // the Project view. The same shape as the band below: say what
        // is wrong, offer the fix, never discard authored data on save.
        //
        // This is layer 1 of two. It cannot be the only layer, because a
        // prefab can be created through five routes (drag-to-project, Save
        // As, prefab-stage save, Apply overrides, scripted SaveAsPrefabAsset)
        // and an inspector band only fires when somebody looks. Layer 2 is
        // the pre-build scan, which does not care how the prefab was made.
        internal static void DrawBakedPrefabBand(Object[] selection)
        {
            var affected = new List<MonoBehaviour>();
            foreach (Object obj in selection)
            {
                if (obj is MonoBehaviour mb &&
                    StaticInstanceBakeUtility.CarriesBakedDataInPrefab(mb))
                {
                    affected.Add(mb);
                }
            }

            if (affected.Count == 0)
                return;

            bool multi = affected.Count > 1;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                (multi
                    ? $"{affected.Count:N0} of the selected components carry baked instance data "
                    : "This component carries baked instance data ") +
                "inside a prefab, where it can never be used. Instance matrices are " +
                "world-space, so a bake is valid only at the transform it was taken at; " +
                "an instantiated prefab is caught by the owner-matrix guard and falls " +
                "back to runtime collection, which draws correctly wherever it lands. " +
                "The baked arrays are therefore dead weight in the asset. Baking is " +
                "against the scene by design — bake the scene instance instead.",
                MessageType.Warning);

            if (!GUILayout.Button(new GUIContent(
                    multi ? $"Clear baked data on {affected.Count:N0} components" : "Clear baked data",
                    "Removes baked arrays from the affected prefab components. " +
                    "Scene-instance bakes are unchanged; the prefab can still " +
                    "collect sources at runtime.")))
            {
                return;
            }

            ClearBakedDataOnTargets(affected);
        }

        // The button's action, extracted so it can be exercised without a GUI.
        internal static void ClearBakedDataOnTargets(IList<MonoBehaviour> targets)
        {
            if (targets == null || targets.Count == 0)
                return;

            foreach (MonoBehaviour mb in targets)
            {
                Undo.RecordObject(mb, "Clear Baked Data");
                ((IStaticInstanceBakeTarget)mb).ClearBakedData();
                EditorUtility.SetDirty(mb);
            }

            AssetDatabase.SaveAssets();
        }

        // Reporting and routing, never a lockout: settings stay visible while a target's subtree
        // holds LOD Groups (hiding them would protect nothing, since Validate already suppresses
        // the bake and the conversion never writes lodDistances, and it would cost multi-edit its
        // usefulness). This band says what is wrong and offers the way out, identically for one
        // target or many. The Status line above carries the bake-suppression news.
        //
        // Detection stays scoped to each target's own subtree: a scene may deliberately keep LOD
        // Group content elsewhere, and a scene-wide test would block bakes that are fine.
        internal static void DrawLodGroupConversionBand(
            LodGroupConversionCache cache, Object[] selection, string singleName)
        {
            cache.Refresh(selection);
            if (cache.NeedCount == 0)
                return;

            bool multi = cache.Total > 1;
            // Every group in scope is one the converter refuses. Saying
            // "convert to resolve this" here would be a loop: the converter
            // reports nothing to do and the bake stays blocked forever.
            bool deadEnd = cache.ConvertibleCount == 0;

            string scope = multi
                ? $"{cache.NeedCount:N0} of {cache.Total:N0} selected still contain LOD Groups"
                : "This hierarchy still contains LOD Groups";

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                deadEnd
                    ? $"{scope}, which Static Instances cannot take — and every one of them looks " +
                      "dynamic, so the converter refuses them. Baking stays blocked until they are " +
                      "gone: move those objects out of the subtree, or remove the LOD Group by " +
                      "hand. If the detection is wrong, the review below can convert one anyway."
                    : $"{scope}, which Static Instances cannot take. Baking is skipped until they " +
                      "are converted — dissolving them keeps LOD0 and re-supplies the coarser " +
                      "levels as Mesh LODs.",
                MessageType.Warning);

            string label;
            if (deadEnd)
                label = "Review skipped LOD Groups…";
            else if (multi)
                label = $"Convert LOD Groups in the {cache.NeedCount:N0} selected hierarchies…";
            else
                label = "Convert LOD Groups in this hierarchy…";

            if (!GUILayout.Button(new GUIContent(
                    label,
                    "Opens the same preview the scene-wide conversion uses, scoped to the " +
                    "selected component hierarchies.")))
            {
                return;
            }

            float fov = Camera.main != null ? Camera.main.fieldOfView : 60f;
            StaticInstanceLodGroupConverter.Plan plan =
                StaticInstanceLodGroupConverter.Analyze(cache.Roots, fov);
            StaticInstanceConvertPreviewWindow.Open(
                plan,
                fov,
                multi ? $"{cache.NeedCount:N0} selected hierarchies" : singleName);
        }

        // LODGroup-style ladder bar core for the SIS per-instance ladder. Unity's LODGroupEditor is
        // internal, so this is a hand-rolled equivalent: one segment per level, draggable
        // boundaries, distance-linear axis. Labelled in metres rather than a screen-size ratio,
        // since on an owner-sized AABB the percentages read as nonsense (the enclosing sphere can
        // cover more than the whole screen at a boundary that is only tens of metres away).
        //
        // isLiftedSegment: segments the global quality floor
        // (StaticInstanceLodSystem.MinMeshLodLevel) lifts to a coarser mesh level are drawn grayed
        // out; the boundary handles stay live (the floor is a runtime override and the authored
        // ladder underneath is untouched), and the caller says where the lift goes via segmentName.
        internal static void DrawLodBar(
            Rect rect,
            SerializedProperty distances,
            int boundaryCount,
            float axisMax,
            System.Func<int, string> segmentName,
            System.Func<int, bool> isHighlightedSegment = null,
            System.Func<int, bool> isLiftedSegment = null,
            float nearCullDistance = 0f,
            float farCullDistance = 0f)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));

            // Distance-linear: the camera is at the left edge and axisMax at the
            // right, so a level's segment is as wide as the range it covers.
            float PositionOf(float distance) =>
                rect.x + rect.width * Mathf.Clamp01(distance / axisMax);

            // Every label in the bar is drawn with GUI.Label, never
            // EditorGUI.LabelField: the latter routes through PrefixLabel,
            // which applies EditorGUI.indentLevel to an explicit rect — and
            // both inspectors draw this bar inside an indented section, so
            // "centered" text landed visibly right of center.
            //
            // Boundary readouts sit to the LEFT of their boundary line (right-
            // aligned, ending just before it), so the number reads as "this
            // boundary's distance" instead of floating between two segments.
            var readoutStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };
            void DrawBoundaryReadout(float lineX, float distance) =>
                GUI.Label(
                    new Rect(lineX - 51f, rect.yMax - 14f, 48f, 13f),
                    $"{distance:F0} m", readoutStyle);

            float previousX = rect.x;
            for (int level = 0; level <= boundaryCount; level++)
            {
                float nextX = level < boundaryCount
                    ? PositionOf(distances.GetArrayElementAtIndex(level).floatValue)
                    : rect.xMax;
                var segment = new Rect(previousX, rect.y, Mathf.Max(0f, nextX - previousX), rect.height);
                // Window semantics: `level` is a segment index into the
                // window; callers translate it to an absolute mesh LOD level
                // inside segmentName / isHighlightedSegment.
                bool isHighlighted =
                    isHighlightedSegment != null && isHighlightedSegment(level);
                bool isLifted = isLiftedSegment != null && isLiftedSegment(level);
                var fillRect = new Rect(segment.x + 1f, segment.y + 1f,
                    Mathf.Max(0f, segment.width - 2f), segment.height - 2f);
                Color fill = isHighlighted
                    ? new Color(0.72f, 0.36f, 0.12f)
                    : Color.Lerp(
                        new Color(0.24f, 0.52f, 0.30f),
                        new Color(0.30f, 0.36f, 0.52f),
                        boundaryCount > 0 ? level / (float)boundaryCount : 0f);
                // Lifted by the global quality floor: the segment's own level
                // is not what renders there, so drain the color out of it.
                if (isLifted)
                    fill = Color.Lerp(fill, new Color(0.30f, 0.30f, 0.30f), 0.75f);
                EditorGUI.DrawRect(fillRect, fill);

                // No fade / hysteresis band overlay is drawn on the bar: the ratio bar cannot carry
                // it (widths stay in metres, and a width in metres has no single position on a
                // ratio axis), and showing it on the distance bar alone would make a mode switch
                // read as "fade and hysteresis do not work in Screen Size". Unity's LODGroup does
                // not overlay its Fade Transition Width either. The values stay visible as their
                // own fields.
                //
                // Runtime semantics are unaffected: every hard boundary is guarded by a band or by
                // hysteresis, and the half-band clamp still holds.

                // The label centers in the segment's VISIBLE part — the part
                // the Culled bands leave uncovered — not in the full segment:
                // the bands paint over
                // the segments, so a label centered on the full width sits
                // visually inside the dark band instead of over its level. A
                // segment entirely under a band gets no label of its own; the
                // band's "Culled" already owns that space.
                float visibleLeft = nearCullDistance > 0f
                    ? Mathf.Max(segment.x, PositionOf(nearCullDistance))
                    : segment.x;
                float visibleRight = farCullDistance > 0f
                    ? Mathf.Min(segment.xMax, PositionOf(farCullDistance))
                    : segment.xMax;
                var labelRect = new Rect(
                    visibleLeft, segment.y,
                    Mathf.Max(0f, visibleRight - visibleLeft), segment.height);
                if (BarLabelFits(labelRect.width))
                {
                    var label = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.white },
                    };
                    GUI.Label(labelRect, segmentName(level), label);
                }

                previousX = nextX;
            }

            // The Draw Range, drawn on the ladder as LOD-Group-style
            // Culled segments at both ends. Display only — the limits are
            // authored on the Draw Range row, and nothing here is draggable.
            //
            // Painted OVER the level segments rather than shortening them,
            // which is the whole value of the feature: a boundary that lands
            // inside a Culled band is a level that can never draw, and seeing
            // the boundary handle sitting inside the band is what makes that
            // authoring mistake visible. Shortening the segments would hide it.
            //
            // Drawn before the handles so the handles stay on top and legible.
            if (nearCullDistance > 0f)
            {
                DrawCulledBand(
                    rect, rect.x, PositionOf(nearCullDistance),
                    "Culled");
            }

            if (farCullDistance > 0f)
            {
                float cullX = PositionOf(farCullDistance);
                DrawCulledBand(rect, cullX, rect.xMax, "Culled");
                // The Culled boundary is a boundary too (it is the Draw Range
                // Max), so it carries the same readout as the draggable ones —
                // it just has no handle, because it is edited on its own row.
                DrawBoundaryReadout(cullX, farCullDistance);
            }

            // Draggable boundaries, drawn after the segments so the handles and
            // their readouts sit on top. Under window semantics every drawn
            // boundary is a live transition — there are no inert handles.
            for (int i = 0; i < boundaryCount; i++)
            {
                SerializedProperty element = distances.GetArrayElementAtIndex(i);
                float x = PositionOf(element.floatValue);
                var handle = new Rect(x - 3f, rect.y, 6f, rect.height);
                EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);
                EditorGUI.DrawRect(
                    new Rect(x - 1f, rect.y, 2f, rect.height), Color.white);

                DrawBoundaryReadout(x, element.floatValue);

                int controlId = GUIUtility.GetControlID(FocusType.Passive);
                Event current = Event.current;
                switch (current.GetTypeForControl(controlId))
                {
                    case EventType.MouseDown when handle.Contains(current.mousePosition):
                        GUIUtility.hotControl = controlId;
                        current.Use();
                        break;
                    case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    {
                        float fraction = Mathf.Clamp01(
                            (current.mousePosition.x - rect.x) / Mathf.Max(1f, rect.width));
                        SetLodDistanceBoundary(distances, i, boundaryCount,
                            fraction * axisMax, farCullDistance);
                        current.Use();
                        break;
                    }

                    case EventType.MouseUp when GUIUtility.hotControl == controlId:
                        GUIUtility.hotControl = 0;
                        current.Use();
                        break;
                }
            }
        }

        // One edit path for the bar and numeric rows. Reading/drawing never grows
        // the ladder; its existing explicit authoring actions own that operation.
        internal static bool SetLodDistanceBoundary(
            SerializedProperty distances, int index, int boundaryCount,
            float value, float farCullDistance)
        {
            if (index < 0 || index >= Mathf.Min(boundaryCount, distances.arraySize) ||
                float.IsNaN(value) || float.IsInfinity(value))
                return false;

            SerializedProperty element = distances.GetArrayElementAtIndex(index);
            float lower = index > 0
                ? distances.GetArrayElementAtIndex(index - 1).floatValue : 0f;
            float upper = index < boundaryCount - 1 && index + 1 < distances.arraySize
                ? distances.GetArrayElementAtIndex(index + 1).floatValue - 0.1f
                : float.MaxValue;
            // An already-past-cull
            // neighbour must not collapse the edit range to lower + 0.1.
            if (farCullDistance > lower + 0.1f)
                upper = Mathf.Min(upper, farCullDistance);
            float next = Mathf.Clamp(value, lower + 0.1f, upper);
            if (element.floatValue.Equals(next))
                return false;
            element.floatValue = next;
            return true;
        }

        internal static StaticInstanceFadeMode ResolveLodFadeMode(
            StaticInstanceFadeMode authored, int ownerForce, int globalForce, bool crossFade)
        {
            return !crossFade || ownerForce >= 0 || globalForce >= 0
                ? StaticInstanceFadeMode.None : authored;
        }

        internal static StaticInstanceFadeMode ResolveLodFadeSelection(
            SerializedProperty forced, SerializedProperty fadeMode,
            int globalForce, bool crossFade, out bool mixed)
        {
            mixed = false;
            var effective = ResolveLodFadeMode((StaticInstanceFadeMode)fadeMode.enumValueIndex,
                forced.intValue, globalForce, crossFade);
            if (!crossFade || globalForce >= 0 ||
                (!forced.hasMultipleDifferentValues && !fadeMode.hasMultipleDifferentValues))
                return effective;

            bool first = true;
            foreach (var target in forced.serializedObject.targetObjects)
            {
                using (var individual = new SerializedObject(target))
                {
                    // Common properties may contain this Inspector event's pending
                    // edit; only read individual values for genuinely mixed fields.
                    int ownerForce = forced.hasMultipleDifferentValues
                        ? individual.FindProperty(forced.propertyPath).intValue : forced.intValue;
                    var authored = (StaticInstanceFadeMode)(fadeMode.hasMultipleDifferentValues
                        ? individual.FindProperty(fadeMode.propertyPath).enumValueIndex : fadeMode.enumValueIndex);
                    var resolved = ResolveLodFadeMode(authored, ownerForce, globalForce, crossFade);
                    if (first) { effective = resolved; first = false; }
                    else if (effective != resolved) { mixed = true; break; }
                }
            }
            return effective;
        }

        internal static void DrawEffectiveLodFade(SerializedProperty forced, SerializedProperty fadeMode)
        {
            var effective = ResolveLodFadeSelection(forced, fadeMode,
                StaticInstanceLodSystem.ForcedMeshLod, StaticInstanceLodSystem.CrossFadeActive,
                out bool mixed);
            using (new EditorGUI.DisabledScope(true))
            using (new EditorGUI.MixedValueScope(mixed))
                EditorGUILayout.EnumPopup(new GUIContent("Effective LOD Fade",
                    "Read-only mesh transition mode after forced LOD and global " +
                    "fade settings. Draw Range continues to use the shared " +
                    "effective Fade Mode."), effective);
        }

        internal static void DrawDistanceLadderRows(
            SerializedProperty distances, int boundaryCount, int minLevel,
            float farCullDistance, bool mixedLadder)
        {
            int count = Mathf.Min(boundaryCount, distances.arraySize);
            using (new EditorGUI.DisabledScope(mixedLadder))
            {
                for (int i = 0; i < count; i++)
                {
                    SerializedProperty element = distances.GetArrayElementAtIndex(i);
                    using (new EditorGUI.MixedValueScope(mixedLadder))
                    {
                        EditorGUI.BeginChangeCheck();
                        float next = EditorGUILayout.DelayedFloatField(
                            new GUIContent(minLevel < 0 ? $"Boundary {i + 1} (m)" :
                                $"LOD {minLevel + i} Transition (m)",
                                "Camera-to-instance AABB surface distance in " +
                                "metres, constrained by adjacent boundaries and " +
                                "the far-cull limit. Mixed ladders must agree " +
                                "before editing."),
                            element.floatValue);
                        if (EditorGUI.EndChangeCheck() && !mixedLadder)
                            SetLodDistanceBoundary(distances, i, boundaryCount, next, farCullDistance);
                    }
                }
            }
        }
        // One Culled band. Dark and desaturated rather than a colour of its
        // own: the band means "nothing is drawn here", so it should read as an
        // absence next to the levels, not as a fifth level.
        private static void DrawCulledBand(Rect bar, float fromX, float toX, string label)
        {
            float left = Mathf.Clamp(fromX, bar.x, bar.xMax);
            float right = Mathf.Clamp(toX, bar.x, bar.xMax);
            if (right - left <= 1f)
                return;

            var band = new Rect(left, bar.y + 1f, right - left, bar.height - 2f);
            EditorGUI.DrawRect(band, new Color(0.10f, 0.10f, 0.10f, 0.85f));
            if (!BarLabelFits(band.width))
                return;

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.72f, 0.72f, 0.72f) },
            };
            // GUI.Label, not EditorGUI.LabelField — see DrawLodBar's header
            // note: LabelField applies the section indent and shifts the text
            // off center.
            GUI.Label(band, label, style);
        }

        // The axis has to reach past the far limit, or the Culled band it is supposed to show would
        // sit exactly at the right edge with no width. Without a far limit it extends 1.5 times
        // past the last boundary.
        internal static float LodBarAxisMax(
            SerializedProperty distances, int boundaryCount, float farCullDistance)
        {
            float lastBoundary = boundaryCount > 0
                ? distances.GetArrayElementAtIndex(boundaryCount - 1).floatValue
                : 0f;
            float axisMax = Mathf.Max(1f, lastBoundary * 1.5f);
            if (farCullDistance > 0f)
                axisMax = Mathf.Max(axisMax, farCullDistance * 1.15f);
            return axisMax;
        }

        // What the bar should draw as a limit, or 0 for "no band". A mixed
        // selection has no single limit to draw, and 0 already means
        // "unlimited" on the far side and "no near limit" on the near one, so
        // the mixed case falls out as no band rather than as a wrong one.
        internal static float BarCullDistance(SerializedProperty limit) =>
            limit == null || limit.hasMultipleDifferentValues
                ? 0f
                : Mathf.Max(0f, limit.floatValue);

        // The caveat every Distances tooltip carries once the bar can draw a
        // far Culled band: the size-derived limit composes as a min() per
        // instance, so the edge the bar draws is an upper bound rather than
        // the per-instance truth.
        internal const string CulledSegmentTooltip =
            " Culled regions show authored Draw Range limits; " +
            "size culling can reject individual instances earlier.";

        // -- Shadow Cast Max Level row ----------------------------------------------

        // Sits in the LOD band because it is a ladder-vocabulary
        // setting: it names a LEVEL, not a distance, and the boundary it
        // acts on is the one that ends that level.
        internal static void DrawShadowCastMaxLevel(
            SerializedProperty prop, int windowMin, int levelCount,
            System.Func<int, string> levelName)
        {
            // The coarsest level is deliberately not offered: at N = the window max, `level > N`
            // can never be true, so the entry would be a silent alias of "Every level casts". One
            // fewer entry, and the option always means something.
            int offerable = Mathf.Max(0, levelCount - 1);
            var names = new GUIContent[offerable + 1];
            names[0] = new GUIContent("Every level casts");
            // Entries read `LOD [n]`; the field's own name already carries "Max".
            for (int i = 0; i < offerable; i++)
                names[i + 1] = new GUIContent(levelName(windowMin + i));

            // A stored value at or past the window's coarsest level is displayed as "Every level
            // casts", because that is what it does (`level > N` is never true there). It is
            // reachable and persistent: narrowing the Level Range over an authored value ceilings
            // it exactly there. Clamping the display into the offered range would show "Up to
            // <windowMax - 1>", one level stronger than the value in force, and would never
            // self-correct because the popup writes nothing unless the selection changes.
            int stored = prop.intValue;
            int selected = stored < 0 || stored >= windowMin + offerable
                ? 0
                : Mathf.Clamp(stored - windowMin + 1, 1, offerable);
            var label = new GUIContent(
                "Shadow Cast Max Level",
                "Stops shadows after the selected level using this component's " +
                "fade behavior; camera visibility is unchanged. Every level " +
                "casts (-1) disables this cutoff.");

            // The agree-or-mixed rule:
            // a multi-selection that disagrees must show as mixed rather than
            // as the first target's value, and BeginProperty is what carries
            // the prefab-override bolding and the revert context menu.
            Rect rect = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(rect, label, prop);
            EditorGUI.BeginChangeCheck();
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            int next = EditorGUI.Popup(rect, label, selected, names);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
                prop.intValue = next == 0 ? -1 : windowMin + next - 1;
            EditorGUI.EndProperty();
        }

        // -- Multi-edit ladder state ----------------------------------------------

        // A multi-selection is drawn with the bar rather than falling back to the raw array (which
        // reads as the bar having broken). Two selected components usually author the same ladder,
        // and that case is drawn exactly as a single selection: every write in DrawLodBar goes through
        // SerializedProperty, so a drag lands on all targets. Only a genuine disagreement needs a
        // substitute, and Unity's convention for that is a mixed-value control rather than no
        // control.
        //
        // "Agree" is judged on what the bar actually reads: the array length (its own size property
        // carries the disagreement) and each boundary the bar would draw. Callers add the window
        // check themselves: a differing Min/Max makes identical distances mean different levels.
        internal static bool LodDistancesAgree(
            SerializedProperty distances, int boundaryCount)
        {
            SerializedProperty size = distances.FindPropertyRelative("Array.size");
            if (size != null && size.hasMultipleDifferentValues)
                return false;

            int drawn = Mathf.Min(boundaryCount, distances.arraySize);
            for (int i = 0; i < drawn; i++)
            {
                if (distances.GetArrayElementAtIndex(i).hasMultipleDifferentValues)
                    return false;
            }

            return true;
        }

        // The mixed-value counterpart of DrawLodBar: the same geometry, drained of colour, with the
        // boundary lines drawn but no distance readout, since there is no single distance to print
        // and the drained bar already says the selection disagrees. Deliberately inert: a draggable
        // handle at an invented position would offer to write a value the reader never chose, and
        // the array field drawn underneath is the honest way to reconcile the selection. Segments
        // are spaced evenly: without agreeing distances there is nothing to scale them by, and an
        // even split reads as a placeholder rather than as data.
        internal static void DrawLodBarMixed(
            Rect rect, int boundaryCount, System.Func<int, string> segmentName)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));

            var fill = new Color(0.28f, 0.28f, 0.28f);
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.68f, 0.68f, 0.68f) },
            };

            float step = rect.width / (boundaryCount + 1);
            for (int level = 0; level <= boundaryCount; level++)
            {
                var segment = new Rect(
                    rect.x + step * level, rect.y, step, rect.height);
                EditorGUI.DrawRect(
                    new Rect(segment.x + 1f, segment.y + 1f,
                        Mathf.Max(0f, segment.width - 2f), segment.height - 2f),
                    fill);
                if (BarLabelFits(segment.width) && segmentName != null)
                    GUI.Label(segment, segmentName(level), labelStyle);
            }

            for (int i = 0; i < boundaryCount; i++)
            {
                float x = rect.x + step * (i + 1);
                EditorGUI.DrawRect(
                    new Rect(x - 1f, rect.y, 2f, rect.height),
                    new Color(0.55f, 0.55f, 0.55f));
            }
        }

        // One line, in the same words on both components. Sits under the mixed
        // bar and above the array field that can still edit the values.
        internal static void DrawMixedLadderNotice(
            string message = "Distances differ across the selection")
        {
            EditorGUILayout.LabelField(" ", message, EditorStyles.miniLabel);
        }

        // -- Global debug controls ------------------------------------------------

        // Project-wide quality knobs are edited only under Project Settings > Graphics > Static
        // Instances, where StaticInstanceGlobalSettingsDrawer re-applies edits to the runtime
        // statics so a Play Mode change is still visible immediately. Only the debug-mask live view
        // below remains on the components.

        // Live view of the global debug masks in StaticInstanceLodSystem (CrossFadeEnabled /
        // ForcedMeshLod), drawn at the end of both Debug bands. Deliberately not serialized: they
        // are diagnostic state that resets to its defaults with the domain, and outside Play Mode
        // (where nothing consumes them) the rows are locked.
        internal static void DrawGlobalDebugMasks()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Global (all SIS)",
                    "Temporary measurement controls affecting every SIS."),
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Global measurement masks: they affect every SIS in " +
                "the scene, and they are not saved — they reset to their " +
                "defaults on script recompile and when exiting Play Mode. " +
                "Editable in Play Mode.",
                MessageType.Warning);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                // With the URP Asset's LOD Cross Fade off, builds strip the LOD_FADE_CROSSFADE
                // variants, so every dither is degraded to a hard switch at runtime
                // (CrossFadeActive / the eval job's SnapTimeFades). Surface that state here instead
                // of leaving a toggle that appears to do nothing.
                bool urpCrossFade = QualitySettings.enableLODCrossFade;
                if (!urpCrossFade)
                    EditorGUILayout.HelpBox(
                        "LOD Cross Fade is disabled in the URP Asset, so " +
                        "every dither (crossfade and draw-range fade) runs " +
                        "as a hard switch — builds strip the crossfade " +
                        "shader variants in this configuration. Enable LOD " +
                        "Cross Fade in the URP Asset's Quality section to " +
                        "restore fades.",
                        MessageType.Info);
                using (new EditorGUI.DisabledScope(!urpCrossFade))
                {
                    StaticInstanceLodSystem.CrossFadeEnabled = EditorGUILayout.Toggle(
                        new GUIContent(
                            "Cross Fade Enabled",
                            "Global fade mask for every SIS LOD and Draw Range " +
                            "transition. Turning it off gives hard switches; " +
                            "URP LOD Cross Fade support is required."),
                        StaticInstanceLodSystem.CrossFadeEnabled);
                }

                // Same Auto + LOD n popup format as the per-component Forced
                // LOD rows, spanning the whole authorable range — each
                // component still clamps to its own level count.
                int steps = StaticInstanceLodSystem.MaxAuthorableMeshLodLevel + 1;
                var names = new GUIContent[steps + 1];
                names[0] = new GUIContent("Auto");
                for (int i = 0; i < steps; i++)
                    names[i + 1] = new GUIContent($"LOD {i}");
                int forced = StaticInstanceLodSystem.ForcedMeshLod;
                int selected = forced < 0 ? 0 : Mathf.Clamp(forced + 1, 1, steps);
                int next = EditorGUILayout.Popup(
                    new GUIContent(
                        "Forced LOD (Global)",
                        "Temporarily pins every SIS to a mesh level, clamped to " +
                        "each component's range. Auto (-1) restores " +
                        "per-component selection."),
                    selected,
                    names);
                if (next != selected)
                    StaticInstanceLodSystem.ForcedMeshLod = next == 0 ? -1 : next - 1;
            }
        }

        // -- Foldout helpers ----------------------------------------------------------

        // Player-Settings-style category header. The caller must always close
        // with EndFoldoutHeaderGroup (nesting header groups is not supported,
        // so inner sections use the plain Foldout below). The rect is inset so
        // the disclosure arrow stays inside the surrounding helpBox frame.
        internal static bool HeaderFoldout(string key, string label, bool defaultOpen) =>
            HeaderFoldout(key, new GUIContent(label), defaultOpen);

        internal static bool HeaderFoldout(string key, GUIContent label, bool defaultOpen)
        {
            bool value = SessionState.GetBool(key, defaultOpen);
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.foldoutHeader);
            rect.xMin += 13f;
            bool next = EditorGUI.BeginFoldoutHeaderGroup(rect, value, label);
            if (next != value)
            {
                SessionState.SetBool(key, next);
                // Some callers key SceneView drawing off this foldout state;
                // SessionState writes do not
                // invalidate the SceneView, so repaint it explicitly or the
                // gizmo lingers until the next redraw.
                SceneView.RepaintAll();
            }

            return next;
        }

        internal static bool Foldout(string key, string label) =>
            Foldout(key, new GUIContent(label));

        internal static bool Foldout(string key, GUIContent label)
        {
            bool value = SessionState.GetBool(key, false);
            bool next = EditorGUILayout.Foldout(value, label, true);
            if (next != value)
                SessionState.SetBool(key, next);
            return next;
        }

        // GUILayout buttons ignore EditorGUI.indentLevel; this variant lines
        // up with the indented fields around it.
        internal static bool IndentedButton(string label) =>
            IndentedButton(new GUIContent(label));

        internal static bool IndentedButton(GUIContent label)
        {
            Rect rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            return GUI.Button(rect, label);
        }
    }
}
