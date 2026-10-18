using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace StaticInstances.Editor.Tests
{
    // Two things that need a test rather than a comment,
    // because both are invisible at the call site and neither shows up in a
    // development build (the configuration tests run in).
    public sealed class StaticInstanceLoggingTests
    {
        // The mechanism itself:
        // in a release player Debug.Log is NOT stripped: the call still runs and
        // its argument is still built, although nobody reads it. [Conditional] is what
        // removes both. Without these attributes a release player pays for
        // output nobody receives, silently and only in the configuration nobody tests.
        [Test]
        public void InfoLogging_IsCompiledOutOfReleaseBuilds()
        {
            MethodInfo[] overloads = typeof(StaticInstanceLog)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == nameof(StaticInstanceLog.Info))
                .ToArray();

            Assert.IsNotEmpty(overloads, "StaticInstanceLog.Info has gone missing.");

            foreach (MethodInfo overload in overloads)
            {
                string[] conditions = overload
                    .GetCustomAttributes<ConditionalAttribute>()
                    .Select(a => a.ConditionString)
                    .ToArray();

                // DEBUG = UNITY_EDITOR || DEVELOPMENT_BUILD (the latter is a
                // deprecated directive since 6000.6).
                CollectionAssert.AreEquivalent(
                    new[] { "DEBUG" },
                    conditions,
                    $"{overload} must vanish from release builds and stay in the " +
                    "Editor and development builds; the second half keeps Info output " +
                    "available there.");
            }
        }

        // The fail-visible tier is deliberately NOT routed through the wrapper.
        // This pins that: routing warnings and errors through Info would strip
        // them from release builds.
        [Test]
        public void TheWrapper_CarriesNoWarningOrErrorEntryPoint()
        {
            string[] names = typeof(StaticInstanceLog)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(m => m.Name)
                .Distinct()
                .ToArray();

            CollectionAssert.AreEquivalent(
                new[] { nameof(StaticInstanceLog.Info) },
                names.Where(n => !n.StartsWith("get_") && !n.StartsWith("set_")).ToArray(),
                "Warnings and errors stay direct Debug calls: they construct only " +
                "on failure branches, and a shipping title is meant to see them.");
        }

        // The split exists so a Scatter does not read its own name on the
        // path that reports nothing. The two halves must agree exactly, or the
        // Initialize call site starts either missing a real shortfall or
        // building a message it then throws away.
        [TestCase(true, 1, 3, new[] { 50f }, true, TestName = "ShortLadder_IsAShortfall")]
        [TestCase(true, 0, 2, new[] { 50f, 100f }, false, TestName = "CompleteLadder_IsNot")]
        [TestCase(true, 0, 1, new[] { 50f, 100f, 200f }, false, TestName = "ExtraDistances_AreNot")]
        [TestCase(false, 0, 3, null, false, TestName = "LodOff_IsNot")]
        [TestCase(true, 2, 2, null, false, TestName = "SingleLevelWindow_IsNot")]
        [TestCase(true, 0, 2, null, true, TestName = "NoDistancesAtAll_IsAShortfall")]
        public void LadderShortfall_ConditionAgreesWithTheMessage(
            bool lodEnabled, int minLevel, int maxLevel, float[] distances, bool expected)
        {
            bool condition = StaticInstanceScatter.HasLadderShortfall(
                lodEnabled, minLevel, maxLevel, distances);
            string message = StaticInstanceScatter.LadderShortfallWarning(
                "Owner", lodEnabled, minLevel, maxLevel, distances);

            Assert.AreEqual(expected, condition, "the condition itself");
            Assert.AreEqual(
                condition, message != null,
                "the condition and the message disagreed. They are separate so the " +
                "owner's name is read only when something is reported; separate is " +
                "also how they drift.");
        }
    }
}
