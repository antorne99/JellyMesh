using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>What the provider keeps in memory of the items hidden from peers.</summary>
public sealed class HiddenItemsTests
{
    private static readonly Guid _library = Guid.NewGuid();

    private readonly ManualClock _clock = new();
    private readonly HiddenItems _hidden;

    public HiddenItemsTests() => _hidden = new HiddenItems(_clock);

    [Fact]
    public void Items_are_kept_per_library_and_type()
    {
        var id = Guid.NewGuid();

        _hidden.Add(_library, BaseItemKind.Movie, [id, id]);

        Assert.Equal([id], _hidden.Of(_library, BaseItemKind.Movie));
        Assert.Empty(_hidden.Of(_library, BaseItemKind.Episode));
        Assert.Empty(_hidden.Of(Guid.NewGuid(), BaseItemKind.Movie));
    }

    [Fact]
    public void Items_over_the_most_kept_are_not_added()
    {
        _hidden.Add(_library, BaseItemKind.Movie, Enumerable.Range(0, HiddenItems.MaxPerType - 1).Select(_ => Guid.NewGuid()).ToList());
        _hidden.Add(_library, BaseItemKind.Movie, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Equal(HiddenItems.MaxPerType, _hidden.Of(_library, BaseItemKind.Movie).Count);
    }

    [Fact]
    public void Removed_items_are_forgotten_and_make_room()
    {
        var ids = Enumerable.Range(0, HiddenItems.MaxPerType).Select(_ => Guid.NewGuid()).ToList();
        _hidden.Add(_library, BaseItemKind.Movie, ids);
        var other = Guid.NewGuid();

        _hidden.Remove(_library, BaseItemKind.Movie, ids[..1]);
        _hidden.Add(_library, BaseItemKind.Movie, [other]);

        var known = _hidden.Of(_library, BaseItemKind.Movie);
        Assert.DoesNotContain(ids[0], known);
        Assert.Contains(other, known);
        Assert.Equal(HiddenItems.MaxPerType, known.Count);
    }

    [Fact]
    public void One_search_of_a_library_runs_at_a_time()
    {
        Assert.True(_hidden.TryBeginSearch(_library, BaseItemKind.Movie));
        Assert.False(_hidden.TryBeginSearch(_library, BaseItemKind.Movie));
        Assert.True(_hidden.TryBeginSearch(_library, BaseItemKind.Episode));

        _hidden.EndSearch(_library, BaseItemKind.Movie);

        Assert.True(_hidden.TryBeginSearch(_library, BaseItemKind.Movie));
    }

    [Fact]
    public void No_search_runs_when_no_more_items_are_kept()
    {
        _hidden.Add(_library, BaseItemKind.Movie, Enumerable.Range(0, HiddenItems.MaxPerType).Select(_ => Guid.NewGuid()).ToList());

        Assert.False(_hidden.TryBeginSearch(_library, BaseItemKind.Movie));
    }

    [Fact]
    public void Items_are_looked_up_again_once_per_interval()
    {
        Assert.False(_hidden.TryBeginCheck(_library, BaseItemKind.Movie));

        _hidden.Add(_library, BaseItemKind.Movie, [Guid.NewGuid()]);
        Assert.False(_hidden.TryBeginCheck(_library, BaseItemKind.Movie));

        _clock.Advance(HiddenItems.CheckInterval);
        Assert.True(_hidden.TryBeginCheck(_library, BaseItemKind.Movie));
        Assert.False(_hidden.TryBeginCheck(_library, BaseItemKind.Movie));
    }

    [Fact]
    public void Items_are_looked_up_again_when_the_clock_was_set_back()
    {
        _hidden.Add(_library, BaseItemKind.Movie, [Guid.NewGuid()]);

        _clock.Advance(-TimeSpan.FromDays(1));

        Assert.True(_hidden.TryBeginCheck(_library, BaseItemKind.Movie));
    }

    [Fact]
    public void Libraries_no_longer_there_are_forgotten()
    {
        var other = Guid.NewGuid();
        _hidden.Add(_library, BaseItemKind.Movie, [Guid.NewGuid()]);
        _hidden.Add(other, BaseItemKind.Movie, [Guid.NewGuid()]);

        _hidden.KeepOnly(new HashSet<Guid> { other });

        Assert.Empty(_hidden.Of(_library, BaseItemKind.Movie));
        Assert.Single(_hidden.Of(other, BaseItemKind.Movie));
    }
}
