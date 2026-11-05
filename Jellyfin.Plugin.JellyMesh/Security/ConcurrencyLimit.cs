using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Counts what runs at the same time for each key, and refuses what goes over. No queue: what it guards can last as long
/// as a film, so waiting for a place would only move the problem.
/// </summary>
/// <typeparam name="TKey">The key.</typeparam>
internal sealed class ConcurrencyLimit<TKey>
    where TKey : notnull
{
    private readonly Lock _lock = new();
    private readonly Dictionary<TKey, int> _running = [];
    private readonly int _limit;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyLimit{TKey}"/> class.
    /// </summary>
    /// <param name="limit">The most running at the same time for one key.</param>
    public ConcurrencyLimit(int limit)
    {
        _limit = limit;
    }

    /// <summary>
    /// Takes a place.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The place, to dispose. Null if there is none free.</returns>
    public IDisposable? TryEnter(TKey key)
    {
        lock (_lock)
        {
            var running = _running.GetValueOrDefault(key);
            if (running >= _limit)
            {
                return null;
            }

            _running[key] = running + 1;
            return new Place(this, key);
        }
    }

    private void Leave(TKey key)
    {
        lock (_lock)
        {
            if (--_running[key] == 0)
            {
                _running.Remove(key);
            }
        }
    }

    private sealed class Place : IDisposable
    {
        private readonly ConcurrencyLimit<TKey> _owner;
        private readonly TKey _key;
        private int _left;

        public Place(ConcurrencyLimit<TKey> owner, TKey key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                _owner.Leave(_key);
            }
        }
    }
}
