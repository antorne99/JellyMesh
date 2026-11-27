using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class StubTablesTests
{
    private static StubEntry Stub(string id) => new()
    {
        Id = id,
        Kind = StubKind.Movie,
        RelativePath = id + ".strm",
        Sources = [],
        IdentityKey = id
    };

    private static SeriesStub Series(string id) => new() { Id = id, RelativePath = id, WorkKey = id };

    [Fact]
    public void TheRowsOfAWriteGoInKeyOrderWithTheirPlaceInTheList()
    {
        var state = StubState.Empty with { Stubs = [Stub("c"), Stub("a"), Stub("b")], Series = [Series("z"), Series("y")] };

        var write = StubTables.Plan(null, state)!;

        Assert.Equal([("a", 1), ("b", 2), ("c", 0)], write.StubRows.Select(row => (row.Row.Id, row.Ordinal)));
        Assert.Equal([("y", 1), ("z", 0)], write.SeriesRows.Select(row => (row.Row.Id, row.Ordinal)));
    }

    [Fact]
    public void TheMovesAndDeletesOfAWriteGoInKeyOrder()
    {
        var before = StubState.Empty with { Stubs = [Stub("e"), Stub("a"), Stub("b"), Stub("c"), Stub("d")] };
        var next = StubState.Empty with { Stubs = [Stub("d"), Stub("c")] };

        var write = StubTables.Plan(before, next)!;

        Assert.Equal([("c", 1), ("d", 0)], write.StubOrdinals);
        Assert.Equal(["a", "b", "e"], write.StubDeletes);
    }
}
