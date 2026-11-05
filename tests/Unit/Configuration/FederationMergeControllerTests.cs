using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public sealed class FederationMergeControllerTests
{
    private readonly InMemoryMergeStore _store = new();
    private readonly Mock<IFederationResolver> _resolver = new();

    private FederationMergeController Controller()
    {
        var service = new ManualMergeService(
            _resolver.Object,
            _store,
            Mock.Of<ILibraryMaterializer>(),
            TimeProvider.System,
            new CapturingLogger<ManualMergeService>());
        return new FederationMergeController(service);
    }

    private void ListTitles(params bool[] localFlags)
    {
        var titles = localFlags
            .Select((isLocal, index) => new MergeTitleView(
                "key" + index,
                isLocal,
                [],
                [new MergeCopyView(new MergedCopy { ServerId = "s", ItemId = "i" + index, IsLocal = isLocal }, "A", null, true)]))
            .ToList();
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 0, [], [])
        {
            MergeCases = [new MergeCaseView(DedupIssueKind.PossibleDuplicate, titles)]
        });
    }

    [Fact]
    public void EveryEndpointRequiresAnAdministrator()
    {
        var type = typeof(FederationMergeController);

        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("JellyMesh/Admin/Merges", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public async Task AMergeAnswersNoContent()
    {
        ListTitles(false, false);

        var result = await Controller().Merge(new MergeRequest(["key0", "key1"]), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(2, Assert.Single(_store.Merges).Copies.Count);
    }

    [Fact]
    public async Task OneTitleIsABadRequest()
    {
        ListTitles(false, false);

        var result = await Controller().Merge(new MergeRequest(["key0"]), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task TwoLocalTitlesAreABadRequestThatSaysWhy()
    {
        ListTitles(true, true, false);

        var result = await Controller().Merge(new MergeRequest(["key0", "key1"]), CancellationToken.None);

        var failure = Assert.IsType<FailureResponse>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("local", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutdatedListIsAConflict()
    {
        ListTitles(false, false);

        var result = await Controller().Merge(new MergeRequest(["key0", "gone"]), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task UndoingAnUnknownMergeIsNotFound()
    {
        var result = await Controller().Undo(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task AnUndoAnswersNoContent()
    {
        ListTitles(false, false);
        await Controller().Merge(new MergeRequest(["key0", "key1"]), CancellationToken.None);

        var result = await Controller().Undo(Assert.Single(_store.Merges).Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(_store.Merges);
    }

    [Fact]
    public void TheListReportsADamagedStore()
    {
        ListTitles(false, false);
        _store.IsDamaged = true;

        var list = Assert.IsType<ManualMergeList>(Controller().GetMerges().Value);

        Assert.True(list.IsStoreDamaged);
    }
}
