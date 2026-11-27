using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The admin's switch of the shared libraries (§10 row 442).
/// </summary>
public sealed record SharedLibrariesRequest
{
    /// <summary>Gets a value indicating whether the shared libraries are wanted.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether the admin confirmed the deletion a switch-off makes.</summary>
    public bool Confirmed { get; init; }

    /// <summary>Gets the libraries to delete the refusal named, which the admin confirmed.</summary>
    public IReadOnlyList<string> ToDelete { get; init; } = [];

    /// <summary>Gets the libraries to release the refusal named, which the admin confirmed.</summary>
    public IReadOnlyList<string> ToRelease { get; init; } = [];
}
