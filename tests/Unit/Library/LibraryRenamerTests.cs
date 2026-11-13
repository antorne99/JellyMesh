using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LibraryRenamerTests : IDisposable
{
    private static readonly JsonSerializerOptions _journalOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jm-renamer-" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "jm-renamer-data-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IServerApplicationPaths> _paths = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ILibraryMonitor> _monitor = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly FakeViewIds _views = new();
    private readonly RetiredLibraryIds _retired = new();
    private readonly Mock<ILibraryChangeTracker> _changes = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly User _limited = new("limited", "auth", "reset");
    private readonly User _blocked = new("blocked", "auth", "reset");
    private readonly User _other = new("other", "auth", "reset");
    private LibraryRenamer _renamer;
    private Func<string, string, bool> _moveFails = (_, _) => false;

    public LibraryRenamerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Casa - Film"));
        _paths.SetupGet(paths => paths.DefaultUserViewsPath).Returns(_root);
        _library.Setup(library => library.GetNewItemId(It.IsAny<string>(), typeof(CollectionFolder))).Returns((string key, Type _) => IdOf(key));
        _users.Setup(users => users.GetUsers()).Returns(() => [_limited, _blocked, _other]);
        _renamer = NewRenamer();
    }

    private Guid OldId => IdOf(Path.Combine(_root, "Casa - Film"));

    private Guid NewId => IdOf(Path.Combine(_root, "Mare - Film"));

    public void Dispose()
    {
        Directory.Delete(_root, true);
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    // A renamer over a journal read again from its file, as after a restart.
    private LibraryRenamer NewRenamer() => new(
        _paths.Object,
        _library.Object,
        _monitor.Object,
        _users.Object,
        _views,
        _retired,
        new LibraryRenameJournal(_data, NullLogger<LibraryRenameJournal>.Instance),
        _store,
        NullLogger<LibraryRenamer>.Instance,
        _changes.Object,
        (from, to) =>
        {
            if (_moveFails(from, to))
            {
                throw new IOException("move");
            }

            Directory.Move(from, to);
        });

    private string JournalPath => Path.Combine(_data, LibraryRenameJournal.FileName);

    private string DamagedPath => JournalPath + ".damaged";

    // Writes the record file by hand, as anyone who can write in the data folder could.
    private void WriteJournal(params LibraryRenameRecord[] records)
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(new { Version = 1, Records = records }, _journalOptions));
    }

    private LibraryRenameRecord Pending(string oldName = "Casa - Film", string newName = "Mare - Film", Guid? oldId = null, Guid? newId = null, string? temporary = null) =>
        new(oldName, newName, oldId ?? OldId, newId ?? NewId, CollectionType.movies, temporary);

    // The library folder as a done rename leaves it, with a user blocking the old id.
    private void MovedToMare(Guid blockedId)
    {
        Directory.Move(Path.Combine(_root, "Casa - Film"), Path.Combine(_root, "Mare - Film"));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [blockedId.ToString("N")]);
    }

    private void AssertSetAside(Guid blockedId)
    {
        Assert.Equal([blockedId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        _users.Verify(users => users.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        Assert.True(File.Exists(DamagedPath));
        Assert.False(File.Exists(JournalPath));
        Assert.False(_renamer.HasPending);
    }

    private static Guid IdOf(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())));

    private Task<Guid?> RenameAsync(string from = "Casa - Film", string to = "Mare - Film", Guid? itemId = null) =>
        _renamer.RenameAsync(from, itemId ?? OldId, CollectionType.movies, to, CancellationToken.None);

    [Fact]
    public async Task The_folder_moves_and_the_old_id_is_retired()
    {
        var id = await RenameAsync();

        Assert.Equal(NewId, id);
        Assert.False(Directory.Exists(Path.Combine(_root, "Casa - Film")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Mare - Film")));
        Assert.True(_retired.Contains(OldId));
        _monitor.Verify(monitor => monitor.Stop(), Times.Once);
    }

    [Fact]
    public async Task The_change_tracker_follows_the_library_to_its_new_id()
    {
        await RenameAsync();

        _changes.Verify(changes => changes.Renamed(OldId, NewId, "Mare - Film"), Times.Once);
    }

    [Fact]
    public async Task Each_user_keeps_the_access_lists_with_the_new_id_in_the_form_they_had()
    {
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N"), _other.Id.ToString("D")]);
        _limited.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, [OldId.ToString("D")]);
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("D"), NewId.ToString("D")]);

        await RenameAsync();

        Assert.Equal([NewId.ToString("N"), _other.Id.ToString("D")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.Equal([NewId.ToString("D")], _limited.GetPreference(PreferenceKind.EnableContentDeletionFromFolders));
        Assert.Equal([NewId.ToString("D")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        _users.Verify(users => users.UpdateUserAsync(_limited), Times.Once);
        _users.Verify(users => users.UpdateUserAsync(_blocked), Times.Once);
        _users.Verify(users => users.UpdateUserAsync(_other), Times.Never);
    }

    [Fact]
    public async Task Without_the_view_ids_of_12_2_the_home_lists_are_left_as_they_are()
    {
        _views.Available = false;
        _limited.SetPreference(PreferenceKind.OrderedViews, [OldId.ToString("N")]);
        _limited.SetPreference(PreferenceKind.LatestItemExcludes, [OldId.ToString("N")]);

        await RenameAsync();

        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.OrderedViews));
        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.LatestItemExcludes));
    }

    [Fact]
    public async Task With_the_view_ids_of_12_2_the_home_lists_follow_the_library_and_its_views()
    {
        var shadowOld = FakeViewIds.Shadow("Casa - Film", OldId);
        var namedOld = FakeViewIds.Named(_limited, OldId);
        _limited.SetPreference(PreferenceKind.LatestItemExcludes, [OldId.ToString("N")]);
        _limited.SetPreference(PreferenceKind.GroupedFolders, [OldId.ToString("N")]);
        _limited.SetPreference(PreferenceKind.MyMediaExcludes, [shadowOld.ToString("N")]);
        _limited.SetPreference(PreferenceKind.OrderedViews, [namedOld.ToString("N"), OldId.ToString("N")]);

        await RenameAsync();

        Assert.Equal([NewId.ToString("N")], _limited.GetPreference(PreferenceKind.LatestItemExcludes));
        Assert.Equal([NewId.ToString("N")], _limited.GetPreference(PreferenceKind.GroupedFolders));
        Assert.Equal([FakeViewIds.Shadow("Mare - Film", NewId).ToString("N")], _limited.GetPreference(PreferenceKind.MyMediaExcludes));
        Assert.Equal([FakeViewIds.Named(_limited, NewId).ToString("N"), NewId.ToString("N")], _limited.GetPreference(PreferenceKind.OrderedViews));
    }

    [Fact]
    public async Task A_taken_name_or_a_missing_folder_changes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Mare - Film"));
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);

        Assert.Null(await RenameAsync());
        Assert.Null(await RenameAsync(from: "Assente - Film", to: "Altro - Film"));

        Assert.True(Directory.Exists(Path.Combine(_root, "Casa - Film")));
        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.False(_retired.Contains(OldId));
        _monitor.Verify(monitor => monitor.Stop(), Times.Never);
        _changes.Verify(changes => changes.Renamed(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_name_that_would_leave_the_libraries_folder_is_refused()
    {
        Assert.Null(await RenameAsync(to: Path.Combine("..", "Fuori")));

        Assert.True(Directory.Exists(Path.Combine(_root, "Casa - Film")));
    }

    [Fact]
    public async Task A_change_of_case_only_still_renames_the_folder()
    {
        var id = await RenameAsync(to: "CASA - FILM");

        Assert.Equal(OldId, id);
        Assert.Equal(["CASA - FILM"], Directory.GetDirectories(_root).Select(Path.GetFileName));
        Assert.False(_retired.Contains(OldId));
    }

    [Fact]
    public async Task Renaming_back_takes_the_id_out_of_the_retired_ones()
    {
        await RenameAsync();
        await RenameAsync(from: "Mare - Film", to: "Casa - Film", itemId: NewId);

        Assert.False(_retired.Contains(OldId));
        Assert.True(_retired.Contains(NewId));
    }

    [Fact]
    public void A_library_manager_of_12_1_has_no_view_ids()
    {
        Assert.False(new LibraryViewIds(_library.Object).IsAvailable);
    }

    [Fact]
    public async Task Before_the_folder_moves_who_blocks_the_old_id_also_blocks_the_new_one()
    {
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);
        _moveFails = (_, _) => true;

        await Assert.ThrowsAsync<IOException>(() => RenameAsync());

        Assert.Equal([OldId.ToString("N"), NewId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.True(Directory.Exists(Path.Combine(_root, "Casa - Film")));
    }

    [Fact]
    public async Task When_a_block_cannot_be_saved_before_the_move_the_folder_stays_and_nothing_is_recorded()
    {
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_blocked)).ThrowsAsync(new InvalidOperationException("database"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RenameAsync());

        Assert.True(Directory.Exists(Path.Combine(_root, "Casa - Film")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Mare - Film")));
        Assert.False(File.Exists(JournalPath));
        Assert.False(_renamer.HasPending);
        Assert.Equal([OldId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
    }

    [Fact]
    public async Task A_done_rename_leaves_nothing_pending()
    {
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);

        await RenameAsync();

        Assert.False(_renamer.HasPending);
        Assert.False(File.Exists(JournalPath));
        Assert.Equal([NewId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
    }

    [Fact]
    public async Task A_user_who_cannot_be_rewritten_does_not_stop_the_others_and_the_rename_stays_pending()
    {
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _other.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_limited)).ThrowsAsync(new InvalidOperationException("database"));

        Assert.Equal(NewId, await RenameAsync());

        Assert.Equal([NewId.ToString("N")], _other.GetPreference(PreferenceKind.EnabledFolders));
        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.True(_renamer.HasPending);
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public async Task After_a_restart_the_pending_rewrite_is_finished_and_forgotten()
    {
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_limited)).ThrowsAsync(new InvalidOperationException("database"));
        await RenameAsync();
        _users.Setup(users => users.UpdateUserAsync(_limited)).Returns(Task.CompletedTask);

        var restarted = NewRenamer();
        Assert.True(restarted.HasPending);
        await restarted.ResumeAsync(CancellationToken.None);

        Assert.Equal([NewId.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.False(restarted.HasPending);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task A_record_is_kept_while_the_server_lists_no_user()
    {
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_limited)).ThrowsAsync(new InvalidOperationException("database"));
        await RenameAsync();
        _users.Setup(users => users.GetUsers()).Returns([]);

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.True(_renamer.HasPending);
    }

    [Fact]
    public async Task A_record_whose_folder_never_moved_is_dropped_and_the_folder_stays()
    {
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);
        _moveFails = (_, _) => true;
        await Assert.ThrowsAsync<IOException>(() => RenameAsync());
        _moveFails = (_, _) => false;

        await NewRenamer().ResumeAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, "Casa - Film")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Mare - Film")));
        Assert.False(File.Exists(JournalPath));
        Assert.Contains(OldId.ToString("N"), _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
    }

    [Fact]
    public async Task A_change_of_case_stopped_between_its_two_moves_keeps_access_closed_and_is_finished_later()
    {
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);
        var target = Path.Combine(_root, "CASA - FILM");
        _moveFails = (_, to) => to == target;

        await Assert.ThrowsAsync<IOException>(() => RenameAsync(to: "CASA - FILM"));

        var temporary = Assert.Single(Directory.GetDirectories(_root));
        Assert.Contains(IdOf(temporary).ToString("N"), _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.True(File.Exists(JournalPath));

        _moveFails = (_, _) => false;
        await NewRenamer().ResumeAsync(CancellationToken.None);

        Assert.Equal(["CASA - FILM"], Directory.GetDirectories(_root).Select(Path.GetFileName));
        Assert.Equal([OldId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task The_old_id_is_retired_once_the_first_move_of_a_change_of_case_is_done()
    {
        _library.Setup(library => library.GetNewItemId(It.IsAny<string>(), typeof(CollectionFolder))).Returns((string key, Type _) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))));
        var oldId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(Path.Combine(_root, "Casa - Film"))));
        var target = Path.Combine(_root, "CASA - FILM");
        _moveFails = (_, to) => to == target;

        await Assert.ThrowsAsync<IOException>(() => RenameAsync(to: "CASA - FILM", itemId: oldId));

        Assert.True(_retired.Contains(oldId));
    }

    [Fact]
    public async Task A_resumed_record_whose_new_name_leaves_the_libraries_folder_is_set_aside()
    {
        var temporary = Guid.NewGuid().ToString("N");
        Directory.Move(Path.Combine(_root, "Casa - Film"), Path.Combine(_root, temporary));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N")]);
        var outside = Path.Combine(_root, "..", "Fuori-" + temporary);
        WriteJournal(Pending(newName: Path.Combine("..", Path.GetFileName(outside)), newId: IdOf(Path.GetFullPath(outside)), temporary: temporary));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, temporary)));
        Assert.False(Directory.Exists(outside));
        AssertSetAside(OldId);
    }

    [Fact]
    public async Task A_resumed_record_whose_temporary_name_leaves_the_libraries_folder_is_set_aside()
    {
        MovedToMare(OldId);
        var outside = Path.Combine("..", "Fuori-" + Guid.NewGuid().ToString("N"));
        _limited.SetPreference(PreferenceKind.EnabledFolders, [IdOf(Path.Combine(_root, outside)).ToString("N")]);
        WriteJournal(Pending(temporary: outside));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal([IdOf(Path.Combine(_root, outside)).ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        AssertSetAside(OldId);
    }

    [Fact]
    public async Task A_resumed_record_whose_temporary_name_is_not_a_guid_is_set_aside()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Lago - Film"));
        var lago = IdOf(Path.Combine(_root, "Lago - Film"));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [lago.ToString("N")]);
        WriteJournal(Pending(
            oldName: "Riva - Film",
            newName: "RIVA - FILM",
            oldId: IdOf(Path.Combine(_root, "Riva - Film")),
            newId: IdOf(Path.Combine(_root, "RIVA - FILM")),
            temporary: "lago - film"));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        AssertSetAside(lago);
    }

    [Fact]
    public async Task A_resumed_record_with_a_temporary_name_but_not_a_change_of_case_is_set_aside()
    {
        var other = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, other));
        var otherId = IdOf(Path.Combine(_root, other));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [otherId.ToString("N")]);
        WriteJournal(Pending(
            oldName: "Riva - Film",
            newName: "Lago - Film",
            oldId: IdOf(Path.Combine(_root, "Riva - Film")),
            newId: IdOf(Path.Combine(_root, "Lago - Film")),
            temporary: other));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, other)));
        Assert.False(Directory.Exists(Path.Combine(_root, "Lago - Film")));
        AssertSetAside(otherId);
    }

    [Fact]
    public async Task A_resumed_change_of_case_through_a_guid_folder_is_finished()
    {
        var temporary = Guid.NewGuid().ToString("N");
        Directory.Move(Path.Combine(_root, "Casa - Film"), Path.Combine(_root, temporary));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [OldId.ToString("N"), IdOf(Path.Combine(_root, temporary)).ToString("N")]);
        WriteJournal(Pending(newName: "CASA - FILM", newId: IdOf(Path.Combine(_root, "CASA - FILM")), temporary: temporary));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal(["CASA - FILM"], Directory.GetDirectories(_root).Select(Path.GetFileName));
        Assert.Equal([OldId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.False(File.Exists(DamagedPath));
        Assert.False(_renamer.HasPending);
    }

    [Fact]
    public async Task A_resumed_record_whose_new_id_is_not_that_of_its_new_folder_is_set_aside()
    {
        MovedToMare(OldId);
        WriteJournal(Pending(newId: Guid.NewGuid()));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        AssertSetAside(OldId);
    }

    [Fact]
    public async Task A_resumed_record_whose_old_id_is_bound_to_no_library_is_set_aside_and_the_others_are_finished()
    {
        var victim = Guid.NewGuid();
        MovedToMare(victim);
        Directory.CreateDirectory(Path.Combine(_root, "Lago - Film"));
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        var bound = Pending(oldName: "Riva - Film", newName: "Lago - Film", oldId: IdOf(Path.Combine(_root, "Riva - Film")), newId: IdOf(Path.Combine(_root, "Lago - Film")));
        _limited.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, [bound.OldId.ToString("N")]);
        WriteJournal(Pending(oldId: victim), bound);
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal([victim.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.Equal([bound.NewId.ToString("N")], _limited.GetPreference(PreferenceKind.EnableContentDeletionFromFolders));
        Assert.True(File.Exists(DamagedPath));
        Assert.Contains(victim.ToString(), await File.ReadAllTextAsync(DamagedPath), StringComparison.Ordinal);
        Assert.False(_renamer.HasPending);
    }

    [Fact]
    public async Task Records_set_aside_at_different_times_are_all_kept()
    {
        MovedToMare(OldId);
        var first = Pending(newId: Guid.NewGuid());
        WriteJournal(first);
        await NewRenamer().ResumeAsync(CancellationToken.None);
        var second = Pending(oldId: Guid.NewGuid());
        WriteJournal(second);
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        var damaged = await File.ReadAllTextAsync(DamagedPath);
        Assert.Contains(first.NewId.ToString(), damaged, StringComparison.Ordinal);
        Assert.Contains(second.OldId.ToString(), damaged, StringComparison.Ordinal);
        AssertSetAside(OldId);
    }

    [Fact]
    public async Task A_resumed_record_whose_old_id_is_a_library_of_the_plugin_is_finished()
    {
        var recorded = Guid.NewGuid();
        var server = new RemoteServerConfig { Id = Guid.NewGuid() };
        server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = recorded.ToString("N") });
        _store.Configuration.Servers.Add(server);
        MovedToMare(recorded);
        WriteJournal(Pending(oldId: recorded));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal([NewId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.False(File.Exists(DamagedPath));
        Assert.False(_renamer.HasPending);
    }

    [Fact]
    public async Task A_resumed_record_pairing_a_library_of_the_plugin_with_another_name_is_set_aside()
    {
        var recorded = Guid.NewGuid();
        var server = new RemoteServerConfig { Id = Guid.NewGuid() };
        server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = recorded.ToString("N") });
        _store.Configuration.Servers.Add(server);
        Directory.CreateDirectory(Path.Combine(_root, "Lago - Film"));
        _blocked.SetPreference(PreferenceKind.BlockedMediaFolders, [recorded.ToString("N")]);
        _limited.SetPreference(PreferenceKind.EnabledFolders, [recorded.ToString("N")]);
        WriteJournal(Pending(oldName: "Inventato - Film", newName: "Lago - Film", oldId: recorded, newId: IdOf(Path.Combine(_root, "Lago - Film"))));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal([recorded.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        AssertSetAside(recorded);
    }

    [Fact]
    public async Task A_resumed_record_whose_old_id_is_a_library_held_for_a_removed_server_is_finished()
    {
        var recorded = Guid.NewGuid();
        var held = new RemovedServerLibrariesConfig { ServerId = Guid.NewGuid() };
        held.Libraries.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = recorded.ToString("D") });
        _store.Configuration.RemovedServerLibraries.Add(held);
        MovedToMare(recorded);
        WriteJournal(Pending(oldId: recorded));
        _renamer = NewRenamer();

        await _renamer.ResumeAsync(CancellationToken.None);

        Assert.Equal([NewId.ToString("N")], _blocked.GetPreference(PreferenceKind.BlockedMediaFolders));
        Assert.False(_renamer.HasPending);
    }

    [Fact]
    public async Task Resuming_a_change_of_case_retires_the_id_of_the_temporary_folder()
    {
        var target = Path.Combine(_root, "CASA - FILM");
        _moveFails = (_, to) => to == target;
        await Assert.ThrowsAsync<IOException>(() => RenameAsync(to: "CASA - FILM"));
        var temporaryId = IdOf(Assert.Single(Directory.GetDirectories(_root)));
        _moveFails = (_, _) => false;

        await NewRenamer().ResumeAsync(CancellationToken.None);

        Assert.True(_retired.Contains(temporaryId));
        Assert.False(_retired.Contains(OldId));
    }

    [Fact]
    public async Task A_library_made_again_with_the_old_name_keeps_its_lists_and_the_record_is_dropped()
    {
        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_limited)).ThrowsAsync(new InvalidOperationException("database"));
        await RenameAsync();
        _users.Setup(users => users.UpdateUserAsync(_limited)).Returns(Task.CompletedTask);
        Directory.CreateDirectory(Path.Combine(_root, "Casa - Film"));

        var restarted = NewRenamer();
        await restarted.ResumeAsync(CancellationToken.None);

        Assert.Equal([OldId.ToString("N")], _limited.GetPreference(PreferenceKind.EnabledFolders));
        Assert.True(Directory.Exists(Path.Combine(_root, "Mare - Film")));
        Assert.False(_retired.Contains(OldId));
        Assert.False(restarted.HasPending);
        Assert.False(File.Exists(JournalPath));
        Assert.False(File.Exists(DamagedPath));
    }

    [Fact]
    public async Task The_record_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _limited.SetPreference(PreferenceKind.EnabledFolders, [OldId.ToString("N")]);
        _users.Setup(users => users.UpdateUserAsync(_limited)).ThrowsAsync(new InvalidOperationException("database"));

        await RenameAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(JournalPath));
    }

    private sealed class FakeViewIds : ILibraryViewIds
    {
        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public static Guid Shadow(string name, Guid parent) => IdOf("shadow" + name + parent.ToString("N"));

        public static Guid Named(User user, Guid parent) => IdOf("named" + user.Id.ToString("N") + parent.ToString("N"));

        public Guid ShadowViewId(string name, Guid parentId, CollectionType? viewType) => Shadow(name, parentId);

        public Guid NamedViewId(User user, Guid parentId, CollectionType? viewType) => Named(user, parentId);
    }
}
