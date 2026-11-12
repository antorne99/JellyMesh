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

        var seriesWorks = seriesPlans.Values.Select(plan => plan.Work).ToList();
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
        var labels = FederatedPaths.Labels(input.Servers.Select(server => (server.Id, (string?)server.Name)), input.State.Labels);
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

            seriesWorks.AddRange(seriesPlans.Values.Select(plan => plan.Work));
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
        foreach (var title in titles)
        {
            var (_, content, winner) = title.First();
            var stubIds = title.DistinctBy(entry => entry.Work.Entry.ServerId).Select(entry => entry.Work.Entry.Id).ToList();
            var local = LocalCopiesOf(input, content, winner);
            var old = input.State.Groups.FirstOrDefault(group => !reused.Contains(group.Id) && (group.StubIds.Any(stubIds.Contains) || group.LocalItemIds.Any(local.Contains)));
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
    private static List<Guid> LocalCopiesOf(StubPlanInput input, ResolvedContent content, IndexedItem winner)
    {
        if (content.Identity.Kind != ContentKind.Episode)
        {
            return content.LocalItemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        }

        var (first, last) = FileRangeOf(content, winner);
        var season = input.View.Contents
            .Where(entry => string.Equals(entry.Identity.WorkKey, content.Identity.WorkKey, StringComparison.Ordinal) && entry.Identity.SeasonNumber == content.Identity.SeasonNumber)
            .ToList();
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

    // Only for a stub with a configured server: one whose servers are all missing is decided by the caller.
    private static bool IsGone(StubPlanInput input, Dictionary<Guid, RemoteServerInfo> servers, StubEntry stub, Dictionary<(Guid ServerId, string LibraryId), int> held)
    {
        // A damaged index says nothing, and a server missing from it is not proof either.
        if (input.Snapshot.Health != IndexHealth.Ok)
        {
            return false;
        }

        var emptyLibraries = new List<(Guid ServerId, string LibraryId)>();
        foreach (var source in stub.Sources)
        {
            // A server missing from the configuration can serve nothing: the configured ones decide.
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
        AddRemote(images, winner, content.Winner!.ServerId, ImageType.Primary, Path.Combine(folder, "poster"));
        AddRemote(images, winner, content.Winner.ServerId, ImageType.Backdrop, Path.Combine(folder, "fanart"));
        return new StubWork(entry, stub is null, metadata, nfo, Path.Combine(folder, "movie.nfo"), url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner));
    }

    private static StubWork EpisodeWork(StubPlanInput input, ResolvedContent content, StubEntry? stub, IndexedItem winner, SeriesPlan series, IndexItemLookup catalog, Guid? serverId, string? label)
    {
        var season = content.Identity.SeasonNumber!.Value;
        var number = content.Identity.EpisodeNumber!.Value;
        var last = content.EpisodeNumberEnd;
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? FederatedPaths.EpisodeStub(series.Entry.RelativePath, series.FileName, season, number, last, label);
        var stem = Path.ChangeExtension(relative, null);

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
        return new StubWork(entry, stub is null, metadata, nfo, stem + ".nfo", url, Hash(nfo, Unsigned(url)), images, ImageTagOf(winner, images), TechnicalTagOf(content, winner));
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

        plan = new SeriesPlan(entry, name ?? string.Empty, new SeriesWork(entry, stub is null, metadata, nfo, Path.Combine(relative, "tvshow.nfo"), Hash(nfo), images, imageTag));
        plans[workKey] = plan;
        return plan;
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
