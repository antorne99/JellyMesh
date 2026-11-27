namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Rules that skip nothing, for plans that write no files.
/// </summary>
internal sealed class NoIgnoreRules : IServerIgnoreRules
{
    /// <summary>Gets the instance.</summary>
    public static NoIgnoreRules Instance { get; } = new();

    /// <inheritdoc />
    public bool Ignores(string folder, string name, bool isDirectory) => false;
}
