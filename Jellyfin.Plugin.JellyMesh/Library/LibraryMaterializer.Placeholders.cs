using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The placeholder part of the materializer, for books and photos: a placeholder with the real extension and a cover,
/// with no <c>.nfo</c> or <c>.opf</c>, since the metadata lives on the locked item (D47, D7).
/// </summary>
public sealed partial class LibraryMaterializer
{
    // Writes the placeholder once, then keeps the item on the remote metadata: at the first run after the scan that makes
    // it, on each change, and at the nightly pass for an item Jellyfin made again, since no .nfo brings it back.
    private async Task WritePlaceholderAsync(Run run, StubWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var placeholder = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            var existing = new LazyItem(_library, placeholder, false, work.IsNew);
            var changed = false;
            if (!File.Exists(placeholder))
            {
                var extension = Path.GetExtension(placeholder).TrimStart('.');
                var bytes = entry.Kind == StubKind.Photo ? PhotoPlaceholders.For(extension) : BookPlaceholders.For(extension);
                await WriteFileAsync(placeholder, bytes, cancellationToken).ConfigureAwait(false);
                run.NeedsScan = true;
                changed = true;
            }

            var applied = false;
            if ((!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || run.VerifyEveryStub)
                && existing.Value is { } item
                && (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || HasLost(item, work.Metadata)))
            {
                await _updater.ApplyMetadataAsync(item, work.Metadata, cancellationToken).ConfigureAwait(false);
                entry = entry with { ContentHash = work.ContentHash };
                changed = applied = true;
            }

            if (run.Thumbnails.ContainsKey(entry.Id))
            {
                entry = entry with { TechnicalTag = work.TechnicalTag };
            }

            // A thumbnail read after the scan reaches the photo here; one read before it, through the providers.
            if (entry.Kind == StubKind.Photo
                && (applied || run.VerifyEveryStub || run.Thumbnails.ContainsKey(entry.Id))
                && existing.Value is Photo photo
                && PhotoThumbnails.PathOf(run.Root, entry.Id) is { } thumbnail
                && PhotoThumbnails.SetPrimary(photo, thumbnail))
            {
                await _updater.SaveAsync(photo, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await UpdateImagesAsync(run, work.Images, work.RemovedImages, ImageOwnerOf(work, placeholder, existing), cancellationToken).ConfigureAwait(false))
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
            _logger.LogError("Cannot write a placeholder: {Error}", exception.GetType().Name);
        }
    }

    // Tells whether an item lost the metadata the plugin gave it: its lock or its title.
    private static bool HasLost(BaseItem item, StubMetadata metadata)
        => item.IsLocked != metadata.IsProtected || (metadata.Title is { } title && !string.Equals(item.Name, title, StringComparison.Ordinal));
}
