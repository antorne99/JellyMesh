using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells a book placeholder of another JellyMesh, so that a remote read in standard mode never imports it (D62).
/// </summary>
public static class FederatedBookMark
{
    /// <summary>The provider id the plugin writes on the items of its own book placeholders.</summary>
    public const string ProviderId = "JellyMesh";

    /// <summary>Tells whether a remote book is a placeholder of another JellyMesh, by its path or by its mark.</summary>
    /// <param name="path">The path of the book on the remote.</param>
    /// <param name="providerIds">The provider ids the remote sent, with their keys whatever their value.</param>
    /// <returns>Whether it is a placeholder.</returns>
    public static bool IsPlaceholder(string? path, IEnumerable<string>? providerIds)
        => HasPlaceholderShape(path) || (providerIds?.Any(key => string.Equals(key, ProviderId, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>Gives the ids of a book item with the mark of its stub over any the remote had (D62).</summary>
    /// <param name="ids">The provider ids of the remote book.</param>
    /// <param name="stubId">The stub.</param>
    /// <returns>The ids to write.</returns>
    public static Dictionary<string, string> WithMark(IReadOnlyDictionary<string, string> ids, string stubId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return new(ids.Where(pair => !string.Equals(pair.Key, ProviderId, StringComparison.OrdinalIgnoreCase)), StringComparer.Ordinal)
        {
            [ProviderId] = stubId
        };
    }

    // Looks for {server guid}/{library id}/Libri anywhere in the path, since the root of the remote is not known.
    private static bool HasPlaceholderShape(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 3 < parts.Length; i++)
        {
            if (Guid.TryParseExact(parts[i], "D", out _)
                && Guid.TryParseExact(parts[i + 1], "N", out _)
                && string.Equals(parts[i + 2], TypeFolder.Books.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
