using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Provider;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ChangeJournalTests
{
    private const string Share = "share";

    private static readonly Guid _first = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly ChangeJournal _journal = new();

    private string Position() => _journal.Read(null, Share, 10).Cursor;

    [Fact]
    public void Without_a_cursor_it_says_where_it_stands_and_to_read_everything()
    {
        _journal.Record(_first);

        var window = _journal.Read(null, Share, 10);

        Assert.True(window.Reset);
        Assert.Empty(window.ItemIds);
        Assert.False(window.HasMore);
    }

    [Fact]
    public void It_gives_what_changed_after_the_cursor()
    {
        _journal.Record(_first);
        var cursor = Position();
        _journal.Record(_second);

        var window = _journal.Read(cursor, Share, 10);

        Assert.False(window.Reset);
        Assert.Equal([_second], window.ItemIds);
    }

    [Fact]
    public void Nothing_new_keeps_the_cursor()
    {
        var cursor = Position();

        var window = _journal.Read(cursor, Share, 10);

        Assert.False(window.Reset);
        Assert.Empty(window.ItemIds);
        Assert.Equal(cursor, window.Cursor);
    }

    [Fact]
    public void An_item_changed_twice_comes_once()
    {
        var cursor = Position();
        _journal.Record(_first);
        _journal.Record(_first);

        Assert.Equal([_first], _journal.Read(cursor, Share, 10).ItemIds);
    }

    [Fact]
    public void Pages_follow_each_other_without_losing_anything()
    {
        var cursor = Position();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        ids.ForEach(_journal.Record);

        var first = _journal.Read(cursor, Share, 2);
        var second = _journal.Read(first.Cursor, Share, 2);
        var third = _journal.Read(second.Cursor, Share, 2);

        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        Assert.Equal(ids, first.ItemIds.Concat(second.ItemIds).Concat(third.ItemIds));
    }

    [Fact]
    public void A_cursor_of_another_run_of_the_server_cannot_be_continued()
    {
        var cursor = new ChangeJournal().Read(null, Share, 10).Cursor;

        Assert.True(_journal.Read(cursor, Share, 10).Reset);
    }

    [Fact]
    public void A_new_share_cannot_be_continued_because_old_items_were_never_read()
    {
        var cursor = Position();

        Assert.True(_journal.Read(cursor, "other share", 10).Reset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c.d")]
    public void A_bad_cursor_cannot_be_continued(string cursor)
    {
        Assert.True(_journal.Read(cursor, Share, 10).Reset);
    }

    [Fact]
    public void A_cursor_from_the_future_cannot_be_continued()
    {
        var parts = Position().Split('.');

        Assert.True(_journal.Read(parts[0] + "." + parts[1] + ".99", Share, 10).Reset);
    }

    [Fact]
    public void A_cursor_the_list_moved_past_cannot_be_continued_so_nothing_is_missed_silently()
    {
        var cursor = Position();
        _journal.Record(_first);
        var after = _journal.Read(cursor, Share, 10).Cursor;
        for (var i = 0; i <= ChangeJournal.Capacity; i++)
        {
            _journal.Record(_second);
        }

        Assert.True(_journal.Read(cursor, Share, 10).Reset);
        Assert.True(_journal.Read(after, Share, 10).Reset);
    }

    [Fact]
    public void A_reset_gives_a_position_that_can_be_continued()
    {
        var position = _journal.Read("garbage", Share, 10).Cursor;
        _journal.Record(_first);

        var window = _journal.Read(position, Share, 10);

        Assert.False(window.Reset);
        Assert.Equal([_first], window.ItemIds);
    }
}
