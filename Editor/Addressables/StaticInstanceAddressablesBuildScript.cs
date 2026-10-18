using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace StaticInstances.Editor.AddressablesSupport
{
    // The opt-in vehicle that lets an Addressables content build strip
    // DOTS variants. Everything Addressables-specific lives in this assembly,
    // which compiles only when com.unity.addressables is installed (asmdef
    // defineConstraints + versionDefines) — that is what keeps Runtime+Editor
    // copyable into a project without the package, structurally rather than by
    // discipline.
    //
    // Applying it is the USER's action, never automatic: create the asset,
    // add it to Addressables' Build & Play Mode Scripts, and select it. Without
    // it a content build is simply never armed, so nothing is stripped — larger
    // bundles, never broken ones.
    //
    // The subclass is deliberately this thin. All the logic is the two statics
    // in StaticInstanceBundleStripping, so a project that already runs its own
    // BuildScriptPackedMode does not need this class at all: it calls the same
    // two methods from its own override — two lines — and implements
    // IStaticInstanceStrippingIntegrated so the settings page can see that it
    // did.
    [CreateAssetMenu(
        fileName = "StaticInstanceAddressablesBuildScript.asset",
        menuName = "Addressables/Content Builders/Static Instances Packed Mode")]
    public class StaticInstanceAddressablesBuildScript
        : BuildScriptPackedMode, IStaticInstanceStrippingIntegrated
    {
        public override string Name
        {
            get { return "Static Instances Packed Mode"; }
        }

        protected override TResult BuildDataImplementation<TResult>(
            AddressablesDataBuilderInput builderInput)
        {
            // Stock behaviour is preserved by construction: the base call runs
            // unmodified inside the try, and every early return happens before
            // it.
            //
            // Begin is INSIDE the try on purpose. It arms the stripper and then
            // walks prefabs and opens scenes, so a throw in there — an
            // unopenable scene path, say — would otherwise leave the stripper
            // armed with a partial keep-set and nothing to tear it down:
            // BuildScriptBase catches the exception and returns an error
            // result, so the Editor session simply carries on, and the next
            // bundled build in it would strip against a keep-set built for
            // another operation. That is the state the arming invariant exists
            // to forbid.
            try
            {
                string refusal = StaticInstanceBundleStripping.BeginContentBuild(
                    EnumerateDeliveredScenePaths(builderInput.AddressableSettings));

                if (refusal != null)
                {
                    // The reason this check is here at all: a bundled build
                    // cannot be failed from inside a scene processor — SBP
                    // swallows the exception and reports success — so
                    // the refusal has to happen before the build, through
                    // Addressables' own failure channel.
                    Debug.LogError(refusal);
                    return AddressableAssetBuildResult.CreateResult<TResult>(null, 0, refusal);
                }

                return base.BuildDataImplementation<TResult>(builderInput);
            }
            finally
            {
                // In a finally because leaving the stripper armed would let the
                // next build operation strip against a keep-set describing
                // content it is not building.
                StaticInstanceBundleStripping.EndContentBuild();
            }
        }

        // The scenes this build actually delivers. Enumerated from the settings
        // rather than from the build's own scene callbacks, because those are
        // suppressed for a scene the content cache considers unchanged — an
        // incremental build would otherwise collect an incomplete keep-set and
        // strip against it.
        //
        // Groups are filtered the way the stock build filters them: a group
        // with no BundledAssetGroupSchema, or one whose schema has
        // IncludeInBuild off, contributes nothing to this build
        // (BuildScriptPackedMode does the same test before packing a group).
        // Without the filter an excluded group's scene could refuse a build
        // that never delivers it — and every such scene would be opened and
        // closed on every content build for nothing.
        internal static IEnumerable<string> EnumerateDeliveredScenePaths(
            AddressableAssetSettings settings)
        {
            if (settings == null)
                yield break;

            var entries = new List<AddressableAssetEntry>();
            foreach (AddressableAssetGroup group in settings.groups)
            {
                if (group == null)
                    continue;

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild)
                    continue;

                entries.Clear();
                group.GatherAllAssets(entries, true, true, false);
                foreach (AddressableAssetEntry entry in entries)
                {
                    if (entry != null && entry.IsScene && !string.IsNullOrEmpty(entry.AssetPath))
                        yield return entry.AssetPath;
                }
            }
        }

        // How the settings page knows whether there is anything to act on.
        // Registered from here because the drawer lives in an assembly that
        // must not reference Addressables — and it tests the MARKER interface,
        // not this type, so a project that integrated the minimal way (two
        // lines in its own build script) is not told to integrate again.
        //
        // Two states answer "nothing to act on": an integrated build script,
        // and no Addressables settings at all. The second is not a subtlety —
        // the package can sit in a project that never made settings, so it runs
        // no content builds, and pointing such a project at "your Addressables
        // settings" would name something that does not exist. Note
        // SettingsExists is checked first because reading Settings CREATES them.
        [InitializeOnLoadMethod]
        private static void RegisterHintProbe()
        {
            StaticInstanceBundleStripping.RegisterHintProbe(() =>
            {
                if (!AddressableAssetSettingsDefaultObject.SettingsExists)
                    return true;

                AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
                return settings == null ||
                    settings.ActivePlayerDataBuilder is IStaticInstanceStrippingIntegrated;
            });
        }
    }
}
