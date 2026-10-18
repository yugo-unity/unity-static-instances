using System.Runtime.CompilerServices;

// The stripper's collection state (StaticInstanceShaderStripping)
// and the two collectors are internal, and deliberately stay that way — they
// are build-pipeline plumbing, not API. The Editor tests are the only thing
// that needs inside access: a collector that silently under-collects is
// invisible from outside, so the only assertion that catches it is
// "which shaders reached the keep list".
[assembly: InternalsVisibleTo("StaticInstances.Editor.Tests")]

// The Addressables build script's assembly is ours and version-gated,
// and the hint probe it registers is plumbing between it and the settings
// drawer — not API a project should see. Making it visible here keeps it out of
// the public surface; the two methods a project's own build
// script calls, and the marker interface it implements, stay public.
[assembly: InternalsVisibleTo("StaticInstances.Editor.Addressables")]
