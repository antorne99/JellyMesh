using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
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
}
