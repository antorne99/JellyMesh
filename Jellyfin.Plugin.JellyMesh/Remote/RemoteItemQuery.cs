using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// One page of <c>/Items</c>.
/// </summary>
/// <param name="LibraryId">The library to read, or null to look up <paramref name="Ids"/>.</param>
/// <param name="Type">The item type.</param>
/// <param name="StartIndex">The index of the first item.</param>
/// <param name="Limit">The most items to return.</param>
/// <param name="Ids">The items to look up, if any.</param>
/// <param name="ChangedSinceUtc">Only items saved on the remote at or after this time of its clock, if set.</param>
/// <param name="ViaProvider">Whether to read the ProviderApi of a JellyMesh remote instead of the stock route.</param>
public sealed record RemoteItemQuery(
    string? LibraryId,
    RemoteItemType Type,
    int StartIndex,
    int Limit,
    IReadOnlyList<string>? Ids = null,
    DateTimeOffset? ChangedSinceUtc = null,
    bool ViaProvider = false);
