using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

public class SmallStateSchemaTests
{
    [Theory]
    [InlineData("manual_merges")]
    [InlineData("manual_merge_copies")]
    [InlineData("last_served_sources")]
    [InlineData("sync_runs")]
    [InlineData("sync_run_servers")]
    [InlineData("library_renames")]
    [InlineData("watch_state_carries")]
    [InlineData("kept_titles")]
    [InlineData("kept_user_states")]
    [InlineData("kept_title_copies")]
    [InlineData("kept_merge_copies")]
    public void ANewDatabaseHasTheEmptyTablesOfTheSmallState(string table)
    {
        using var database = new TestDatabase();

        Assert.Equal(0, database.Count(table));
    }
}
