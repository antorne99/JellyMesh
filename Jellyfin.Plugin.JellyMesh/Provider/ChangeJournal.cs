using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The feed of what changed in the catalog: a bounded list in memory. It only records that an item changed, and what a
/// peer may see is decided when it reads. A cursor names this run of the server: after a restart, or when the list has
/// moved past it, the reader is told to read everything, so nothing can be missed silently. Removals are not here: the
/// reconciliation finds them.
/// </summary>
public sealed class ChangeJournal
{
    /// <summary>
    /// The most changes kept.
    /// </summary>
    public const int Capacity = 100_000;

    private const char Separator = '.';

    private readonly Lock _lock = new();
    private readonly Queue<Change> _changes = new();
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _lastSequence;
    private long _lastDropped;

    /// <summary>
    /// Records a change.
    /// </summary>
    /// <param name="itemId">The item.</param>
    public void Record(Guid itemId)
    {
        lock (_lock)
        {
            _changes.Enqueue(new Change(++_lastSequence, itemId));
            if (_changes.Count > Capacity)
            {
                _lastDropped = _changes.Dequeue().Sequence;
            }
        }
    }

    /// <summary>
    /// Reads the changes after a cursor.
    /// </summary>
    /// <param name="cursor">The cursor, null to learn the current position.</param>
    /// <param name="shareKey">The <see cref="PeerGrant.ShareKey"/> of the reader.</param>
    /// <param name="limit">The most changes to go through.</param>
    /// <returns>The changes.</returns>
    public FeedWindow Read(string? cursor, string shareKey, int limit)
    {
        lock (_lock)
        {
            if (!TryParse(cursor, shareKey, out var after) || after < _lastDropped || after > _lastSequence)
            {
                return new FeedWindow(true, CursorOf(shareKey, _lastSequence), [], false);
            }

            var page = _changes.Where(change => change.Sequence > after).Take(limit).ToList();
            var last = page.Count > 0 ? page[^1].Sequence : after;
            return new FeedWindow(false, CursorOf(shareKey, last), page.Select(change => change.ItemId).Distinct().ToList(), last < _lastSequence);
        }
    }

    private string CursorOf(string shareKey, long sequence)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{_epoch}{Separator}{shareKey}{Separator}{sequence}");
    }

    private bool TryParse(string? cursor, string shareKey, out long sequence)
    {
        sequence = 0;
        var parts = cursor?.Split(Separator) ?? [];
        return parts.Length == 3
            && parts[0] == _epoch
            && parts[1] == shareKey
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private readonly record struct Change(long Sequence, Guid ItemId);
}
