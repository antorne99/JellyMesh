using System;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Reads the key of versions before 0.4 from <c>JELLYMESH_SECRET_KEY</c>, only to move saved credentials to the key file.
/// </summary>
public sealed class EnvironmentSecretKeySource : ILegacySecretKeySource
{
    /// <summary>
    /// The environment variable holding the key.
    /// </summary>
    public const string VariableName = "JELLYMESH_SECRET_KEY";

    /// <inheritdoc />
    public string? GetSecretKey() => Environment.GetEnvironmentVariable(VariableName);
}
