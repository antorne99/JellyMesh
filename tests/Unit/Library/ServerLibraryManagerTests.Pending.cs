using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// A library is made only after its intent is saved, so a crash before its record is recovered exactly and nothing else is.
public sealed partial class ServerLibraryManagerTests
{
    [Fact]
    public async Task An_admin_library_with_the_name_and_folder_the_plugin_would_give_is_adopted_not_taken_as_made()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Add("Casa - Serie", CollectionTypeOptions.tvshows, Absolute(_showsFolder));
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.Shows);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var series = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Kind == ServerLibraryKind.Series);
        Assert.Equal(("Casa - Serie", true), (series.Name, series.Adopted));
        Assert.Single(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task A_library_made_before_a_crash_is_recorded_from_its_intent_without_a_second_one()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.OnCreate = () => fixture.Hooks.BeforeUpdate = () => throw new InvalidOperationException("crash");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.OnCreate = null;
        var made = fixture.Libraries.Created.Single().Name;
        Assert.Single(fixture.Server.PendingLibraries);

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == made);
        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count);
        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task A_library_made_again_at_a_switch_on_before_a_crash_is_recorded_though_its_key_counts_as_made()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Serie"));
        fixture.Libraries.OnCreate = () => fixture.Hooks.BeforeUpdate = () => throw new InvalidOperationException("crash");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.OnCreate = null;

        var manager = fixture.NewManager();
        await manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(3, fixture.Libraries.Created.Count);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.Empty(fixture.Server.PendingLibraries);
        Assert.NotNull(manager.FollowForPlacement());
    }

    [Fact]
    public async Task An_intent_whose_library_was_never_made_is_dropped_and_the_library_is_made()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.CreateFailure = new InvalidOperationException("crash");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        Assert.Empty(fixture.Server.PendingLibraries);
        fixture.Libraries.CreateFailure = null;
        var pending = new PendingLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film" };
        pending.Paths.Add(Absolute(_filmsFolder));
        fixture.Store.Update(config => config.Servers[0].PendingLibraries.Add(pending));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task An_intent_is_not_a_claim_on_a_library_with_other_folders()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.OnCreate = () => fixture.Hooks.BeforeUpdate = () => throw new InvalidOperationException("crash");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.OnCreate = null;
        var made = fixture.Libraries.Find(fixture.Libraries.Created.Single().Name);
        Assert.Equal(2, made.Locations.Length);
        made.Locations = [made.Locations[0]];

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.DoesNotContain(fixture.Server.AutomaticLibraryRegistry, record => record.Name == made.Name);
        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task A_library_made_before_a_crash_is_recorded_at_the_next_switch_on_and_not_kept_as_the_admin_s()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.OnCreate = () => fixture.Hooks.BeforeUpdate = () => throw new InvalidOperationException("crash");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.OnCreate = null;
        var made = fixture.Libraries.Find(fixture.Libraries.Created.Single().Name);

        await fixture.NewManager().Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Name == made.Name);
        Assert.DoesNotContain(made.ItemId, fixture.Server.PreexistingLibraryIds);
        Assert.Empty(fixture.Server.PendingLibraries);
    }
}
