using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Ignores the entries a rule picks by folder, name and kind.
internal sealed class FakeIgnoreRules(Func<string, string, bool, bool> ignores) : IServerIgnoreRules
{
    // Ignores the entries with one of the given names, in any folder.
    public FakeIgnoreRules(params string[] names)
        : this((_, name, _) => names.Contains(name, StringComparer.Ordinal))
    {
    }

    public bool Ignores(string folder, string name, bool isDirectory) => ignores(folder, name, isDirectory);
}
