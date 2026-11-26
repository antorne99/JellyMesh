using Emby.Naming.Common;
using Emby.Naming.Video;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Jellyfin's own naming rules, to check how the server reads a stub path.
internal static class JellyfinNaming
{
    private static readonly NamingOptions _naming = new();

    // Whether Jellyfin reads the file as an extra of another video.
    public static bool IsExtra(string path) => ExtraRuleResolver.GetExtraInfo(path, _naming).ExtraType is not null;
}
