using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryMaterializer"/>. Writes only inside the federated folder. The registry is saved before the files, so
/// that a scan running at the same time finds the stub of every file it meets.
/// </summary>
public sealed class LibraryMaterializer : ILibraryMaterializer
{
    // How many images of one server are read at once. A choice of Claude's, under the 8 reads per peer of decision 31.
    private const int ImageReadsAtOnce = 4;

    private static readonly string[] _imageExtensions = [".jpg", ".png", ".webp", ".gif"];

    private readonly StubRegistry _registry;
    private readonly IFederationResolver _resolver;
    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _servers;
    private readonly IJellyfinRemoteClient _client;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly StubItemUpdater _updater;
    private readonly StubTechnicalData _technical;
    private readonly StubRemover _remover;
    private readonly StubUrlBuilder _urls;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LibraryMaterializer> _logger;
    private readonly ILocalRefreshRequester? _refresher;
    private readonly IVersionLinker? _linker;
    private volatile MaterializationReport? _lastReport;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaterializer"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="resolver">The resolver.</param>
    /// <param name="index">The index.</param>
    /// <param name="servers">The server registry.</param>
    /// <param name="client">The remote client.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="updater">The item updater.</param>
    /// <param name="technical">The technical data of the stubs.</param>
    /// <param name="remover">The stub remover.</param>
    /// <param name="urls">The builder of <c>.strm</c> URLs.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="refresher">What asks Jellyfin to read the metadata of new local films.</param>
    /// <param name="linker">What detaches a stub from its versions before it is removed.</param>
    public LibraryMaterializer(
        StubRegistry registry,
        IFederationResolver resolver,
        IFederationIndex index,
        IRemoteServerRegistry servers,
        IJellyfinRemoteClient client,
        IPluginConfigurationStore store,
        ILibraryManager library,
        StubItemUpdater updater,
        StubTechnicalData technical,
        StubRemover remover,
        StubUrlBuilder urls,
        TimeProvider timeProvider,
        ILogger<LibraryMaterializer> logger,
        ILocalRefreshRequester? refresher = null,
        IVersionLinker? linker = null)
    {
        _registry = registry;
        _resolver = resolver;
        _index = index;
        _servers = servers;
        _client = client;
        _store = store;
        _library = library;
        _updater = updater;
        _technical = technical;
        _remover = remover;
        _urls = urls;
        _timeProvider = timeProvider;
        _logger = logger;
        _refresher = refresher;
        _linker = linker;
    }

    /// <inheritdoc />
    public MaterializationReport? LastReport => _lastReport;

    /// <inheritdoc />
    public Task<MaterializationReport> MaterializeAsync(CancellationToken cancellationToken) => MaterializeAsync(false, cancellationToken);

    /// <inheritdoc />
    public async Task<MaterializationReport> MaterializeAsync(bool verifyEveryStub, CancellationToken cancellationToken)
    {
        var (root, isProtected, writesCollections) = _store.Read(config => (config.FederatedFolderPath, config.MetadataProtection, config.FederateCollections));
        if (root.Length == 0)
        {
            var report = new MaterializationReport(_timeProvider.GetUtcNow(), false, 0, 0, 0, 0, 0, []);
            _lastReport = report;
            return report;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await RunAsync(root, isProtected, writesCollections, verifyEveryStub, cancellationToken).ConfigureAwait(false);
            _lastReport = report;
            return report;
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public void PrepareServerFolders()
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return;
        }

        try
        {
            WriteServerFolders(root, _servers.GetServers());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot write the folders of the servers in the federated folder: {Error}", LogText.CleanMessage(ex.Message));
        }
    }

    // The folders of a server exist before any library points at them, and a folder that holds nothing is never empty.
    private static void WriteServerFolders(string root, IReadOnlyList<RemoteServerInfo> servers)
    {
        WriteHiddenFile(root, FederatedPaths.MovieFolder(string.Empty));
        WriteHiddenFile(root, FederatedPaths.SeriesFolderOf(string.Empty));
        foreach (var server in servers)
        {
            WriteHiddenFile(root, FederatedPaths.MovieFolder(server.Id, string.Empty));
            WriteHiddenFile(root, FederatedPaths.SeriesFolderOf(server.Id, string.Empty));
        }
    }

