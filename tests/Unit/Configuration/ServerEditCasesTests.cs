using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

// Row 434: what a save of the edit form sets off, and the cases around it.
public sealed partial class ServerEditTests
{
    [Fact]
    public async Task A_check_waits_for_the_edit_in_progress_and_sends_nothing_meanwhile()
    {
        var id = await AddAsync("1");
        var held = await _fixture.Registry.EnterEditAsync(id, CancellationToken.None);

        var check = Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);
        await Task.Delay(100);

        Assert.False(check.IsCompleted);
        Assert.Empty(Requests);
        held!.Dispose();
        Assert.IsType<ServerEditCheck>((await check).Value);
    }

    [Fact]
    public async Task Two_saves_run_one_at_a_time_and_the_last_one_wins()
    {
        var id = await AddAsync("1");
        var held = await _fixture.Registry.EnterEditAsync(id, CancellationToken.None);

        var first = Controller().SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);
        var second = Controller().SaveServerEdit(id, Edit(name: "Ufficio"), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(first.IsCompleted || second.IsCompleted);
        held!.Dispose();
        await Task.WhenAll(first, second);

        Assert.Equal("Ufficio", _fixture.Store.Configuration.Servers.Single().Name);
    }

    [Fact]
    public async Task An_edit_of_a_server_being_removed_is_refused_and_the_removal_waits_for_the_edit_in_progress()
    {
        var id = await AddAsync("1");
        var held = await _fixture.Registry.EnterEditAsync(id, CancellationToken.None);

        var removal = Controller().RemoveServer(id, CancellationToken.None);
        await Task.Delay(100);
        var check = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);
        var save = await Controller().SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.False(removal.IsCompleted);
        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(RemoteFailure.BeingRemoved, failure.Failure);
            Assert.Equal("This server is being removed.", failure.Message);
        }

        Assert.Equal("Friend", _fixture.Store.Configuration.Servers.Single().Name);
        held!.Dispose();
        await removal;
    }

    [Fact]
    public async Task A_removal_that_ended_lets_the_server_be_edited_again()
    {
        var id = await AddAsync("1");

        // The sync stub removes nothing, so the server stays.
        await Controller().RemoveServer(id, CancellationToken.None);
        var result = await Controller().SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Equal("Casa", Assert.IsType<ServerStatus>(result.Value).Name);
    }

    [Fact]
    public async Task A_new_name_is_saved_then_renamed_through_the_renamer_without_asking_again()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));

        var result = await Controller(renamer.Object).SaveServerEdit(id, Edit(name: " Casa "), CancellationToken.None);

        Assert.Equal("Casa", Assert.IsType<ServerStatus>(result.Value).Name);
        renamer.Verify(service => service.Rename(id, "Casa", true), Times.Once);
    }

    [Fact]
    public async Task An_unchanged_name_starts_no_rename()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));

        await Controller(renamer.Object).SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task A_new_name_is_refused_on_the_name_while_a_rename_has_not_finished(bool busy, bool pending, bool failed)
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(busy, pending, failed));
        var before = Snapshot();

        var check = await Controller(renamer.Object).CheckServerEdit(id, Edit(name: "Casa"), CancellationToken.None);
        var save = await Controller(renamer.Object).SaveServerEdit(id, Edit(name: "Casa", url: OtherUrl), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(RemoteFailure.RenameUnfinished, failure.Failure);
            Assert.Equal(RemoteEditField.Name, failure.Field);
        }

        Assert.Equal(before, Snapshot());
        Assert.Empty(Requests);
        renamer.Verify(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_new_name_is_refused_while_a_renamed_library_waits_for_a_restart()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));
        renamer.SetupGet(service => service.NeedsRestart).Returns(true);

        var result = await Controller(renamer.Object).SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Equal(RemoteFailure.RenameUnfinished, Refusal(result).Failure.Failure);
    }

    [Fact]
    public async Task A_rename_that_has_not_finished_leaves_the_other_fields_editable()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, true, false));

        var result = await Controller(renamer.Object).SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.IsType<ServerStatus>(result.Value);
        Assert.Equal(OtherUrl, _fixture.Store.Configuration.Servers.Single().Url);
    }

    [Theory]
    [InlineData("Other")]
    [InlineData(" other ")]
    public async Task A_name_another_server_has_is_refused_on_the_name(string name)
    {
        var id = await AddAsync("1");
        _fixture.Store.Configuration.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Other", Url = OtherUrl });
        var before = Snapshot(id);

        var check = await Controller().CheckServerEdit(id, Edit(name: name), CancellationToken.None);
        var save = await Controller().SaveServerEdit(id, Edit(name: name), CancellationToken.None);

        foreach (var (status, failure) in new[] { Refusal(check), Refusal(save) })
        {
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Equal(RemoteFailure.NameTaken, failure.Failure);
            Assert.Equal(RemoteEditField.Name, failure.Field);
            Assert.Equal("Another server already has this name.", failure.Message);
        }

        Assert.Equal(before, Snapshot(id));
    }

    [Fact]
    public async Task A_name_that_differs_only_in_case_from_the_saved_one_is_a_rename()
    {
        var id = await AddAsync("1");
        var renamer = Renamer(new ServerRenameState(false, false, false));

        await Controller(renamer.Object).SaveServerEdit(id, Edit(name: "FRIEND"), CancellationToken.None);

        renamer.Verify(service => service.Rename(id, "FRIEND", true), Times.Once);
    }

    private static Mock<IServerRenamer> Renamer(ServerRenameState state)
    {
        var renamer = new Mock<IServerRenamer>();
        renamer.Setup(service => service.GetState(It.IsAny<Guid>())).Returns(state);
        renamer.Setup(service => service.Rename(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(new ServerRenameRequest(ServerRenameOutcome.Started, Task.CompletedTask, []));
        return renamer;
    }
}
