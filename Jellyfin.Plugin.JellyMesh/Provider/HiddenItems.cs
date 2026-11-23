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
/// <param name="clock">The clock.</param>
internal sealed class HiddenItems(TimeProvider clock)
{
    /// <summary>The most items kept for a type of a library. A read that meets more of them fails.</summary>
    public const int MaxPerType = 10_000;

    /// <summary>How often the items kept are looked up again, at most.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid Library, BaseItemKind Kind), Entry> _entries = [];

    /// <summary>
    /// Gets the items known for a type of a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <returns>The items, which no later change touches.</returns>
    public ImmutableHashSet<Guid> Of(Guid libraryId, BaseItemKind kind)
    {
        lock (_lock)
        {
            return _entries.GetValueOrDefault((libraryId, kind))?.Ids ?? ImmutableHashSet<Guid>.Empty;
        }
    }

    /// <summary>
    /// Starts a search of the hidden items of a library, unless one is running or no more items are kept.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <returns>Whether to search, and then to call <see cref="EndSearch"/>.</returns>
    public bool TryBeginSearch(Guid libraryId, BaseItemKind kind)
    {
        lock (_lock)
        {
            var entry = EntryOf(libraryId, kind);
            if (entry.IsSearching || entry.Ids.Count >= MaxPerType)
            {
                return false;
            }

            entry.IsSearching = true;
            return true;
        }
    }

    /// <summary>
    /// Ends a search.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    public void EndSearch(Guid libraryId, BaseItemKind kind)
    {
        lock (_lock)
        {
            EntryOf(libraryId, kind).IsSearching = false;
        }
    }

    /// <summary>
    /// Tells whether the items known are due to be looked up again, and counts that as done.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <returns>Whether to look them up.</returns>
    public bool TryBeginCheck(Guid libraryId, BaseItemKind kind)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue((libraryId, kind), out var entry) || entry.Ids.IsEmpty || clock.GetUtcNow() - entry.CheckedUtc < CheckInterval)
            {
                return false;
            }

            entry.CheckedUtc = clock.GetUtcNow();
            return true;
        }
    }

    /// <summary>
    /// Adds items to those known for a type of a library, up to the most it keeps.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="kind">The type.</param>
    /// <param name="ids">The items.</param>
    public void Add(Guid libraryId, BaseItemKind kind, IReadOnlyCollection<Guid> ids)
    {
        lock (_lock)
        {
            var entry = EntryOf(libraryId, kind);
            if (entry.Ids.IsEmpty)
            {
                entry.CheckedUtc = clock.GetUtcNow();
            }

            entry.Ids = entry.Ids.Union(ids.Where(id => !entry.Ids.Contains(id)).Distinct().Take(MaxPerType - entry.Ids.Count));
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
            var entry = EntryOf(libraryId, kind);
            entry.Ids = entry.Ids.Except(ids);
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
            foreach (var key in _entries.Keys.Where(key => !libraryIds.Contains(key.Library)).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    private Entry EntryOf(Guid libraryId, BaseItemKind kind)
    {
        if (!_entries.TryGetValue((libraryId, kind), out var entry))
        {
            _entries[(libraryId, kind)] = entry = new Entry();
        }

        return entry;
    }

    private sealed class Entry
    {
        public ImmutableHashSet<Guid> Ids { get; set; } = ImmutableHashSet<Guid>.Empty;

        public DateTimeOffset CheckedUtc { get; set; }

        public bool IsSearching { get; set; }
    }
}
