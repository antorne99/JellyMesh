using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests;

/// <summary>The tests that time work: they run alone, after the others, so that the other tests do not slow them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedTests
{
    /// <summary>The name of the collection.</summary>
    public const string Name = "Timed tests";
}
