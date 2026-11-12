using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The collection (BoxSet) a film belongs to on its server. The name is untrusted text.
/// </summary>
/// <param name="Name">The name, which Jellyfin matches exactly.</param>
/// <param name="TmdbId">The TMDb ID of the collection, digits only, if the server has one.</param>
public sealed record IndexedBoxSet(string Name, string? TmdbId)
{
    /// <summary>
    /// Longest collection name kept, after trimming. The name is copied into every film of the collection.
    /// </summary>
    public const int MaxNameLength = 256;

    /// <summary>
    /// Makes a collection from what a server says of it: the name trimmed, the TMDb ID only if it is 1 to 12 digits.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="tmdbId">The TMDb ID.</param>
    /// <returns>The collection, null when the name is blank or too long, so that it is left out and not truncated.</returns>
    public static IndexedBoxSet? From(string? name, string? tmdbId)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
        {
            return null;
        }

        // Digits only: it goes into an attribute of the .nfo.
        return new IndexedBoxSet(trimmed, tmdbId is { Length: > 0 and <= 12 } && tmdbId.All(char.IsAsciiDigit) ? tmdbId : null);
    }
}
