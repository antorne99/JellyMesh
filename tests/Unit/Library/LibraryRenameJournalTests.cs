using System;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LibraryRenameJournalTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private LibraryRenameJournal Journal() => new(_database, NullLogger<LibraryRenameJournal>.Instance);

    private static LibraryRenameRecord Rename(string name) => new("Old " + name, name, Guid.NewGuid(), Guid.NewGuid(), CollectionType.movies, null);

    // A database opened again, as from a copy, may hold other renames: they are read again, and no row of the old reading
    // is dropped by its id.
    [Fact]
    public async Task AfterTheDatabaseIsOpenedAgainTheRenamesAreReadAgain()
    {
        var journal = Journal();
        var first = Rename("A");
        journal.Add(first);
        await _database.ExecuteAsync("DELETE FROM library_renames;");
        var second = Rename("B");
        Journal().Add(second);

        _database.Reopen();

        Assert.Equal([second], journal.Pending());
        Assert.True(journal.Remove(second));
        Assert.Equal(0, _database.Count("library_renames"));
    }
}
