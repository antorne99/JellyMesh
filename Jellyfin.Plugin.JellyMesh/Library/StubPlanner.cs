using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
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

    // The images of a film or a series, under the names Jellyfin reads in a title folder. An episode keeps one (decision 50).
    private static readonly (ImageType Type, string Stem)[] _titleImages =
    [
        (ImageType.Primary, "poster"),
        (ImageType.Backdrop, "fanart"),
        (ImageType.Logo, "logo"),
        (ImageType.Banner, "banner"),
        (ImageType.Thumb, "landscape")
    ];

    /// <summary>
    /// Makes the plan.
    /// </summary>
    /// <param name="input">What is known.</param>
    /// <returns>The plan.</returns>
    public static StubPlan Plan(StubPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var catalog = IndexItemLookup.Build(input.Snapshot);

        // Stubs of the server folders belong to PlanByServer alone.
        var flat = input.State.Stubs.Where(stub => stub.ServerId is null).ToList();
        var flatSeries = input.State.Series.Where(series => series.ServerId is null).ToList();
        var matcher = new StubMatcher(flat);
        var claims = matcher.ClaimAll(StubContents.Group(input.View.Contents));

        var paths = new PathAllocator(flat, flatSeries, FederatedPaths.MovieFolder, FederatedPaths.SeriesFolderOf);
        var scope = new SeriesScope(null, flatSeries, input.View.Contents, paths.SeriesFolder, input.LocalSeriesOf);
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
                works.Add(MovieWork(input, content, stub, winner, catalog, null, id => FederatedPaths.MovieStub(paths.MovieFolder(winner.Name, winner.ProductionYear, id))));
            }
            else if (content.Identity.SeasonNumber is null || content.Identity.EpisodeNumber is null)
            {
                unplaceable++;
            }
            else
            {
                var seriesPlan = SeriesPlanOf(input, content, winner, catalog, scope, seriesPlans);
                works.Add(EpisodeWork(input, content, stub, winner, seriesPlan, catalog, null, null));
            }
        }

        var seriesWorks = seriesPlans.Values.Select(plan => WithSeasons(input, plan, catalog)).ToList();
        var written = works.ToDictionary(work => work.Entry.Id, StringComparer.Ordinal);
        var unclaimed = flat.Where(stub => !matcher.IsClaimed(stub)).ToList();

        var held = new Dictionary<(Guid ServerId, string LibraryId), int>();
        var servers = input.Servers.ToDictionary(server => server.Id);
        var removed = input.State.RemovedServers.Where(server => !servers.ContainsKey(server)).ToHashSet();
        var removals = new List<StubEntry>();
        var unconfigured = Sweep(input, servers, removed, unclaimed, held, removals);

        // Claimed stubs that get no work, such as those turning local, stay as they are. So do those still in doubt.
        var stubs = input.State.Stubs
            .Where(stub => !written.ContainsKey(stub.Id))
            .Concat(works.Select(work => work.Entry))
            .ToList();
        var series = input.State.Series
            .Where(entry => seriesWorks.TrueForAll(work => work.Entry.Id != entry.Id))
            .Concat(seriesWorks.Select(work => work.Entry))
            .ToList();

        // A server configured again loses its mark, so that a later reset of the settings keeps its titles (decision 44).
        var marks = input.State.RemovedServers.Where(removed.Contains).ToList();
        return new StubPlan(input.State with { Stubs = stubs, Series = series, RemovedServers = marks }, works, seriesWorks, removals, HeldLibraries(input, held), unplaceable)
        {
            MergedStubs = claims.Sum(claim => claim.Merged.Count),
            Unconfigured = unconfigured
        };
    }

    /// <summary>
    /// Makes the plan of the layout with one folder per server. Each server folder holds one stub per title, with the best
    /// copy of that server and its other copies as internal sources, even when a local copy exists. The stubs of one title,
    /// and its local copy, form a version group; episodes only with the same range. Stubs of the flat layout are left as
    /// they are. Pure, like <see cref="Plan"/>.
    /// </summary>
    /// <param name="input">What is known.</param>
    /// <returns>The plan.</returns>
    public static StubPlan PlanByServer(StubPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var catalog = IndexItemLookup.Build(input.Snapshot);
        var labels = FederatedPaths.Labels(input.Servers.Select(server => (server.Id, (string?)server.Name)), KeptLabels(input));
        var works = new List<StubWork>();
        var placed = new List<(StubWork Work, ResolvedContent Content, IndexedItem Winner)>();
        var seriesWorks = new List<SeriesWork>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var merged = new HashSet<string>(StringComparer.Ordinal);
        var unplaceable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in input.Servers)
        {
            var contents = ServerContents(input.View, input.Snapshot, catalog, server);
            var own = input.State.Stubs.Where(stub => stub.ServerId == server.Id).ToList();
            var ownSeries = input.State.Series.Where(series => series.ServerId == server.Id).ToList();
            var matcher = new StubMatcher(own);
            var claims = matcher.ClaimAll(StubContents.Group(contents));

            var paths = new PathAllocator(own, ownSeries, name => FederatedPaths.MovieFolder(server.Id, name), name => FederatedPaths.SeriesFolderOf(server.Id, name));
            var scope = new SeriesScope(server.Id, ownSeries, contents, paths.SeriesFolder, _ => null);
            var seriesPlans = new Dictionary<string, SeriesPlan>(StringComparer.Ordinal);
            var label = labels[server.Id];
            foreach (var (content, stub) in claims)
            {
                if (catalog.Find(server.Id, content.Winner!.ItemId) is not { } winner)
                {
                    continue;
                }

                if (content.Identity.Kind == ContentKind.Movie)
                {
                    placed.Add((MovieWork(input, content, stub, winner, catalog, server.Id, id => FederatedPaths.MovieStub(server.Id, paths.MovieFolder(winner.Name, winner.ProductionYear, id), label)), content, winner));
                }
                else if (content.Identity.SeasonNumber is null || content.Identity.EpisodeNumber is null)
                {
                    unplaceable.Add(content.Identity.Key);
                }
                else
                {
                    var seriesPlan = SeriesPlanOf(input, content, winner, catalog, scope, seriesPlans);
                    placed.Add((EpisodeWork(input, content, stub, winner, seriesPlan, catalog, server.Id, label), content, winner));
                }
            }

            seriesWorks.AddRange(seriesPlans.Values.Select(plan => WithSeasons(input, plan, catalog)));
            works.AddRange(placed.Skip(works.Count).Select(entry => entry.Work));
            claimed.UnionWith(own.Where(matcher.IsClaimed).Select(stub => stub.Id));
            merged.UnionWith(claims.SelectMany(claim => claim.Merged).Select(stub => stub.Id));
        }

        var written = works.ToDictionary(work => work.Entry.Id, StringComparer.Ordinal);
        var unclaimed = input.State.Stubs.Where(stub => stub.ServerId is not null && !claimed.Contains(stub.Id)).ToList();

        var held = new Dictionary<(Guid ServerId, string LibraryId), int>();
        var servers = input.Servers.ToDictionary(server => server.Id);
        var removed = input.State.RemovedServers.Where(server => !servers.ContainsKey(server)).ToHashSet();
        var removals = new List<StubEntry>();
        var unconfigured = Sweep(input, servers, removed, unclaimed, held, removals);

        // Stubs of the flat layout, merged stubs and those still in doubt stay as they are.
        var stubs = input.State.Stubs
            .Where(stub => !written.ContainsKey(stub.Id))
            .Concat(works.Select(work => work.Entry))
            .ToList();
        var series = input.State.Series
            .Where(entry => seriesWorks.TrueForAll(work => work.Entry.Id != entry.Id))
            .Concat(seriesWorks.Select(work => work.Entry))
            .ToList();

        var marks = input.State.RemovedServers.Where(removed.Contains).ToList();
        var groups = Groups(input, placed, stubs, removals.Select(stub => stub.Id).Concat(merged));
        var state = input.State with { Stubs = stubs, Series = series, RemovedServers = marks, Groups = groups, Labels = labels };
        return new StubPlan(state, works, seriesWorks, removals, HeldLibraries(input, held), unplaceable.Count)
        {
            MergedStubs = merged.Count,
            Unconfigured = unconfigured
        };
    }

    /// <summary>
    /// Claims the stubs of each server folder against the contents as that server sees them, as <see cref="PlanByServer"/>
    /// does: a merged stub is always merged into a stub of its own server. Stubs of the flat layout are left out.
    /// </summary>
    /// <param name="view">The resolved catalog.</param>
    /// <param name="snapshot">The index the view was read from.</param>
    /// <param name="servers">The configured servers.</param>
    /// <param name="stubs">The stubs.</param>
    /// <returns>The claims of every server.</returns>
    public static IReadOnlyList<StubClaim> ClaimByServer(FederationView view, IndexSnapshot snapshot, IReadOnlyList<RemoteServerInfo> servers, IReadOnlyList<StubEntry> stubs)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(stubs);

        var catalog = IndexItemLookup.Build(snapshot);
        return servers
            .SelectMany(server => new StubMatcher(stubs.Where(stub => stub.ServerId == server.Id))
                .ClaimAll(StubContents.Group(ServerContents(view, snapshot, catalog, server))))
            .ToList();
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

    // Links the stubs of one title in different server folders, and its local copy, as versions. A group keeps its ID, and
    // its primary while that stays in it; a stub that got no work, such as one of a server still in doubt, keeps its group.
    private static List<VersionGroup> Groups(StubPlanInput input, List<(StubWork Work, ResolvedContent Content, IndexedItem Winner)> placed, List<StubEntry> stubs, IEnumerable<string> gone)
    {
        var written = placed.Select(entry => entry.Work.Entry.Id).ToHashSet(StringComparer.Ordinal);
        var left = gone.ToHashSet(StringComparer.Ordinal);
        var kept = stubs
            .Where(stub => stub.ServerId is not null && !written.Contains(stub.Id) && !left.Contains(stub.Id))
            .ToDictionary(stub => stub.Id, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var reused = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<VersionGroup>();

        // Episodes join only when the files behind them cover the same range, whatever range the stubs show.
        var covered = input.View.Contents
            .SelectMany(content => content.RemoteItems.Select(item => (Item: item, content.Identity.Key)))
            .ToLookup(entry => entry.Item, entry => entry.Key);
        var titles = placed
            .GroupBy(entry => GroupKeyOf(entry.Content, entry.Winner, entry.Work.Entry.ServerId!.Value, covered), StringComparer.Ordinal)
            .OrderBy(title => title.Key, StringComparer.Ordinal);
        var seasons = input.View.Contents.ToLookup(entry => (entry.Identity.WorkKey, entry.Identity.SeasonNumber));
        var oldGroups = OldGroupIndex.Of(input.State.Groups);
        foreach (var title in titles)
        {
            var (_, content, winner) = title.First();
            var stubIds = title.DistinctBy(entry => entry.Work.Entry.ServerId).Select(entry => entry.Work.Entry.Id).ToList();
            var local = LocalCopiesOf(seasons, content, winner);
            var old = oldGroups.FirstHolding(stubIds, local, reused);
            if (old is not null)
            {
                reused.Add(old.Id);
                var servers = title.Select(entry => entry.Work.Entry.ServerId).ToHashSet();
                stubIds.AddRange(old.StubIds.Where(id => kept.TryGetValue(id, out var stub) && servers.Add(stub.ServerId)));
            }

            AddGroup(groups, assigned, old?.Id ?? NewId(), content.Identity.Key, stubIds, local, old?.PrimaryStubId);
        }

        foreach (var old in input.State.Groups.Where(group => !reused.Contains(group.Id)))
        {
            AddGroup(groups, assigned, old.Id, old.IdentityKey, old.StubIds.Where(kept.ContainsKey).ToList(), old.LocalItemIds, old.PrimaryStubId);
        }

        return groups;
    }

    // Adds a group of the stubs not in one yet, if it still has two copies.
    private static void AddGroup(List<VersionGroup> groups, HashSet<string> assigned, string id, string identityKey, List<string> stubIds, IReadOnlyList<Guid> local, string? primary)
    {
        var members = stubIds.Where(stub => !assigned.Contains(stub)).Distinct(StringComparer.Ordinal).ToList();
        if (members.Count + (local.Count > 0 ? 1 : 0) < 2)
        {
            return;
        }

        assigned.UnionWith(members);
        groups.Add(new VersionGroup
        {
            Id = id,
            IdentityKey = identityKey,
            StubIds = members,
            LocalItemIds = local,
            PrimaryStubId = primary is not null && members.Contains(primary) ? primary : null
        });
    }

    // The key of a version group: the title for a film, and for an episode the episodes the file behind the stub covers,
    // which the dedup takes from its first and last number.
    private static string GroupKeyOf(ResolvedContent content, IndexedItem winner, Guid serverId, ILookup<ResolvedItem, string> covered)
    {
        return content.Identity.Kind == ContentKind.Episode
            ? string.Join('\n', covered[new ResolvedItem(serverId, winner.ItemId)].Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            : content.Identity.Key;
    }

    private static (int First, int Last) FileRangeOf(ResolvedContent content, IndexedItem winner)
    {
        var first = winner.IndexNumber ?? content.Identity.EpisodeNumber!.Value;
        return (first, winner.IndexNumberEnd is { } end && end > first ? end : first);
    }

    // The local copies of a stub. For episodes, only a local file that covers each episode of the file behind the stub and
    // no other episode of the season in the view. The view holds no episode that is only local, so a longer local file can
    // still pass.
    private static List<Guid> LocalCopiesOf(ILookup<(string WorkKey, int? SeasonNumber), ResolvedContent> seasons, ResolvedContent content, IndexedItem winner)
    {
        if (content.Identity.Kind != ContentKind.Episode)
        {
            return content.LocalItemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        }

        var (first, last) = FileRangeOf(content, winner);
        var season = seasons[(content.Identity.WorkKey, content.Identity.SeasonNumber)].ToList();
        var inside = season.Where(entry => entry.Identity.EpisodeNumber >= first && entry.Identity.EpisodeNumber <= last).ToList();
        var outside = season.Except(inside).SelectMany(entry => entry.LocalItemIds).ToHashSet();

        // Only the contents of the stub's own remote items: another copy of the same episode, matched by its item alone,
        // has its own stub.
        var episodes = inside
            .Where(entry => entry.RemoteItems.Intersect(content.RemoteItems).Any())
            .GroupBy(entry => entry.Identity.EpisodeNumber)
            .Select(episode => episode.SelectMany(entry => entry.LocalItemIds))
            .ToList();
        if (episodes.Count != last - first + 1)
        {
            return [];
        }

        return episodes
            .Aggregate((common, next) => common.Intersect(next))
            .Where(id => id != Guid.Empty && !outside.Contains(id))
            .Distinct()
            .ToList();
    }

    // Sorts the unclaimed stubs into removals, kept stubs, and stubs whose servers are all unconfigured, which it counts.
    private static int Sweep(
        StubPlanInput input,
        Dictionary<Guid, RemoteServerInfo> servers,
        HashSet<Guid> removed,
        List<StubEntry> unclaimed,
        Dictionary<(Guid ServerId, string LibraryId), int> held,
        List<StubEntry> removals)
    {
        var unconfigured = 0;
        foreach (var stub in unclaimed)
        {
            // Every server missing and not all removed by the administrator: the configuration may be back, so nothing goes.
            if (stub.Sources.Count > 0 && stub.Sources.All(source => !servers.ContainsKey(source.ServerId)))
            {
                if (stub.Sources.All(source => removed.Contains(source.ServerId)))
                {
                    removals.Add(stub);
                }
                else
                {
                    unconfigured++;
                }
            }
            else if (IsGone(input, servers, stub, held))
            {
                removals.Add(stub);
            }
        }

        return unconfigured;
    }

    // The stored labels still taken: those of configured servers, and of servers left out of the settings without a
    // removal that still have stubs. A removed server frees its label (decision 408).
    private static Dictionary<Guid, string> KeptLabels(StubPlanInput input)
    {
        var configured = input.Servers.Select(server => server.Id).ToHashSet();
        var removed = input.State.RemovedServers.ToHashSet();
        var withStubs = input.State.Stubs.Select(stub => stub.ServerId).Concat(input.State.Series.Select(series => series.ServerId)).OfType<Guid>().ToHashSet();
        return input.State.Labels
            .Where(entry => configured.Contains(entry.Key) || (!removed.Contains(entry.Key) && withStubs.Contains(entry.Key)))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private static List<HeldLibrary> HeldLibraries(StubPlanInput input, Dictionary<(Guid ServerId, string LibraryId), int> held)
    {
        var names = input.Servers.ToDictionary(server => server.Id, server => server.Name);
        return held
            .Select(entry => new HeldLibrary(entry.Key.ServerId, names.GetValueOrDefault(entry.Key.ServerId, string.Empty), entry.Key.LibraryId, entry.Value))
            .OrderBy(library => library.ServerName, StringComparer.Ordinal)
            .ToList();
    }

    // The contents as one server sees them: its items only, its best copy as the winner and its other copies as backups,
    // whether or not a local copy exists.
    private static List<ResolvedContent> ServerContents(FederationView view, IndexSnapshot snapshot, IndexItemLookup catalog, RemoteServerInfo server)
    {
        if (snapshot.Find(server.Id) is not { } serverCatalog)
        {
            return [];
        }

        var items = CatalogItemMapper.ToCatalogItems(serverCatalog).ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var contents = new List<ResolvedContent>();
        foreach (var content in view.Contents)
        {
            var own = content.RemoteItems.Where(item => item.ServerId == server.Id).ToList();
            var ranked = QualityRanking.Rank(own.SelectMany(item => items.TryGetValue(item.ItemId, out var entry) ? entry.ToSources() : []))
                .Select(source => new ResolvedSource(
                    server.Id,
                    server.Name,
                    serverCatalog.State,
                    source.ItemId,
                    source.MediaSourceId,
                    catalog.Find(server.Id, source.ItemId)?.Name,
                    source.Quality,
                    catalog.Find(server.Id, source.ItemId)?.LastSeenUtc ?? DateTimeOffset.MinValue))
                .ToList();
            if (ranked.Count > 0)
            {
                contents.Add(content with { IsLocal = false, Winner = ranked[0], Backups = ranked.Skip(1).ToList(), RemoteItems = own });
            }
        }

        return contents;
    }

    // Only for a stub with a configured server: one whose servers are all missing is decided by the caller.
    // A damaged index, a server not read in full or an empty imported library keeps the stub (EmptyLibraryHold).
    private static bool IsGone(StubPlanInput input, Dictionary<Guid, RemoteServerInfo> servers, StubEntry stub, Dictionary<(Guid ServerId, string LibraryId), int> held)
    {
        if (EmptyLibraryHold.Of(input.Snapshot, servers, stub.Sources) is not { } libraries)
        {
            return false;
        }

        foreach (var library in libraries)
        {
            held[library] = held.GetValueOrDefault(library) + 1;
        }

        return libraries.Count == 0;
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

    private static StubWork MovieWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, IndexItemLookup catalog, Guid? serverId, Func<string, string> newPath)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? newPath(id);
        var folder = Path.GetDirectoryName(relative)!;

        var entry = (stub ?? NewEntry(id, StubKind.Movie, relative)) with
        {
            ServerId = serverId,
            IdentityKey = content.Identity.Key,
            Sources = SourcesOf(content, catalog)
        };

        var metadata = StubMetadata.From(winner, input.IsProtected, input.WritesCollections);
        var nfo = NfoDocument.Movie(metadata);
        var url = input.StrmUrl(id);
        var images = new List<StubImage>();
        var slots = _titleImages.Select(image => (image.Type, Path.Combine(folder, image.Stem))).ToList();
        foreach (var (type, stem) in slots)
        {
            AddRemote(images, winner, content.Winner!.ServerId, type, stem);
        }

        return new StubWork(entry, stub is null, metadata, nfo, Path.Combine(folder, "movie.nfo"), url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner))
        {
            RemovedImages = Removed(entry.ImageTag, slots, images)
        };
    }

    private static StubWork EpisodeWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, SeriesPlan series, IndexItemLookup catalog, Guid? serverId, string? label)
    {
        var season = content.Identity.SeasonNumber!.Value;
        var number = content.Identity.EpisodeNumber!.Value;
        var last = content.EpisodeNumberEnd;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? FederatedPaths.EpisodeStub(series.Entry.RelativePath, series.FileName, season, number, last, label);
        var stem = Path.ChangeExtension(relative, null);

        // A stub that stays in the folder of another season number gets no season files there.
        var folder = Path.GetDirectoryName(relative)!;
        if (string.Equals(Path.GetFileName(folder), FederatedPaths.SeasonFolder(season), StringComparison.Ordinal))
        {
            series.Episodes.Add(new SeasonEpisode(folder, season, content.Winner!.ServerId, winner));
        }

        var entry = (stub ?? NewEntry(id, StubKind.Episode, relative)) with
        {
            ServerId = serverId,
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
        return new StubWork(entry, stub is null, metadata, nfo, stem + ".nfo", url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner))
        {
            RemovedImages = Removed(entry.ImageTag, [(ImageType.Primary, stem + "-thumb")], images)
        };
    }

    private static SeriesPlan SeriesPlanOf(
        StubPlanInput input,
        ResolvedContent content,
        IndexedItem winner,
        IndexItemLookup catalog,
        SeriesScope scope,
        Dictionary<string, SeriesPlan> plans)
    {
        var workKey = content.Identity.WorkKey;
        if (plans.TryGetValue(workKey, out var plan))
        {
            return plan;
        }

        var known = SeriesItems(scope.Contents, workKey, catalog);
        var stub = scope.Existing.FirstOrDefault(entry => string.Equals(entry.WorkKey, workKey, StringComparison.Ordinal))
            ?? scope.Existing.FirstOrDefault(entry => entry.Sources.Any(source => known.Exists(item => item.ServerId == source.ServerId && item.Item.ItemId == source.ItemId)));

        // Stay with the remote series already in use, so that the metadata does not flip between servers.
        var source = known.Find(item => stub?.Sources.Any(entry => entry.ServerId == item.ServerId && entry.ItemId == item.Item.ItemId) == true);
        if (source.Item is null && known.Count > 0)
        {
            source = known[0];
        }

        var local = scope.LocalSeriesOf(workKey);
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
        var relative = stub?.RelativePath ?? scope.NewFolder(name, metadata.Year, id);

        var nfo = NfoDocument.Series(metadata);
        var images = new List<StubImage>();
        var imageTag = SeriesImages(images, relative, local, remote, source.ServerId);
        var entry = (stub ?? new SeriesStub { Id = id, RelativePath = relative, WorkKey = workKey, NeedsRefresh = true }) with
        {
            ServerId = scope.ServerId,
            WorkKey = workKey,
            Sources = known.Select(item => new StubSource(item.ServerId, item.Item.ItemId, string.Empty)).ToList()
        };

        // Without the remote row nothing says an image was removed.
        var work = new SeriesWork(entry, stub is null, metadata, nfo, Path.Combine(relative, "tvshow.nfo"), Hash(nfo), images, imageTag)
        {
            RemovedImages = source.Item is null ? [] : Removed(entry.ImageTag, _titleImages.Select(image => (image.Type, Path.Combine(relative, image.Stem))), images)
        };
        plan = new SeriesPlan(entry, name ?? string.Empty, work, source.Item is null ? null : (source.ServerId, source.Item.ItemId), local is not null);
        plans[workKey] = plan;
        return plan;
    }

    // One season work per season folder that gets episode work. A season that has none now is forgotten, so that it is
    // written again if it comes back. A series merged with a local one shows its seasons, so it gets none (Q248).
    private static SeriesWork WithSeasons(StubPlanInput input, SeriesPlan plan, IndexItemLookup catalog)
    {
        if (plan.HasLocal)
        {
            return plan.Work with { Seasons = [] };
        }

        var written = new Dictionary<string, SeasonStub>(StringComparer.Ordinal);
        foreach (var season in plan.Entry.Seasons)
        {
            written.TryAdd(season.RelativePath, season);
        }

        var seasons = new List<SeasonWork>();
        foreach (var folder in plan.Episodes.GroupBy(episode => episode.Folder, StringComparer.Ordinal).OrderBy(folder => folder.Key, StringComparer.Ordinal))
        {
            var number = folder.First().Season;
            if (SeasonOf(plan, folder, number, catalog) is not { } found)
            {
                continue;
            }

            var metadata = new StubMetadata
            {
                IsProtected = input.IsProtected,
                Title = found.Season.Name,
                Overview = input.IsProtected ? found.Season.Overview : null,
                SeasonNumber = number
            };
            var nfo = NfoDocument.Season(metadata);
            var images = new List<StubImage>();
            var poster = Path.Combine(folder.Key, "poster");
            AddRemote(images, found.Season, found.ServerId, ImageType.Primary, poster);
            var entry = written.GetValueOrDefault(folder.Key) ?? new SeasonStub { RelativePath = folder.Key };
            seasons.Add(new SeasonWork(entry, metadata, nfo, Path.Combine(folder.Key, "season.nfo"), Hash(nfo), images, ImageTagOf(found.Season, images))
            {
                RemovedImages = Removed(entry.ImageTag, [(ImageType.Primary, poster)], images)
            });
        }

        return plan.Work with { Seasons = seasons };
    }

    // The remote season of a folder: the one of the series the metadata comes from, else the season its episodes name,
    // those of the same server first. Null if the index has none.
    private static (Guid ServerId, IndexedItem Season)? SeasonOf(SeriesPlan plan, IEnumerable<SeasonEpisode> episodes, int number, IndexItemLookup catalog)
    {
        if (plan.Source is { } source && catalog.FindSeason(source.ServerId, source.ItemId, number) is { } season)
        {
            return (source.ServerId, season);
        }

        return episodes
            .OrderBy(episode => episode.ServerId == plan.Source?.ServerId ? 0 : 1)
            .ThenBy(episode => episode.Episode.IndexNumber)
            .ThenBy(episode => episode.ServerId)
            .ThenBy(episode => episode.Episode.ItemId, StringComparer.Ordinal)
            .Select(episode => (episode.ServerId, Season: episode.Episode.SeasonId is { } id ? catalog.Find(episode.ServerId, id) : null))
            .Where(entry => entry.Season is { Kind: IndexedItemKind.Season } found && found.IndexNumber == number)
            .Select(entry => ((Guid, IndexedItem)?)(entry.ServerId, entry.Season!))
            .FirstOrDefault();
    }

    // Every remote row of the series, in a fixed order, so that a first choice does not depend on the order of a read.
    private static List<(Guid ServerId, IndexedItem Item)> SeriesItems(IReadOnlyList<ResolvedContent> contents, string workKey, IndexItemLookup catalog)
    {
        return contents
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

        // A series merged with a local one shows its images (decision 3), so these come only without one.
        foreach (var (type, stem) in local is null ? _titleImages.Skip(2) : [])
        {
            Add(type, stem, null);
        }

        return tag.ToString();
    }

    private static void AddRemote(List<StubImage> images, IndexedItem item, Guid serverId, ImageType type, string stem)
    {
        if (TagOf(item, type) is not null)
        {
            images.Add(new StubImage(type, stem, serverId, item.ItemId, null));
        }
    }

    // The images an earlier tag lists, hence written by the plugin, whose type the remote row no longer has (Q235).
    private static List<StubImage> Removed(string? writtenTag, IEnumerable<(ImageType Type, string Stem)> slots, List<StubImage> images)
    {
        if (string.IsNullOrEmpty(writtenTag))
        {
            return [];
        }

        var written = writtenTag.Split(';').Select(part => part.Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);
        return slots
            .Where(slot => written.Contains(slot.Type.ToString()) && images.TrueForAll(image => image.Type != slot.Type))
            .Select(slot => new StubImage(slot.Type, slot.Stem, null, null, null))
            .ToList();
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

    // The remote series the metadata comes from, if the index has it, whether a local series is merged with it, and the
    // episodes placed in its season folders.
    private sealed record SeriesPlan(SeriesStub Entry, string FileName, SeriesWork Work, (Guid ServerId, string ItemId)? Source, bool HasLocal)
    {
        public List<SeasonEpisode> Episodes { get; } = [];
    }

    private sealed record SeasonEpisode(string Folder, int Season, Guid ServerId, IndexedItem Episode);

    // Finds the first group of the last plan, in its order, that holds one of some stubs or local copies and is not taken.
    private sealed class OldGroupIndex(IReadOnlyList<VersionGroup> groups, ILookup<string, int> byStub, ILookup<Guid, int> byLocal)
    {
        public static OldGroupIndex Of(IReadOnlyList<VersionGroup> groups) => new(
            groups,
            groups.SelectMany((group, order) => group.StubIds.Select(stub => (stub, order))).ToLookup(entry => entry.stub, entry => entry.order, StringComparer.Ordinal),
            groups.SelectMany((group, order) => group.LocalItemIds.Select(local => (local, order))).ToLookup(entry => entry.local, entry => entry.order));

        public VersionGroup? FirstHolding(IEnumerable<string> stubIds, IEnumerable<Guid> local, HashSet<string> taken)
        {
            var order = stubIds.SelectMany(stub => byStub[stub])
                .Concat(local.SelectMany(id => byLocal[id]))
                .Where(index => !taken.Contains(groups[index].Id))
                .DefaultIfEmpty(-1)
                .Min();
            return order < 0 ? null : groups[order];
        }
    }

    /// <summary>
    /// Where the series of a plan live: the flat layout, or the folder of one server, whose series come from that server
    /// alone.
    /// </summary>
    /// <param name="ServerId">The server, null in the flat layout.</param>
    /// <param name="Existing">The series stubs of the scope.</param>
    /// <param name="Contents">The contents whose remote items name the series.</param>
    /// <param name="NewFolder">Gives the relative folder of a new series from its title, year and ID.</param>
    /// <param name="LocalSeriesOf">Finds the local series of a work key.</param>
    private sealed record SeriesScope(
        Guid? ServerId,
        IReadOnlyList<SeriesStub> Existing,
        IReadOnlyList<ResolvedContent> Contents,
        Func<string?, int?, string, string> NewFolder,
        Func<string, LocalSeries?> LocalSeriesOf);

    /// <summary>
    /// Keeps folder names unique. Two different titles can reach the same name, with a different year missing or after
    /// sanitizing, and they must not share a folder. Paths of stubs that exist never change.
    /// </summary>
    private sealed class PathAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

        private readonly Func<string, string> _movieFolder;
        private readonly Func<string, string> _seriesFolder;

        public PathAllocator(IEnumerable<StubEntry> stubs, IEnumerable<SeriesStub> series, Func<string, string> movieFolder, Func<string, string> seriesFolder)
        {
            _movieFolder = movieFolder;
            _seriesFolder = seriesFolder;
            foreach (var stub in stubs.Where(stub => stub.Kind == StubKind.Movie))
            {
                _taken.Add(Path.GetDirectoryName(stub.RelativePath)!);
            }

            foreach (var entry in series)
            {
                _taken.Add(entry.RelativePath);
            }
        }

        // The folder name of a film, unique in its scope.
        public string MovieFolder(string? title, int? year, string id)
            => Unique(FederatedPaths.TitleFolder(title, year), id, _movieFolder);

        // The relative folder of a series, unique in its scope.
        public string SeriesFolder(string? title, int? year, string id)
            => _seriesFolder(Unique(FederatedPaths.TitleFolder(title, year), id, _seriesFolder));

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
