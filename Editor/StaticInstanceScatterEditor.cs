using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Inspector for wide-area scattered content (SIS): a one-line bake
    // status, the everyday rendering row, a single boxed "LOD" foldout for
    // the per-instance ladder, and a collapsed "Debug (Development)" group.
    // The ladder is per instance with no owner-level state machine: every
    // instance picks its level as a stateless function of distance (the `Time`
    // fade mode adds state to the fade only; level selection keeps just the
    // per-instance hysteresis memory of last frame's level, and only where no
    // band exists), so the boundary bar shows a cross-fade band. Shared pieces (the BRG warning, bake status, draw
    // range row, fade duration override, the level range row, the bar core,
    // missing-level cache/warning, and the foldout helpers) live in
    // StaticInstanceEditorCommon.
    [CustomEditor(typeof(StaticInstanceScatter))]
    [CanEditMultipleObjects]
    public sealed class StaticInstanceScatterEditor : UnityEditor.Editor
    {
        // Scatter-specific SessionState keys preserve foldout state.
        private const string LodGroupKey = "SI.Inspector.Scatter.Lod";
        private const string LodWarningListKey = "SI.Inspector.Scatter.LodWarningList";
        private const string DebugKey = "SI.Inspector.Scatter.Debug";

        // The hash walk is cached per selection and refreshed on undo/redo.
        private string bakeStatus;
        private MessageType bakeStatusType = MessageType.None;

        // Warning support: which source meshes fall short of the requested
        // level. Cached like the bake status; see
        // StaticInstanceEditorCommon.MissingLevelCache.
        private readonly StaticInstanceEditorCommon.MissingLevelCache missingLevelCache =
            new StaticInstanceEditorCommon.MissingLevelCache();

        // Cached measurements for the bounds spread rows.
        private readonly StaticInstanceEditorCommon.SpreadRatioCache spreadRatioCache =
            new StaticInstanceEditorCommon.SpreadRatioCache();

        // LOD Group detection for the conversion band, cached on the
        // same contract as the bake status.
        private readonly StaticInstanceEditorCommon.LodGroupConversionCache lodGroupCache =
            new StaticInstanceEditorCommon.LodGroupConversionCache();

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnUndoRedoPerformed()
        {
            InvalidateCaches();
        }

        // Both cached answers are hierarchy questions, and the conversion is
        // itself a hierarchy change — without this the band would keep asking
        // for a conversion that already happened, and the status line would
        // keep reporting a block that is gone.
        private void OnHierarchyChanged()
        {
            InvalidateCaches();
        }

        private void InvalidateCaches()
        {
            bakeStatus = null;
            spreadRatioCache.Invalidate();
            missingLevelCache.Invalidate();
            lodGroupCache.Invalidate();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var scatter = (StaticInstanceScatter)target;

            StaticInstanceEditorCommon.DrawBrgVariantsWarning();

            StaticInstanceEditorCommon.DrawBakeStatus(
                scatter,
                targets,
                serializedObject.isEditingMultipleObjects,
                "scatter",
                ref bakeStatus,
                ref bakeStatusType);

            // The three bands may all appear at once — a scene
            // where every condition is true is already abnormal, and showing
            // everything is the honest answer. Keep a fixed order:
            // nesting warning → baked-prefab band → LOD-Group conversion band.
            StaticInstanceEditorCommon.DrawNestingWarning(targets);

            // Baked data that got into a prefab.
            StaticInstanceEditorCommon.DrawBakedPrefabBand(targets);

            // Reports and routes, never hides settings —
            // single or multi selection alike.
            StaticInstanceEditorCommon.DrawLodGroupConversionBand(
                lodGroupCache, targets, scatter.gameObject.name);

            // The pre-LOD strip, in this order: the
            // boundary that is authored (Draw Range), how it fades (Fade
            // Mode / Width), what guards it when it does not (Hysteresis),
            // the motion-vector option, then bounds directly above the LOD band.
            //
            // Draw range is a per-component setting with no per-renderer
            // equivalent. Editable in Play Mode
            // (OnValidate pushes it).
            StaticInstanceEditorCommon.DrawRangeRow(
                serializedObject.FindProperty("minDrawDistance"),
                serializedObject.FindProperty("cullDistance"));
            // Fade controls sit beside Draw Range because they govern both
            // draw-range and LOD transitions. Duration is enabled for Time mode.
            StaticInstanceEditorCommon.DrawFadeRows(
                serializedObject.FindProperty("fadeMode"),
                serializedObject.FindProperty("crossFadeWidth"),
                serializedObject.FindProperty("lodFadeDuration"));
            // Hysteresis governs both boundary families, so it sits outside the LOD
            // section: that section hides itself when the ladder is inactive, while the
            // draw-range limits it guards keep running.
            StaticInstanceEditorCommon.DrawHysteresisRow(
                serializedObject.FindProperty("lodHysteresis"),
                ((StaticInstanceScatter)target).EffectiveFadeMode,
                serializedObject.FindProperty("crossFadeWidth"),
                serializedObject.FindProperty("fadeMode"));

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("perObjectMotionVectors"),
                new GUIContent("Motion Vector",
                    "Requests a MotionVectors pass for shader vertex animation (such as VAT). " +
                    "The shader must provide current and previous deformed positions. Instance " +
                    "transforms remain static. Adds a rendering pass; applied at initialization, " +
                    "not live. Groups drawing a wind-stripped SpeedTree material are excluded."));
            // Separate transition controls from the derived bounds display.
            EditorGUILayout.Space();
            // Bounds are derived and read-only.
            DrawBounds(scatter);

            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (StaticInstanceEditorCommon.HeaderFoldout(
                        LodGroupKey,
                        new GUIContent(
                            "LOD",
                            "Per-instance mesh LOD levels and transition " +
                            "boundaries."),
                        true))
                {
                    DrawLod(scatter);
                }

                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            // Project-wide quality knobs are edited only under Project Settings >
            // Graphics > Static Instances (StaticInstanceGlobalSettingsDrawer applies
            // Play-Mode edits immediately).

            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (StaticInstanceEditorCommon.HeaderFoldout(
                        DebugKey,
                        new GUIContent(
                            "Debug (Development)",
                            "Measurement-only master switches and read-only " +
                            "runtime statistics."),
                        false))
                {
                    DrawDebug(scatter);
                }

                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            serializedObject.ApplyModifiedProperties();
        }

        // The per-instance ladder: level range, the boundary bar (with its
        // cross-fade overlay), the missing-level warning, and the read-only
        // baked bounds.
        private void DrawLod(StaticInstanceScatter scatter)
        {
            EditorGUI.indentLevel++;

            SerializedProperty lodEnabled = serializedObject.FindProperty("lodEnabled");
            EditorGUILayout.PropertyField(lodEnabled, new GUIContent(
                "Enabled",
                "Enables per-instance mesh LOD; meshes must contain the " +
                "required levels. Screen Size with Time fades is the " +
                "recommended starting point."));

            SerializedProperty maxLevel = serializedObject.FindProperty("maxMeshLodLevel");
            SerializedProperty minLevel = serializedObject.FindProperty("minMeshLodLevel");
            if (lodEnabled.boolValue)
            {
                // The level count sizes the distance array, so it is locked in Play Mode.
                StaticInstanceEditorCommon.DrawLevelRange(
                    minLevel, maxLevel, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel,
                    Application.isPlaying,
                    serializedObject.FindProperty("lodDistances"),
                    serializedObject.FindProperty("lodRatios"));
            }

            // Window semantics: the ladder spans min..max, so it only exists
            // when the window covers more than one level.
            bool lodActive = lodEnabled.boolValue &&
                maxLevel.intValue - minLevel.intValue > 0;
            if (lodActive)
            {
                // The mode decides which ladder is authored.
                SerializedProperty lodMode = serializedObject.FindProperty("lodMode");
                StaticInstanceEditorCommon.DrawLodModeRows(
                    lodMode, serializedObject.FindProperty("referenceFovDegrees"));

                bool screenSize = !lodMode.hasMultipleDifferentValues &&
                    (StaticInstanceLodMode)lodMode.enumValueIndex ==
                        StaticInstanceLodMode.ScreenSize;

                if (screenSize)
                {
                    // The ratio bar and the numeric rows under
                    // it. SIS has no combined level and no owner-level
                    // highlight, so it passes neither callback.
                    StaticInstanceEditorCommon.DrawScreenSizeLadder(
                        serializedObject.FindProperty("lodRatios"),
                        serializedObject.FindProperty("cullRatio"),
                        serializedObject.FindProperty("minDrawDistance"),
                        serializedObject.FindProperty("cullDistance"),
                        maxLevel.intValue - minLevel.intValue,
                        level => "LOD " + (minLevel.intValue + level));
                }
                else
                {
                    DrawLodDistances(minLevel.intValue, maxLevel.intValue);
                }
                StaticInstanceEditorCommon.DrawShadowCastMaxLevel(
                    serializedObject.FindProperty("shadowCastMaxLevel"),
                    minLevel.intValue,
                    maxLevel.intValue - minLevel.intValue + 1,
                    level => "LOD " + level);

                // Fade Mode / Fade Width and Hysteresis draw with Draw Range (see
                // DrawFadeRows). What stays here is the band-overlap warning, the one
                // thing that is about these boundaries only.
                SerializedProperty fadeMode = serializedObject.FindProperty("fadeMode");
                SerializedProperty crossFadeWidth =
                    serializedObject.FindProperty("crossFadeWidth");
                bool globalCrossFade = StaticInstanceLodSystem.CrossFadeActive;
                // Tested positively against Distance: a Time Scatter creates no band, but
                // `crossFadeWidth` keeps its authored value while greyed out, so `!= None`
                // would warn about overlapping bands that do not exist.
                bool distanceMode = !fadeMode.hasMultipleDifferentValues &&
                    (StaticInstanceFadeMode)fadeMode.enumValueIndex ==
                        StaticInstanceFadeMode.Distance;
                bool crossFadeOn = globalCrossFade && distanceMode &&
                    crossFadeWidth.floatValue > 0f &&
                    !crossFadeWidth.hasMultipleDifferentValues;
                if (crossFadeOn && !serializedObject.isEditingMultipleObjects)
                {
                    StaticInstanceEditorCommon.DrawBandOverlapWarning(
                        serializedObject.FindProperty("lodDistances"),
                        maxLevel.intValue - minLevel.intValue,
                        crossFadeWidth.floatValue);
                }
            }

            DrawMissingLevelInfo(scatter, lodActive, maxLevel.intValue);

            EditorGUI.indentLevel--;
        }

        // Boundary bar only — the band-overlap warning and Hysteresis are drawn
        // by DrawLod after it. The bar's band overlay shows whichever of Fade
        // Width or Hysteresis governs the boundaries.
        private void DrawLodDistances(int minLevel, int maxLevel)
        {
            SerializedProperty distances = serializedObject.FindProperty("lodDistances");
            SerializedProperty crossFadeWidth = serializedObject.FindProperty("crossFadeWidth");
            // Boundary i is the transition (min + i) → (min + i + 1); sliding
            // the window relabels the segments over the same distances.
            int boundaryCount = maxLevel - minLevel;

            // Draw the bar when the selection agrees on the window and boundaries; a
            // disagreement falls back to the mixed placeholder plus the array
            // field.
            bool windowAgrees =
                !serializedObject.FindProperty("minMeshLodLevel").hasMultipleDifferentValues &&
                !serializedObject.FindProperty("maxMeshLodLevel").hasMultipleDifferentValues;
            bool mixedLadder = serializedObject.isEditingMultipleObjects &&
                (!windowAgrees ||
                 !StaticInstanceEditorCommon.LodDistancesAgree(distances, boundaryCount));
            if (mixedLadder)
            {
                Rect mixedRect = EditorGUILayout.GetControlRect(true, 34f);
                mixedRect = EditorGUI.PrefixLabel(mixedRect, new GUIContent(
                    "Distances",
                    "Selected components have different ladders. Select one to " +
                    "edit its boundaries."));
                StaticInstanceEditorCommon.DrawLodBarMixed(
                    mixedRect,
                    Mathf.Max(1, boundaryCount),
                    windowAgrees ? level => $"LOD {minLevel + level}" : null);
                StaticInstanceEditorCommon.DrawMixedLadderNotice();
            }
            else
            {
                // Grown on the user's Level Range edit or by the
                // shortfall button, never by a repaint — see
                // StaticInstanceEditorCommon.DrawLadderShortfall for why a
                // repaint that writes is a repaint Undo cannot survive.
                boundaryCount = StaticInstanceEditorCommon.DrawLadderShortfall(
                    serializedObject.FindProperty("minMeshLodLevel"),
                    serializedObject.FindProperty("maxMeshLodLevel"),
                    distances,
                    boundaryCount);
                if (boundaryCount == 0)
                    return;

                // The Draw Range's own limits, drawn on the bar as
                // Culled bands. The axis stays camera-anchored and metric (0 at
                // the left, to scale) and simply extends far enough for the far
                // band to be visible.
                float nearCull = StaticInstanceEditorCommon.BarCullDistance(
                    serializedObject.FindProperty("minDrawDistance"));
                float farCull = StaticInstanceEditorCommon.BarCullDistance(
                    serializedObject.FindProperty("cullDistance"));
                // Axis runs from the camera out past the last boundary, so the
                // coarsest level always has a visible segment to sit in.
                float axisMax = StaticInstanceEditorCommon.LodBarAxisMax(
                    distances, boundaryCount, farCull);
                Rect barRect = EditorGUILayout.GetControlRect(true, 34f);
                barRect = EditorGUI.PrefixLabel(barRect, new GUIContent(
                    "Distances",
                    "Camera-to-instance AABB surface distance in metres: drag " +
                    "boundaries to edit; grey levels are lifted by global Min " +
                    "Level." +
                    StaticInstanceEditorCommon.CulledSegmentTooltip));
                // The global quality floor lifts the level an instance
                // resolves to (DrivingViewEvalJob: min(max(level, floor),
                // window max)); mirror exactly that here.
                int globalMin = StaticInstanceEditorCommon.CurrentGlobalMinMeshLodLevel;
                bool IsLifted(int level) => level < globalMin && level < maxLevel;
                StaticInstanceEditorCommon.DrawLodBar(
                    barRect,
                    distances,
                    boundaryCount,
                    axisMax,
                    level => IsLifted(minLevel + level)
                        ? $"LOD {minLevel + level} → {Mathf.Min(globalMin, maxLevel)}"
                        : $"LOD {minLevel + level}",
                    null,
                    level => IsLifted(minLevel + level),
                    nearCull,
                    farCull);
            }
            StaticInstanceEditorCommon.DrawDistanceLadderRows(
                distances, boundaryCount, windowAgrees ? minLevel : -1,
                StaticInstanceEditorCommon.BarCullDistance(
                    serializedObject.FindProperty("cullDistance")), mixedLadder);
        }

        // A popup of the actual steps rather than a raw int field.
        private void DrawForcedLod(int minLevel, int maxLevel)
        {
            SerializedProperty forced = serializedObject.FindProperty("forcedLodLevel");
            // Only the window's own levels are offered — anything outside
            // min..max would just be clamped back by the evaluation job.
            int steps = maxLevel - minLevel + 1;
            var names = new GUIContent[steps + 1];
            names[0] = new GUIContent("Auto");
            for (int i = 0; i < steps; i++)
                names[i + 1] = new GUIContent($"LOD {minLevel + i}");

            int selected = forced.intValue < 0
                ? 0
                : Mathf.Clamp(forced.intValue - minLevel + 1, 1, steps);
            using (new EditorGUI.MixedValueScope(forced.hasMultipleDifferentValues))
            {
                EditorGUI.BeginChangeCheck();
                int next = EditorGUILayout.Popup(
                    new GUIContent("Forced LOD",
                        "Pins mesh LOD and disables mesh-to-mesh fades; Draw " +
                        "Range fades remain active. Auto (-1) restores " +
                        "automatic selection."),
                    selected, names);
                if (EditorGUI.EndChangeCheck())
                    forced.intValue = next == 0 ? -1 : minLevel + next - 1;
            }
            StaticInstanceEditorCommon.DrawEffectiveLodFade(forced, serializedObject.FindProperty("fadeMode"));
        }

        // Source meshes without the requested level. The required level is
        // maxMeshLodLevel directly.
        private void DrawMissingLevelInfo(StaticInstanceScatter scatter, bool lodActive, int maxLevel)
        {
            if (serializedObject.isEditingMultipleObjects || !lodActive)
                return;

            missingLevelCache.Refresh(
                scatter, maxLevel, scatter.BakedInstanceCount, scatter.CollectSourceMeshes);
            StaticInstanceEditorCommon.DrawMissingLevelWarning(
                missingLevelCache, maxLevel, LodWarningListKey);
        }

        // Read-only union AABB of the baked instances, drawn inline directly
        // above the LOD band. There is no authored bounds field because every LOD
        // decision here is per instance, so no owner-level AABB needs to be
        // authored or computed from renderers up front.
        //
        // No foldout: the header costs a row, and folding would hide the values
        // behind a click while this display exists for glanceability (one stray
        // instance inflating the box is only caught if the abnormal Size is in view).
        // Two labeled rows rather than one merged string, which would clip the Size
        // half in a narrow inspector.
        //
        // The tooltips state what this row does not drive (it is not what Draw Range
        // culls on) as well as what it does.
        private void DrawBounds(StaticInstanceScatter scatter)
        {
            if (serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.LabelField(
                    BoundsLabel,
                    new GUIContent($"{targets.Length} scatters selected"));
                return;
            }

            if (!scatter.HasBakedData)
            {
                EditorGUILayout.LabelField(
                    BoundsLabel,
                    new GUIContent("bake to display"));
                return;
            }

            Bounds bounds = scatter.ComputeBakedBounds();
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Bounds Center",
                    "Read-only world-space center of the baked union AABB. Does " +
                    "not replace per-instance culling bounds."),
                new GUIContent(bounds.center.ToString("F1")));
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Bounds Size",
                    "Read-only world-space size of the baked union AABB in " +
                    "metres. Large values can reveal stray instances."),
                new GUIContent(bounds.size.ToString("F1")));

            // Placed inside `DrawBounds`, after its two early returns: multi-edit and
            // unbaked both return above, and the rows must not draw in either case.
            // Like the Bounds rows they are read-only, derived and drive nothing at
            // runtime. The rows also have their own gate (ratio 0 when nothing is baked).
            StaticInstanceEditorCommon.DrawSpreadRatioRows(
                spreadRatioCache, scatter, scatter.BakedInstanceCount);
        }

        // Read-only, derived, and consumed by nothing at runtime — stated on
        // the rows themselves to avoid implying that it drives runtime decisions.
        private const string BoundsTooltip =
            "Read-only baked union AABB for reference; runtime culling uses per-instance bounds";

        private static readonly GUIContent BoundsLabel = new GUIContent(
            "Bounds", BoundsTooltip);

        // Development/evaluation-only content, collapsed by default.
        private void DrawDebug(StaticInstanceScatter scatter)
        {
            EditorGUI.indentLevel++;

            // Forced LOD and LOD Offset are runtime-changeable; OnValidate pushes them into the shared
            // system like MaxMeshLodLevel.
            int maxLevel = serializedObject.FindProperty("maxMeshLodLevel").intValue;
            int minLevel = serializedObject.FindProperty("minMeshLodLevel").intValue;
            DrawForcedLod(minLevel, maxLevel);
            int offsetRange = Mathf.Max(1, maxLevel - minLevel);
            EditorGUILayout.IntSlider(
                serializedObject.FindProperty("lodLevelOffset"),
                -offsetRange,
                offsetRange,
                new GUIContent(
                    "LOD Offset",
                    "Shifts the selected level (+1 coarser, -1 finer), clamped " +
                    "to the level range. Applies after automatic or forced " +
                    "selection."));
            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("visible"),
                new GUIContent(
                    "Visible",
                    "Enables this component's camera and shadow output. Turning " +
                    "it off retains its rendering resources."));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("castShadows"),
                new GUIContent(
                    "Cast Shadows",
                    "Master shadow-casting mask for this component. When on, " +
                    "each source renderer's ShadowCastingMode is still " +
                    "respected."));
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
            {
                if (!Application.isPlaying)
                {
                    EditorGUILayout.LabelField(new GUIContent(
                        "Runtime statistics are shown in Play Mode.",
                        "The counts below are built at Initialize, which only runs while playing."));
                }
                else if (serializedObject.isEditingMultipleObjects)
                {
                    // The statistics read the component directly, so they are
                    // single-selection only.
                    EditorGUILayout.LabelField(new GUIContent(
                        "Select a single scatter to view runtime statistics.",
                        "The counts below are per component."));
                }
                else
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            "Instances",
                            "Initialized instance count, not a per-frame " +
                            "visible count."),
                        new GUIContent(scatter.InstanceCount.ToString()));
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            "Draw Groups",
                            "Instanced draw units (mesh x submesh x material " +
                            "x per-renderer attributes)."),
                        new GUIContent(scatter.DrawGroupCount.ToString()));
                    EditorGUILayout.LabelField(
                        new GUIContent("Unique Meshes", "Meshes registered with the BRG."),
                        new GUIContent(scatter.UniqueMeshCount.ToString()));
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            "Unique Materials", "Materials registered with the BRG."),
                        new GUIContent(scatter.UniqueMaterialCount.ToString()));
                }
            }

            // Global debug masks last, under their own heading, so the
            // per-component rows above cannot be mistaken for global state.
            StaticInstanceEditorCommon.DrawGlobalDebugMasks();

            EditorGUI.indentLevel--;
            if (Application.isPlaying)
                Repaint();
        }
    }
}
