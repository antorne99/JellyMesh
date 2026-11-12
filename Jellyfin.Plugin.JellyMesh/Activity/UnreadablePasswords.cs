using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// Remembers which servers have a stored password that does not decrypt, so that each is written once until it is fixed.
/// </summary>
internal sealed class UnreadablePasswords
{
    private readonly Lock _gate = new();
    private HashSet<Guid> _known = [];

    // Takes every configured server and returns those whose password stopped decrypting since the last call.
    public IReadOnlyList<RemoteServerInfo> Update(IReadOnlyList<RemoteServerInfo> servers)
    {
        var unreadable = servers.Where(server => server.NeedsPassword).ToList();
        lock (_gate)
        {
            var fresh = unreadable.Where(server => !_known.Contains(server.Id)).ToList();
            _known = unreadable.Select(server => server.Id).ToHashSet();
            return fresh;
        }
    }
}
