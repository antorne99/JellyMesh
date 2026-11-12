using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jm-renamer-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IServerApplicationPaths> _paths = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ILibraryMonitor> _monitor = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly FakeViewIds _views = new();
    private readonly RetiredLibraryIds _retired = new();
    private readonly User _limited = new("limited", "auth", "reset");
    private readonly User _blocked = new("blocked", "auth", "reset");
    private readonly User _other = new("other", "auth", "reset");
    private readonly LibraryRenamer _renamer;

    public LibraryRenamerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Casa - Film"));
        _paths.SetupGet(paths => paths.DefaultUserViewsPath).Returns(_root);
        _library.Setup(library => library.GetNewItemId(It.IsAny<string>(), typeof(CollectionFolder))).Returns((string key, Type _) => IdOf(key));
        _users.Setup(users => users.GetUsers()).Returns(() => [_limited, _blocked, _other]);
        _renamer = new LibraryRenamer(_paths.Object, _library.Object, _monitor.Object, _users.Object, _views, _retired, NullLogger<LibraryRenamer>.Instance);
    }

    private Guid OldId => IdOf(Path.Combine(_root, "Casa - Film"));

    private Guid NewId => IdOf(Path.Combine(_root, "Mare - Film"));

    public void Dispose() => Directory.Delete(_root, true);

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
