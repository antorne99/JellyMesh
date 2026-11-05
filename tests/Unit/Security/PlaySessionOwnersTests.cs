using System;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public class PlaySessionOwnersTests
{
    private static readonly Guid _owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly PlaySessionOwners _owners = new();

    private void Flood(Guid owner, int count)
    {
        for (var index = 0; index < count; index++)
        {
            _owners.Add(owner, "flood" + index);
        }
    }

    [Fact]
    public void An_owner_keeps_no_more_than_the_limit_and_the_oldest_goes_first()
    {
        _owners.Add(_owner, "oldest");

        Flood(_owner, PlaySessionOwners.MaxPerOwner);

        Assert.False(_owners.Owns(_owner, "oldest"));
        Assert.True(_owners.Owns(_owner, "flood0"));
        Assert.True(_owners.Owns(_owner, "flood" + (PlaySessionOwners.MaxPerOwner - 1)));
    }

    [Fact]
    public void A_session_in_use_stays_through_a_flood()
    {
        _owners.Add(_owner, "in-use");

        for (var index = 0; index < PlaySessionOwners.MaxPerOwner; index++)
        {
            Assert.True(_owners.Owns(_owner, "in-use"));
            _owners.Add(_owner, "flood" + index);
        }

        Assert.True(_owners.Owns(_owner, "in-use"));
    }

    [Fact]
    public void A_flood_only_pushes_out_what_the_flooder_owns()
    {
        _owners.Add(_other, "theirs");

        Flood(_owner, PlaySessionOwners.MaxPerOwner * 2);

        Assert.True(_owners.Owns(_other, "theirs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Nothing_else_is_owned_by_anyone(string? playSession)
    {
        _owners.Add(_owner, "mine");

        Assert.False(_owners.Owns(_owner, playSession));
        Assert.False(_owners.BelongsToAnother(_other, playSession));
    }

    [Fact]
    public void A_session_belongs_to_another_only_for_someone_else()
    {
        _owners.Add(_owner, "mine");

        Assert.True(_owners.BelongsToAnother(_other, "mine"));
        Assert.True(_owners.BelongsToAnother(_other, "MINE"));
        Assert.False(_owners.BelongsToAnother(_owner, "mine"));
    }
}
