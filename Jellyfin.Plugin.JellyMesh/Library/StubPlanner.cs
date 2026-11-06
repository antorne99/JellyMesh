using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Works out what the federated folder should hold. Pure: it touches no file and no item.
/// </summary>
/// <remarks>
/// A stub whose content turned local is left alone here: only the post-scan task may remove it, after handing over the
/// watch state (decision 2). This plan removes only what is proved gone from the remotes.
/// </remarks>
public static class StubPlanner
{
    private static readonly string[] _seriesKeyProviders = ["Custom", "Tvdb", "Imdb"];

    /// <summary>
    /// Makes the plan.
    /// </summary>
    /// <param name="input">What is known.</param>
    /// <returns>The plan.</returns>
    public static StubPlan Plan(StubPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var catalog = IndexItemLookup.Build(input.Snapshot);
        var matcher = new StubMatcher(input.State.Stubs);
        var claims = matcher.ClaimAll(StubContents.Group(input.View.Contents));

        var paths = new PathAllocator(input.State);
        var seriesPlans = new Dictionary<string, SeriesPlan>(StringComparer.Ordinal);
        var works = new List<StubWork>();
        var unplaceable = 0;

        foreach (var claim in claims.Where(entry => entry.Content.Winner is not null))
        {
            var (content, stub) = claim;
            var winner = catalog.Find(content.Winner!.ServerId, content.Winner.ItemId);
            if (winner is null)
            {
                continue;
            }

            if (content.Identity.Kind == ContentKind.Movie)
            {
                works.Add(MovieWork(input, content, stub, winner, catalog, paths));
            }
            else if (content.Identity.SeasonNumber is null || content.Identity.EpisodeNumber is null)
            {
                unplaceable++;
            }
            else
            {
                var seriesPlan = SeriesPlanOf(input, content, winner, catalog, paths, seriesPlans);
                works.Add(EpisodeWork(input, content, stub, winner, seriesPlan, catalog));
            }
        }

        var seriesWorks = seriesPlans.Values.Select(plan => plan.Work).ToList();
        var written = works.ToDictionary(work => work.Entry.Id, StringComparer.Ordinal);
        var unclaimed = input.State.Stubs.Where(stub => !matcher.IsClaimed(stub)).ToList();

        var held = new Dictionary<(Guid ServerId, string LibraryId), int>();
        var removals = unclaimed.Where(stub => IsGone(input, stub, held)).ToList();

        // Claimed stubs that get no work, such as those turning local, stay as they are. So do those still in doubt.
        var stubs = input.State.Stubs
            .Where(stub => !written.ContainsKey(stub.Id))
            .Concat(works.Select(work => work.Entry))
            .ToList();
        var series = input.State.Series
            .Where(entry => seriesWorks.TrueForAll(work => work.Entry.Id != entry.Id))
            .Concat(seriesWorks.Select(work => work.Entry))
            .ToList();

        var names = input.Servers.ToDictionary(server => server.Id, server => server.Name);
        var heldLibraries = held
            .Select(entry => new HeldLibrary(entry.Key.ServerId, names.GetValueOrDefault(entry.Key.ServerId, string.Empty), entry.Key.LibraryId, entry.Value))
            .OrderBy(library => library.ServerName, StringComparer.Ordinal)
            .ToList();

        return new StubPlan(input.State with { Stubs = stubs, Series = series }, works, seriesWorks, removals, heldLibraries, unplaceable)
        {
            MergedStubs = claims.Sum(claim => claim.Merged.Count)
        };
    }

    /// <summary>
    /// Hashes the content of a stub.
    /// </summary>
    /// <param name="parts">The parts.</param>
    /// <returns>The hash.</returns>
    public static string Hash(params string[] parts)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    // A signature is no change of content: a new key must not put remote metadata back on every item, and the materializer
    // keeps the file and the item on the current URL by itself.
    private static string Unsigned(string url) => url.Split('?', 2)[0];

