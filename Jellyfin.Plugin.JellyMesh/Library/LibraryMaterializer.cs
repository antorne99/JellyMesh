using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryMaterializer"/>. Writes only inside the federated folder. The registry is saved before the files, so
/// that a scan running at the same time finds the stub of every file it meets.
/// </summary>
public sealed class LibraryMaterializer : ILibraryMaterializer
{
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
        ILogger<LibraryMaterializer> logger)
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
    }

    /// <inheritdoc />
    public MaterializationReport? LastReport => _lastReport;

    /// <inheritdoc />
    public async Task<MaterializationReport> MaterializeAsync(CancellationToken cancellationToken)
    {
        var (root, isProtected) = _store.Read(config => (config.FederatedFolderPath, config.MetadataProtection));
        if (root.Length == 0)
        {
            var report = new MaterializationReport(_timeProvider.GetUtcNow(), false, 0, 0, 0, 0, 0, []);
            _lastReport = report;
            return report;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await RunAsync(root, isProtected, cancellationToken).ConfigureAwait(false);
            _lastReport = report;
            return report;
        }
        finally
        {
            _registry.Exit();
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
        => WriteFileAsync(path, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);

    private static void DeleteOtherExtensions(string stem, string keep)
    {
        var folder = Path.GetDirectoryName(stem)!;
        var name = Path.GetFileName(stem) + ".";
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(file).StartsWith(name, StringComparison.Ordinal)
                && !string.Equals(file, keep, StringComparison.Ordinal)
                && Path.GetExtension(file) is ".jpg" or ".png" or ".webp" or ".gif")
            {
                File.Delete(file);
            }
        }
    }

    private async Task<MaterializationReport> RunAsync(string root, bool isProtected, CancellationToken cancellationToken)
    {
        // One snapshot and one server list for the view and the plan: a removal during the run waits for the next one.
        var snapshot = _index.Snapshot;
        var servers = _servers.GetServers();
        var view = _resolver.Resolve(snapshot, servers);
        var plan = StubPlanner.Plan(new StubPlanInput(
            view,
            snapshot,
            _registry.State,
            servers,
            isProtected,
            _urls.Build,
            workKey => LocalSeriesOf(view, workKey)));

        WriteHiddenFile(root, FederatedPaths.MovieFolder(string.Empty));
        WriteHiddenFile(root, FederatedPaths.SeriesFolderOf(string.Empty));

        await _registry.SaveAsync(plan.State, cancellationToken).ConfigureAwait(false);

        var run = new Run(root, plan.State);
        foreach (var series in plan.Series)
        {
            await WriteSeriesAsync(run, series, cancellationToken).ConfigureAwait(false);
        }

        foreach (var stub in plan.Stubs)
        {
            await WriteStubAsync(run, stub, cancellationToken).ConfigureAwait(false);
        }

        // A stub with no work, such as one waiting for the post-scan task after its local copy arrived, can still be opened.
        var handled = plan.Stubs.Select(work => work.Entry).Concat(plan.Removals).Select(stub => stub.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stub in plan.State.Stubs.Where(stub => !handled.Contains(stub.Id)))
        {
            await SignStubAsync(run, stub, cancellationToken).ConfigureAwait(false);
        }

        run.Commit();
        foreach (var stub in plan.Removals)
        {
            RemoveGone(run, stub);
        }

        await _registry.SaveAsync(run.State, cancellationToken).ConfigureAwait(false);

        foreach (var held in plan.Held)
        {
            _logger.LogWarning(
                "A complete read of a library of remote server {ServerName} came back empty. {Count} stubs are kept until it has items again, or it is deselected",
                held.ServerName,
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
        return new MaterializationReport(_timeProvider.GetUtcNow(), true, run.Created, run.Updated, run.Removed, run.Failed, plan.Unplaceable, plan.Held);
    }

    private LocalSeries? LocalSeriesOf(FederationView view, string workKey)
    {
        if (!view.LocalItemByWork.TryGetValue(workKey, out var itemId)
            || _library.GetItemById(itemId) is not Episode episode
            || _library.GetItemById(episode.SeriesId) is not Series series)
        {
            return null;
        }

        return new LocalSeries(
            series.Name,
            series.ProductionYear,
            series.Overview,
            series.ProviderIds,
            LocalImage(series, ImageType.Primary),
            LocalImage(series, ImageType.Backdrop));
    }

    private string? LocalImage(BaseItem item, ImageType type)
    {
        return item.GetImageInfo(type, 0) is { IsLocalFile: true } image && File.Exists(image.Path) ? image.Path : null;
    }

    private async Task WriteSeriesAsync(Run run, SeriesWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var nfo = FederatedPaths.Resolve(run.Root, work.NfoPath);
        var folder = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            var existing = work.IsNew ? null : _library.FindByPath(folder, true);
            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || !File.Exists(nfo))
            {
                await WriteTextAsync(nfo, work.Nfo, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    await _updater.ApplyMetadataAsync(existing, work.Metadata, cancellationToken).ConfigureAwait(false);
                }

                // The merge key may have changed, and only a scan regroups the rows (Phase 0, V10).
                entry = entry with { ContentHash = work.ContentHash, NeedsRefresh = entry.NeedsRefresh || existing is not null };
                run.NeedsScan = true;
            }

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await WriteImagesAsync(run, work.Images, existing, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }

            run.Put(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Failed++;
            _logger.LogError("Cannot write a stub series: {Error}", exception.GetType().Name);
        }
    }

    private async Task WriteStubAsync(Run run, StubWork work, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var strm = FederatedPaths.Resolve(run.Root, entry.RelativePath);
        try
        {
            var existing = work.IsNew ? null : _library.FindByPath(strm, false);
            var changed = false;
            if (!string.Equals(entry.ContentHash, work.ContentHash, StringComparison.Ordinal) || !File.Exists(strm))
            {
                await WriteTextAsync(strm, work.StrmUrl, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(FederatedPaths.Resolve(run.Root, work.NfoPath), work.Nfo, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    await _updater.ApplyMetadataAsync(existing, work.Metadata, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    run.NeedsScan = true;
                }

                entry = entry with { ContentHash = work.ContentHash };
                changed = true;
            }

            changed |= await ApplyStrmUrlAsync(strm, work.StrmUrl, existing, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(entry.ImageTag, work.ImageTag, StringComparison.Ordinal)
                && await WriteImagesAsync(run, work.Images, existing, cancellationToken).ConfigureAwait(false))
            {
                entry = entry with { ImageTag = work.ImageTag };
            }

            if (!string.Equals(entry.TechnicalTag, work.TechnicalTag, StringComparison.Ordinal))
            {
                if (existing is not null)
                {
                    _technical.WriteItemAndStreams(existing, entry, cancellationToken);
                    _technical.WriteStreamsAndChapters(existing, entry, cancellationToken);
                    await _updater.SaveAsync(existing, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                }

                entry = entry with { TechnicalTag = work.TechnicalTag };
            }

            run.Put(entry);
            run.Created += work.IsNew ? 1 : 0;
            run.Updated += !work.IsNew && changed ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Failed++;
            _logger.LogError("Cannot write a stub: {Error}", exception.GetType().Name);
        }
    }

    private async Task SignStubAsync(Run run, StubEntry stub, CancellationToken cancellationToken)
    {
        var strm = FederatedPaths.Resolve(run.Root, stub.RelativePath);
        try
        {
            if (File.Exists(strm))
            {
                await ApplyStrmUrlAsync(strm, _urls.Build(stub.Id), _library.FindByPath(strm, false), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Failed++;
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
            if (image.ServerId is not { } serverId || image.ItemId is not { } itemId || run.UnreachableServers.Contains(serverId))
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
                    run.UnreachableServers.Add(serverId);
                }

                return null;
            }

            path = stem + ExtensionOf(read.Value!.ContentType);
            await WriteFileAsync(path, read.Value.Content, cancellationToken).ConfigureAwait(false);
        }

        DeleteOtherExtensions(stem, path);
        return path;
    }

    private void RemoveGone(Run run, StubEntry stub)
    {
        try
        {
            run.State = _remover.Remove(run.Root, run.State, stub);
            run.Removed++;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            run.Failed++;
            _logger.LogError("Cannot remove a stub: {Error}", exception.GetType().Name);
        }
    }

    // Entries change in place by position, and the state is rebuilt once: a new list per stub made a run quadratic.
    private sealed class Run
    {
        private readonly List<StubEntry> _stubs;
        private readonly List<SeriesStub> _series;
        private readonly Dictionary<string, int> _stubAt;
        private readonly Dictionary<string, int> _seriesAt;

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

        public HashSet<Guid> UnreachableServers { get; } = [];

        public bool NeedsScan { get; set; }

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public int Failed { get; set; }

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
