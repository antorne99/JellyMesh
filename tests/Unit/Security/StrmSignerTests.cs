using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public sealed class StrmSignerTests : IDisposable
{
    private const string Stub = "0f5a64432ea2468e99dfe6f2749fe353";

    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-signer").FullName;
    private readonly CapturingLogger<StrmSigner> _logger = new();

    private string KeyFile => Path.Combine(_folder, StrmSigner.FileName);

    public void Dispose() => Directory.Delete(_folder, true);

    private StrmSigner Signer(string? folder = null) => new(folder ?? _folder, _logger);

    [Fact]
    public void A_signature_validates_for_its_stub_only()
    {
        var signer = Signer();

        var signature = signer.Sign(Stub);

        Assert.True(signer.IsValid(Stub, signature));
        Assert.False(signer.IsValid("another-stub", signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("not hex at all, and not ascii: è")]
    public void Anything_else_is_not_a_signature(string? signature)
    {
        Assert.False(Signer().IsValid(Stub, signature));
    }

    [Fact]
    public void A_signature_cut_short_or_with_a_changed_character_is_refused()
    {
        var signer = Signer();
        var signature = signer.Sign(Stub);
        var changed = (signature[0] == 'a' ? 'b' : 'a') + signature[1..];

        Assert.False(signer.IsValid(Stub, signature[..^1]));
        Assert.False(signer.IsValid(Stub, changed));
    }

    [Fact]
    public void The_key_survives_a_restart()
    {
        var signature = Signer().Sign(Stub);

        Assert.True(Signer().IsValid(Stub, signature));
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void Two_servers_have_different_keys()
    {
        var other = Directory.CreateTempSubdirectory("jellymesh-signer").FullName;
        try
        {
            Assert.False(Signer(other).IsValid(Stub, Signer().Sign(Stub)));
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_is_created()
    {
        var folder = Path.Combine(_folder, "new", "folder");

        var signature = Signer(folder).Sign(Stub);

        Assert.True(Signer(folder).IsValid(Stub, signature));
    }

    [Fact]
    public void A_damaged_key_is_replaced_and_says_so()
    {
        var signature = Signer().Sign(Stub);
        File.WriteAllBytes(KeyFile, [1, 2, 3]);

        var signer = Signer();

        Assert.False(signer.IsValid(Stub, signature));
        Assert.Equal(32, new FileInfo(KeyFile).Length);
        Assert.Contains("damaged", Assert.Single(_logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        _ = Signer();

        Assert.Equal([KeyFile], Directory.GetFiles(_folder));
    }

    [Fact]
    public void Only_the_owner_can_read_the_key()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _ = Signer();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_still_gives_a_working_signer()
    {
        var blocked = Path.Combine(_folder, "file");
        File.WriteAllText(blocked, string.Empty);

        var signer = Signer(Path.Combine(blocked, "below"));

        Assert.True(signer.IsValid(Stub, signer.Sign(Stub)));
        Assert.DoesNotContain(_folder, Assert.Single(_logger.Lines), StringComparison.Ordinal);
    }
}
