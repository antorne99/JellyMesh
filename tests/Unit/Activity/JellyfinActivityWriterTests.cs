using System;
using System.Collections.Concurrent;
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
        var written = new ConcurrentQueue<ActivityLog>();
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>())).Callback<ActivityLog>(written.Enqueue).Returns(Task.CompletedTask);
        using var writer = new JellyfinActivityWriter(manager.Object, new CapturingLogger<JellyfinActivityWriter>());

        writer.Write(new ActivityEntry("JellyMesh: Friend is offline", "JellyMeshServerOffline", LogLevel.Warning)
        {
            ShortOverview = "Unreachable",
            Overview = "Its titles stay."
        });
        await writer.StopAsync();

        var log = Assert.Single(written);
        Assert.Equal("JellyMesh: Friend is offline", log.Name);
        Assert.Equal("JellyMeshServerOffline", log.Type);
        Assert.Equal(Guid.Empty, log.UserId);
        Assert.Equal(LogLevel.Warning, log.LogSeverity);
        Assert.Equal("Unreachable", log.ShortOverview);
        Assert.Equal("Its titles stay.", log.Overview);
    }

    // Security review of 833: the caller never waits for the database of the server.
    [Fact]
    public async Task A_slow_activity_log_never_holds_the_caller_and_keeps_the_order()
    {
        var gate = new TaskCompletionSource();
        var written = new ConcurrentQueue<string>();
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>()))
            .Returns<ActivityLog>(async log =>
            {
                await gate.Task.ConfigureAwait(false);
                written.Enqueue(log.Name);
            });
        using var writer = new JellyfinActivityWriter(manager.Object, new CapturingLogger<JellyfinActivityWriter>());

        writer.Write(new ActivityEntry("first", "type", LogLevel.Information));
        writer.Write(new ActivityEntry("second", "type", LogLevel.Information));

        Assert.Empty(written);
        gate.SetResult();
        await writer.StopAsync();
        Assert.Equal(["first", "second"], written.ToArray());
    }

    [Fact]
    public async Task A_cancelled_write_is_never_thrown_and_the_next_one_is_written()
    {
        var calls = 0;
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>()))
            .Returns(() => ++calls == 1 ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask);
        using var writer = new JellyfinActivityWriter(manager.Object, new CapturingLogger<JellyfinActivityWriter>());

        writer.Write(new ActivityEntry("name", "type", LogLevel.Information));
        writer.Write(new ActivityEntry("name", "type", LogLevel.Information));
        await writer.StopAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failing_activity_log_is_logged_with_the_type_only_and_never_thrown()
    {
        var manager = new Mock<IActivityManager>();
        manager.Setup(m => m.CreateAsync(It.IsAny<ActivityLog>())).ThrowsAsync(new InvalidOperationException("http://" + TestValues.Host));
        var logger = new CapturingLogger<JellyfinActivityWriter>();
        using var writer = new JellyfinActivityWriter(manager.Object, logger);

        writer.Write(new ActivityEntry("name", "type", LogLevel.Information));
        await writer.StopAsync();

        var line = Assert.Single(logger.Lines);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_after_stop_is_dropped_without_throwing()
    {
        var manager = new Mock<IActivityManager>();
        var writer = new JellyfinActivityWriter(manager.Object, new CapturingLogger<JellyfinActivityWriter>());
        writer.Dispose();

        writer.Write(new ActivityEntry("name", "type", LogLevel.Information));

        Assert.DoesNotContain(manager.Invocations, invocation => invocation.Method.Name == nameof(IActivityManager.CreateAsync));
    }
}
