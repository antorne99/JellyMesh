using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The page warns about a library added over the federated folder or taken from it since the last sync, whose links the
// next sync rebuilds (Q53).
public sealed class LibraryChangeTrackerTests
{
    private readonly InMemoryConfigurationStore _store = new();
    private readonly List<VirtualFolderInfo> _libraries = [];
    private readonly Mock<ILibraryManager> _manager = new(MockBehavior.Strict);
    private readonly CapturingLogger<LibraryChangeTracker> _logger = new();
    private readonly LibraryChangeTracker _tracker;

    public LibraryChangeTrackerTests()
    {
        _store.Configuration.FederatedFolderPath = "/federata";
        _manager.Setup(manager => manager.GetVirtualFolders()).Returns(() => new List<VirtualFolderInfo>(_libraries));
        _tracker = new LibraryChangeTracker(_manager.Object, _store, _logger);
    }

    private static VirtualFolderInfo Library(string id, string name, params string[] locations) => new()
    {
        ItemId = id,
        Name = name,
        CollectionType = CollectionTypeOptions.movies,
        Locations = locations
    };

    [Fact]
    public void WithNoChangeSinceTheSyncThereIsNothingToSay()
    {
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _tracker.Record();

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void ALocationThatIsNoPathIsSkippedAndTheOthersStillCount()
    {
        _libraries.Add(Library("1", "Rotta", "/federata/a\0b", "/federata/a/Film"));
        _libraries.Add(Library("2", "Solo rotta", "/federata/b\0c"));
        _tracker.Record();
        _libraries.Add(Library("3", "Nuova", "/federata/b/Film"));

        var changes = _tracker.Compare();

        Assert.Equal(["Nuova"], changes.Added);
        Assert.Empty(changes.Removed);
        Assert.NotEmpty(_logger.Lines);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("/federata/", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryAddedInTheFolderAfterTheSyncIsNamed()
    {
        _tracker.Record();
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _libraries.Add(Library("2", "Tutto", "/"));
        _libraries.Add(Library("3", "Locale", "/media/film"));

        var changes = _tracker.Compare();

        Assert.Equal(["Casa - Film", "Tutto"], changes.Added);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void ALibraryTakenFromTheFolderAfterTheSyncIsNamed()
    {
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _libraries.Add(Library("2", "Film", "/media/film", "/federata/b/Film"));
        _tracker.Record();
        _libraries.RemoveAt(0);
        _libraries[0] = Library("2", "Film", "/media/film");

        var changes = _tracker.Compare();

        Assert.Empty(changes.Added);
        Assert.Equal(["Casa - Film", "Film"], changes.Removed);
    }

    [Fact]
    public void ARenamedLibraryIsNotAChange()
    {
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _libraries[0] = Library("1", "Amici - Film", "/federata/a/Film");

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void ALibraryThePluginRenamedToANewIdIsNotAChange()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        _libraries.Add(Library(oldId.ToString("N"), "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _libraries[0] = Library(newId.ToString("N"), "Mare - Film", "/federata/a/Film");

        _tracker.Renamed(oldId, newId, "Mare - Film");

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void ALibraryAddedDuringAPluginRenameIsStillNamed()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        _libraries.Add(Library(oldId.ToString("N"), "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _libraries[0] = Library(newId.ToString("N"), "Mare - Film", "/federata/a/Film");
        _libraries.Add(Library("2", "Tutto", "/"));

        _tracker.Renamed(oldId, newId, "Mare - Film");
        var changes = _tracker.Compare();

        Assert.Equal(["Tutto"], changes.Added);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void ARenamedLibraryStillWithNoIdUntilItsScanIsNotAChange()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        _libraries.Add(Library(oldId.ToString("N"), "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _libraries[0] = Library(string.Empty, "Mare - Film", "/federata/a/Film");

        _tracker.Renamed(oldId, newId, "Mare - Film");

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
        _libraries[0] = Library(newId.ToString("N"), "Mare - Film", "/federata/a/Film");
        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void ASyncBeforeTheScanOfARenamedLibraryRecordsItsNewId()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        _libraries.Add(Library(oldId.ToString("N"), "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _libraries[0] = Library(string.Empty, "Mare - Film", "/federata/a/Film");
        _tracker.Renamed(oldId, newId, "Mare - Film");

        _tracker.Record();
        _libraries[0] = Library(newId.ToString("N"), "Mare - Film", "/federata/a/Film");

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void ARenameBeforeAnySyncChangesNothing()
    {
        _tracker.Renamed(Guid.NewGuid(), Guid.NewGuid(), "Mare - Film");
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void TheNextSyncClearsTheWarning()
    {
        _tracker.Record();
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _tracker.Record();

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void BeforeAnySyncTheFirstLookIsTheStart()
    {
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        Assert.Equal(LibraryChanges.None, _tracker.Compare());

        _libraries.Clear();

        Assert.Equal(["Casa - Film"], _tracker.Compare().Removed);
    }

    [Fact]
    public void AnotherFederatedFolderStartsAgain()
    {
        _libraries.Add(Library("1", "Casa - Film", "/federata/a/Film"));
        _tracker.Record();
        _store.Configuration.FederatedFolderPath = "/altra";

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void WithNoFolderThereIsNothingToSay()
    {
        _store.Configuration.FederatedFolderPath = string.Empty;
        _tracker.Record();
        _libraries.Add(Library("1", "Tutto", "/"));

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }

    [Fact]
    public void LibrariesThatCannotBeReadSayNothing()
    {
        _tracker.Record();
        _manager.Setup(manager => manager.GetVirtualFolders()).Throws(new IOException("/secret"));

        Assert.Equal(LibraryChanges.None, _tracker.Compare());
    }
}
