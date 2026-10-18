using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StaticInstances
{
    // Build-time stripping settings: the DOTS_INSTANCING_ON shader variants
    // and whether baked source GameObjects are removed from the build. Lives
    // in the URP Global Settings via IRenderPipelineGraphicsSettings (the same
    // pattern as URPShaderStrippingSetting), so it shows up under Project
    // Settings > Graphics next to the BatchRendererGroup Variants preset and a
    // UPM-distributed package still stores per-project values on the user
    // side. The container never ships: an editor-side
    // IRenderPipelineGraphicsSettingsStripper removes it from players.
    //
    // The stripping itself only runs when every render pipeline asset
    // referenced by the build (Graphics default + quality levels) has the
    // GPU Resident Drawer disabled; see StaticInstanceShaderStripper.
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [UnityEngine.Categorization.CategoryInfo(Name = "Static Instances", Order = 45)]
    // Last — see the ordering note on StaticInstanceGlobalSettings.
    [UnityEngine.Categorization.ElementInfo(Order = 2)]
    public class StaticInstanceStrippingSettings : IRenderPipelineGraphicsSettings
    {
        internal enum Version : int
        {
            Initial = 0,
        }

        [SerializeField, HideInInspector]
        private Version m_Version;

        public int version => (int)m_Version;

        // Decides what a build contains, which is build policy rather than a
        // property of one owner, so it is a global setting. Default true.
        [SerializeField]
        [Tooltip("Remove redundant baked rendering sources from builds, preserving gameplay and physics objects. Disable when a build needs the original renderers.")]
        private bool m_StripBakedSourcesOnBuild = true;

        public bool stripBakedSourcesOnBuild
        {
            get => m_StripBakedSourcesOnBuild;
            set => this.SetValueAndNotify(ref m_StripBakedSourcesOnBuild, value);
        }

        [SerializeField]
        [Tooltip("Strip unused DOTS variants when GRD is off in all build pipelines. Keep BatchRendererGroup Variants at Keep All; Addressables needs the SIS builder.")]
        private bool m_StripUnusedDotsInstancingVariants = true;

        [SerializeField]
        [Tooltip("Additional shaders whose DOTS variants must be kept, including shaders used only by dynamically supplied content.")]
        private List<Shader> m_AdditionalDotsInstancingShaders = new List<Shader>();

        public bool stripUnusedDotsInstancingVariants
        {
            get => m_StripUnusedDotsInstancingVariants;
            set => this.SetValueAndNotify(ref m_StripUnusedDotsInstancingVariants, value);
        }

        public IReadOnlyList<Shader> additionalDotsInstancingShaders =>
            m_AdditionalDotsInstancingShaders;
    }
}
