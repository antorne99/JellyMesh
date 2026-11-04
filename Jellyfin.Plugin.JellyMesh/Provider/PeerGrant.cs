using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What a connected server may do here (decision 12): it comes in as a dedicated local user, and this is bound to that user.
/// </summary>
/// <param name="UserId">The local user the other server logs in with.</param>
/// <param name="SharedLibraryIds">The libraries shared with it, in the <c>N</c> format.</param>
/// <param name="MaxStreams">The most streams at the same time.</param>
public sealed record PeerGrant(Guid UserId, IReadOnlySet<string> SharedLibraryIds, int MaxStreams)
{
    /// <summary>
    /// Gets a value that changes with the shared libraries. It is part of a feed cursor: a peer with a new share has to
    /// read everything again, because the feed only tells what changed in the items.
    /// </summary>
    public string ShareKey
    {
        get
        {
            var text = string.Join(',', SharedLibraryIds.Order(StringComparer.OrdinalIgnoreCase));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
        }
    }
}
