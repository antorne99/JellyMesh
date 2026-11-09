using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public sealed class FileSecretKeySourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-key-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger<FileSecretKeySource> _logger = new();

    private string KeyPath => Path.Combine(_folder, FileSecretKeySource.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void TheFirstStartWritesALongRandomKeyOnlyTheOwnerReads()
    {
        var key = new FileSecretKeySource(_folder, _logger).GetSecretKey();

        Assert.NotNull(key);
        Assert.True(key.Length >= CredentialCipher.MinimumKeyLength);
        Assert.Equal(key, File.ReadAllText(KeyPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        }
    }

    [Fact]
    public void ARestartReadsTheSameKey()
    {
        var first = new FileSecretKeySource(_folder, _logger).GetSecretKey();

        Assert.Equal(first, new FileSecretKeySource(_folder, _logger).GetSecretKey());
    }

    [Fact]
    public void TwoInstallsGetDifferentKeys()
    {
        var other = Path.Combine(_folder, "other");

        Assert.NotEqual(new FileSecretKeySource(_folder, _logger).GetSecretKey(), new FileSecretKeySource(other, _logger).GetSecretKey());
    }

    [Fact]
    public void ADamagedFileIsReplacedAndSaysSo()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(KeyPath, "damaged");

        var key = new FileSecretKeySource(_folder, _logger).GetSecretKey();

        Assert.NotEqual("damaged", key);
        Assert.Equal(key, File.ReadAllText(KeyPath));
        Assert.Contains(_logger.Lines, line => line.Contains("damaged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFolderThatCannotBeWrittenGivesNoKeyRatherThanOneForThisRunOnly()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder + ".file", string.Empty);

        var key = new FileSecretKeySource(Path.Combine(_folder + ".file", "sub"), _logger).GetSecretKey();

        Assert.Null(key);
        File.Delete(_folder + ".file");
    }

    [Fact]
    public void TheKeyNeverReachesTheLog()
    {
        var key = new FileSecretKeySource(_folder, _logger).GetSecretKey()!;

        Assert.DoesNotContain(_logger.Lines, line => line.Contains(key[..16], StringComparison.Ordinal));
    }
}
