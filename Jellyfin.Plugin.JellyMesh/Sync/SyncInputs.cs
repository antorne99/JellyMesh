using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What the materialization and the version links depend on at one moment. Lists the index keeps whole are compared by
/// reference; everything else by a hash.
/// </summary>
public sealed class SyncInputs
{
    private readonly IReadOnlyList<object> _references;
    private readonly string _fingerprint;
    private readonly long _libraryEvents;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncInputs"/> class.
    /// </summary>
    /// <param name="references">The lists compared by reference.</param>
    /// <param name="fingerprint">The hash of the rest.</param>
    /// <param name="libraryEvents">How many changes of local items had been seen.</param>
    /// <param name="registryGeneration">The generation of the stub registry.</param>
    internal SyncInputs(IReadOnlyList<object> references, string fingerprint, long libraryEvents, long registryGeneration)
    {
        _references = references;
        _fingerprint = fingerprint;
        _libraryEvents = libraryEvents;
        RegistryGeneration = registryGeneration;
    }

    /// <summary>
    /// Gets the generation of the stub registry.
    /// </summary>
    public long RegistryGeneration { get; }

    /// <summary>
    /// Gives the same inputs with the registry as a run left it.
    /// </summary>
    /// <param name="generation">The generation.</param>
    /// <returns>The inputs.</returns>
    public SyncInputs WithRegistryGeneration(long generation) => new(_references, _fingerprint, _libraryEvents, generation);

    /// <summary>
    /// Tells whether two captures saw the same inputs.
    /// </summary>
    /// <param name="other">The other inputs.</param>
    /// <returns>True when they are the same.</returns>
    public bool SameAs(SyncInputs other)
    {
        return other is not null
            && string.Equals(_fingerprint, other._fingerprint, System.StringComparison.Ordinal)
            && _libraryEvents == other._libraryEvents
            && RegistryGeneration == other.RegistryGeneration
            && _references.SequenceEqual(other._references, ReferenceEqualityComparer.Instance);
    }
}
