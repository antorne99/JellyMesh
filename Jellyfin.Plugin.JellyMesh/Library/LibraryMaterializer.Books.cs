using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The book part of the materializer: a placeholder with the real extension and a cover, with no <c>.nfo</c> or
/// <c>.opf</c>, since the metadata lives on the locked item (D47).
/// </summary>
public sealed partial class LibraryMaterializer
{
    // Writes the placeholder once, then keeps the item on the remote metadata. The hash is kept only once an item took it,
    // so a new book gets its metadata, people included, at the first run after the scan that creates it.
    private async Task WriteBookAsync(Run run, StubWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var placeholder = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            var existing = new LazyItem(_library, placeholder, false, work.IsNew);
            var changed = false;
            if (!File.Exists(placeholder))
            {
                await WriteFileAsync(placeholder, BookPlaceholders.For(Path.GetExtension(placeholder).TrimStart('.')), cancellationToken).ConfigureAwait(false);
                run.NeedsScan = true;
                changed = true;
            }

            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) && existing.Value is { } item)
            {
                await _updater.ApplyMetadataAsync(item, work.Metadata, cancellationToken).ConfigureAwait(false);
                entry = entry with { ContentHash = work.ContentHash };
                changed = true;
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await UpdateImagesAsync(run, work.Images, work.RemovedImages, existing.Value, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }

            run.Put(entry);
            if (work.IsNew)
            {
                run.Count(entry.ServerId, created: 1);
            }
            else if (changed)
            {
                run.Count(entry.ServerId, updated: 1);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Fail(entry.ServerId);
            _logger.LogError("Cannot write a book placeholder: {Error}", exception.GetType().Name);
        }
    }
}
