using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace StaticInstances.Editor
{
    // Draws every visible global SIS setting and keeps the runtime mirrors in
    // sync when settings are changed or undone during Play Mode.
    [CustomPropertyDrawer(typeof(StaticInstanceGlobalSettings))]
    internal sealed class StaticInstanceGlobalSettingsDrawer : PropertyDrawer
    {
        private static readonly (string Field, string Header)[] Sections =
        {
            ("m_MinLevel", "LOD"),
            ("m_CrossFade", "Fade"),
            ("m_SizeCullDistanceScale", "Size-Based Culling"),
            ("m_FovTrackingEnabled", "Camera"),
            ("m_ReceiverSphereCullEnabled", "Shadows"),
        };

        private static readonly (string Field, string Label)[] Labels =
        {
            ("m_LodDistanceScale", "Distance Scale"),
            ("m_LodBias", "LOD Bias"),
            ("m_FovTrackingEnabled", "FOV Tracking"),
            ("m_CrossFade", "Enabled"),
            ("m_LodFadeDuration", "Duration"),
            ("m_FadeCurve", "Curve"),
            ("m_SizeCullDistanceScale", "Cull Distance Scale"),
            ("m_ReceiverSphereCullEnabled", "Receiver Sphere Cull"),
        };

        private readonly struct Authored : System.IEquatable<Authored>
        {
            private readonly int minLevel;
            private readonly float distanceScale;
            private readonly float lodBias;
            private readonly float fadeDuration;
            private readonly bool crossFade;
            private readonly float sizeCullDistanceScale;
            private readonly float derivedCullFadeFraction;
            private readonly StaticInstanceLodSystem.FadeCurveMode fadeCurve;

            public Authored(StaticInstanceGlobalSettings settings)
            {
                minLevel = settings.minMeshLodLevel;
                distanceScale = settings.lodDistanceScale;
                lodBias = settings.lodBias;
                fadeDuration = settings.lodFadeDuration;
                crossFade = settings.crossFade;
                sizeCullDistanceScale = settings.sizeCullDistanceScale;
                derivedCullFadeFraction = settings.derivedCullFadeFraction;
                fadeCurve = settings.fadeCurve;
            }

            public bool Equals(Authored other) =>
                minLevel == other.minLevel &&
                distanceScale == other.distanceScale &&
                lodBias == other.lodBias &&
                fadeDuration == other.fadeDuration &&
                crossFade == other.crossFade &&
                sizeCullDistanceScale == other.sizeCullDistanceScale &&
                derivedCullFadeFraction == other.derivedCullFadeFraction &&
                fadeCurve == other.fadeCurve;

            public override bool Equals(object obj) => obj is Authored other && Equals(other);
            public override int GetHashCode() => minLevel;
        }

        private static Authored lastAuthored;

        [InitializeOnLoadMethod]
        private static void HookUndoRedo()
        {
            if (GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceGlobalSettings settings))
                lastAuthored = new Authored(settings);
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private static void OnUndoRedo()
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceGlobalSettings settings))
                return;
            Authored authored = new Authored(settings);
            if (authored.Equals(lastAuthored))
                return;
            settings.ApplyToSystem();
            lastAuthored = authored;
        }

        private static string HeaderFor(string fieldName)
        {
            foreach ((string field, string header) in Sections)
            {
                if (field == fieldName)
                    return header;
            }
            return null;
        }

        private static GUIContent LabelFor(SerializedProperty property)
        {
            foreach ((string field, string label) in Labels)
            {
                if (field == property.name)
                    return new GUIContent(label, property.tooltip);
            }
            return null;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = 0f;
            SerializedProperty it = property.Copy();
            SerializedProperty end = it.GetEndProperty();
            bool enterChildren = true;
            bool first = true;
            while (it.NextVisible(enterChildren) &&
                   !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                if (!first)
                    height += EditorGUIUtility.standardVerticalSpacing;
                first = false;
                if (HeaderFor(it.name) != null)
                {
                    height += EditorGUIUtility.singleLineHeight +
                        EditorGUIUtility.standardVerticalSpacing;
                }
                height += EditorGUI.GetPropertyHeight(it, true);
            }
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using var indent = new EditorGUI.IndentLevelScope(-1);
            EditorGUI.BeginChangeCheck();

            SerializedProperty it = property.Copy();
            SerializedProperty end = it.GetEndProperty();
            bool enterChildren = true;
            bool first = true;
            bool inSection = false;
            while (it.NextVisible(enterChildren) &&
                   !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                if (!first)
                    position.y += EditorGUIUtility.standardVerticalSpacing;
                first = false;

                string header = HeaderFor(it.name);
                if (header != null)
                {
                    if (inSection)
                        EditorGUI.indentLevel--;
                    position.height = EditorGUIUtility.singleLineHeight;
                    EditorGUI.LabelField(position, header, EditorStyles.boldLabel);
                    position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                    EditorGUI.indentLevel++;
                    inSection = true;
                }

                position.height = EditorGUI.GetPropertyHeight(it, true);
                GUIContent rowLabel = LabelFor(it);
                if (rowLabel == null)
                    EditorGUI.PropertyField(position, it, true);
                else
                    EditorGUI.PropertyField(position, it, rowLabel, true);
                position.y += position.height;
            }

            if (inSection)
                EditorGUI.indentLevel--;

            if (EditorGUI.EndChangeCheck())
            {
                EditorApplication.delayCall += () =>
                {
                    if (GraphicsSettings.TryGetRenderPipelineSettings(
                            out StaticInstanceGlobalSettings settings))
                    {
                        settings.ApplyToSystem();
                        lastAuthored = new Authored(settings);
                    }
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                };
            }
        }
    }
}
