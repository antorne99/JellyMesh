using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

// Rows 566, 567: no server is renamed, and no failed rename resumes, while the folder migration has not ended or
// while that cannot be read.
public sealed partial class ServerEditTests
{
    private static Mock<IStubRegistry> Layout(bool? olderLayout)
    {
        var stubs = new Mock<IStubRegistry>();
        stubs.Setup(registry => registry.UsesOlderLayout()).Returns(olderLayout);
        return stubs;
    }

    [Theory]
    [InlineData(true, RemoteFailure.MigrationUnfinished)]
    [InlineData(null, RemoteFailure.MigrationUnknown)]
    public async Task A_new_name_is_refused_on_the_name_until_the_folder_migration_ends(bool? olderLayout, RemoteFailure expected)
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));
        var stubs = Layout(olderLayout);
        var before = Snapshot();

        var check = await Controller(renamer.Object, stubs.Object).CheckServerEdit(id, Edit(name: "Casa"), CancellationToken.None);
        var save = await Controller(renamer.Object, stubs.Object).SaveServerEdit(id, Edit(name: "Casa", url: OtherUrl), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(expected, failure.Failure);
            Assert.Equal(RemoteEditField.Name, failure.Field);
            Assert.Equal(FederationAdminController.FailureMessage(expected), failure.Message);
        }

        Assert.Equal(before, Snapshot());
        Assert.Empty(Requests);
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_new_name_is_refused_as_not_known_while_the_database_is_suspended()
    {
        var id = await AddAsync("1");
        var stubs = new Mock<IStubRegistry>();
        stubs.Setup(registry => registry.UsesOlderLayout()).Throws(new StoreSuspendedException());

        var save = await Controller(stubs: stubs.Object).SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Equal((StatusCodes.Status409Conflict, RemoteFailure.MigrationUnknown), (Refusal(save).Status, Refusal(save).Failure.Failure));
        Assert.Equal("Friend", _fixture.Store.Configuration.Servers.Single().Name);
    }

    [Fact]
    public void The_refusals_say_when_the_name_can_change()
    {
        Assert.Contains("after the folder migration ends", FederationAdminController.FailureMessage(RemoteFailure.MigrationUnfinished), StringComparison.Ordinal);
        Assert.Contains("Try again", FederationAdminController.FailureMessage(RemoteFailure.MigrationUnknown), StringComparison.Ordinal);
        Assert.Contains("resumes after the folder migration ends", FederationAdminController.FailureMessage(RemoteFailure.RenameWaitsForMigration), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task An_edit_that_keeps_the_name_is_saved_while_the_folder_migration_has_not_ended(bool? olderLayout)
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));

        var save = await Controller(renamer.Object, Layout(olderLayout).Object).SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.Equal(OtherUrl, Assert.IsType<ServerStatus>(save.Value).Url);
        Assert.Equal("Friend", _fixture.Store.Configuration.Servers.Single().Name);
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_new_name_is_saved_and_renamed_once_the_folder_migration_has_ended()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));

        var save = await Controller(renamer.Object, Layout(false).Object).SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Equal("Casa", Assert.IsType<ServerStatus>(save.Value).Name);
        renamer.Verify(service => service.Rename(id, "Casa", true), Times.Once);
    }

    // Row 567: a save of the same name does not resume a failed rename until the folder migration ends.
    [Theory]
    [InlineData(true, RemoteFailure.RenameWaitsForMigration)]
    [InlineData(null, RemoteFailure.MigrationUnknown)]
    public async Task The_resume_of_a_failed_rename_is_refused_on_the_name_until_the_folder_migration_ends(bool? olderLayout, RemoteFailure expected)
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, true));
        var stubs = Layout(olderLayout);
        var before = Snapshot();

        var check = await Controller(renamer.Object, stubs.Object).CheckServerEdit(id, Edit(), CancellationToken.None);
        var save = await Controller(renamer.Object, stubs.Object).SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(expected, failure.Failure);
            Assert.Equal(RemoteEditField.Name, failure.Field);
            Assert.Equal(FederationAdminController.FailureMessage(expected), failure.Message);
        }

        Assert.Equal(before, Snapshot());
        Assert.Empty(Requests);
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    // The page leaves the name out while a failed rename waits for the migration, so the other fields still save.
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task An_edit_without_the_name_is_saved_while_a_failed_rename_waits_for_the_folder_migration(bool? olderLayout)
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, true));
        var edit = new ServerEditRequest { Url = OtherUrl, Username = TestValues.Username };

        var check = await Controller(renamer.Object, Layout(olderLayout).Object).CheckServerEdit(id, edit, CancellationToken.None);
        var save = await Controller(renamer.Object, Layout(olderLayout).Object).SaveServerEdit(id, edit, CancellationToken.None);

        Assert.Null(Assert.IsType<ServerEditCheck>(check.Value).Consequences.Rename);
        Assert.Equal(OtherUrl, Assert.IsType<ServerStatus>(save.Value).Url);
        Assert.Equal("Friend", _fixture.Store.Configuration.Servers.Single().Name);
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_rename_resumes_with_a_save_of_the_same_name_once_the_folder_migration_has_ended()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, true));

        var save = await Controller(renamer.Object, Layout(false).Object).SaveServerEdit(id, Edit(), CancellationToken.None);

        Assert.IsType<ServerStatus>(save.Value);
        renamer.Verify(service => service.Rename(id, "Friend", true), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task The_status_tells_the_page_whether_the_older_layouts_are_in_use(bool? olderLayout)
    {
        await AddAsync("1");

        var status = await Controller(stubs: Layout(olderLayout).Object).GetStatus(CancellationToken.None);

        Assert.Equal(olderLayout, status.Value!.OlderLayoutInUse);
    }
}
