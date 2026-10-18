using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StaticInstances
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [UnityEngine.Categorization.CategoryInfo(Name = "Static Instances", Order = 45)]
    // Order within the category: the runtime quality knobs first, then
    // StaticInstanceEditorSettings (scene operations and bake switches), then
    // StaticInstanceStrippingSettings, which keeps the three build/bake policy
    // toggles together at the bottom of the page.
    [UnityEngine.Categorization.ElementInfo(Order = 0)]
    public class StaticInstanceGlobalSettings : IRenderPipelineGraphicsSettings
    {
        internal enum Version : int
        {
            Initial = 0,
        }

        [SerializeField, HideInInspector]
        private Version m_Version;

        public int version => (int)m_Version;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        // Exposed as minMeshLodLevel and the runtime static MinMeshLodLevel (public
        // API that game code drives for a quality tier).
        [SerializeField, Range(0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel)]
        [Tooltip("Minimum mesh LOD for all SIS owners. Higher levels are coarser; the owner's coarser minimum still applies.")]
        private int m_MinLevel;

        // 0.25-2 covers the practical quality-tier band while keeping drag
        // precision near 1; anything more extreme stays API-only (the runtime
        // static accepts any value >= 0).
        [SerializeField, Range(0.25f, 2f)]
        [Tooltip("LOD distance multiplier: below 1 switches sooner; above 1 retains detail farther.")]
        private float m_LodDistanceScale = 1f;

        [SerializeField, Range(0.25f, 2f)]
        [Tooltip("SI-only multiplier for LOD and scalable draw distances; 1 is neutral. Screen Size absolute draw limits are unchanged.")]
        private float m_LodBias = 1f;

        // Project-wide override on every component's Fade Mode, and it only points
        // one way: Disabled hard-switches everything regardless of what each
        // component authored, while Enabled merely stops forcing and cannot make a
        // component that authored None fade. Authored values are kept either way.
        [SerializeField]
        [Tooltip("Enable authored LOD and Draw Range fades, or disable for hard switches. Requires LOD Cross Fade in the URP Asset.")]
        private bool m_CrossFade = true;

        public bool crossFade
        {
            get => m_CrossFade;
            set => this.SetValueAndNotify(ref m_CrossFade, value);
        }

        [SerializeField, Range(0f, 1f)]
        [Tooltip("Default Time fade duration in seconds, used when an owner's Fade Duration is 0.")]
        private float m_LodFadeDuration = 0.5f;

        [SerializeField]
        [Tooltip("Blend curve for Time and Distance fades. Does not change LOD selection or cull boundaries.")]
        private StaticInstanceLodSystem.FadeCurveMode m_FadeCurve =
            StaticInstanceLodSystem.FadeCurveMode.Linear;

        public StaticInstanceLodSystem.FadeCurveMode fadeCurve
        {
            get => m_FadeCurve;
            set => this.SetValueAndNotify(ref m_FadeCurve, value);
        }

        // 0 disables the feature and is the default: with it off the output, the
        // command counts and the fade bin dimension are unchanged. The upper end
        // is generous because "size" here is the bounding-sphere diameter: a 2 m
        // prop at 50 reaches 100 m, a 20 m barn 1 km.
        [SerializeField, Range(0f, 100f)]
        [Tooltip("Far-cull metres per metre of instance size for Distance mode; 0 disables it. Changes rebuild culling data.")]
        private float m_SizeCullDistanceScale;

        public float sizeCullDistanceScale
        {
            get => m_SizeCullDistanceScale;
            set => this.SetValueAndNotify(ref m_SizeCullDistanceScale, Mathf.Max(0f, value));
        }

        // Upper bound 0.5 because half the limit is already an extreme band.
        // Authored rather than fixed because the band is a distance, so how long
        // it reads depends on camera speed; lowering the scale above cannot
        // substitute, since the band scales with the limit.
        [SerializeField, Range(0f, 0.5f)]
        [Tooltip("Fraction of a size- or ratio-derived far limit used as the Distance fade band. 0 removes the band; Time uses duration.")]
        private float m_DerivedCullFadeFraction = 0.15f;

        public float derivedCullFadeFraction
        {
            get => m_DerivedCullFadeFraction;
            set => this.SetValueAndNotify(ref m_DerivedCullFadeFraction, Mathf.Max(0f, value));
        }

        // Runtime FOV tracking. Declared here, immediately before the Shadows
        // opener, because section membership is declaration order (see the
        // drawer's Sections table): a band opens at its first field and runs to
        // the next listed one. Default off; when off, behaviour is identical to not
        // having the feature.
        [SerializeField]
        [Tooltip("Adjust Distance-mode boundaries for perspective FOV using each owner's Reference FOV. Zooming in extends the draw range.")]
        private bool m_FovTrackingEnabled;

        public bool fovTrackingEnabled
        {
            get => m_FovTrackingEnabled;
            set => this.SetValueAndNotify(ref m_FovTrackingEnabled, value);
        }

        // The Reference FOV is authored per component (on the LOD band), so there
        // is no project-wide reference value here.
        //
        // Receiver Sphere Cull defaults to ON. An asset that already stores a value
        // keeps it, since Unity deserializes over the field initializer.
        [SerializeField]
        [Tooltip("Reject clusters and instances whose shadows cannot reach a receiver. Profile the benefit in your scene.")]
        private bool m_ReceiverSphereCullEnabled = true;

        public bool receiverSphereCullEnabled
        {
            get => m_ReceiverSphereCullEnabled;
            set => this.SetValueAndNotify(ref m_ReceiverSphereCullEnabled, value);
        }

        public int minMeshLodLevel
        {
            get => m_MinLevel;
            set => this.SetValueAndNotify(
                ref m_MinLevel,
                Mathf.Clamp(value, 0, StaticInstanceLodSystem.MaxAuthorableMeshLodLevel));
        }

        public float lodDistanceScale
        {
            get => m_LodDistanceScale;
            set => this.SetValueAndNotify(ref m_LodDistanceScale, Mathf.Max(0f, value));
        }

        public float lodBias
        {
            get => m_LodBias;
            set => this.SetValueAndNotify(ref m_LodBias, Mathf.Max(0f, value));
        }

        public float lodFadeDuration
        {
            get => m_LodFadeDuration;
            set => this.SetValueAndNotify(ref m_LodFadeDuration, Mathf.Max(0f, value));
        }

        // Pushes the authored values into the runtime statics. Called at load
        // (below) and by the graphics-settings drawer after an edit so a change
        // made during Play Mode is visible immediately; game code that drove the
        // statics at runtime can call it to return to the authored values.
        public void ApplyToSystem()
        {
            StaticInstanceLodSystem.MinMeshLodLevel = minMeshLodLevel;
            StaticInstanceLodSystem.LodDistanceScale = lodDistanceScale;
            StaticInstanceLodSystem.LodBias = lodBias;
            StaticInstanceLodSystem.LodFadeDuration = lodFadeDuration;
            StaticInstanceLodSystem.CrossFadeEnabled = crossFade;
            StaticInstanceLodSystem.SizeCullDistanceScale = sizeCullDistanceScale;
            StaticInstanceLodSystem.DerivedCullFadeFraction = derivedCullFadeFraction;
            StaticInstanceLodSystem.FadeCurve = fadeCurve;
            StaticInstanceLodSystem.ReceiverSphereCullEnabled = receiverSphereCullEnabled;

            // Resolved per camera per frame from these statics, so a mid-play
            // change takes effect on the next culling request.
            StaticInstanceLodSystem.FovTrackingEnabled = fovTrackingEnabled;

        }

        // BeforeSceneLoad so the statics hold the authored values before any
        // component's OnEnable runs. Domain reload resets the statics to their
        // compile-time defaults; this restores the authored values on every
        // entry into Play Mode and at player startup. A project without the
        // container keeps the compile-time defaults.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyOnLoad()
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings(
                    out StaticInstanceGlobalSettings settings))
            {
                return;
            }

            settings.ApplyToSystem();
        }
    }
}
