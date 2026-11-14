using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="IFederationIndex"/> over <see cref="IndexFile"/>. A change is saved first and only then made visible, so
/// memory and disk do not drift apart.
/// </summary>
public sealed class FederationIndex : IFederationIndex, IDisposable
{
    private readonly IndexFile _file;
    private readonly ILogger<FederationIndex> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile IndexSnapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationIndex"/> class.
    /// </summary>
    /// <param name="file">The index file.</param>
    /// <param name="logger">The logger.</param>
    public FederationIndex(IndexFile file, ILogger<FederationIndex> logger)
    {
        ArgumentNullException.ThrowIfNull(file);

        _file = file;
        _logger = logger;
        _snapshot = file.Load();
    }

    /// <inheritdoc />
    public IndexSnapshot Snapshot => _snapshot;

    /// <inheritdoc />
    public Task<bool> ReplaceServerAsync(Guid serverId, IReadOnlyList<IndexedItem> items, DateTimeOffset syncedUtc, SyncMark? mark, CancellationToken cancellationToken)
    {
        var catalog = new ServerCatalog
        {
            ServerId = serverId,
            State = ServerSyncState.Complete,
            LastAttemptUtc = syncedUtc,
            LastSuccessUtc = syncedUtc,
            Mark = mark,
            Items = items
        };

        // Real data arrived, so the damage is behind us: any server still missing is stale or unknown by itself.
        return CommitAsync(current => current with { Health = IndexHealth.Ok, Servers = WithServer(current, catalog) }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MergeServerAsync(Guid serverId, IReadOnlyList<IndexedItem> changed, DateTimeOffset syncedUtc, SyncMark mark, CancellationToken cancellationToken)
    {
        return CommitAsync(
            current =>
            {
                if (current.Find(serverId) is not { State: ServerSyncState.Complete } catalog)
                {
                    return current;
                }

                return current with
                {
                    Servers = WithServer(current, catalog with { LastAttemptUtc = syncedUtc, Mark = mark, Items = changed.Count == 0 ? catalog.Items : Merged(catalog.Items, changed) })
                };
            },
            cancellationToken,
            persist: changed.Count > 0);
    }

    /// <inheritdoc />
    public Task<bool> MarkServerFailedAsync(Guid serverId, RemoteFailure failure, DateTimeOffset attemptUtc, CancellationToken cancellationToken)
    {
        return CommitAsync(
            current =>
            {
                var catalog = current.Find(serverId) ?? new ServerCatalog { ServerId = serverId, State = ServerSyncState.Stale };
                return current with
                {
                    Servers = WithServer(current, catalog with { State = ServerSyncState.Stale, LastFailure = failure, LastAttemptUtc = attemptUtc })
                };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DropServersAsync(IReadOnlySet<Guid> serverIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverIds);

        return CommitAsync(
            current => current.Servers.Any(server => serverIds.Contains(server.ServerId))
                ? current with { Servers = current.Servers.Where(server => !serverIds.Contains(server.ServerId)).ToList() }
                : current,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _writeGate.Dispose();

    private static List<ServerCatalog> WithServer(IndexSnapshot snapshot, ServerCatalog catalog)
    {
        return [.. snapshot.Servers.Where(server => server.ServerId != catalog.ServerId), catalog];
    }

    private static List<IndexedItem> Merged(IReadOnlyList<IndexedItem> items, IReadOnlyList<IndexedItem> changed)
    {
        var updates = changed.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var known = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. items.Select(item => updates.GetValueOrDefault(item.ItemId, item)),
            .. updates.Values.Where(item => !known.Contains(item.ItemId))
        ];
    }

    // Without persist the change stays in memory until the next save: after a restart an older mark only rereads a little more.
    private async Task<bool> CommitAsync(Func<IndexSnapshot, IndexSnapshot> change, CancellationToken cancellationToken, bool persist = true)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _snapshot;
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return true;
            }

            if (persist)
            {
                await _file.SaveAsync(next, current.Health == IndexHealth.Ok, cancellationToken).ConfigureAwait(false);
            }

            _snapshot = next;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot save the federation index: {Error}", exception.GetType().Name);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
