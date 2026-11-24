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
public static partial class StubPlanner
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
        input = WithLostSeries(input, catalog, stub => stub.ServerId is null);

        // Stubs of the server folders belong to PlanByServer alone.
        var flat = input.State.Stubs.Where(stub => stub.ServerId is null).ToList();
        var flatSeries = input.State.Series.Where(series => series.ServerId is null).ToList();
        var matcher = new StubMatcher(flat);

        // Music videos exist only in the server folders.
        var claims = matcher.ClaimAll(StubContents.Group(input.View.Contents.Where(content => content.Identity.Kind != ContentKind.MusicVideo).ToList()));

        var paths = new PathAllocator(flat, flatSeries, input.IgnoreRules);
        var scope = new SeriesScope(null, flatSeries, ByWork(input.View.Contents), (_, _, title, year, id) => paths.SeriesFolder(TypeFolder.Series.Name, title, year, id), input.LocalSeriesOf);
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
                works.Add(MovieWork(input, content, stub, winner, catalog, null, StubKind.Movie, id => FederatedPaths.MovieStub(paths.MovieFolderName(TypeFolder.Movies.Name, winner.Name, winner.ProductionYear, id))));
            }
            else if (content.Identity.SeasonNumber is null || content.Identity.EpisodeNumber is null)
            {
                unplaceable++;
            }
            else
            {
                var seriesPlan = SeriesPlanOf(input, content, winner, catalog, scope, seriesPlans);
                works.Add(EpisodeWork(input, content, stub, winner, seriesPlan, catalog, paths, null, null));
            }
        }

        var seriesWorks = SeriesWorks(input, seriesPlans.Values, catalog);
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
    /// and its local copy, form a version group; episodes only with the same range. Stubs of the older layouts are left as
    /// they are. Pure, like <see cref="Plan"/>.
    /// </summary>
    /// <param name="input">What is known.</param>
    /// <returns>The plan.</returns>
    public static StubPlan PlanByServer(StubPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var catalog = IndexItemLookup.Build(input.Snapshot);
        var labels = FederatedPaths.Labels(input.Servers.Select(server => (server.Id, (string?)server.Name)), input.State.TakenLabels(input.Servers.Select(server => server.Id)));
        var works = new List<StubWork>();
        var placed = new List<(StubWork Work, ResolvedContent Content, IndexedItem Winner)>();
        var seriesWorks = new List<SeriesWork>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var merged = new HashSet<string>(StringComparer.Ordinal);
        var unplaceable = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<StubWork>();
        var videoWorks = new List<StubWork>();
        var books = new List<StubWork>();

        // Stubs of an older layout are left as they are, out of every part, until the migration empties them (§10 row 444).
        var legacy = input.KeepsOlderLayout;
        input = WithLostSeries(input, catalog, stub => stub.ServerId is not null && (legacy || IsCurrent(stub.RelativePath)));
        foreach (var server in input.Servers)
        {
            // Music videos and home videos exist only in the new layout: an older one keeps them as they are.
            var contents = ServerContents(input.View, input.Snapshot, catalog, server).Where(content => !legacy || content.Identity.Kind != ContentKind.MusicVideo).ToList();
            var own = input.State.Stubs.Where(stub => stub.ServerId == server.Id && stub.Kind is not (StubKind.Track or StubKind.Book) && !IsUntitledVideo(stub.Kind, legacy) && (legacy || IsCurrent(stub.RelativePath))).ToList();
            var ownBooks = input.State.Stubs.Where(stub => stub.ServerId == server.Id && stub.Kind == StubKind.Book).ToList();
            var ownVideos = input.State.Stubs.Where(stub => stub.ServerId == server.Id && IsUntitledVideo(stub.Kind, legacy)).ToList();
            var ownSeries = input.State.Series.Where(series => series.ServerId == server.Id && (legacy || IsCurrent(series.RelativePath))).ToList();
            var label = labels[server.Id];
            var parts = legacy
                ? [ServerPart.Legacy(server.Id, contents, own, ownSeries)]
                : PartsOf(input.PlacementOf, catalog, server.Id, contents, own, ownSeries, unplaceable);
            foreach (var part in parts)
            {
                var matcher = new StubMatcher(part.Stubs);
                var claims = matcher.ClaimAll(StubContents.Group(part.Contents));

                var paths = new PathAllocator(part.Stubs, part.Series, input.IgnoreRules);
                var scope = new SeriesScope(server.Id, part.Series, ByWork(part.Contents), (remote, winner, title, year, id) => paths.SeriesFolder(part.FolderOf([remote, winner], TypeFolder.Series), title, year, id), _ => null);
                var seriesPlans = new Dictionary<string, SeriesPlan>(StringComparer.Ordinal);
                foreach (var (content, stub) in claims)
                {
                    if (catalog.Find(server.Id, content.Winner!.ItemId) is not { } winner)
                    {
                        continue;
                    }

                    if (content.Identity.Kind is ContentKind.Movie or ContentKind.MusicVideo)
                    {
                        var (kind, type) = content.Identity.Kind == ContentKind.Movie ? (StubKind.Movie, TypeFolder.Movies) : (StubKind.MusicVideo, TypeFolder.MusicVideos);
                        var folder = part.FolderOf([winner], type);
                        placed.Add((MovieWork(input, content, stub, winner, catalog, server.Id, kind, id => FederatedPaths.MovieStubIn(folder, paths.MovieFolderName(folder, winner.Name, winner.ProductionYear, id, label), label)), content, winner));
                    }
                    else if (content.Identity.SeasonNumber is null || content.Identity.EpisodeNumber is null)
                    {
                        unplaceable.Add(content.Identity.Key);
                    }
                    else
                    {
                        var seriesPlan = SeriesPlanOf(input, content, winner, catalog, scope, seriesPlans);
                        placed.Add((EpisodeWork(input, content, stub, winner, seriesPlan, catalog, paths, server.Id, label), content, winner));
                    }
                }

                seriesWorks.AddRange(SeriesWorks(input, seriesPlans.Values, catalog));
                works.AddRange(placed.Skip(works.Count).Select(entry => entry.Work));
                claimed.UnionWith(part.Stubs.Where(matcher.IsClaimed).Select(stub => stub.Id));
                merged.UnionWith(claims.SelectMany(claim => claim.Merged).Select(stub => stub.Id));
            }

            // Tracks come only in the new layout (Q49), from the index: the dedup has no track identity yet.
            if (!legacy)
            {
                var serverTracks = TrackStubPlanner.Plan(input, catalog, server.Id, input.State.Stubs.Where(stub => stub.ServerId == server.Id && stub.Kind == StubKind.Track).ToList());
                tracks.AddRange(serverTracks);
                claimed.UnionWith(serverTracks.Select(work => work.Entry.Id));
            }

            if (legacy)
            {
                claimed.UnionWith(ownVideos.Select(stub => stub.Id));
            }
            else
            {
                videoWorks.AddRange(VideoWorks(input, catalog, server, ownVideos, claimed, unplaceable));
            }

            books.AddRange(BookWorks(input, server.Id, ownBooks, legacy, claimed, unplaceable));
        }

        works.AddRange(tracks);
        works.AddRange(videoWorks);
        works.AddRange(books);
        var written = works.ToDictionary(work => work.Entry.Id, StringComparer.Ordinal);
        var unclaimed = input.State.Stubs.Where(stub => stub.ServerId is not null && (legacy || IsCurrent(stub.RelativePath)) && !claimed.Contains(stub.Id)).ToList();

        var held = new Dictionary<(Guid ServerId, string LibraryId), int>();
        var servers = input.Servers.ToDictionary(server => server.Id);
        var removed = input.State.RemovedServers.Where(server => !servers.ContainsKey(server)).ToHashSet();
        var removals = new List<StubEntry>();
        var unconfigured = Sweep(input, servers, removed, unclaimed, held, removals);

        // Stubs of the older layouts, merged stubs and those still in doubt stay as they are.
        var stubs = input.State.Stubs
            .Where(stub => !written.ContainsKey(stub.Id))
            .Concat(works.Select(work => work.Entry))
            .ToList();
        var series = input.State.Series
            .Where(entry => seriesWorks.TrueForAll(work => work.Entry.Id != entry.Id))
            .Concat(seriesWorks.Select(work => work.Entry))
            .ToList();

        var marks = input.State.RemovedServers.Where(removed.Contains).ToList();
        var groups = Groups(input, placed, stubs.Where(stub => legacy || IsCurrent(stub.RelativePath)).ToList(), removals.Select(stub => stub.Id).Concat(merged));
        var state = (input.State with { Stubs = stubs, Series = series, RemovedServers = marks, Groups = groups, Labels = labels })
            .WithServerNames(input.Servers.Select(server => (server.Id, server.Name)));
        return new StubPlan(state, works, seriesWorks, removals, HeldLibraries(input, held), unplaceable.Count)
        {
            MergedStubs = merged.Count,
            Unconfigured = unconfigured
        };
    }

    /// <summary>
    /// Claims the stubs of each server folder against the contents as that server sees them, as <see cref="PlanByServer"/>
    /// does: a merged stub is always merged into a stub of its own server. Stubs of the older layouts are left out.
    /// </summary>
    /// <param name="view">The resolved catalog.</param>
    /// <param name="snapshot">The index the view was read from.</param>
    /// <param name="servers">The configured servers.</param>
    /// <param name="stubs">The stubs.</param>
    /// <returns>The claims of every server.</returns>
    /// <param name="placementOf">Where a type folder of a remote library goes, as in <see cref="StubPlanInput.PlacementOf"/>.</param>
    public static IReadOnlyList<StubClaim> ClaimByServer(
        FederationView view,
        IndexSnapshot snapshot,
        IReadOnlyList<RemoteServerInfo> servers,
        IReadOnlyList<StubEntry> stubs,
        Func<Guid, string, TypeFolder, string>? placementOf = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(stubs);

        var catalog = IndexItemLookup.Build(snapshot);
        var claims = new List<StubClaim>();
        foreach (var server in servers)
        {
            var contents = ServerContents(view, snapshot, catalog, server);
            var own = stubs.Where(stub => stub.ServerId == server.Id && IsCurrent(stub.RelativePath) && !IsUntitledVideo(stub.Kind, legacy: false)).ToList();
            var parts = PartsOf(placementOf ?? StubPlanInput.DefaultPlacement, catalog, server.Id, contents, own, [], []);
            claims.AddRange(parts.SelectMany(part => new StubMatcher(part.Stubs).ClaimAll(StubContents.Group(part.Contents))));
        }

        return claims;
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
            var stubIds = title.DistinctBy(entry => SlotOf(input, entry.Work.Entry)).Select(entry => entry.Work.Entry.Id).ToList();
            var local = LocalCopiesOf(seasons, content, winner);
            var old = oldGroups.FirstHolding(stubIds, local, reused);
            if (old is not null)
            {
                reused.Add(old.Id);
                var slots = title.Select(entry => SlotOf(input, entry.Work.Entry)).ToHashSet();
                stubIds.AddRange(old.StubIds.Where(id => kept.TryGetValue(id, out var stub) && slots.Add(SlotOf(input, stub))));
            }

            AddGroup(groups, assigned, old?.Id ?? NewId(), content.Identity.Key, stubIds, local, old?.PrimaryStubId);
        }

        foreach (var old in input.State.Groups.Where(group => !reused.Contains(group.Id)))
        {
            AddGroup(groups, assigned, old.Id, old.IdentityKey, old.StubIds.Where(kept.ContainsKey).ToList(), old.LocalItemIds, old.PrimaryStubId);
        }

        return groups;
    }

    // A version group holds one stub per server and local library: an older stub has the server alone.
    private static (Guid? ServerId, string Placement) SlotOf(StubPlanInput input, StubEntry stub)
        => (stub.ServerId, FederatedPaths.RemoteLibraryOf(stub.RelativePath) is { } library && stub.ServerId is { } server
            ? input.PlacementOf(server, library, TypeFolder.Of(stub.Kind))
            : string.Empty);

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

    // Splits the contents of a server by the local library their remote libraries go to, with the stubs already there. A
    // title in two remote libraries of one local library is one content, with the copies of both (D19).
    private static List<ServerPart> PartsOf(
        Func<Guid, string, TypeFolder, string> placementOf,
        IndexItemLookup catalog,
        Guid serverId,
        List<ResolvedContent> contents,
        List<StubEntry> stubs,
        List<SeriesStub> series,
        HashSet<string> unplaceable)
    {
        var split = new Dictionary<string, List<ResolvedContent>>(StringComparer.Ordinal);
        foreach (var content in contents)
        {
            var type = TypeFolder.Of(content.Identity.Kind);
            var byKey = new Dictionary<string, List<ResolvedSource>>(StringComparer.Ordinal);
            foreach (var source in content.Backups.Prepend(content.Winner!))
            {
                foreach (var key in LibrariesOf(catalog.Find(serverId, source.ItemId)).Select(library => placementOf(serverId, library, type)).Distinct(StringComparer.Ordinal))
                {
                    byKey.TryAdd(key, []);
                    byKey[key].Add(source);
                }
            }

            if (byKey.Count == 0)
            {
                unplaceable.Add(content.Identity.Key);
            }

            foreach (var (key, sources) in byKey)
            {
                split.TryAdd(key, []);
                split[key].Add(content with
                {
                    Winner = sources[0],
                    Backups = sources.Skip(1).ToList(),
                    RemoteItems = content.RemoteItems.Where(item => sources.Exists(source => source.ItemId == item.ItemId)).ToList()
                });
            }
        }

        string? KeyOf(string path, TypeFolder type) => FederatedPaths.RemoteLibraryOf(path) is { } library ? placementOf(serverId, library, type) : null;
        var stubKeys = stubs.Select(stub => KeyOf(stub.RelativePath, TypeFolder.Of(stub.Kind)));
        var seriesKeys = series.Select(entry => KeyOf(entry.RelativePath, TypeFolder.Series));
        return split.Keys.Concat(stubKeys).Concat(seriesKeys).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key => new ServerPart(
                split.GetValueOrDefault(key) ?? [],
                stubs.Where(stub => KeyOf(stub.RelativePath, TypeFolder.Of(stub.Kind)) == key).ToList(),
                series.Where(entry => KeyOf(entry.RelativePath, TypeFolder.Series) == key).ToList(),
                (items, type) => FederatedPaths.TypeFolderOf(serverId, items.SelectMany(LibrariesOf).First(library => placementOf(serverId, library, type) == key), type)))
            .ToList();
    }

    // Whether a stub or series path is in the remote library layout, not in an older one.
    private static bool IsCurrent(string relativePath) => FederatedPaths.RemoteLibraryOf(relativePath) is not null;

    // The remote libraries of an entry that can name a folder.
    private static IEnumerable<string> LibrariesOf(IndexedItem? item) => item?.LibraryIds.Where(FederatedPaths.IsLibraryId) ?? [];

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

    // A film, or a music video: one title in a folder of its own, its .nfo named after the stub (F52).
    private static StubWork MovieWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, IndexItemLookup catalog, Guid? serverId, StubKind kind, Func<string, string> newPath)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? newPath(id);
        var folder = Path.GetDirectoryName(relative)!;

        var entry = (stub ?? NewEntry(id, kind, relative)) with
        {
            ServerId = serverId,
            IdentityKey = content.Identity.Key,
            Sources = SourcesOf(content, catalog)
        };

        var isFilm = kind == StubKind.Movie;
        var metadata = isFilm ? StubMetadata.From(winner, input.IsProtected, input.WritesCollections) : StubMetadata.From(winner, input.IsProtected);
        var nfo = isFilm ? NfoDocument.Movie(metadata) : NfoDocument.MusicVideo(metadata);
        var url = input.StrmUrl(id);
        var images = new List<StubImage>();
        var slots = _titleImages.Select(image => (image.Type, Path.Combine(folder, image.Stem))).ToList();
        foreach (var (type, stem) in slots)
        {
            AddRemote(images, winner, content.Winner!.ServerId, type, stem);
        }

        var nfoPath = isFilm ? Path.Combine(folder, "movie.nfo") : Path.ChangeExtension(relative, ".nfo");
        return new StubWork(entry, stub is null, metadata, nfo, nfoPath, url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner))
        {
            RemovedImages = Removed(entry.ImageTag, slots, images)
        };
    }

    private static StubWork EpisodeWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, SeriesPlan series, IndexItemLookup catalog, PathAllocator paths, Guid? serverId, string? label)
    {
        var season = content.Identity.SeasonNumber!.Value;
        var number = content.Identity.EpisodeNumber!.Value;
        var last = content.EpisodeNumberEnd;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? paths.EpisodeStub(series, season, number, last, label, id);
        var stem = Path.ChangeExtension(relative, null);

        series.HasStubs |= stub?.SeriesStubId is null || string.Equals(stub.SeriesStubId, series.Entry.Id, StringComparison.Ordinal);

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

    // Gives each series folder that has none its row again, with the remote series of its stubs, and each stub the row of its
    // folder when its own is gone. A stub in no series folder, or of no known series, loses the row it names.
    private static StubPlanInput WithLostSeries(StubPlanInput input, IndexItemLookup catalog, Func<StubEntry, bool> isPlanned)
    {
        var rows = input.State.Series.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var lost = input.State.Stubs.Where(stub => stub.Kind == StubKind.Episode && !(stub.SeriesStubId is { } id && rows.Contains(id)) && isPlanned(stub)).ToList();
        if (lost.Count == 0)
        {
            return input;
        }

        var byFolder = input.State.Series
            .DistinctBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(entry => entry.RelativePath, entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var works = input.View.Contents.Where(content => content.Identity.Kind == ContentKind.Episode).ToLookup(content => content.Identity.Key, content => content.Identity.WorkKey, StringComparer.Ordinal);
        var keys = input.State.Series.Select(entry => (entry.ServerId, entry.WorkKey)).ToHashSet();
        var found = new List<SeriesStub>();
        foreach (var folder in lost.GroupBy(SeriesFolderOf, StringComparer.OrdinalIgnoreCase).Where(folder => !byFolder.ContainsKey(folder.Key) && IsSeriesFolder(folder.Key)))
        {
            var sources = folder.SelectMany(stub => stub.Sources)
                .Select(source => (source.ServerId, catalog.Find(source.ServerId, source.ItemId)?.SeriesId))
                .Where(source => source.SeriesId is not null)
                .Distinct()
                .Select(source => new StubSource(source.ServerId, source.SeriesId!, string.Empty))
                .ToList();

            // With sources no work has its key, so it goes to a work of its remote series. Else it takes a key no row has.
            var id = NewId();
            var server = folder.First().ServerId;
            var key = sources.Count > 0 ? id : folder.SelectMany(stub => works[stub.IdentityKey]).FirstOrDefault(work => keys.Add((server, work)));
            if (key is not null)
            {
                found.Add(new SeriesStub { Id = id, ServerId = server, RelativePath = folder.Key, WorkKey = key, Sources = sources, NeedsRefresh = true });
                byFolder.Add(folder.Key, id);
            }
        }

        var owners = lost.ToDictionary(stub => stub.Id, stub => byFolder.GetValueOrDefault(SeriesFolderOf(stub)), StringComparer.Ordinal);
        return input with
        {
            State = input.State with
            {
                Stubs = input.State.Stubs.Select(stub => owners.TryGetValue(stub.Id, out var owner) ? stub with { SeriesStubId = owner } : stub).ToList(),
                Series = [.. input.State.Series, .. found]
            }
        };
    }

    // The folder above the season folder of an episode stub.
    private static string SeriesFolderOf(StubEntry stub) => Path.GetDirectoryName(Path.GetDirectoryName(stub.RelativePath)) ?? string.Empty;

    private static bool IsSeriesFolder(string folder) => string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), TypeFolder.Series.Name, StringComparison.Ordinal);

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

        var known = SeriesItems(scope.Contents[workKey], catalog);
        var stub = scope.Claim(workKey, known);

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
            OtherLibraryIds = winner.OtherLibraryIds,
            LastSeenUtc = winner.LastSeenUtc,
            Name = winner.SeriesName
        };

        var metadata = SeriesMetadata(remote, local, input.IsProtected);
        var name = metadata.Title ?? remote.Name;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? scope.NewFolder(remote, winner, name, metadata.Year, id);

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

    // The work of each series. A new one whose stubs all stay in the folder of another series gets none.
    private static List<SeriesWork> SeriesWorks(StubPlanInput input, IReadOnlyCollection<SeriesPlan> plans, IndexItemLookup catalog)
    {
        var folders = plans.Select(plan => plan.Entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        return plans.Where(plan => plan.HasStubs || !plan.Work.IsNew).Select(plan => WithSeasons(input, plan, folders, catalog)).ToList();
    }

    // One season work per season folder that gets episode work, in no folder of another series of the plan. A season that has
    // none now is forgotten, so that it is written again. A series merged with a local one gets none (Q248).
    private static SeriesWork WithSeasons(StubPlanInput input, SeriesPlan plan, HashSet<string> seriesFolders, IndexItemLookup catalog)
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
            var series = Path.GetDirectoryName(folder.Key)!;
            if (!string.Equals(series, plan.Entry.RelativePath, StringComparison.Ordinal) && seriesFolders.Contains(series))
            {
                continue;
            }

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

    // Groups the contents by work key, so that each series reads only its own episodes.
    private static ILookup<string, ResolvedContent> ByWork(IEnumerable<ResolvedContent> contents)
        => contents.ToLookup(content => content.Identity.WorkKey, StringComparer.Ordinal);

    // Every remote row of the series, in a fixed order, so that a first choice does not depend on the order of a read.
    private static List<(Guid ServerId, IndexedItem Item)> SeriesItems(IEnumerable<ResolvedContent> contents, IndexItemLookup catalog)
    {
        return contents
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

        // Whether a stub has this series as its own.
        public bool HasStubs { get; set; }
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
    /// <param name="Contents">The contents whose remote items name the series, by work key.</param>
    /// <param name="NewFolder">Gives the relative folder of a new series from its remote row, the episode that brings it, its
    /// title, year and ID.</param>
    /// <param name="LocalSeriesOf">Finds the local series of a work key.</param>
    private sealed record SeriesScope(
        Guid? ServerId,
        IReadOnlyList<SeriesStub> Existing,
        ILookup<string, ResolvedContent> Contents,
        Func<IndexedItem, IndexedItem, string?, int?, string, string> NewFolder,
        Func<string, LocalSeries?> LocalSeriesOf)
    {
        private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

        // Finds the series stub of a work by its key, then by a remote series among those no other work has, written ones first.
        public SeriesStub? Claim(string workKey, List<(Guid ServerId, IndexedItem Item)> known)
        {
            var stub = Existing.FirstOrDefault(entry => string.Equals(entry.WorkKey, workKey, StringComparison.Ordinal))
                ?? Existing.Where(entry => !Contents.Contains(entry.WorkKey)
                    && !_claimed.Contains(entry.Id)
                    && entry.Sources.Any(source => known.Exists(item => item.ServerId == source.ServerId && item.Item.ItemId == source.ItemId)))
                    .MinBy(entry => entry.ContentHash is null);
            if (stub is not null)
            {
                _claimed.Add(stub.Id);
            }

            return stub;
        }
    }

    /// <summary>
    /// Keeps folder names and episode files unique. Two different titles can reach the same name, with a different year missing
    /// or after sanitizing, and they must not share a folder or a file. Paths of stubs that exist never change.
    /// </summary>
    private sealed class PathAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly IServerIgnoreRules _rules;

        public PathAllocator(IEnumerable<StubEntry> stubs, IEnumerable<SeriesStub> series, IServerIgnoreRules rules)
        {
            _rules = rules;
            foreach (var stub in stubs)
            {
                var hasFolder = stub.Kind is StubKind.Movie or StubKind.MusicVideo or StubKind.Book;
                _taken.Add(hasFolder ? Path.GetDirectoryName(stub.RelativePath)! : stub.RelativePath);
                if (stub.Kind == StubKind.Episode)
                {
                    _taken.Add(SeriesFolderOf(stub));
                }
            }

            foreach (var entry in series)
            {
                _taken.Add(entry.RelativePath);
            }
        }

        // The folder name of a film, unique in its type folder, with the id when Jellyfin would read its stub as an extra or skip it.
        public string MovieFolderName(string typeFolder, string? title, int? year, string id, string? label = null)
        {
            var name = FederatedPaths.TitleFolder(title, year);
            var folder = Path.Combine(typeFolder, name);
            var stub = label is null ? Path.Combine(folder, name + ".strm") : FederatedPaths.MovieStubIn(typeFolder, name, label);
            var hidden = IsReadAsExtra(stub) || _rules.Ignores(typeFolder, name, true) || _rules.Ignores(folder, Path.GetFileName(stub), false);
            return Unique(typeFolder, name, id, hidden);
        }

        // The relative folder of a series, unique in its type folder, with the id when the server would skip it.
        public string SeriesFolder(string typeFolder, string? title, int? year, string id)
        {
            var name = FederatedPaths.TitleFolder(title, year);
            return Path.Combine(typeFolder, Unique(typeFolder, name, id, _rules.Ignores(typeFolder, name, true)));
        }

        // The path of an episode stub, unique in its series folder.
        public string EpisodeStub(SeriesPlan series, int season, int number, int? last, string? label, string id)
        {
            var path = FederatedPaths.EpisodeStub(series.Entry.RelativePath, series.FileName, season, number, last, label);
            if (_taken.Add(path))
            {
                return path;
            }

            var unique = FederatedPaths.EpisodeStub(series.Entry.RelativePath, series.FileName, season, number, last, label, id);
            _taken.Add(unique);
            return unique;
        }

        private string Unique(string typeFolder, string name, string id, bool hidden = false)
        {
            if (!hidden && _taken.Add(Path.Combine(typeFolder, name)))
            {
                return name;
            }

            var unique = string.Create(CultureInfo.InvariantCulture, $"{name} [{id[..8]}]");
            _taken.Add(Path.Combine(typeFolder, unique));
            return unique;
        }
    }

    /// <summary>
    /// The stubs of one server that share a local library, with the contents they are planned from. While the folder keeps the
    /// 0.5 layout a server is one part, in its 0.5 folder.
    /// </summary>
    /// <param name="Contents">The contents, with only the copies in the type folders of the part.</param>
    /// <param name="Stubs">The stubs of the part.</param>
    /// <param name="Series">The series stubs of the part.</param>
    /// <param name="TypeFolderOf">Gives the relative type folder of a new title from the remote rows it comes from.</param>
    private sealed record ServerPart(
        List<ResolvedContent> Contents,
        List<StubEntry> Stubs,
        List<SeriesStub> Series,
        Func<IReadOnlyList<IndexedItem>, TypeFolder, string> TypeFolderOf)
    {
        public static ServerPart Legacy(Guid serverId, List<ResolvedContent> contents, List<StubEntry> stubs, List<SeriesStub> series)
            => new(contents, stubs, series, (_, type) => Path.Combine(FederatedPaths.ServerFolder(serverId), type.Name));

        public string FolderOf(IReadOnlyList<IndexedItem> items, TypeFolder type) => TypeFolderOf(items, type);
    }
}
