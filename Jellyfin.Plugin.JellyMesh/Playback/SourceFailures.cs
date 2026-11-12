using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The sources that stopped serving a user during playback, in memory for <see cref="PlaybackTimeouts.FailedSourceMemory"/>
/// (row 57). It holds the user, the stub and the source, never an address or a token.
/// </summary>
public sealed class SourceFailures
{
    private readonly ConcurrentDictionary<Guid, List<Failure>> _failures = new();

    /// <summary>
    /// Records that a source of a stub stopped serving a user.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="stubId">The stub.</param>
    /// <param name="source">The source.</param>
    /// <param name="now">The current time.</param>
    public void Record(Guid userId, string stubId, StubSource source, DateTimeOffset now)
    {
        var failures = _failures.GetOrAdd(userId, _ => []);
        lock (failures)
        {
            failures.Add(new Failure(stubId, source, now));
        }
    }

    /// <summary>
    /// Tells whether a source of a stub stopped serving a user within the memory.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="stubId">The stub.</param>
    /// <param name="source">The source.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Whether it goes last.</returns>
    public bool Failed(Guid userId, string stubId, StubSource source, DateTimeOffset now)
    {
        return Recent(userId, now).Any(failure => string.Equals(failure.StubId, stubId, StringComparison.Ordinal) && failure.Source == source);
    }

    /// <summary>
    /// Counts the failures of the versions of a title for a user within the memory.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="stubIds">The stubs of the versions of the title.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The number of failures.</returns>
    public int Count(Guid userId, IReadOnlyCollection<string> stubIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stubIds);

        return Recent(userId, now).Count(failure => stubIds.Contains(failure.StubId, StringComparer.Ordinal));
    }

    /// <summary>
    /// Forgets the failures older than the memory.
    /// </summary>
    /// <param name="now">The current time.</param>
    public void ForgetOld(DateTimeOffset now)
    {
        // The empty lists stay: one per user, and removing one could drop a failure recorded at the same time.
        foreach (var failures in _failures.Values)
        {
            lock (failures)
            {
                failures.RemoveAll(failure => !IsRecent(failure, now));
            }
        }
    }

    private static bool IsRecent(Failure failure, DateTimeOffset now) => now - failure.At < PlaybackTimeouts.FailedSourceMemory;

    private List<Failure> Recent(Guid userId, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(userId, out var failures))
        {
            return [];
        }

        lock (failures)
        {
            return failures.Where(failure => IsRecent(failure, now)).ToList();
        }
    }

    private sealed record Failure(string StubId, StubSource Source, DateTimeOffset At);
}
