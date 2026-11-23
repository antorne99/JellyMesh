using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Knows a book again by a provider ID, else by title, year, series and extension (row 535). Pure and stateless.
/// </summary>
public static class BookIdentity
{
    /// <summary>
    /// Pairs each saved book with every current copy of it.
    /// </summary>
    /// <param name="saved">The saved books.</param>
    /// <param name="current">The current books.</param>
    /// <returns>The pairs.</returns>
    public static IReadOnlyList<(BookItem Saved, BookItem Current)> Pair(IReadOnlyCollection<BookItem> saved, IReadOnlyCollection<BookItem> current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var byId = current.SelectMany(book => IdKeys(book).Select(id => (Id: id, Book: book))).ToLookup(entry => entry.Id, entry => entry.Book, StringComparer.Ordinal);
        var byData = current.Where(book => DataKey(book) is not null).ToLookup(book => DataKey(book)!, StringComparer.Ordinal);
        return saved
            .SelectMany(book => IdKeys(book).SelectMany(id => byId[id])
                .Concat(DataKey(book) is { } key ? byData[key].Where(other => !HaveOtherIds(book, other)) : [])
                .DistinctBy(copy => (copy.ServerId, copy.ItemId))
                .Select(copy => (book, copy)))
            .ToList();
    }

    private static IEnumerable<string> IdKeys(BookItem book) => book.ProviderIds
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
        .Select(pair => pair.Key.ToUpperInvariant() + "\n" + pair.Value.Trim().ToUpperInvariant());

    // The normalized title, year, series and extension, or null without a title, year or extension.
    private static string? DataKey(BookItem book)
    {
        var title = TitleNormalizer.Normalize(book.Title, book.Year);
        return title.Length == 0 || book.Year is not { } year || string.IsNullOrWhiteSpace(book.Extension)
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{title}\n{year}\n{TitleNormalizer.Normalize(book.SeriesName, null)}\n{book.Extension.ToUpperInvariant()}");
    }

    // Whether two books have different IDs of one provider.
    private static bool HaveOtherIds(BookItem left, BookItem right)
    {
        var ids = left.ProviderIds
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToLookup(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        return right.ProviderIds.Any(other => !string.IsNullOrWhiteSpace(other.Value)
            && ids[other.Key].Any(id => !string.Equals(id, other.Value.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
