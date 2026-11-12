using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class VersionLinkPlannerTests
{
    private static readonly Guid _local = Guid.NewGuid();
    private static readonly Guid _a = Guid.NewGuid();
    private static readonly Guid _b = Guid.NewGuid();
    private static readonly Guid _c = Guid.NewGuid();
    private static readonly Guid _outsider = Guid.NewGuid();
    private static readonly Guid _other = Guid.NewGuid();

    private static readonly Dictionary<string, Guid> _stubs = new() { ["sa"] = _a, ["sb"] = _b, ["sc"] = _c };

    private static VersionLinkState Fresh(Guid id) => new(id, null, []);

    private static VersionLinkState Alternate(Guid id, Guid primary) => new(id, primary, []);

    private static VersionLinkState Primary(Guid id, params Guid[] alternates) => new(id, null, alternates);

    private static string Pick(IReadOnlyList<string> stubs, string best)
    {
        Assert.Contains(best, stubs);
        return best;
    }

    [Fact]
    public void TheLocalCopyIsThePrimaryAndClearsTheStubOne()
    {
        var primary = VersionLinkPlanner.ChoosePrimary([_local], _stubs, "sb", stubs => Pick(stubs, "sa"));

        Assert.Equal(new VersionPrimary(_local, null), primary);
    }

    [Fact]
    public void WithoutALocalCopyTheBestStubIsChosenAndRecorded()
    {
        var primary = VersionLinkPlanner.ChoosePrimary([], _stubs, null, stubs => Pick(stubs, "sc"));

        Assert.Equal(new VersionPrimary(_c, "sc"), primary);
    }

    [Fact]
    public void TheRecordedStubStaysThePrimaryWhileItIsInTheGroup()
    {
        var primary = VersionLinkPlanner.ChoosePrimary([], _stubs, "sb", _ => throw new InvalidOperationException("no new choice"));

        Assert.Equal(new VersionPrimary(_b, "sb"), primary);
    }

    [Fact]
    public void ARecordedStubWithoutItsItemLendsThePlaceToTheBestOneAndStaysRecorded()
    {
        var primary = VersionLinkPlanner.ChoosePrimary([], new Dictionary<string, Guid> { ["sa"] = _a, ["sc"] = _c }, "sb", stubs => Pick(stubs, "sa"));

        Assert.Equal(new VersionPrimary(_a, "sb"), primary);
    }

    [Fact]
    public void AFreshGroupSavesTheAlternatesThenThePrimary()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a, _b], [Fresh(_local), Fresh(_a), Fresh(_b)]);

        Assert.Equal(
            [new VersionLinkUpdate(_a, _local, []), new VersionLinkUpdate(_b, _local, []), new VersionLinkUpdate(_local, null, [_a, _b])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_a, _b], plan.Joined);
    }

    [Fact]
    public void NothingIsSavedWhenJellyfinAlreadyMatches()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a, _b], [Primary(_local, _b, _a), Alternate(_a, _local), Alternate(_b, _local)]);

        Assert.Empty(plan.Updates);
        Assert.Empty(plan.Joined);
    }

    [Fact]
    public void ALostLinkIsMadeAgain()
    {
        // A stub that vanished at a scan comes back with the same ID and no link.
        var plan = VersionLinkPlanner.Plan(_local, [_a, _b], [Primary(_local, _a), Alternate(_a, _local), Fresh(_b)]);

        Assert.Equal(
            [new VersionLinkUpdate(_b, _local, []), new VersionLinkUpdate(_local, null, [_a, _b])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_b], plan.Joined);
    }

    [Fact]
    public void AStubJellyfinPromotedGoesBackToBeingAnAlternate()
    {
        // The local primary was deleted: Jellyfin made A the primary, the plugin chose B.
        var plan = VersionLinkPlanner.Plan(_b, [_a, _c], [Primary(_a, _b, _c), Alternate(_b, _a), Alternate(_c, _a)]);

        Assert.Equal(
            [new VersionLinkUpdate(_a, _b, []), new VersionLinkUpdate(_c, _b, []), new VersionLinkUpdate(_b, null, [_a, _c])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_a, _c, _b], plan.Joined);
    }

    [Fact]
    public void AGroupTheAdminSplitIsLinkedAgain()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a, _b], [Fresh(_local), Fresh(_a), Fresh(_b)]);

        Assert.Equal(3, plan.Updates.Count);
    }

    [Fact]
    public void AnItemTheAdminMergedIntoTheGroupIsDetached()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a], [Primary(_local, _a, _outsider), Alternate(_a, _local), Alternate(_outsider, _local)]);

        Assert.Equal(
            [new VersionLinkUpdate(_outsider, null, []), new VersionLinkUpdate(_local, null, [_a])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Empty(plan.Joined);
    }

    [Fact]
    public void AMemberTheAdminMergedElsewhereIsTakenBackAndTheOtherGroupKeepsTheRest()
    {
        var plan = VersionLinkPlanner.Plan(
            _local,
            [_a],
            [Primary(_local), Alternate(_a, _outsider), Primary(_outsider, _a, _other), Alternate(_other, _outsider)]);

        Assert.Equal(
            [new VersionLinkUpdate(_outsider, null, [_other]), new VersionLinkUpdate(_a, _local, []), new VersionLinkUpdate(_local, null, [_a])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_a], plan.Joined);
    }

    [Fact]
    public void AFilmTheAdminMergedWithTheLocalCopyStaysLinked()
    {
        var plan = VersionLinkPlanner.Plan(
            _local,
            [_a],
            [Primary(_local, _other), Fresh(_a), Alternate(_other, _local)],
            id => id == _local || id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_a, _local, []), new VersionLinkUpdate(_local, null, [_a, _other])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_a], plan.Joined);
    }

    [Fact]
    public void AStubTheAdminMergedWithTheLocalCopyIsStillDetached()
    {
        var plan = VersionLinkPlanner.Plan(
            _local,
            [_a],
            [Primary(_local, _a, _outsider, _other), Alternate(_a, _local), Alternate(_outsider, _local), Alternate(_other, _local)],
            id => id == _local || id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_outsider, null, []), new VersionLinkUpdate(_local, null, [_a, _other])],
            plan.Updates,
            UpdateComparer.Instance);
    }

    [Fact]
    public void AFilmTheAdminMergedWithAStubPrimaryIsDetached()
    {
        var plan = VersionLinkPlanner.Plan(_b, [_a], [Primary(_b, _a, _other), Alternate(_a, _b), Alternate(_other, _b)], id => id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_other, null, []), new VersionLinkUpdate(_b, null, [_a])],
            plan.Updates,
            UpdateComparer.Instance);
    }

    [Fact]
    public void ALocalCopyMergedIntoAnotherFilmKeepsThatLink()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a], [Alternate(_local, _other), Fresh(_a), Primary(_other, _local)], id => id == _local || id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_a, _local, []), new VersionLinkUpdate(_local, _other, [_a])],
            plan.Updates,
            UpdateComparer.Instance);
        Assert.Equal([_a], plan.Joined);
    }

    [Fact]
    public void ALoneLocalCopyLosesOnlyItsStubs()
    {
        var plan = VersionLinkPlanner.Plan(
            _local,
            [],
            [Primary(_local, _a, _other), Alternate(_a, _local), Alternate(_other, _local)],
            id => id == _local || id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_a, null, []), new VersionLinkUpdate(_local, null, [_other])],
            plan.Updates,
            UpdateComparer.Instance);
    }

    [Fact]
    public void ALoneStubIsDetachedFromEverything()
    {
        var plan = VersionLinkPlanner.Plan(
            _a,
            [],
            [Alternate(_a, _local), Primary(_local, _a, _other), Alternate(_other, _local)],
            id => id == _local || id == _other);

        Assert.Equal(
            [new VersionLinkUpdate(_local, null, [_other]), new VersionLinkUpdate(_a, null, [])],
            plan.Updates,
            UpdateComparer.Instance);
    }

    [Fact]
    public void AMemberWithoutAKnownStateIsLinkedAsNew()
    {
        var plan = VersionLinkPlanner.Plan(_local, [_a], []);

        Assert.Equal(
            [new VersionLinkUpdate(_a, _local, []), new VersionLinkUpdate(_local, null, [_a])],
            plan.Updates,
            UpdateComparer.Instance);
    }

    private sealed class UpdateComparer : IEqualityComparer<VersionLinkUpdate>
    {
        public static readonly UpdateComparer Instance = new();

        public bool Equals(VersionLinkUpdate? x, VersionLinkUpdate? y) =>
            x is not null && y is not null && x.ItemId == y.ItemId && x.PrimaryVersionId == y.PrimaryVersionId && x.LinkedIds.SequenceEqual(y.LinkedIds);

        public int GetHashCode(VersionLinkUpdate obj) => obj.ItemId.GetHashCode();
    }
}
