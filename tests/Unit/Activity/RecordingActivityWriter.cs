using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Activity;

namespace Jellyfin.Plugin.JellyMesh.Tests.Activity;

/// <summary>Keeps every entry written, in order.</summary>
internal sealed class RecordingActivityWriter : IActivityWriter
{
    private readonly ConcurrentQueue<ActivityEntry> _entries = new();

    public IReadOnlyList<ActivityEntry> Entries => _entries.ToList();

    public IReadOnlyList<ActivityEntry> OfType(string type) => Entries.Where(entry => entry.Type == type).ToList();

    public void Write(ActivityEntry entry) => _entries.Enqueue(entry);
}