    private static bool IsGone(StubPlanInput input, StubEntry stub, Dictionary<(Guid ServerId, string LibraryId), int> held)
    {
        var servers = input.Servers.ToDictionary(server => server.Id);

        // Removing every source was the administrator's choice, and that needs no index to prove.
        if (stub.Sources.Count > 0 && stub.Sources.All(source => !servers.ContainsKey(source.ServerId)))
        {
            return true;
        }

        // A damaged index says nothing, and a server missing from it is not proof either.
        if (input.Snapshot.Health != IndexHealth.Ok)
        {
            return false;
        }

        var emptyLibraries = new List<(Guid ServerId, string LibraryId)>();
        foreach (var source in stub.Sources)
        {
            // A server taken out of the configuration can serve nothing: that was the administrator's choice.
            if (!servers.TryGetValue(source.ServerId, out var server))
            {
                continue;
            }

            if (!input.Snapshot.IsAuthoritativeFor(source.ServerId))
            {
                return false;
            }

            var library = source.LibraryId;
            if (library is null || !server.ImportedLibraryIds.Contains(library))
            {
                continue;
            }

            if (!input.Snapshot.Find(source.ServerId)!.Items.Any(item => string.Equals(item.LibraryId, library, StringComparison.Ordinal)))
            {
                emptyLibraries.Add((source.ServerId, library));
            }
        }

        foreach (var library in emptyLibraries.Distinct())
        {
            held[library] = held.GetValueOrDefault(library) + 1;
        }

        return emptyLibraries.Count == 0;
    }

    private static List<StubSource> SourcesOf(ResolvedContent content, IndexItemLookup catalog)
    {
        return content.Backups.Prepend(content.Winner!)
            .Select(source => new StubSource(source.ServerId, source.ItemId, source.MediaSourceId)
            {
                LibraryId = catalog.Find(source.ServerId, source.ItemId)?.LibraryId
            })
            .ToList();
    }

