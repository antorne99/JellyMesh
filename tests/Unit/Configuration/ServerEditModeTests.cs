using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

// Row 438: the library mode changes in the Edit form, with its effects in the confirmation and after the save.
public sealed partial class ServerEditTests
{
    private LibraryMode StoredMode => _fixture.Store.Configuration.Servers.Single().LibraryMode;

    private async Task<Guid> AddInModeAsync(LibraryMode mode, params string[] libraries)
    {
        var id = await AddAsync(libraries);
        _fixture.Store.Update(config => config.Servers.Single().LibraryMode = mode);
        _libraries.Setup(manager => manager.ApplyMode(id))
            .Returns(() => Step("mode", new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.Started, Task.CompletedTask, [])));
        return id;
    }

    [Fact]
    public async Task A_new_library_mode_is_saved_with_the_form_then_applied_after_the_libraries_and_before_the_sync()
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1", "2");
        _sync.Setup(service => service.PrepareServerFolders()).Callback(() => _steps.Add("libraries"));
        _sync.Setup(service => service.StartServerSync(id)).Returns(() => Step("sync", SyncStartResult.Started));

        var result = await Controller().SaveServerEdit(id, Edit(libraries: ["1"], mode: LibraryMode.ByRemoteLibrary), CancellationToken.None);

        Assert.IsType<ServerStatus>(result.Value);
        Assert.Equal(LibraryMode.ByRemoteLibrary, StoredMode);
        Assert.Equal(["libraries", "mode", "sync"], _steps.Where(step => step != "logout"));
    }

    [Fact]
    public async Task With_the_remote_down_a_new_library_mode_is_saved_and_applied()
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");
        _offline = true;

        var result = await Controller().SaveServerEdit(id, Edit(mode: LibraryMode.None), CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, Assert.IsType<ServerStatus>(result.Value).RemoteWarning);
        Assert.Equal(LibraryMode.None, StoredMode);
        _libraries.Verify(manager => manager.ApplyMode(id), Times.Once);
        _sync.Verify(service => service.StartServerSync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LibraryMode.ByType)]
    public async Task A_library_mode_left_as_it_is_applies_nothing(LibraryMode? mode)
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");

        await Controller().SaveServerEdit(id, Edit(name: "Casa", mode: mode), CancellationToken.None);

        Assert.Equal(LibraryMode.ByType, StoredMode);
        _libraries.Verify(manager => manager.ApplyMode(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_library_mode_is_refused_on_its_field_by_the_check_and_the_save()
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");

        var check = await Controller().CheckServerEdit(id, Edit(mode: (LibraryMode)7), CancellationToken.None);
        var save = await Controller().SaveServerEdit(id, Edit(mode: (LibraryMode)7), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status400BadRequest, status);
            Assert.Equal(RemoteEditField.LibraryMode, failure.Field);
        }

        Assert.Equal(LibraryMode.ByType, StoredMode);
        Assert.Empty(Requests);
    }

    [Fact]
    public async Task A_save_that_fails_keeps_the_saved_library_mode()
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");
        _offline = true;

        await Controller().SaveServerEdit(id, Edit(url: OtherUrl, mode: LibraryMode.None), CancellationToken.None);

        Assert.Equal(LibraryMode.ByType, StoredMode);
        _libraries.Verify(manager => manager.ApplyMode(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_check_of_a_new_library_mode_lists_the_libraries_it_deletes_and_releases(bool offline)
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");
        var change = new LibraryModeChange(LibraryMode.ByType, LibraryMode.None, ["Friend - Film"], ["Film di casa"]);
        _libraries.Setup(manager => manager.PreviewModeChange(id, LibraryMode.None)).Returns(change);
        _offline = offline;

        var result = await Controller().CheckServerEdit(id, Edit(mode: LibraryMode.None), CancellationToken.None);

        var check = Assert.IsType<ServerEditCheck>(result.Value);
        Assert.Equal(change, check.Consequences.ModeChange);
        Assert.True(check.Consequences.Any);
        Assert.Equal(offline, check.RemoteOffline);
        Assert.Equal(LibraryMode.ByType, StoredMode);
    }

    [Fact]
    public async Task The_check_of_an_unchanged_library_mode_lists_no_mode_change()
    {
        var id = await AddInModeAsync(LibraryMode.ByType, "1");

        var result = await Controller().CheckServerEdit(id, Edit(mode: LibraryMode.ByType), CancellationToken.None);

        Assert.Null(Assert.IsType<ServerEditCheck>(result.Value).Consequences.ModeChange);
        Assert.False(result.Value!.Consequences.Any);
    }
}
