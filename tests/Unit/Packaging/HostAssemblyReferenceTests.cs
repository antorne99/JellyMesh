using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Packaging;

public class HostAssemblyReferenceTests
{
    // Jellyfin 12.1 ships Microsoft.Data.Sqlite 10.0.11.0, the oldest of 12.x (decision 421).
    private static readonly Version OldestHostSqlite = new(10, 0, 11, 0);

    [Fact]
    public void ReferencesNoNewerSqliteThanJellyfin121Ships()
    {
        var reference = typeof(KeptWatchStateStore).Assembly.GetReferencedAssemblies()
            .Single(name => name.Name == "Microsoft.Data.Sqlite");

        Assert.True(
            reference.Version <= OldestHostSqlite,
            $"Microsoft.Data.Sqlite {reference.Version} does not bind on Jellyfin 12.1, which ships {OldestHostSqlite}.");
    }
}
