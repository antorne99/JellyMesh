using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

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
    /// <param name="maxCopies">The most copies that may share an ID or a place; more leave the saved ones unpaired.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairs, and how many saved books have more copies than that.</returns>
    public static SavedPairs<BookItem> Pair(IReadOnlyCollection<BookItem> saved, IReadOnlyCollection<BookItem> current, int maxCopies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var byId = current.SelectMany(book => IdKeys(book).Select(id => (Id: id, Book: book))).ToLookup(entry => entry.Id, entry => entry.Book, StringComparer.Ordinal);
        var byData = current.Where(book => DataKey(book) is not null).ToLookup(book => DataKey(book)!, StringComparer.Ordinal);
        var pairs = new List<(BookItem Saved, BookItem Current)>();
        var left = 0;
        foreach (var book in saved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sharing = IdKeys(book).Select(id => byId[id]).ToList();
            var alike = DataKey(book) is { } key ? byData[key] : [];
            if (sharing.Exists(copies => copies.Count() > maxCopies) || alike.Count() > maxCopies)
            {
                left++;
                continue;
            }

            var ids = IdsOf(book);
            var found = sharing.SelectMany(copies => copies)
                .Concat(alike.Where(other => !HasOtherIds(ids, other)))
                .DistinctBy(copy => (copy.ServerId, copy.ItemId))
                .ToList();
            if (found.Count > maxCopies)
            {
                left++;
                continue;
            }

            pairs.AddRange(found.Select(copy => (book, copy)));
        }

        return new(pairs, left);
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

    private static ILookup<string, string> IdsOf(BookItem book) => book.ProviderIds
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
        .ToLookup(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    // Whether a book has a different ID of a provider than these.
    private static bool HasOtherIds(ILookup<string, string> ids, BookItem book) => book.ProviderIds.Any(other =>
        !string.IsNullOrWhiteSpace(other.Value)
        && ids[other.Key].Any(id => !string.Equals(id, other.Value.Trim(), StringComparison.OrdinalIgnoreCase)));
}
