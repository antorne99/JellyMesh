using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Remembers, in memory, when a library scan ran for each library without an id, so each gets one a day (decision 333).
/// </summary>
internal sealed class UnreadyScanLimit
{
    /// <summary>
    /// The shortest time between two scans for the same library.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _scanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="UnreadyScanLimit"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public UnreadyScanLimit(TimeProvider time) => _time = time;

    /// <summary>
    /// Tells whether a scan may run for these libraries without an id and, if so, records it for all of them.
    /// </summary>
    /// <param name="names">The names of the libraries without an id.</param>
    /// <returns>Whether a scan may run.</returns>
    public bool TryClaim(IReadOnlyCollection<string> names)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var name in _scanned.Where(entry => now - entry.Value >= Interval).Select(entry => entry.Key).ToList())
            {
                _scanned.Remove(name);
            }

            if (names.All(_scanned.ContainsKey))
            {
                return false;
            }

            foreach (var name in names)
            {
                _scanned[name] = now;
            }

            return true;
        }
    }
}
