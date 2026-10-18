using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // Screen Size LOD is per instance, so it cannot use a cluster-uniform
    // shortcut that has only cluster-wide information.
    public sealed class StaticInstanceClusterUniformEligibilityTests
    {
        // Well inside the first band for both readings, so the verdict turns on
        // ELIGIBILITY alone and not on where the probe sits.
        private const float ProbeDistance = 12f;
        private const float AuthoredWidth = 10f;

        [Test]
        public unsafe void ScreenSizeOwner_IsNotEligibleForTheClusterUniformShortcut()
        {
            Classify(screenSize: 1, out int eligible, out int uniform);

            Assert.AreEqual(
                0, eligible,
                "A Screen Size owner must NOT be eligible for the cluster-uniform " +
                "shortcut. Its ladder is per INSTANCE (p.Boundaries holds 1/ratio, " +
                "scaled by ScreenRadii[i] x screenTerm in the eval job) and this " +
                "classifier has no per-instance radius, so it cannot choose a " +
                "Screen Size LOD safely.");
            Assert.AreEqual(
                0, uniform,
                "An ineligible cluster cannot be counted uniform.");
        }

        // The control: the gate must not have swallowed the Distance path with
        // it. Without this, deleting the whole predicate's body would still pass
        // the test above.
        [Test]
        public unsafe void DistanceOwner_IsStillEligible_Control()
        {
            Classify(screenSize: 0, out int eligible, out int uniform);

            Assert.AreEqual(
                1, eligible,
                "A Distance-mode PerInstance owner authoring a crossfade width must " +
                "stay eligible — the cluster-uniform saving is not conditional on the mode gate.");
            Assert.AreEqual(
                1, uniform,
                "A point-sized cluster well inside one band must resolve uniform, or " +
                "this control is not exercising the path it claims to.");
        }

        [TestCase(.5f, float.MaxValue, 0f, 0)]
        [TestCase(2f, float.MaxValue, 0f, 1)]
        [TestCase(1f, 10f, 0f, 1)]
        [TestCase(2f, 10f, 0f, 0)]
        [TestCase(1f, float.MaxValue, 8f, 0)]
        [TestCase(2f, float.MaxValue, 8f, 1)]
        public void BiasMovesClusterLodAndNearFarFadeBands(float bias, float far, float near, int expected)
        {
            Classify(0, out int eligible, out int uniform, bias, far, near);
            Assert.AreEqual(1, eligible);
            Assert.AreEqual(expected, uniform);
        }
        // Drives the real Burst classifier with one cluster. Extents 0.01 make
        // dMin ~ dMax ~ the centre distance; the draw-range predicates are
        // retired (farMin = the no-limit sentinel, nearMax = 0) so the verdict
        // is the ladder and the eligibility predicate alone.
        private static unsafe void Classify(byte screenSize, out int eligible, out int uniform,
            float bias = 1f, float far = float.MaxValue, float near = 0f)
        {
            var p = new OwnerLodParams
            {
                RefTan = Mathf.Tan(
                    StaticInstanceLodSystem.DefaultReferenceFovDegrees * 0.5f * Mathf.Deg2Rad),
                ClusterBase = 0,
                ClusterStride = 1,
                InstanceBase = 0,
                Boundaries = new float3(40f, 100f, float.MaxValue),
                CrossFadeWidth = AuthoredWidth,
                Hysteresis = 0f,
                DistanceFade = 1,
                MinLevel = 0,
                MaxLevel = 2,
                Policy = (byte)LodSelectionPolicy.PerInstance,
                ForcedLevel = -1,
                LevelOffset = 0,
                ScreenSize = screenSize,
                AbsoluteFar = 0f,
                AbsoluteNear = 0f,
            };

            var bounds = new CullingBounds
            {
                Center = new float3(0f, 0f, ProbeDistance),
                Extents = new float3(0.01f, 0.01f, 0.01f),
            };

            int owner = 0;
            float farMin = far;
            float nearMax = near;
            int* counters = stackalloc int[3];
            byte uniformLevel = 0;

            StaticInstanceCullingBurst.ClassifyClusterUniformity(
                &bounds, &owner, &farMin, &nearMax, &p,
                0f, 0f, 0f,
                1f,        // distanceScale
                bias,      // lodBias
                1f,        // cameraFovTerm
                0,         // fovApplies — the default has FovTrackingEnabled OFF
                0,         // globalMinLevel
                1,         // crossFadeEnabled
                1,         // clusterCount
                counters,
                &uniformLevel);

            eligible = counters[0];
            uniform = counters[1];
        }
    }
}
