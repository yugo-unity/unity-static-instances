using System.Runtime.CompilerServices;

// The Editor tests need internal access to the builder's buffer layout and the
// renderer's culling state. Mirrors the grant StaticInstances.Editor already makes.
[assembly: InternalsVisibleTo("StaticInstances.Editor.Tests")]
