namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The generations of the stub registry a full link run started and ended on.
/// </summary>
/// <param name="StartGeneration">The generation when the run took the registry.</param>
/// <param name="EndGeneration">The generation when it let it go, after its own save if any.</param>
public sealed record VersionLinkRun(long StartGeneration, long EndGeneration);
