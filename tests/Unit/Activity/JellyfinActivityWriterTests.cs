using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Activity;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Activity;

public class JellyfinActivityWriterTests
{
    [Fact]
    public async Task An_entry_becomes_a_system_entry_of_the_activity_log()
    {
        ActivityLog? written = null;
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>())).Callback<ActivityLog>(log => written = log).Returns(Task.CompletedTask);
        var writer = new JellyfinActivityWriter(manager.Object, new CapturingLogger<JellyfinActivityWriter>());

        await writer.WriteAsync(new ActivityEntry("JellyMesh: Friend is offline", "JellyMeshServerOffline", LogLevel.Warning)
        {
            ShortOverview = "Unreachable",
            Overview = "Its titles stay."
        });

        Assert.NotNull(written);
        Assert.Equal("JellyMesh: Friend is offline", written.Name);
        Assert.Equal("JellyMeshServerOffline", written.Type);
        Assert.Equal(Guid.Empty, written.UserId);
        Assert.Equal(LogLevel.Warning, written.LogSeverity);
        Assert.Equal("Unreachable", written.ShortOverview);
        Assert.Equal("Its titles stay.", written.Overview);
    }

    [Fact]
    public async Task A_failing_activity_log_is_logged_with_the_type_only_and_never_thrown()
    {
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>())).ThrowsAsync(new InvalidOperationException("http://" + TestValues.Host));
        var logger = new CapturingLogger<JellyfinActivityWriter>();
        var writer = new JellyfinActivityWriter(manager.Object, logger);

        await writer.WriteAsync(new ActivityEntry("name", "type", LogLevel.Information));

        var line = Assert.Single(logger.Lines);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
    }
}
