using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Ignores the entries with one of the given names, in any folder.
internal sealed class FakeIgnoreRules(params string[] names) : IServerIgnoreRules
{
    public bool Ignores(string folder, string name, bool isDirectory) => names.Contains(name, StringComparer.Ordinal);
}
