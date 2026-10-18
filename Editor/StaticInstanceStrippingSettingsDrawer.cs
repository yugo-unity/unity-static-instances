using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // Graphics-settings UI for the stripping container: the manual keep list
    // only shows while the automatic stripping is enabled (it has no effect
    // otherwise).
    [CustomPropertyDrawer(typeof(StaticInstanceStrippingSettings))]
    internal sealed class StaticInstanceStrippingSettingsDrawer : PropertyDrawer
    {
        private const string SourcesField = "m_StripBakedSourcesOnBuild";
        private const string EnabledField = "m_StripUnusedDotsInstancingVariants";
        private const string ShadersField = "m_AdditionalDotsInstancingShaders";

        // Info, not Warning: Addressables without the build script is a safe
        // default state (bundles keep the DOTS variants: larger, but they always
        // render), so a permanent warning would only be noise.
        private static readonly GUIContent BuilderHint = EditorGUIUtility.TrTextContent(
            "Addressables is installed but its content builds are not being stripped. " +
            "Create a Static Instances Packed Mode asset (Create > Addressables > " +
            "Content Builders), add it under Build & Play Mode Scripts on your " +
            "Addressables settings and select it — or call " +
            "StaticInstanceBundleStripping.BeginContentBuild / EndContentBuild from your " +
            "own build script. Without it, Addressables content keeps every DOTS variant.");

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            SerializedProperty enabled = property.FindPropertyRelative(EnabledField);
            float height = EditorGUI.GetPropertyHeight(enabled) +
                EditorGUIUtility.standardVerticalSpacing +
                EditorGUI.GetPropertyHeight(property.FindPropertyRelative(SourcesField));
            if (enabled.boolValue)
            {
                height += EditorGUIUtility.standardVerticalSpacing +
                    EditorGUI.GetPropertyHeight(property.FindPropertyRelative(ShadersField));

                if (ShouldShowBuilderHint())
                {
                    height += EditorGUIUtility.standardVerticalSpacing +
                        BuilderHintHeight();
                }
            }

            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // See StaticInstanceEditorSettingsDrawer: one level out so the
            // section lines up with URP's own.
            using var indent = new EditorGUI.IndentLevelScope(-1);
            // Source stripping is independent of the shader-variant stripping
            // below, so it is drawn unconditionally and first.
            SerializedProperty sources = property.FindPropertyRelative(SourcesField);
            position.height = EditorGUI.GetPropertyHeight(sources);
            EditorGUI.PropertyField(position, sources);
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;

            SerializedProperty enabled = property.FindPropertyRelative(EnabledField);
            position.height = EditorGUI.GetPropertyHeight(enabled);
            EditorGUI.PropertyField(position, enabled);

            if (!enabled.boolValue)
                return;

            SerializedProperty shaders = property.FindPropertyRelative(ShadersField);
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
            position.height = EditorGUI.GetPropertyHeight(shaders);
            EditorGUI.PropertyField(position, shaders, true);

            if (!ShouldShowBuilderHint())
                return;

            // Remembered for the height pass, which cannot ask the GUI for a
            // width (see BuilderHintHeight).
            lastDrawWidth = position.width;

            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
            position.height = BuilderHintHeight();
            EditorGUI.HelpBox(position, BuilderHint.text, MessageType.Info);
        }

        // Shown only while it is both true and actionable: the option it sits
        // under is on, the Addressables package is present, and there is
        // something to act on. The question is answered by a delegate the
        // version-gated assembly registers — null means the package is absent —
        // so this drawer never touches an Addressables type and a project
        // without the package sees nothing and errors nowhere.
        private static bool ShouldShowBuilderHint()
        {
            bool? nothingToActOn = StaticInstanceBundleStripping.NothingToActOn;
            return nothingToActOn.HasValue && !nothingToActOn.Value;
        }

        // Measured with the style and a width carried over from the last
        // repaint. A conservative fallback covers the first height query.
        private static float lastDrawWidth;

        private static float BuilderHintHeight()
        {
            float width = lastDrawWidth > 1f ? lastDrawWidth : 320f;
            float minimum = EditorGUIUtility.singleLineHeight * 3f;
            GUIStyle style;
            try
            {
                style = EditorStyles.helpBox;
            }
            catch (System.NullReferenceException)
            {
                // A fresh batch editor can ask for property height before its
                // built-in GUI styles have been initialized. The first real
                // repaint measures the text with the style and remembered width.
                return minimum;
            }

            return style == null
                ? minimum
                : Mathf.Max(minimum, style.CalcHeight(BuilderHint, width));
        }
    }
}
