using NUnit.Framework;
using UnityEngine;

namespace StaticInstances.Editor.Tests
{
    // The shape-aware size correction. These pin the contract, not the
    // arithmetic for its own sake:
    //
    // (1) THE CUBE IS EXACTLY 1. The factor is
    //     `c = sqrt((xy+yz+zx)/(x²+y²+z²))`, normalized so a cube reads exactly 1
    //     and the Size Cull Distance Scale keeps a fixed meaning for compact
    //     content.
    //
    // (2) IT ONLY EVER SHRINKS. `c <= 1` for every box, with equality at the
    //     cube — a factor above 1 would EXTEND a cull distance, which this
    //     feature must never do. The implementation clamps rather than
    //     trusting the closed form, because a float round-trip at the cube can
    //     land just above 1.
    //
    // (3) ROTATION IS EXCLUDED BY CONSTRUCTION. The factor is baked because the
    //     world AABB is rotation-inflated: a 45°-rotated plate must measure as
    //     the plate it is.
    public sealed class StaticInstanceShapeFactorTests
    {
        private static Bounds LocalBox(float x, float y, float z) =>
            new Bounds(Vector3.zero, new Vector3(x, y, z) * 2f); // extents = (x,y,z)

        private static float Factor(Bounds local, Matrix4x4 matrix) =>
            StaticInstanceBuilder.ShapeFactorOf(local, matrix);

        [Test]
        public void Cube_IsExactlyOne()
        {
            float c = Factor(LocalBox(1f, 1f, 1f), Matrix4x4.identity);

            // Bit-exact, not approximate.
            Assert.AreEqual(
                1f, c,
                "a cube must read exactly 1");
        }

        [Test]
        public void Cube_IsOne_AtEveryUniformScale()
        {
            // Scale must not leak into a SHAPE factor: a 10 m cube and a 0.1 m
            // cube are the same shape, and the size input is already carried by
            // the radius the factor multiplies.
            foreach (float s in new[] { 0.01f, 0.5f, 1f, 7.5f, 100f })
            {
                float c = Factor(LocalBox(1f, 1f, 1f), Matrix4x4.Scale(Vector3.one * s));
                Assert.AreEqual(1f, c, 1e-6f, $"uniform scale {s} changed the shape factor");
            }
        }

        [Test]
        public void ThinPlateAndPole_MatchTheWorkedValues()
        {
            // The worked numbers for the cube-normalized form: plate 0.74, pole 0.22.
            float plate = Factor(LocalBox(1f, 1f, 0.05f), Matrix4x4.identity);
            float pole = Factor(LocalBox(0.1f, 4f, 0.1f), Matrix4x4.identity);

            Assert.AreEqual(0.74f, plate, 0.005f, "thin plate (1, 1, 0.05)");
            Assert.AreEqual(0.22f, pole, 0.005f, "pole (0.1, 4, 0.1)");
        }

        [Test]
        public void EveryShape_IsAtMostOne_SoTheFactorNeverExtendsACullDistance()
        {
            var boxes = new[]
            {
                LocalBox(1f, 1f, 1f),
                LocalBox(1f, 1f, 0.05f),
                LocalBox(0.1f, 4f, 0.1f),
                LocalBox(3f, 0.001f, 3f),
                LocalBox(0.001f, 0.001f, 12f),
                LocalBox(2f, 3f, 5f),
            };

            foreach (Bounds b in boxes)
            {
                float c = Factor(b, Matrix4x4.identity);
                Assert.LessOrEqual(c, 1f, $"factor above 1 for extents {b.extents}");
                Assert.Greater(c, 0f, $"factor at or below 0 for extents {b.extents}");
            }
        }

        [Test]
        public void Rotation_DoesNotInflateTheFactor_WhichIsWhyItIsBaked()
        {
            Bounds plate = LocalBox(1f, 1f, 0.05f);
            float unrotated = Factor(plate, Matrix4x4.identity);
            float rotated = Factor(
                plate, Matrix4x4.Rotate(Quaternion.Euler(45f, 30f, 15f)));

            // Same plate, same answer. The world AABB of the rotated plate is
            // far bulkier than the plate, which is exactly the under-correction
            // this split avoids — and the reason the factor cannot be
            // recomputed at initialization from the serialized bounds.
            Assert.AreEqual(
                unrotated, rotated, 1e-5f,
                "rotation changed the shape factor, so the local-extents split is not in effect");
        }

        [Test]
        public void NonUniformScale_IsHonoured_SoAStretchedCubeReadsAsAPlate()
        {
            // A cube scaled flat IS a plate; the factor must see the scale,
            // even though it must ignore rotation.
            float c = Factor(
                LocalBox(1f, 1f, 1f), Matrix4x4.Scale(new Vector3(1f, 1f, 0.05f)));
            float authoredPlate = Factor(LocalBox(1f, 1f, 0.05f), Matrix4x4.identity);

            Assert.AreEqual(
                authoredPlate, c, 1e-5f,
                "a flattened cube must read like an authored plate of the same dimensions");
        }

        [Test]
        public void DegenerateExtents_ReturnIdentity_NeverZero()
        {
            // A broken or point-like mesh must not cull its instance to
            // nothing: identity is the safe answer, and 0 would be fatal.
            Assert.AreEqual(1f, Factor(LocalBox(0f, 0f, 0f), Matrix4x4.identity),
                "all-zero extents must read as identity");
            Assert.AreEqual(1f, Factor(LocalBox(1f, 0f, 0f), Matrix4x4.identity),
                "a degenerate line must read as identity");
            Assert.AreEqual(1f, Factor(LocalBox(1f, 1f, 1f), Matrix4x4.Scale(Vector3.zero)),
                "a zero-scale matrix must read as identity");
        }
    }
}
