using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StaticInstances
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [UnityEngine.Categorization.CategoryInfo(Name = "Static Instances", Order = 45)]
    // Ordered between the quality knobs and the stripping switches; see the
    // ordering note on StaticInstanceGlobalSettings.
    [UnityEngine.Categorization.ElementInfo(Order = 1)]
    public class StaticInstanceEditorSettings : IRenderPipelineGraphicsSettings
    {
        internal enum Version : int
        {
            Initial = 0,
        }

        [SerializeField, HideInInspector]
        private Version m_Version;

        public int version => (int)m_Version;

        // Default ON. Phrased affirmatively rather than as "Skip...": a
        // default-on toggle reads more clearly that way.
        [SerializeField]
        [Tooltip("Rebake unbaked or stale targets in open scenes before Play. Leaves changed scenes dirty; save them to keep the bake.")]
        private bool m_BakeOnEnteringPlayMode = true;

        public bool bakeOnEnteringPlayMode
        {
            get => m_BakeOnEnteringPlayMode;
            set => this.SetValueAndNotify(ref m_BakeOnEnteringPlayMode, value);
        }

        // Default ON. More consequential than the play-mode switch above:
        // turning it off changes what the player contains, which is why the
        // tooltip states the runtime cost.
        [SerializeField]
        [Tooltip("Rebake on the build scene copy without changing scene assets. When off, unbaked targets collect at runtime; stale bakes still fail the build.")]
        private bool m_BakeOnBuild = true;

        public bool bakeOnBuild
        {
            get => m_BakeOnBuild;
            set => this.SetValueAndNotify(ref m_BakeOnBuild, value);
        }
    }
}
