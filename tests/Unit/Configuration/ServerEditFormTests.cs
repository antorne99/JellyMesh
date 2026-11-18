using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

// F35: what the single edit form of the page sends, the library mode among its fields, and the routes it replaced.
public sealed partial class ServerEditTests
{
    private static readonly AutomaticLibrariesState TwoLibraries = new(
        true,
        false,
        [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Friend - Film"), new AutomaticLibraryStatus(ServerLibraryKind.Series, "Serie") { Adopted = true }],
        [],
        AutomaticLibrariesError.None)
    { Mode = LibraryMode.ByType };

    private static ServerEditRequest WithMode(ServerEditRequest edit, LibraryMode? mode) => new()
    {
        Name = edit.Name,
        Url = edit.Url,
        Username = edit.Username,
        Password = edit.Password,
        LibraryIds = edit.LibraryIds,
        LibraryMode = mode
    };

    private void SavedMode(Guid id, LibraryMode mode, AutomaticLibrariesState? state = null)
    {
        _fixture.Store.Configuration.Servers.Single(server => server.Id == id).LibraryMode = mode;
        _libraries.Setup(manager => manager.GetState(id)).Returns(state ?? new AutomaticLibrariesState(mode != LibraryMode.None, false, [], [], AutomaticLibrariesError.None) { Mode = mode });
    }

    private void Starts()
    {
        var started = new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.Started, Task.CompletedTask, []);
        _libraries.Setup(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>())).Returns(started);
        _libraries.Setup(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>())).Returns(started);
    }

    // The page never gets the saved user back, so an empty user keeps it, as an empty password does (row 432).
    [Fact]
    public async Task An_empty_user_keeps_the_saved_one_and_changes_no_login()
    {
        var id = await AddAsync("1");
        await KeepLoginAsync(id);

        var check = await Controller().CheckServerEdit(id, Edit(name: "Casa", user: string.Empty), CancellationToken.None);
        var save = await Controller().SaveServerEdit(id, Edit(name: "Casa", user: string.Empty), CancellationToken.None);

        Assert.Equal(0, Assert.IsType<ServerEditCheck>(check.Value).Consequences.StoppedStreams);
        Assert.Equal("Casa", Assert.IsType<ServerStatus>(save.Value).Name);
        Assert.Equal(TestValues.Username, _fixture.Vault.Unprotect(id, _fixture.Store.Configuration.Servers.Single().EncryptedCredentials)!.Username);
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout" && DeviceOf(request) == $"jellymesh-{id:N}");
    }

    [Fact]
    public async Task An_empty_user_is_refused_on_the_user_when_the_saved_one_no_longer_decrypts()
    {
        var id = await AddAsync("1");
        _fixture.KeySource.Key = TestValues.OtherKey;
        var before = Snapshot();

        var save = await Controller().SaveServerEdit(id, Edit(user: string.Empty, password: "new-pass"), CancellationToken.None);

        Assert.Equal((StatusCodes.Status400BadRequest, RemoteEditField.Username), (Refusal(save).Status, Refusal(save).Failure.Field));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_check_lists_the_switch_to_none_with_the_libraries_it_deletes_and_those_it_releases()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        var before = Snapshot();

        var result = await Controller().CheckServerEdit(id, WithMode(Edit(), LibraryMode.None), CancellationToken.None);

        var check = Assert.IsType<ServerEditCheck>(result.Value);
        var change = check.Consequences.ModeChange!;
        Assert.Equal((LibraryMode.ByType, LibraryMode.None), (change.From, change.To));
        Assert.Equal(["Friend - Film"], change.Deleted);
        Assert.Equal(["Serie"], change.Released);
        Assert.True(check.Consequences.Any);
        Assert.Equal(before, Snapshot());
        _libraries.Verify(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_save_with_no_mode_keeps_it_and_starts_nothing()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();

        var check = await Controller().CheckServerEdit(id, Edit(name: "Casa"), CancellationToken.None);
        await Controller().SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Null(Assert.IsType<ServerEditCheck>(check.Value).Consequences.ModeChange);
        Assert.Equal(LibraryMode.ByType, _fixture.Store.Configuration.Servers.Single().LibraryMode);
        _libraries.Verify(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>()), Times.Never);
        _libraries.Verify(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    // "Recreate missing libraries" sends the saved mode again, which brings back the libraries of the registry.
    [Fact]
    public async Task The_saved_mode_sent_again_is_no_consequence_and_applies_it_again()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();

        var check = await Controller().CheckServerEdit(id, WithMode(Edit(), LibraryMode.ByType), CancellationToken.None);
        await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.ByType), CancellationToken.None);

        Assert.False(Assert.IsType<ServerEditCheck>(check.Value).Consequences.Any);
        _libraries.Verify(manager => manager.Enable(id, LibraryMode.ByType), Times.Once);
        _libraries.Verify(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task None_sent_again_starts_nothing()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.None);
        Starts();

        await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.None), CancellationToken.None);

        _libraries.Verify(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>()), Times.Never);
        _libraries.Verify(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_save_to_none_deletes_the_libraries_after_the_save_with_the_confirmation_of_the_form()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();

        var result = await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.None), CancellationToken.None);

        Assert.IsType<ServerStatus>(result.Value);
        _libraries.Verify(manager => manager.Disable(id, true), Times.Once);
    }

    [Fact]
    public async Task A_save_from_none_keeps_the_mode_and_starts_its_libraries()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.None);
        Starts();

        var check = await Controller().CheckServerEdit(id, WithMode(Edit(), LibraryMode.ByRemoteLibrary), CancellationToken.None);
        await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.ByRemoteLibrary), CancellationToken.None);

        var change = Assert.IsType<ServerEditCheck>(check.Value).Consequences.ModeChange!;
        Assert.Equal((LibraryMode.None, LibraryMode.ByRemoteLibrary), (change.From, change.To));
        Assert.Empty(change.Deleted);
        Assert.Equal(LibraryMode.ByRemoteLibrary, _fixture.Store.Configuration.Servers.Single().LibraryMode);
        _libraries.Verify(manager => manager.Enable(id, LibraryMode.ByRemoteLibrary), Times.Once);
    }

    // Until F40 the mode of a server with libraries changes only through none; the refusal comes before anything is saved.
    [Fact]
    public async Task A_change_between_two_modes_with_libraries_is_refused_on_the_mode_before_the_save()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();
        var before = Snapshot();

        var check = await Controller().CheckServerEdit(id, WithMode(Edit(name: "Casa"), LibraryMode.ByRemoteLibrary), CancellationToken.None);
        var save = await Controller().SaveServerEdit(id, WithMode(Edit(name: "Casa"), LibraryMode.ByRemoteLibrary), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(RemoteEditField.LibraryMode, failure.Field);
            Assert.Equal(FederationAdminController.RefusalMessage(AutomaticLibrariesOutcome.ModeChangeNotAllowed), failure.Message);
        }

        Assert.Equal(before, Snapshot());
        Assert.Equal(LibraryMode.ByType, _fixture.Store.Configuration.Servers.Single().LibraryMode);
        Assert.Empty(Requests);
        _libraries.Verify(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>()), Times.Never);
    }

    [Fact]
    public async Task A_mode_change_while_the_libraries_are_being_updated_is_refused_on_the_mode()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries with { Busy = true });
        var before = Snapshot();

        var save = await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.None), CancellationToken.None);

        Assert.Equal((StatusCodes.Status409Conflict, RemoteEditField.LibraryMode), (Refusal(save).Status, Refusal(save).Failure.Field));
        Assert.Equal(FederationAdminController.RefusalMessage(AutomaticLibrariesOutcome.Busy), Refusal(save).Failure.Message);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task An_unknown_mode_is_refused_on_the_mode()
    {
        var id = await AddAsync("1");

        var check = await Controller().CheckServerEdit(id, WithMode(Edit(), (LibraryMode)7), CancellationToken.None);

        Assert.Equal((StatusCodes.Status400BadRequest, RemoteEditField.LibraryMode), (Refusal(check).Status, Refusal(check).Failure.Field));
        Assert.Empty(Requests);
    }

    // D28: the mode stays editable with the remote down.
    [Fact]
    public async Task A_mode_change_with_the_remote_down_is_saved_and_applied()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();
        _offline = true;

        var result = await Controller().SaveServerEdit(id, WithMode(Edit(), LibraryMode.None), CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, Assert.IsType<ServerStatus>(result.Value).RemoteWarning);
        _libraries.Verify(manager => manager.Disable(id, true), Times.Once);
    }

    // The page says a failed rename finishes with a save of the same name, as the old rename endpoint did.
    [Fact]
    public async Task A_save_of_the_same_name_runs_a_failed_rename_again_and_its_check_lists_it()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, true));

        var check = await Controller(renamer.Object).CheckServerEdit(id, Edit(), CancellationToken.None);
        await Controller(renamer.Object).SaveServerEdit(id, Edit(), CancellationToken.None);

        var rename = Assert.IsType<ServerEditCheck>(check.Value).Consequences.Rename!;
        Assert.Equal(("Friend", "Friend"), (rename.OldName, rename.NewName));
        renamer.Verify(service => service.Rename(id, "Friend", true), Times.Once);
    }

    // "Recreate missing libraries" sends no name or address, so it renames nothing and runs no failed rename again.
    [Fact]
    public async Task A_form_with_no_name_or_address_keeps_them_and_runs_no_failed_rename()
    {
        var id = await AddAsync("1");
        SavedMode(id, LibraryMode.ByType, TwoLibraries);
        Starts();
        var renamer = Renamer(new ServerRenameState(false, false, true));
        var form = new ServerEditRequest { Username = string.Empty, Password = string.Empty, LibraryMode = LibraryMode.ByType };

        var check = await Controller(renamer.Object).CheckServerEdit(id, form, CancellationToken.None);
        var save = await Controller(renamer.Object).SaveServerEdit(id, form, CancellationToken.None);

        Assert.False(Assert.IsType<ServerEditCheck>(check.Value).Consequences.Any);
        var saved = Assert.IsType<ServerStatus>(save.Value);
        Assert.Equal(("Friend", TestValues.Url), (saved.Name, saved.Url));
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        _libraries.Verify(manager => manager.Enable(id, LibraryMode.ByType), Times.Once);
    }

    [Fact]
    public async Task A_save_of_the_same_name_starts_no_rename_when_none_failed()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, true, false));

        await Controller(renamer.Object).SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    // Row 432: the check and the save replace the routes of credentials, rename, libraries and automatic libraries.
    [Theory]
    [InlineData("Credentials")]
    [InlineData("Libraries")]
    [InlineData("AutomaticLibraries")]
    [InlineData("Rename")]
    public void The_routes_the_edit_form_replaced_are_gone(string route)
    {
        var templates = typeof(FederationAdminController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .ToList();

        Assert.DoesNotContain("Servers/{serverId:guid}/" + route, templates);
        Assert.Contains("Servers/{serverId:guid}/Rename/Cancel", templates);
        Assert.DoesNotContain("'/" + route + "'", PageScript(), StringComparison.Ordinal);
    }

    private static string PageScript()
    {
        var name = typeof(FederationAdminController).Namespace + ".configPage.html";
        using var stream = typeof(FederationAdminController).Assembly.GetManifestResourceStream(name)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
