using System;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Reads the secret key from <c>JELLYMESH_SECRET_KEY</c>.
/// </summary>
public sealed class EnvironmentSecretKeySource : ISecretKeySource
{
    /// <summary>
    /// The environment variable holding the key.
    /// </summary>
    public const string VariableName = "JELLYMESH_SECRET_KEY";

    /// <inheritdoc />
    public string? GetSecretKey() => Environment.GetEnvironmentVariable(VariableName);
}
