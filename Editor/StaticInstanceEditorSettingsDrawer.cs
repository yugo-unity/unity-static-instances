using UnityEditor;
using UnityEngine;

namespace StaticInstances.Editor
{
    // This container owns policy settings; commands live in the operations window.
    [CustomPropertyDrawer(typeof(StaticInstanceEditorSettings))]
    internal sealed class StaticInstanceEditorSettingsDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            SerializedProperty it = property.Copy();
            SerializedProperty end = it.GetEndProperty();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                height += EditorGUIUtility.standardVerticalSpacing;
                height += EditorGUI.GetPropertyHeight(it, true);
            }
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using var indent = new EditorGUI.IndentLevelScope(-1);
            position.height = EditorGUIUtility.singleLineHeight;
            // GUI.Button does not apply the PropertyField indentation adjustment.
            Rect buttonPosition = position;
            buttonPosition.xMin -= 15f; // One Unity IMGUI indentation level.
            if (GUI.Button(buttonPosition, new GUIContent("Open Operations Window",
                    "Open scene bake, validation and conversion operations.")))
                StaticInstanceOperationsWindow.Open();
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
            SerializedProperty it = property.Copy();
            SerializedProperty end = it.GetEndProperty();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                enterChildren = false;
                position.height = EditorGUI.GetPropertyHeight(it, true);
                EditorGUI.PropertyField(position, it, true);
                position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
            }
        }
    }
}