    private static StubWork MovieWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, IndexItemLookup catalog, PathAllocator paths)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? FederatedPaths.MovieStub(paths.MovieFolder(winner.Name, winner.ProductionYear, id));
        var folder = Path.GetDirectoryName(relative)!;

        var entry = (stub ?? NewEntry(id, StubKind.Movie, relative)) with
        {
            IdentityKey = content.Identity.Key,
            Sources = SourcesOf(content, catalog)
        };

        var metadata = StubMetadata.From(winner, input.IsProtected);
        var nfo = NfoDocument.Movie(metadata);
        var url = input.StrmUrl(id);
        var images = new List<StubImage>();
        AddRemote(images, winner, content.Winner!.ServerId, ImageType.Primary, Path.Combine(folder, "poster"));
        AddRemote(images, winner, content.Winner.ServerId, ImageType.Backdrop, Path.Combine(folder, "fanart"));
        return new StubWork(entry, stub is null, metadata, nfo, Path.Combine(folder, "movie.nfo"), url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner));
    }

    private static StubWork EpisodeWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, SeriesPlan series, IndexItemLookup catalog)
    {
        var season = content.Identity.SeasonNumber!.Value;
        var number = content.Identity.EpisodeNumber!.Value;
        var last = content.EpisodeNumberEnd;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? FederatedPaths.EpisodeStub(series.Entry.RelativePath, series.FileName, season, number, last);
        var stem = Path.ChangeExtension(relative, null);

        var entry = (stub ?? NewEntry(id, StubKind.Episode, relative)) with
        {
            IdentityKey = content.Identity.Key,
            Sources = SourcesOf(content, catalog),

            // A stub stays in the series folder it was created in, even after two series became one work.
            SeriesStubId = stub?.SeriesStubId ?? series.Entry.Id,
            SeasonNumber = season,
            EpisodeNumber = number,
            EpisodeNumberEnd = last,

            // New numbers reach an existing item with the technical data, because its .nfo is not read again.
            TechnicalTag = (stub?.SeasonNumber, stub?.EpisodeNumber, stub?.EpisodeNumberEnd) == (season, number, last) ? stub!.TechnicalTag : null
        };

        var metadata = StubMetadata.From(winner, input.IsProtected) with { SeasonNumber = season, EpisodeNumber = number, EpisodeNumberEnd = last };
        var nfo = NfoDocument.Episode(metadata);
        var url = input.StrmUrl(id);
        var images = new List<StubImage>();
        AddRemote(images, winner, content.Winner!.ServerId, ImageType.Primary, stem + "-thumb");
        return new StubWork(entry, stub is null, metadata, nfo, stem + ".nfo", url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner));
    }

    private static SeriesPlan SeriesPlanOf(
        StubPlanInput input,
        ResolvedContent content,
        IndexedItem winner,
        IndexItemLookup catalog,
        PathAllocator paths,
        Dictionary<string, SeriesPlan> plans)
    {
        var workKey = content.Identity.WorkKey;
        if (plans.TryGetValue(workKey, out var plan))
        {
            return plan;
        }

        var known = SeriesItems(input, workKey, catalog);
        var stub = input.State.Series.FirstOrDefault(entry => string.Equals(entry.WorkKey, workKey, StringComparison.Ordinal))
            ?? input.State.Series.FirstOrDefault(entry => entry.Sources.Any(source => known.Exists(item => item.ServerId == source.ServerId && item.Item.ItemId == source.ItemId)));

        // Stay with the remote series already in use, so that the metadata does not flip between servers.
        var source = known.Find(item => stub?.Sources.Any(entry => entry.ServerId == item.ServerId && entry.ItemId == item.Item.ItemId) == true);
        if (source.Item is null && known.Count > 0)
        {
            source = known[0];
        }

        var local = input.LocalSeriesOf(workKey);
        var remote = source.Item ?? new IndexedItem
        {
            ItemId = winner.SeriesId ?? winner.ItemId,
            Kind = IndexedItemKind.Series,
            LibraryId = winner.LibraryId,
            LastSeenUtc = winner.LastSeenUtc,
            Name = winner.SeriesName
        };

        var metadata = SeriesMetadata(remote, local, input.IsProtected);
        var name = metadata.Title ?? remote.Name;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? FederatedPaths.SeriesFolderOf(paths.SeriesFolder(name, metadata.Year, id));

        var nfo = NfoDocument.Series(metadata);
        var images = new List<StubImage>();
        var imageTag = SeriesImages(images, relative, local, remote, source.ServerId);
        var entry = (stub ?? new SeriesStub { Id = id, RelativePath = relative, WorkKey = workKey, NeedsRefresh = true }) with
        {
            WorkKey = workKey,
            Sources = known.Select(item => new StubSource(item.ServerId, item.Item.ItemId, string.Empty)).ToList()
        };

        plan = new SeriesPlan(entry, name ?? string.Empty, new SeriesWork(entry, stub is null, metadata, nfo, Path.Combine(relative, "tvshow.nfo"), Hash(nfo), images, imageTag));
        plans[workKey] = plan;
        return plan;
    }

    // Every remote row of the series, in a fixed order, so that a first choice does not depend on the order of a read.
    private static List<(Guid ServerId, IndexedItem Item)> SeriesItems(StubPlanInput input, string workKey, IndexItemLookup catalog)
    {
        return input.View.Contents
            .Where(content => string.Equals(content.Identity.WorkKey, workKey, StringComparison.Ordinal))
            .OrderBy(content => content.Identity.SeasonNumber)
            .ThenBy(content => content.Identity.EpisodeNumber)
            .SelectMany(content => content.RemoteItems)
            .Select(item => (item.ServerId, Episode: catalog.Find(item.ServerId, item.ItemId)))
            .Where(entry => entry.Episode?.SeriesId is not null)
            .Select(entry => (entry.ServerId, Item: catalog.Find(entry.ServerId, entry.Episode!.SeriesId!)))
            .Where(entry => entry.Item is not null)
            .Select(entry => (entry.ServerId, Item: entry.Item!))
            .DistinctBy(entry => (entry.ServerId, entry.Item.ItemId))
            .ToList();
    }

    private static StubMetadata SeriesMetadata(IndexedItem remote, LocalSeries? local, bool isProtected)
    {
        var metadata = StubMetadata.From(remote, isProtected);
        if (local is null)
        {
            return metadata;
        }

        // The merge key: the first of Custom, Tvdb and Imdb, or else the exact name. An ID the local series does not have
        // would change the key, so only its IDs are used (decision 2). The row shown can be the stub's (decision 3).
        var ids = local.ProviderIds
            .Where(id => _seriesKeyProviders.Contains(id.Key, StringComparer.OrdinalIgnoreCase) || string.Equals(id.Key, "Tmdb", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(id => id.Key, id => id.Value, StringComparer.OrdinalIgnoreCase);

        return metadata with
        {
            Title = local.Name,
            Year = local.Year ?? metadata.Year,
            ProviderIds = ids,
            Overview = isProtected ? local.Overview : null
        };
    }

    private static string SeriesImages(List<StubImage> images, string folder, LocalSeries? local, IndexedItem remote, Guid serverId)
    {
        var tag = new StringBuilder();
        void Add(ImageType type, string stem, string? localPath)
        {
            if (localPath is not null)
            {
                images.Add(new StubImage(type, Path.Combine(folder, stem), null, null, localPath));
                tag.Append(CultureInfo.InvariantCulture, $"{type}=local:{localPath};");
            }
            else if (TagOf(remote, type) is { } remoteTag)
            {
                images.Add(new StubImage(type, Path.Combine(folder, stem), serverId, remote.ItemId, null));
                tag.Append(CultureInfo.InvariantCulture, $"{type}={remoteTag};");
            }
        }

        Add(ImageType.Primary, "poster", local?.PosterPath);
        Add(ImageType.Backdrop, "fanart", local?.BackdropPath);
        return tag.ToString();
    }

    private static void AddRemote(List<StubImage> images, IndexedItem item, Guid serverId, ImageType type, string stem)
    {
        if (TagOf(item, type) is not null)
        {
            images.Add(new StubImage(type, stem, serverId, item.ItemId, null));
        }
    }

    private static string? TagOf(IndexedItem item, ImageType type)
    {
        return type == ImageType.Backdrop
            ? (item.BackdropImageTags.Count > 0 ? item.BackdropImageTags[0] : null)
            : item.ImageTags.GetValueOrDefault(type.ToString());
    }

    private static string ImageTagOf(IndexedItem item, List<StubImage> images)
    {
        return string.Concat(images.Select(image => string.Create(CultureInfo.InvariantCulture, $"{image.Type}={TagOf(item, image.Type)};")));
    }

    private static string TechnicalTagOf(ResolvedContent content, IndexedItem winner)
    {
        var version = winner.Versions.FirstOrDefault(entry => string.Equals(entry.MediaSourceId, content.Winner!.MediaSourceId, StringComparison.Ordinal));
        return string.Join('|', content.Winner!.ServerId.ToString("N"), content.Winner.ItemId, content.Winner.MediaSourceId, version?.ETag, winner.Etag);
    }

    private static StubEntry NewEntry(string id, StubKind kind, string relativePath) => new()
    {
        Id = id,
        Kind = kind,
        RelativePath = relativePath,
        Sources = [],
        IdentityKey = string.Empty
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed record SeriesPlan(SeriesStub Entry, string FileName, SeriesWork Work);

    /// <summary>
    /// Keeps folder names unique. Two different titles can reach the same name, with a different year missing or after
    /// sanitizing, and they must not share a folder. Paths of stubs that exist never change.
    /// </summary>
    private sealed class PathAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

        public PathAllocator(StubState state)
        {
            foreach (var stub in state.Stubs.Where(stub => stub.Kind == StubKind.Movie))
            {
                _taken.Add(Path.GetDirectoryName(stub.RelativePath)!);
            }

            foreach (var series in state.Series)
            {
                _taken.Add(series.RelativePath);
            }
        }

        public string MovieFolder(string? title, int? year, string id)
            => Unique(FederatedPaths.TitleFolder(title, year), id, FederatedPaths.MovieFolder);

        public string SeriesFolder(string? title, int? year, string id)
            => Unique(FederatedPaths.TitleFolder(title, year), id, FederatedPaths.SeriesFolderOf);

        private string Unique(string name, string id, Func<string, string> relative)
        {
            if (_taken.Add(relative(name)))
            {
                return name;
            }

            var unique = string.Create(CultureInfo.InvariantCulture, $"{name} [{id[..8]}]");
            _taken.Add(relative(unique));
            return unique;
        }
    }
}
