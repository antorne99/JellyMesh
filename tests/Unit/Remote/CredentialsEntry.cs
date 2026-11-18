using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Enters a login again through the edit save, with the saved name and address, as the edit form does.
internal static class CredentialsEntry
{
    // The saved user with a password other than the saved one, so that the save logs in again.
    public static RemoteCredentials NewPassword { get; } = new() { Username = TestValues.Username, Password = "n3w-pa55" };

    public static Task<RemoteResult<RemoteEditReport>> EnterCredentialsAsync(this IRemoteServerRegistry registry, RemoteServerInfo server, RemoteCredentials credentials, CancellationToken cancellationToken)
        => registry.SaveEditAsync(server.Id, Edit(server.Name, server.Url, credentials), null, cancellationToken);

    // For a server the registry does not know, with the name and address of the fixtures.
    public static Task<RemoteResult<RemoteEditReport>> EnterCredentialsAsync(this IRemoteServerRegistry registry, Guid serverId, RemoteCredentials credentials, CancellationToken cancellationToken)
        => registry.SaveEditAsync(serverId, Edit("Friend", TestValues.Url, credentials), null, cancellationToken);

    private static RemoteServerEdit Edit(string name, string url, RemoteCredentials credentials) => new()
    {
        Name = name,
        Url = url,
        Username = credentials.Username,
        Password = credentials.Password
    };
}