    private static string ExtensionOf(string contentType) => contentType.ToUpperInvariant() switch
    {
        "IMAGE/PNG" => ".png",
        "IMAGE/WEBP" => ".webp",
        "IMAGE/GIF" => ".gif",
        _ => ".jpg"
    };

    private static void WriteHiddenFile(string root, string relativeFolder)
    {
        var folder = FederatedPaths.Resolve(root, relativeFolder);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FederatedPaths.HiddenFileName);
        if (!File.Exists(file))
        {
            File.WriteAllBytes(file, []);
        }
    }

    // A move, so that a scan never reads half a file. The name starts with a dot, so Jellyfin ignores it until then.
    private static async Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Task WriteTextAsync(string path, string text, CancellationToken cancellationToken)
        => WriteFileAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    private static void DeleteOtherExtensions(string stem, string keep)
    {
        var folder = Path.GetDirectoryName(stem)!;
        var name = Path.GetFileName(stem) + ".";
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(file).StartsWith(name, StringComparison.Ordinal)
                && !string.Equals(file, keep, StringComparison.Ordinal)
                && _imageExtensions.Contains(Path.GetExtension(file)))
            {
                File.Delete(file);
            }
        }
    }

    private static string UrlTagOf(string url)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 16);
    }

    // The URL is confirmed while it is the one to write and nobody has written the file since: a new signing key changes the
    // first, a hand edit or an older version the second.
    private static bool IsUrlConfirmed(StubEntry stub, string strm, string urlTag)
    {
        return string.Equals(stub.UrlTag, urlTag, StringComparison.Ordinal) && stub.StrmWrittenUtc == File.GetLastWriteTimeUtc(strm);
    }

    private async Task<MaterializationReport> RunAsync(string root, bool isProtected, bool writesCollections, bool verifyEveryStub, CancellationToken cancellationToken)
    {
        // One snapshot and one server list for the view and the plan: a removal during the run waits for the next one.
        var snapshot = _index.Snapshot;
        var servers = _servers.GetServers();
        var view = _resolver.Resolve(snapshot, servers);
        _refresher?.Request(view.LocalItemsAwaitingRefresh);
        var plan = StubPlanner.PlanByServer(new StubPlanInput(view, snapshot, _registry.State, servers, isProtected, _urls.Build, _ => null) { WritesCollections = writesCollections });

        WriteServerFolders(root, servers);

        await _registry.SaveAsync(plan.State, cancellationToken).ConfigureAwait(false);

        var run = new Run(root, plan.State) { VerifyEveryStub = verifyEveryStub };
        await FetchImagesAsync(run, plan, cancellationToken).ConfigureAwait(false);
        foreach (var series in plan.Series)
        {
            await WriteSeriesAsync(run, series, cancellationToken).ConfigureAwait(false);
        }

        foreach (var stub in plan.Stubs)
        {
            await WriteStubAsync(run, stub, cancellationToken).ConfigureAwait(false);
        }

        // A stub with no work, of the flat layout or merged and waiting for the post-scan task, can still be opened.
        var handled = plan.Stubs.Select(work => work.Entry).Concat(plan.Removals).Select(stub => stub.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stub in plan.State.Stubs.Where(stub => !handled.Contains(stub.Id)))
        {
            await SignStubAsync(run, stub, cancellationToken).ConfigureAwait(false);
        }

        run.Commit();
        RemoveGone(run, await DetachRemovalsAsync(run, plan.Removals, cancellationToken).ConfigureAwait(false));
        RemoveServerFolders(run, plan.State.RemovedServers);

        await _registry.SaveAsync(run.State, cancellationToken).ConfigureAwait(false);

        foreach (var held in plan.Held)
        {
            _logger.LogWarning(
                "A complete read of a library of remote server {ServerName} came back empty. {Count} stubs are kept until it has items again, or it is deselected",
                LogText.Clean(held.ServerName),
                held.Stubs);
        }

        if (view.MergeCases.Count > 0)
        {
            _logger.LogInformation("{Count} groups of films may be duplicates. Review them under Duplicate films on the JellyMesh page", view.MergeCases.Count);
        }

        if (run.NeedsScan || plan.MergedStubs > 0)
        {
            _library.QueueLibraryScan();
        }

        _logger.LogInformation(
            "Materialized the federated folder: {Created} created, {Updated} updated, {Removed} removed, {Failed} failed",
            run.Created,
            run.Updated,
            run.Removed,
            run.Failed);
        return new MaterializationReport(_timeProvider.GetUtcNow(), true, run.Created, run.Updated, run.Removed, run.Failed, plan.Unplaceable, plan.Held)
        {
            Unconfigured = plan.Unconfigured,
            ByServer = run.ByServer()
        };
    }

    private async Task WriteSeriesAsync(Run run, SeriesWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var nfo = FederatedPaths.Resolve(run.Root, work.NfoPath);
        var folder = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            var existing = new LazyItem(_library, folder, true, work.IsNew);
            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || !File.Exists(nfo))
            {
                await WriteTextAsync(nfo, work.Nfo, cancellationToken).ConfigureAwait(false);
                if (existing.Value is { } item)
                {
                    await _updater.ApplyMetadataAsync(item, work.Metadata, cancellationToken).ConfigureAwait(false);
                }

                // The merge key may have changed, and only a scan regroups the rows (Phase 0, V10).
                entry = entry with { ContentHash = work.ContentHash, NeedsRefresh = entry.NeedsRefresh || existing.Value is not null };
                run.NeedsScan = true;
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await UpdateImagesAsync(run, work.Images, work.RemovedImages, existing.Value, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }

            var seasons = new List<SeasonStub>(work.Seasons.Count);
            foreach (var season in work.Seasons)
            {
                seasons.Add(await WriteSeasonAsync(run, season, work.IsNew, entry.ServerId, cancellationToken).ConfigureAwait(false));
            }

            run.Put(entry with { Seasons = seasons });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Fail(entry.ServerId);
            _logger.LogError("Cannot write a stub series: {Error}", exception.GetType().Name);
        }
    }

    // Returns the season as now on disk. A season of a series that exists may already have its item, made from the folder
    // name before this version: Jellyfin does not read the new season.nfo for it, so name, plot and poster go to the item.
    private async Task<SeasonStub> WriteSeasonAsync(Run run, SeasonWork work, bool seriesIsNew, Guid? serverId, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var nfo = FederatedPaths.Resolve(run.Root, work.NfoPath);
        try
        {
            var existing = new LazyItem(_library, Path.GetDirectoryName(nfo)!, true, seriesIsNew);
            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || !File.Exists(nfo))
            {
                await WriteTextAsync(nfo, work.Nfo, cancellationToken).ConfigureAwait(false);
                if (existing.Value is { } item)
                {
                    await _updater.ApplySeasonAsync(item, work.Metadata, cancellationToken).ConfigureAwait(false);
                }

                entry = entry with { ContentHash = work.ContentHash };
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await UpdateImagesAsync(run, work.Images, work.RemovedImages, existing.Value, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Fail(serverId);
            _logger.LogError("Cannot write a stub season: {Error}", exception.GetType().Name);
        }

        return entry;
    }

    private async Task WriteStubAsync(Run run, StubWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var strm = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            // The item is looked up only when something has to reach it: on a large catalog one lookup per stub was most of a run.
            var existing = new LazyItem(_library, strm, false, work.IsNew);
            var changed = false;
            var written = false;
            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || !File.Exists(strm))
            {
                await WriteTextAsync(strm, work.StrmUrl, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(FederatedPaths.Resolve(run.Root, work.NfoPath), work.Nfo, cancellationToken).ConfigureAwait(false);
                if (existing.Value is { } item)
                {
                    await _updater.ApplyMetadataAsync(item, work.Metadata, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    run.NeedsScan = true;
                }

                entry = entry with { ContentHash = work.ContentHash };
                changed = true;
                written = true;
            }

            var urlTag = UrlTagOf(work.StrmUrl);
            if (written || run.VerifyEveryStub || !IsUrlConfirmed(entry, strm, urlTag))
            {
                changed |= await ApplyStrmUrlAsync(strm, work.StrmUrl, existing.Value, cancellationToken).ConfigureAwait(false);
                entry = entry with { UrlTag = urlTag, StrmWrittenUtc = File.GetLastWriteTimeUtc(strm) };
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await UpdateImagesAsync(run, work.Images, work.RemovedImages, existing.Value, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }

            if (!string.Equals(entry.TechnicalTag, work.TechnicalTag, StringComparison.Ordinal))
            {
                if (existing.Value is { } item)
                {
                    _technical.WriteItemAndStreams(item, entry, cancellationToken);
                    _technical.WriteStreamsAndChapters(item, entry, cancellationToken);
                    await _updater.SaveAsync(item, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                }

                entry = entry with { TechnicalTag = work.TechnicalTag };
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
            _logger.LogError("Cannot write a stub: {Error}", exception.GetType().Name);
        }
    }

    private async Task SignStubAsync(Run run, StubEntry stub, CancellationToken cancellationToken)
    {
        var strm = FederatedPaths.Resolve(run.Root, stub.RelativePath);
        try
        {
            var url = _urls.Build(stub.Id);
            var urlTag = UrlTagOf(url);
            if (File.Exists(strm) && (run.VerifyEveryStub || !IsUrlConfirmed(stub, strm, urlTag)))
            {
                await ApplyStrmUrlAsync(strm, url, _library.FindByPath(strm, false), cancellationToken).ConfigureAwait(false);
                run.Put(stub with { UrlTag = urlTag, StrmWrittenUtc = File.GetLastWriteTimeUtc(strm) });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Fail(stub.ServerId);
            _logger.LogError("Cannot sign a stub: {Error}", exception.GetType().Name);
        }
    }

    // Keeps the file and the item on the current URL. Only those two change: the path, hence the item and its watch state,
    // stay. The item matters most: it is what ffmpeg opens, and an old URL would be refused until the next refresh.
    private async Task<bool> ApplyStrmUrlAsync(string strm, string url, BaseItem? item, CancellationToken cancellationToken)
    {
        var stale = !string.Equals(await File.ReadAllTextAsync(strm, cancellationToken).ConfigureAwait(false), url, StringComparison.Ordinal);
        if (stale)
        {
            await WriteTextAsync(strm, url, cancellationToken).ConfigureAwait(false);
        }

        if (item is not null)
        {
            await _updater.ApplyStrmUrlAsync(item, url, cancellationToken).ConfigureAwait(false);
        }

        return stale;
    }

    // Removes the images the remote dropped, then writes the others. True once all of them are on disk.
    private async Task<bool> UpdateImagesAsync(Run run, IReadOnlyList<StubImage> images, IReadOnlyList<StubImage> removed, BaseItem? item, CancellationToken cancellationToken)
    {
        foreach (var image in removed)
        {
            var stem = FederatedPaths.Resolve(run.Root, image.RelativeStem);
            var files = _imageExtensions.Select(extension => stem + extension).ToList();
            foreach (var file in files.Where(File.Exists))
            {
                File.Delete(file);
            }

            if (item is not null)
            {
                await _updater.RemoveImageAsync(item, image.Type, files, cancellationToken).ConfigureAwait(false);
            }
        }

        return await WriteImagesAsync(run, images, item, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteImagesAsync(Run run, IReadOnlyList<StubImage> images, BaseItem? item, CancellationToken cancellationToken)
    {
        var complete = true;
        foreach (var image in images)
        {
            var path = await WriteImageAsync(run, image, cancellationToken).ConfigureAwait(false);
            if (path is null)
            {
                complete = false;
            }
            else if (item is not null)
            {
                await _updater.ApplyImageAsync(item, image.Type, path, cancellationToken).ConfigureAwait(false);
            }
        }

        return complete;
    }

    private async Task<string?> WriteImageAsync(Run run, StubImage image, CancellationToken cancellationToken)
    {
        var stem = FederatedPaths.Resolve(run.Root, image.RelativeStem);
        string path;
        if (image.LocalPath is { } source)
        {
            path = stem + Path.GetExtension(source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path, overwrite: true);
        }
        else
        {
            return run.Fetched.TryRemove(image.RelativeStem, out var fetched)
                ? fetched
                : await DownloadImageAsync(run, image, cancellationToken).ConfigureAwait(false);
        }

        DeleteOtherExtensions(stem, path);
        return path;
    }

    // Remote images are read before the stubs are written, a few at a time for each server; the stubs stay one at a time.
    // A server's first image goes alone, so a server that does not answer still costs one timeout.
    private async Task FetchImagesAsync(Run run, StubPlan plan, CancellationToken cancellationToken)
    {
        var wanted = plan.Series
            .Where(work => !string.Equals(work.Entry.ImageTag, work.ImageTag, StringComparison.Ordinal))
            .SelectMany(work => work.Images)
            .Concat(plan.Series
                .SelectMany(work => work.Seasons)
                .Where(season => !string.Equals(season.Entry.ImageTag, season.ImageTag, StringComparison.Ordinal))
                .SelectMany(season => season.Images))
            .Concat(plan.Stubs
                .Where(work => !string.Equals(work.Entry.ImageTag, work.ImageTag, StringComparison.Ordinal))
                .SelectMany(work => work.Images))
            .Where(image => image.LocalPath is null && image.ServerId is not null && image.ItemId is not null);
        var options = new ParallelOptions { MaxDegreeOfParallelism = ImageReadsAtOnce, CancellationToken = cancellationToken };
        foreach (var server in wanted.GroupBy(image => image.ServerId!.Value))
        {
            var images = server.ToList();
            await FetchImageAsync(run, images[0], cancellationToken).ConfigureAwait(false);
            await Parallel.ForEachAsync(images.Skip(1), options, async (image, token) => await FetchImageAsync(run, image, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    private async Task FetchImageAsync(Run run, StubImage image, CancellationToken cancellationToken)
    {
        try
        {
            run.Fetched[image.RelativeStem] = await DownloadImageAsync(run, image, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left to the write of its stub, which reads it again and counts the failure.
        }
    }

    private async Task<string?> DownloadImageAsync(Run run, StubImage image, CancellationToken cancellationToken)
    {
        var stem = FederatedPaths.Resolve(run.Root, image.RelativeStem);
        if (image.ServerId is not { } serverId || image.ItemId is not { } itemId || run.UnreachableServers.ContainsKey(serverId))
        {
            return null;
        }

        var read = await _servers.RunWithSessionAsync(
            serverId,
            (endpoint, session, token) => _client.GetImageAsync(endpoint, session, itemId, image.Type.ToString(), 0, token),
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            // One timeout per image would hold a whole run back. They are tried again at the next one.
            if (read.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout)
            {
                run.UnreachableServers.TryAdd(serverId, true);
            }

            return null;
        }

        var path = stem + ExtensionOf(read.Value!.ContentType);
        await WriteFileAsync(path, read.Value.Content, cancellationToken).ConfigureAwait(false);
        DeleteOtherExtensions(stem, path);
        return path;
    }

    // The stubs to remove this round: one that could not leave its version group, also at the old path of a pending server
    // rename, stays, counted as failed, for the next round (Q326, Q351).
    private async Task<IReadOnlyList<StubEntry>> DetachRemovalsAsync(Run run, IReadOnlyList<StubEntry> removals, CancellationToken cancellationToken)
    {
        if (_linker is null || removals.Count == 0)
        {
            return removals;
        }

        var kept = await _linker.DetachForRemovalAsync(run.State.WithOldPaths(removals), cancellationToken).ConfigureAwait(false);
        if (kept.Count == 0)
        {
            return removals;
        }

        var ids = kept.Select(stub => stub.Id).ToHashSet(StringComparer.Ordinal);
        var stuck = removals.Where(stub => ids.Contains(stub.Id)).ToList();
        run.Failed += stuck.Count;
        foreach (var serverId in stuck.Select(stub => stub.ServerId).OfType<Guid>())
        {
            run.CountServer(serverId, failed: 1);
        }

        return removals.Where(stub => !ids.Contains(stub.Id)).ToList();
    }

    private void RemoveGone(Run run, IReadOnlyList<StubEntry> stubs)
    {
        var removal = _remover.RemoveAll(run.Root, run.State, stubs);
        run.State = removal.State;
        run.Removed += removal.Removed;
        run.Failed += removal.Failures.Count;
        foreach (var failure in removal.Failures)
        {
            _logger.LogError("Cannot remove a stub: {Error}", failure.GetType().Name);
        }

        // A stub still in the registry was not removed. The totals count the failures themselves.
        var left = removal.State.Stubs.Select(stub => stub.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stub in stubs)
        {
            if (stub.ServerId is { } serverId)
            {
                var kept = left.Contains(stub.Id);
                run.CountServer(serverId, removed: kept ? 0 : 1, failed: kept ? 1 : 0);
            }
        }
    }

    // A server added back during the run keeps its folder (decision 116): the servers are read again before each delete.
    private void RemoveServerFolders(Run run, IReadOnlyList<Guid> removedServers)
    {
        var removal = _remover.RemoveServerFolders(run.Root, run.State, removedServers, serverId => _servers.GetServers().Any(server => server.Id == serverId));
        run.State = removal.State;
        foreach (var failure in removal.Failures)
        {
            _logger.LogError("Cannot remove the folder of a removed server: {Error}", failure.GetType().Name);
        }

        if (removal.Removed > 0)
        {
            _logger.LogInformation("Removed the folders of {Count} removed servers", removal.Removed);
        }
    }

    // The library item of a path, looked up on first use. A stub minted by this run has none yet.
    private sealed class LazyItem(ILibraryManager library, string path, bool isFolder, bool isNew)
    {
        private bool _looked = isNew;
        private BaseItem? _item;

        public BaseItem? Value
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    _item = library.FindByPath(path, isFolder);
                }

                return _item;
            }
        }
    }

    // Entries change in place by position, and the state is rebuilt once: a new list per stub made a run quadratic.
    private sealed class Run
    {
        private readonly List<StubEntry> _stubs;
        private readonly List<SeriesStub> _series;
        private readonly Dictionary<string, int> _stubAt;
        private readonly Dictionary<string, int> _seriesAt;
        private readonly Dictionary<Guid, StubCounts> _byServer = [];

        public Run(string root, StubState state)
        {
            Root = root;
            State = state;
            _stubs = [.. state.Stubs];
            _series = [.. state.Series];
            _stubAt = Positions(_stubs, stub => stub.Id);
            _seriesAt = Positions(_series, series => series.Id);
        }

        public string Root { get; }

        public StubState State { get; set; }

        public ConcurrentDictionary<Guid, bool> UnreachableServers { get; } = new();

        /// <summary>Gets the remote images read ahead, by relative stem, with null for one that could not be read.</summary>
        public ConcurrentDictionary<string, string?> Fetched { get; } = new(StringComparer.Ordinal);

        public bool NeedsScan { get; set; }

        /// <summary>Gets a value indicating whether every stub gets its item checked, as the nightly safety net.</summary>
        public bool VerifyEveryStub { get; init; }

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public int Failed { get; set; }

        /// <summary>Adds to the totals and to the counts of the server, if the stub has one.</summary>
        public void Count(Guid? serverId, int created = 0, int updated = 0)
        {
            Created += created;
            Updated += updated;
            if (serverId is { } id)
            {
                CountServer(id, created, updated);
            }
        }

        /// <summary>Counts a stub or series that could not be written.</summary>
        public void Fail(Guid? serverId)
        {
            Failed++;
            if (serverId is { } id)
            {
                CountServer(id, failed: 1);
            }
        }

        /// <summary>Adds to the counts of one server only.</summary>
        public void CountServer(Guid serverId, int created = 0, int updated = 0, int removed = 0, int failed = 0)
        {
            var counts = _byServer.GetValueOrDefault(serverId) ?? new StubCounts(0, 0, 0, 0);
            _byServer[serverId] = new StubCounts(counts.Created + created, counts.Updated + updated, counts.Removed + removed, counts.Failed + failed);
        }

        public IReadOnlyDictionary<Guid, StubCounts> ByServer() => new Dictionary<Guid, StubCounts>(_byServer);

        public void Put(StubEntry stub)
        {
            if (_stubAt.TryGetValue(stub.Id, out var at))
            {
                _stubs[at] = stub;
            }
        }

        public void Put(SeriesStub series)
        {
            if (_seriesAt.TryGetValue(series.Id, out var at))
            {
                _series[at] = series;
            }
        }

        /// <summary>Applies every <see cref="Put(StubEntry)"/> to <see cref="State"/>.</summary>
        public void Commit() => State = State with { Stubs = [.. _stubs], Series = [.. _series] };

        private static Dictionary<string, int> Positions<T>(List<T> items, Func<T, string> id)
        {
            var positions = new Dictionary<string, int>(items.Count, StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                positions.TryAdd(id(items[i]), i);
            }

            return positions;
        }
    }
}
