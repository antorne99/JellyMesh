using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Reads the federated folder from <c>JELLYMESH_FEDERATED_ROOT</c>.
/// </summary>
public sealed class EnvironmentFederatedRootSource : IFederatedRootSource
{
    /// <summary>
    /// The environment variable holding the folder.
    /// </summary>
    public const string VariableName = "JELLYMESH_FEDERATED_ROOT";

    /// <inheritdoc />
    public string? GetFederatedRoot() => Environment.GetEnvironmentVariable(VariableName);
}
