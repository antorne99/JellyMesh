using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LibraryRenameResumeServiceTests
{
    [Fact]
    public async Task At_start_the_renames_left_halfway_are_finished_and_a_failure_does_not_stop_the_server()
    {
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.ResumeRenamesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database"));
        using var service = new LibraryRenameResumeService(libraries.Object, NullLogger<LibraryRenameResumeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        libraries.Verify(manager => manager.ResumeRenamesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }
}
