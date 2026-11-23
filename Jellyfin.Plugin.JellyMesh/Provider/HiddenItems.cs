using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The items of each library found hidden from peers, kept in memory so that the next read leaves them out of its query.
/// </summary>
internal sealed class HiddenItems
{
    /// <summary>The most items kept for a type of a library. A read that meets more of them fails.</summary>
    public const int MaxPerType = 10_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid Library, BaseItemKind Kind), ImmutableHashSet<Guid>> _items = [];

    /// <summary>
    /// Gives the items known for a type of a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <returns>The items, which no later change touches.</returns>
    public ImmutableHashSet<Guid> Of(Guid libraryId, BaseItemKind kind)
    {
        lock (_lock)
        {
            return _items.GetValueOrDefault((libraryId, kind), ImmutableHashSet<Guid>.Empty);
        }
    }

    /// <summary>
    /// Adds items to those known for a type of a library, up to the most it keeps.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <param name="ids">The items.</param>
    public void Add(Guid libraryId, BaseItemKind kind, IEnumerable<Guid> ids)
    {
        lock (_lock)
        {
            var known = Of(libraryId, kind);
            _items[(libraryId, kind)] = known.Union(ids.Where(id => !known.Contains(id)).Distinct().Take(MaxPerType - known.Count));
        }
    }

    /// <summary>
    /// Forgets items of a type of a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <param name="ids">The items.</param>
    public void Remove(Guid libraryId, BaseItemKind kind, IReadOnlyCollection<Guid> ids)
    {
        lock (_lock)
        {
            var left = Of(libraryId, kind).Except(ids);
            if (left.IsEmpty)
            {
                _items.Remove((libraryId, kind));
            }
            else
            {
                _items[(libraryId, kind)] = left;
            }
        }
    }

    /// <summary>
    /// Forgets every library but these.
    /// </summary>
    /// <param name="libraryIds">The libraries.</param>
    public void KeepOnly(IReadOnlySet<Guid> libraryIds)
    {
        lock (_lock)
        {
            foreach (var key in _items.Keys.Where(key => !libraryIds.Contains(key.Library)).ToList())
            {
                _items.Remove(key);
            }
        }
    }
}
