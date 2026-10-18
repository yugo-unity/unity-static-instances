using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StaticInstances.Editor.Tests
{
    public sealed class StaticInstanceDistanceNumericTests
    {
        Scene scene;
        [SetUp] public void SetUp() { scene=EditorSceneManager.NewPreviewScene(); }
        [TearDown] public void TearDown() { EditorSceneManager.ClosePreviewScene(scene); }
        StaticInstanceScatter Make(params float[] values)
        {
            var go=new GameObject("Numeric ladder");SceneManager.MoveGameObjectToScene(go,scene);
            var scatter=go.AddComponent<StaticInstanceScatter>();var so=new SerializedObject(scatter);
            var array=so.FindProperty("lodDistances");array.arraySize=values.Length;
            for(int i=0;i<values.Length;i++)array.GetArrayElementAtIndex(i).floatValue=values[i];
            so.ApplyModifiedPropertiesWithoutUndo();return scatter;
        }
        static SerializedProperty Distances(SerializedObject so)=>so.FindProperty("lodDistances");
        [TestCase(0,-20f,0f,.1f)]
        [TestCase(0,100f,0f,19.9f)]
        [TestCase(1,0f,0f,10.1f)]
        [TestCase(2,100f,35f,35f)]
        [TestCase(2,100f,15f,100f)]
        public void EditsUseExistingNeighbourAndReachableFarCullRules(int index,float requested,float far,float expected)
        {
            var so=new SerializedObject(Make(10,20,30));var array=Distances(so);
            Assert.IsTrue(StaticInstanceEditorCommon.SetLodDistanceBoundary(array,index,3,requested,far));
            Assert.AreEqual(expected,array.GetArrayElementAtIndex(index).floatValue,.0001f);
        }
        [Test]
        public void InvalidOrMissingEditsDoNotGrowOrRewriteTheArray()
        {
            var so=new SerializedObject(Make(10));var array=Distances(so);
            Assert.IsFalse(StaticInstanceEditorCommon.SetLodDistanceBoundary(array,1,3,20,0));
            Assert.IsFalse(StaticInstanceEditorCommon.SetLodDistanceBoundary(array,0,3,float.NaN,0));
            Assert.IsFalse(StaticInstanceEditorCommon.SetLodDistanceBoundary(array,0,3,float.PositiveInfinity,0));
            Assert.IsFalse(so.hasModifiedProperties);Assert.AreEqual(1,array.arraySize);Assert.AreEqual(10,array.GetArrayElementAtIndex(0).floatValue);
        }
        [Test]
        public void AgreedMultiEditIsOneUndoableSerializedChange()
        {
            var a=Make(10,20);var b=Make(10,20);var so=new SerializedObject(new Object[]{a,b});var array=Distances(so);
            Assert.IsTrue(StaticInstanceEditorCommon.LodDistancesAgree(array,2));
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Edit LOD distance");
            Assert.IsTrue(StaticInstanceEditorCommon.SetLodDistanceBoundary(array,0,2,12.345f,0));so.ApplyModifiedProperties();Undo.FlushUndoRecordObjects();
            foreach(var item in new[]{a,b})Assert.AreEqual(12.345f,Distances(new SerializedObject(item)).GetArrayElementAtIndex(0).floatValue);
            Undo.CollapseUndoOperations(group);Undo.PerformUndo();
            foreach(var item in new[]{a,b})Assert.AreEqual(10,Distances(new SerializedObject(item)).GetArrayElementAtIndex(0).floatValue);
            Undo.PerformRedo();foreach(var item in new[]{a,b})Assert.AreEqual(12.345f,Distances(new SerializedObject(item)).GetArrayElementAtIndex(0).floatValue);
        }
        public sealed class NumericWindow : EditorWindow
        {
            [System.NonSerialized] public SerializedObject data;public bool mixed;public int draws;public System.Exception failure;
            void OnGUI(){if(data==null)return;try{StaticInstanceEditorCommon.DrawDistanceLadderRows(Distances(data),3,0,0,mixed);StaticInstanceEditorCommon.DrawEffectiveLodFade(data.FindProperty("forcedLodLevel"),data.FindProperty("fadeMode"));draws++;}catch(System.Exception ex){failure=ex;}}
        }
        [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator RepaintPreservesShortLadder() => RepaintPreservesLadder(false);
        [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator RepaintPreservesMixedLadder() => RepaintPreservesLadder(true);
        System.Collections.IEnumerator RepaintPreservesLadder(bool mixed)
        {
            var a=Make(10);var b=Make(mixed?20:10);
            a.ForcedLodLevel=1;b.ForcedLodLevel=mixed?-1:1;
            a.FadeMode=StaticInstanceFadeMode.Distance;b.FadeMode=mixed?StaticInstanceFadeMode.Time:StaticInstanceFadeMode.Distance;
            var so=new SerializedObject(new Object[]{a,b});
            Assert.AreEqual(!mixed,StaticInstanceEditorCommon.LodDistancesAgree(Distances(so),1));
            var window=ScriptableObject.CreateInstance<NumericWindow>();
            try
            {
                window.data=so;window.mixed=mixed;window.ShowUtility();
                yield return null;
                window.Repaint();
                yield return null;
                window.SendEvent(new Event{type=EventType.Layout});window.SendEvent(new Event{type=EventType.Repaint});
                Assert.IsNull(window.failure);Assert.Greater(window.draws,0,"Actual IMGUI event must run");
                Assert.IsFalse(so.hasModifiedProperties);Assert.AreEqual(1,Distances(so).arraySize);
                Assert.AreEqual(1,a.ForcedLodLevel);Assert.AreEqual(mixed?-1:1,b.ForcedLodLevel);
                Assert.AreEqual(StaticInstanceFadeMode.Distance,a.FadeMode);Assert.AreEqual(mixed?StaticInstanceFadeMode.Time:StaticInstanceFadeMode.Distance,b.FadeMode);
                Assert.AreEqual(10,Distances(new SerializedObject(a)).GetArrayElementAtIndex(0).floatValue);
                Assert.AreEqual(mixed?20:10,Distances(new SerializedObject(b)).GetArrayElementAtIndex(0).floatValue);
            }
            finally { window.Close();Object.DestroyImmediate(window); }
        }
    }
}
