using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Merges film titles by hand and undoes it (decision 20). A merge only changes what the resolver returns, so everything
/// after it is the path of any two copies that became one title: the materializer updates the stub that stays, and the
/// post-scan task hands the watch state of the other over and removes it (decision 2).
/// </summary>
public sealed class ManualMergeService
{
    private readonly IFederationResolver _resolver;
    private readonly IManualMergeStore _store;
    private readonly ILibraryMaterializer _materializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManualMergeService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualMergeService"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="store">The store of merges.</param>
    /// <param name="materializer">The materializer.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ManualMergeService(
        IFederationResolver resolver,
        IManualMergeStore store,
        ILibraryMaterializer materializer,
        TimeProvider timeProvider,
        ILogger<ManualMergeService> logger)
    {
        _resolver = resolver;
        _store = store;
        _materializer = materializer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets what the configuration page shows.
    /// </summary>
    /// <returns>The list.</returns>
    public ManualMergeList GetList()
    {
        var view = _resolver.Resolve();
        return new ManualMergeList(view.MergeCases, view.ManualMerges, _store.IsDamaged);
    }

    /// <summary>
    /// Merges titles of one case. A title made by earlier merges is taken in, so one undo separates everything again.
    /// </summary>
    /// <param name="workKeys">The titles, from <see cref="GetList"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<ManualMergeOutcome> MergeAsync(IReadOnlyCollection<string> workKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workKeys);

        var keys = workKeys.Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count < 2)
        {
            return ManualMergeOutcome.TooFewTitles;
        }

        var titles = _resolver.Resolve().MergeCases
            .Select(entry => entry.Titles.Where(title => keys.Contains(title.WorkKey, StringComparer.Ordinal)).ToList())
            .FirstOrDefault(found => found.Count == keys.Count);
        if (titles is null)
        {
            return ManualMergeOutcome.ListChanged;
        }

        if (titles.Count(title => title.HasLocalCopy) > 1)
        {
            return ManualMergeOutcome.TwoLocalTitles;
        }

        var replaced = titles.SelectMany(title => title.MergeIds).ToHashSet();
        await _store.UpdateAsync(
            current =>
            {
                var earlier = current.Where(merge => replaced.Contains(merge.Id)).SelectMany(merge => merge.Copies);
                var copies = titles.SelectMany(title => title.Copies).Select(copy => copy.Copy).Concat(earlier);
                var merge = new ManualMerge
                {
                    Id = Guid.NewGuid(),
                    CreatedUtc = _timeProvider.GetUtcNow(),
                    Copies = copies.DistinctBy(copy => (copy.ServerId, copy.ItemId)).ToList()
                };
                return [.. current.Where(merge => !replaced.Contains(merge.Id)), merge];
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Merged {Count} film titles by hand", titles.Count);
        await ApplyAsync(cancellationToken).ConfigureAwait(false);
        return ManualMergeOutcome.Merged;
    }

    /// <summary>
    /// Undoes a merge. The stub that stayed keeps its watch state, and the copies that were merged into it get stubs again.
    /// </summary>
    /// <param name="mergeId">The merge.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the merge existed.</returns>
    public async Task<bool> UndoAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        var found = false;
        await _store.UpdateAsync(
            current =>
            {
                found = current.Any(merge => merge.Id == mergeId);
                return current.Where(merge => merge.Id != mergeId).ToList();
            },
            cancellationToken).ConfigureAwait(false);

        if (found)
        {
            _logger.LogInformation("Undid a manual merge of film titles");
            await ApplyAsync(cancellationToken).ConfigureAwait(false);
        }

        return found;
    }

    // The choice is saved by now, so a failure here only delays it to the next sync.
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _materializer.MaterializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not apply a manual merge yet, the next sync does: {Error}", exception.GetType().Name);
        }
    }
}
