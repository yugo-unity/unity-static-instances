using System;
using System.Reflection;
using System.Reflection.Emit;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // `ReceiverVolumeConstants` sizes its `fixed byte` buffers from two hardcoded
    // element sizes, while every write and read strides by the compiler's `sizeof`.
    // If `ReceiverSplitSphere` gains a field, the sixth sphere would overlap
    // `WorldToLight` with no crash. A fixed-buffer size must be a compile-time
    // constant and cannot be derived from `sizeof`, so these tests assert that the
    // two agree. They also exercise the culling code directly.
    public sealed class StaticInstanceReceiverVolumeTests
    {
        [Test]
        public void PlaneSizeConstant_MatchesTheRealTypeSize()
        {
            Assert.That(
                UnsafeUtility.SizeOf<Plane>(),
                Is.EqualTo(ReceiverVolumeConstants.PlaneSize),
                "ReceiverVolumeConstants.PlaneSize no longer matches sizeof(Plane). " +
                "The fixed buffer is sized from the constant but written through a " +
                "Plane*, so they must agree or the writes run past the buffer and " +
                "corrupt the fields after it.");
        }

        [Test]
        public void SphereSizeConstant_MatchesTheRealTypeSize()
        {
            Assert.That(
                UnsafeUtility.SizeOf<ReceiverSplitSphere>(),
                Is.EqualTo(ReceiverVolumeConstants.SphereSize),
                "ReceiverVolumeConstants.SphereSize no longer matches " +
                "sizeof(ReceiverSplitSphere). At full capacity the last sphere " +
                "would land on top of WorldToLight — a wrong rotation, not a crash.");
        }

        [Test]
        public void TheBuffersHoldCapacityElements_WithoutOverlappingWhatFollows()
        {
            // The invariant the two constants exist to serve, stated directly:
            // the payload must fit in the space the struct reserves for it.
            int planeBytes = ReceiverVolumeConstants.Capacity * UnsafeUtility.SizeOf<Plane>();
            int sphereBytes =
                ReceiverVolumeConstants.Capacity * UnsafeUtility.SizeOf<ReceiverSplitSphere>();

            Assert.That(
                planeBytes + sphereBytes,
                Is.LessThanOrEqualTo(UnsafeUtility.SizeOf<ReceiverVolumeConstants>()),
                "The two fixed buffers no longer fit inside the struct that holds them.");
        }

        // Exercise the actual mask job with no draw-range fade storage. This
        // covers both the assembled split mask and the receiver rejection,
        // including a cluster wholly inside the split planes.
        [TestCase(0, true, false, 10f, 3, 0)]
        [TestCase(3, true, false, 10f, 3, 0)]
        [TestCase(3, true, false, 0f, 3, 3)]
        [TestCase(3, false, false, 10f, 3, 3)]
        [TestCase(1, true, false, 10f, 1, 1)]
        [TestCase(1, true, true, 10f, 1, 0)]
        [TestCase(0, false, false, 10f, 3, 3)]
        public unsafe void NoDrawRange_VisibilityJobPreservesReceiverAndSingleBitPolicy(
            int insideMask, bool receiverCull, bool singleBitCull,
            float casterX, int intersectMask, int expectedMask)
        {
            using var bounds = new NativeArray<CullingBounds>(new[]
            {
                new CullingBounds
                {
                    Center = new Vector3(casterX, 0f, 0f),
                    Extents = Vector3.one * 0.1f,
                },
            }, Allocator.Temp);
            using var clusters = new NativeArray<int>(1, Allocator.Temp);
            byte inside = (byte)insideMask;
            byte intersect = (byte)intersectMask;
            byte visible = 0xFF;
            // Empty split plane ranges accept the straddling control; the
            // receiver spheres must independently reject the distant caster.
            SplitPlaneRange* splits = stackalloc SplitPlaneRange[2];
            splits[0] = default;
            splits[1] = default;
            ReceiverVolumeConstants constants = default;
            constants.WorldToLight = float3x3.identity;
            constants.ShadowDirection = new float3(0f, 0f, 1f);
            constants.SplitCount = 2;
            var spheres = (ReceiverSplitSphere*)constants.SphereBytes;
            spheres[0] = new ReceiverSplitSphere { SphereLightSpace = new float4(0f, 0f, 0f, 1f) };
            spheres[1] = new ReceiverSplitSphere { SphereLightSpace = new float4(0f, 0f, 0f, 2f) };

            Type type = typeof(StaticInstanceRendererSystem).GetNestedType(
                "ComputeVisibilityMasksJob", BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null);
            object job = Activator.CreateInstance(type);
            void Set(string name, object value) => type.GetField(name).SetValue(job, value);
            Set("Bounds", bounds);
            Set("InstanceClusters", clusters);
            // Mono's reflection binder rejects System.Reflection.Pointer for
            // pointer fields. Emit a typed field store into the boxed job so
            // all pointer assignments bypass that binder, without exposing
            // the private production job or duplicating its implementation.
            void SetPointer(string name, void* value)
            {
                var setter = new DynamicMethod("Set" + name, typeof(void),
                    new[] { typeof(object), typeof(IntPtr) }, type.Module, true);
                ILGenerator il = setter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox, type);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Stfld, type.GetField(name));
                il.Emit(OpCodes.Ret);
                ((Action<object, IntPtr>)setter.CreateDelegate(typeof(Action<object, IntPtr>)))(
                    job, (IntPtr)value);
            }
            SetPointer("Splits", splits);
            SetPointer("ClusterIntersectMasks", &intersect);
            SetPointer("ClusterInsideMasks", &inside);
            SetPointer("VisibilityMasks", &visible);
            Set("SplitCount", 2);
            Set("DistanceCull", false);
            Set("ReceiverInstanceCull", receiverCull);
            Set("SingleBitCull", singleBitCull);
            Set("ReceiverConstants", constants);
            type.GetMethod("Execute").Invoke(job, new object[] { 0 });

            Assert.That(visible, Is.EqualTo((byte)expectedMask));
        }

        // The reach test's two rejection arms, exercised directly. These are the
        // arms the per-instance test relies on being able to CLEAR a bit — the
        // whole point of the reach test is that the cluster pass could not.
        [Test]
        public unsafe void AReceiverSphereOutOfXyRange_IsNotReached()
        {
            var sphere = new ReceiverSplitSphere
            {
                // Receiver at the origin, radius 1.
                SphereLightSpace = new float4(0f, 0f, 0f, 1f),
                CascadeBlendCullingFactor = 0f,
            };

            // Caster of radius 1 sitting 10 units away in light-space x: the two
            // spheres cannot even overlap as circles, so no shadow lands here.
            uint mask = StaticInstanceCullingBurst.ComputeReceiverSphereMask(
                new float3(10f, 0f, 0f), 1f, 0f, &sphere, 1);

            Assert.That(mask, Is.EqualTo(0u));
        }

        [Test]
        public unsafe void AReceiverSphereWithinRange_IsReached()
        {
            var sphere = new ReceiverSplitSphere
            {
                SphereLightSpace = new float4(0f, 0f, 0f, 1f),
                CascadeBlendCullingFactor = 0f,
            };

            uint mask = StaticInstanceCullingBurst.ComputeReceiverSphereMask(
                new float3(0.5f, 0f, 0f), 1f, 0f, &sphere, 1);

            Assert.That(mask, Is.EqualTo(1u));
        }

        [Test]
        public unsafe void ACasterPastTheReceiverInTheLightDirection_IsNotReached()
        {
            var sphere = new ReceiverSplitSphere
            {
                SphereLightSpace = new float4(0f, 0f, 0f, 1f),
                CascadeBlendCullingFactor = 0f,
            };

            // Positive z in light space is "past" the receiver: the shadow
            // travels away from it and can never land on it.
            uint mask = StaticInstanceCullingBurst.ComputeReceiverSphereMask(
                new float3(0f, 0f, 10f), 1f, 0f, &sphere, 1);

            Assert.That(mask, Is.EqualTo(0u));
        }

        // The core-containment break: a caster wholly inside split 0's core
        // sphere is dropped from every coarser split, because shaders sample the
        // lowest-index cascade that covers a pixel. This is the arm that can
        // only ever strip HIGHER-index splits, which is why the countbits
        // gate is free for it.
        [Test]
        public unsafe void ACasterInsideTheCoreOfSplitZero_IsDroppedFromCoarserSplits()
        {
            var spheres = stackalloc ReceiverSplitSphere[2];
            spheres[0] = new ReceiverSplitSphere
            {
                SphereLightSpace = new float4(0f, 0f, 0f, 100f),
                CascadeBlendCullingFactor = 1f,
            };
            spheres[1] = new ReceiverSplitSphere
            {
                SphereLightSpace = new float4(0f, 0f, 0f, 1000f),
                CascadeBlendCullingFactor = 1f,
            };

            // A tiny caster at the centre, with no shadow length: deep inside
            // split 0's core, so split 1 must not be set.
            uint mask = StaticInstanceCullingBurst.ComputeReceiverSphereMask(
                float3.zero, 1f, 0f, spheres, 2);

            Assert.That(mask, Is.EqualTo(1u), "split 1 should have been broken out of");
        }

        // The glancing-angle clamp widens the result, which is the SAFE
        // direction: a longer shadow keeps more splits. A plane the shadow runs
        // parallel to must not produce a negative or NaN length.
        [Test]
        public unsafe void ShadowLength_IsNeverNegative_EvenAtAGlancingAngle()
        {
            var plane = new Plane(new Vector3(0f, 1f, 0f), 0f);
            float3 shadowDirection = new float3(1f, 0f, 0f); // parallel to the plane

            float length = StaticInstanceCullingBurst.ComputeShadowLength(
                new float3(0f, -5f, 0f), 1f, shadowDirection, &plane, 1);

            Assert.That(length, Is.GreaterThanOrEqualTo(0f));
            Assert.That(float.IsNaN(length), Is.False);
        }
    }
}
