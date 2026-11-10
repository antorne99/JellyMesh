namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// What forgetting the servers missing from the settings did.
/// </summary>
/// <param name="Servers">How many servers were forgotten now.</param>
public sealed record ForgetResponse(int Servers);
