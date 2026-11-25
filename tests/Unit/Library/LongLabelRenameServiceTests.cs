using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Row 564: the saved labels over 40 bytes are renamed at the start after the upgrade.
public sealed class LongLabelRenameServiceTests
{
    private readonly Mock<IServerRenamer> _renamer = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly List<string> _steps = [];

    public LongLabelRenameServiceTests()
    {
        _renamer.Setup(renamer => renamer.RenameLongLabelsAsync(It.IsAny<CancellationToken>())).Callback(() => _steps.Add("rename")).ReturnsAsync(1);
        _stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(false);
    }

    [Fact]
    public async Task At_start_the_long_labels_are_renamed()
    {
        await RunAsync();

        Assert.Equal(["rename"], _steps);
    }

    [Fact]
    public async Task An_unread_registry_is_read_again_before_the_renames()
    {
        var unread = true;
        _stubs.Setup(stubs => stubs.IsUnread).Returns(() => unread);
        _stubs.Setup(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>())).Callback(() =>
        {
            _steps.Add("reload");
            unread = false;
        }).Returns(Task.CompletedTask);

        await RunAsync();

        Assert.Equal(["reload", "rename"], _steps);
    }

    [Fact]
    public async Task A_registry_that_stays_unread_is_tried_again()
    {
        var reads = 0;
        _stubs.Setup(stubs => stubs.IsUnread).Returns(() => reads < 2);
        _stubs.Setup(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>())).Callback(() => reads++).Returns(Task.CompletedTask);

        await RunAsync();

        Assert.Equal(2, reads);
        Assert.Equal(["rename"], _steps);
    }

    [Fact]
    public async Task A_suspended_store_is_tried_again()
    {
        var calls = 0;
        _renamer.Setup(renamer => renamer.RenameLongLabelsAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? Task.FromException<int>(new StoreSuspendedException()) : Task.FromResult(0));

        await RunAsync();

        Assert.Equal(2, calls);
    }

    // Row 565: while the older layouts hold titles the migration still carries, nothing is renamed at this start.
    [Fact]
    public async Task While_the_older_layouts_hold_titles_nothing_is_renamed()
    {
        _stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(true);

        await RunAsync();

        Assert.Empty(_steps);
        _renamer.Verify(renamer => renamer.RenameLongLabelsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_first_start_after_the_migration_renames()
    {
        var olderLayout = true;
        _stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(() => olderLayout);

        await RunAsync();
        Assert.Empty(_steps);

        olderLayout = false;
        await RunAsync();

        Assert.Equal(["rename"], _steps);
    }

    [Fact]
    public async Task A_registry_that_cannot_tell_its_layout_is_tried_again()
    {
        var asks = 0;
        _stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(() => ++asks == 1 ? (bool?)null : false);

        await RunAsync();

        Assert.Equal(2, asks);
        Assert.Equal(["rename"], _steps);
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_server()
    {
        _renamer.Setup(renamer => renamer.RenameLongLabelsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database"));
        using var service = NewService();

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        _renamer.Verify(renamer => renamer.RenameLongLabelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private LongLabelRenameService NewService() => new(_renamer.Object, _stubs.Object, NullLogger<LongLabelRenameService>.Instance) { RetryDelay = TimeSpan.Zero };

    private async Task RunAsync()
    {
        using var service = NewService();
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
