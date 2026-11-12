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
/// after it is the path of any two copies that became one title: copies of one server keep one stub, whose post-scan task
/// hands the state of the other over (decision 2), and copies of different servers or a local one become versions (Q23).
/// When titles of one library become one entity, the merge carries their state to the primary (decision 172).
/// </summary>
public sealed class ManualMergeService
{
    private readonly IFederationResolver _resolver;
    private readonly IManualMergeStore _store;
    private readonly ILibraryMaterializer _materializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManualMergeService> _logger;
    private readonly IVersionLinker? _linker;
    private readonly IMergeWatchStateCarry? _carry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualMergeService"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="store">The store of merges.</param>
    /// <param name="materializer">The materializer.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="linker">The version linker, if any.</param>
    /// <param name="carry">The watch state carry of a merge, if any.</param>
    public ManualMergeService(
        IFederationResolver resolver,
        IManualMergeStore store,
        ILibraryMaterializer materializer,
        TimeProvider timeProvider,
        ILogger<ManualMergeService> logger,
        IVersionLinker? linker = null,
        IMergeWatchStateCarry? carry = null)
    {
        _resolver = resolver;
        _store = store;
        _materializer = materializer;
        _timeProvider = timeProvider;
        _logger = logger;
        _linker = linker;
        _carry = carry;
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
    /// Tells what a merge of these titles would do, changing nothing. It passes the checks of <see cref="MergeAsync"/>.
    /// </summary>
    /// <param name="workKeys">The titles, from <see cref="GetList"/>.</param>
    /// <returns>The outcome the merge would have, and what would play.</returns>
    public MergePreview Preview(IReadOnlyCollection<string> workKeys)
    {
        ArgumentNullException.ThrowIfNull(workKeys);

        var (outcome, titles) = Check(workKeys);
        return new MergePreview(outcome, outcome == ManualMergeOutcome.Merged ? MergeForecast.Of(titles) : null);
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

        var (outcome, titles) = Check(workKeys);
        if (outcome != ManualMergeOutcome.Merged)
        {
            return outcome;
        }

        var heads = FindHeads(titles);
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
        await ApplyAsync(false, cancellationToken).ConfigureAwait(false);
        await CarryAsync(heads, cancellationToken).ConfigureAwait(false);
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
        ManualMerge? undone = null;
        await _store.UpdateAsync(
            current =>
            {
                undone = current.FirstOrDefault(merge => merge.Id == mergeId);
                return current.Where(merge => merge.Id != mergeId).ToList();
            },
            cancellationToken).ConfigureAwait(false);

        if (undone is not null)
        {
            _logger.LogInformation("Undid a manual merge of film titles");
            await ApplyUndoAsync(undone, cancellationToken).ConfigureAwait(false);
        }

        return undone is not null;
    }

    /// <summary>
    /// Takes the copies of a server out of the merges. A merge left with fewer than two copies goes; the others keep their
    /// ID and date, and a merge with no copy there stays the same.
    /// </summary>
    /// <param name="merges">The merges.</param>
    /// <param name="serverId">The server, as in <see cref="MergedCopy.ServerId"/>.</param>
    /// <returns>The merges without the server.</returns>
    public static IReadOnlyList<ManualMerge> WithoutServer(IReadOnlyList<ManualMerge> merges, string serverId)
    {
        ArgumentNullException.ThrowIfNull(merges);

        var kept = new List<ManualMerge>(merges.Count);
        foreach (var merge in merges)
        {
            if (!HasCopyOn(merge, serverId))
            {
                kept.Add(merge);
                continue;
            }

            var copies = merge.Copies.Where(copy => !IsOn(copy, serverId)).ToList();
            if (copies.Count >= 2)
            {
                kept.Add(merge with { Copies = copies });
            }
        }

        return kept;
    }

    /// <summary>
    /// Tells whether a merge has a copy on a server.
    /// </summary>
    /// <param name="merge">The merge.</param>
    /// <param name="serverId">The server, as in <see cref="MergedCopy.ServerId"/>.</param>
    /// <returns>Whether it has one.</returns>
    public static bool HasCopyOn(ManualMerge merge, string serverId)
    {
        ArgumentNullException.ThrowIfNull(merge);

        return merge.Copies.Any(copy => IsOn(copy, serverId));
    }

    // A local copy is never on a remote server, whatever its server ID says.
    private static bool IsOn(MergedCopy copy, string serverId)
        => !copy.IsLocal && string.Equals(copy.ServerId, serverId, StringComparison.OrdinalIgnoreCase);

    // The entities the titles show before the merge links them.
    private IReadOnlyList<MergeHead> FindHeads(List<MergeTitleView> titles)
    {
        try
        {
            return _carry?.FindHeads(titles.SelectMany(title => title.Copies).Select(copy => copy.Copy).ToList()) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot find the titles a manual merge joins: {Error}", exception.GetType().Name);
            return [];
        }
    }

    // The state moves once, at the merge, when two entities of one library become one (decision 172).
    private async Task CarryAsync(IReadOnlyList<MergeHead> heads, CancellationToken cancellationToken)
    {
        if (_carry is null || heads.Count < 2)
        {
            return;
        }

        try
        {
            await _carry.CarryAsync(heads, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot carry the watch state of a manual merge: {Error}", exception.GetType().Name);
        }
    }

    // Finds the titles of a merge, or why it cannot be made.
    private (ManualMergeOutcome Outcome, List<MergeTitleView> Titles) Check(IReadOnlyCollection<string> workKeys)
    {
        var keys = workKeys.Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count < 2)
        {
            return (ManualMergeOutcome.TooFewTitles, []);
        }

        var titles = _resolver.Resolve().MergeCases
            .Select(entry => entry.Titles.Where(title => keys.Contains(title.WorkKey, StringComparer.Ordinal)).ToList())
            .FirstOrDefault(found => found.Count == keys.Count);
        if (titles is null)
        {
            return (ManualMergeOutcome.ListChanged, []);
        }

        return (titles.Count(title => title.HasLocalCopy) > 1 ? ManualMergeOutcome.TwoLocalTitles : ManualMergeOutcome.Merged, titles);
    }

    // The undo is saved by now. The stubs of the group it dissolves are read before the materializer changes the groups,
    // and detached at once (D5, Q171); a failure leaves them to the nightly run (Q144).
    private async Task ApplyUndoAsync(ManualMerge undone, CancellationToken cancellationToken)
    {
        try
        {
            var dissolved = _linker?.GroupedStubsOf(undone.Copies) ?? [];
            await _materializer.MaterializeAsync(cancellationToken).ConfigureAwait(false);
            if (_linker is not null)
            {
                await _linker.LinkAsync(false, cancellationToken).ConfigureAwait(false);
                await _linker.DetachAsync(dissolved, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not apply the undo of a manual merge yet, a later sync or the nightly run does: {Error}", exception.GetType().Name);
        }
    }

    // The choice is saved by now, so a failure only delays it to the next sync.
    private async Task ApplyAsync(bool thorough, CancellationToken cancellationToken)
    {
        try
        {
            await _materializer.MaterializeAsync(cancellationToken).ConfigureAwait(false);
            if (_linker is not null)
            {
                await _linker.LinkAsync(thorough, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not apply a manual merge yet, a later sync or the nightly run does: {Error}", exception.GetType().Name);
        }
    }
}
