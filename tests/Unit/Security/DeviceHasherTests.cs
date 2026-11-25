using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public sealed class DeviceHasherTests : IDisposable
{
    private const string Device = "TW96aWxsYS81LjAgKGlQaG9uZSk";

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _user = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-device").FullName;
    private readonly CapturingLogger<StrmSigner> _logger = new();

    public void Dispose() => Directory.Delete(_folder, true);

    private DeviceHasher Hasher(string folder = "data") => new(new StrmSigner(Path.Combine(_folder, folder), _logger).DeriveKey(DeviceHasher.KeyLabel));

    [Fact]
    public void A_hash_has_the_shape_a_provider_takes()
    {
        var hash = Hasher().Hash(_server, _user, Device);

        Assert.NotNull(hash);
        Assert.Equal(ProviderProtocol.DeviceLength, hash.Length);
        Assert.True(hash.All(char.IsAsciiHexDigitLower));
    }

    [Fact]
    public void A_hash_says_nothing_of_the_device_in_clear()
    {
        var hash = Hasher().Hash(_server, _user, Device)!;

        Assert.DoesNotContain(Device, hash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(Device)), hash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_user.ToString("N"), hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_device_keeps_its_hash_across_restarts()
    {
        Assert.Equal(Hasher().Hash(_server, _user, Device), Hasher().Hash(_server, _user, Device));
    }

    [Fact]
    public void Each_server_user_and_device_has_a_hash_of_its_own()
    {
        var hasher = Hasher();

        string?[] hashes =
        [
            hasher.Hash(_server, _user, Device),
            hasher.Hash(Guid.NewGuid(), _user, Device),
            hasher.Hash(_server, Guid.NewGuid(), Device),
            hasher.Hash(_server, _user, Device + "2"),
            hasher.Hash(_user, _server, Device)
        ];

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void Another_server_hashes_the_same_device_otherwise()
    {
        Assert.NotEqual(Hasher("one").Hash(_server, _user, Device), Hasher("two").Hash(_server, _user, Device));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_device_has_no_hash(string? device)
    {
        Assert.Null(Hasher().Hash(_server, _user, device));
    }

    // A value signed for a stub is readable, so the derived key must be none of them.
    [Fact]
    public void The_key_is_neither_the_signing_key_nor_a_signature()
    {
        var folder = Path.Combine(_folder, "data");
        var signer = new StrmSigner(folder, _logger);

        var key = signer.DeriveKey(DeviceHasher.KeyLabel);

        Assert.Equal(32, key.Length);
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(folder, StrmSigner.FileName)), key);
        Assert.NotEqual(signer.Sign(DeviceHasher.KeyLabel), Convert.ToHexStringLower(key));
        Assert.NotEqual(signer.Sign(DeviceHasher.KeyLabel + (char)1), Convert.ToHexStringLower(key));
        Assert.NotEqual(signer.DeriveKey("another purpose"), key);
    }
}
