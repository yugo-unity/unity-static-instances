using System.Reflection;
using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // A segment's `motionMode` is taken from its FIRST group, and the engine skips a
    // Camera-mode range in the MotionVectors pass before it reads `HasMotion` on any
    // command. So motion-vector-ness has to be part of the segment key, or an MV group
    // sharing a segment with a non-MV one silently loses its per-object motion vectors.
    //
    // These tests pin the draw-segment filter used for motion vectors.
    public sealed class StaticInstanceSegmentKeyTests
    {
        private static bool SameFilter(in CullingDrawGroup a, in CullingDrawGroup b)
        {
            MethodInfo m = typeof(StaticInstanceRendererSystem).GetMethod(
                "SameFilter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "SameFilter is the segment key; renaming it must not silently skip this test");
            return (bool)m.Invoke(null, new object[] { a, b });
        }

        private static CullingDrawGroup Group(byte flags) => new CullingDrawGroup
        {
            Layer = 3,
            ShadowMode = 1,
            RenderingLayerMask = 1u,
            Flags = flags,
        };

        [Test]
        public void TwoGroupsDifferingOnlyInMotionVectors_AreNotTheSameSegment()
        {
            CullingDrawGroup mv = Group(CullingDrawGroup.MotionVectorsFlag);
            CullingDrawGroup plain = Group(0);

            Assert.IsFalse(SameFilter(mv, plain),
                "an MV group and a non-MV group must never share a segment: the range's " +
                "motionMode comes from the first group, and a Camera range discards HasMotion");
            Assert.IsFalse(SameFilter(plain, mv), "and the comparison must be symmetric");
        }

        [Test]
        public void TwoGroupsAgreeingOnTheFilterAttributes_AreTheSameSegment_EvenWithOtherFlagsSet()
        {
            // Over-cutting is not free either: a segment per draw group would give up the
            // merging this path exists for. Only the MV bit is part of the key, so a flag the
            // filter does not carry must still merge.
            CullingDrawGroup a = Group(CullingDrawGroup.FlipWindingFlag);
            CullingDrawGroup b = Group(0);

            Assert.IsTrue(SameFilter(a, b),
                "flags outside the filter attributes must not cut a segment");
            Assert.IsTrue(SameFilter(a, a));
        }
    }
}
