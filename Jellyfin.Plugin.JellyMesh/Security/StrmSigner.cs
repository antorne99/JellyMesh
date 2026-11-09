using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Signs the URL of a <c>.strm</c> with a key of this server. The signature proves only that this server wrote the URL,
/// and it is readable through <c>/Items?Fields=MediaSources</c> (decision 17), so it never replaces the loopback check.
/// </summary>
/// <remarks>
/// The key is a random file in the plugin data folder, next to the stub registry it belongs with. It stays out of the
/// plugin XML, which the stock endpoints exchange with the browser, and it is not the credential key: that one protects
/// remote credentials, and replacing it has no reason to touch stubs. Losing
/// the file costs nothing lasting: the next materialization signs every stub again, on the same paths.
/// </remarks>
public sealed class StrmSigner
{
    /// <summary>The file name of the key.</summary>
    public const string FileName = "strm-signing.key";

    private const int KeyLength = 32;

    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSigner"/> class.
    /// </summary>
    /// <param name="folder">The folder of the key.</param>
    /// <param name="logger">The logger.</param>
    public StrmSigner(string folder, ILogger<StrmSigner> logger)
    {
        _key = LoadOrCreate(Path.Combine(folder, FileName), logger);
    }

    /// <summary>
    /// Signs a stub.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>The signature, in lowercase hexadecimal.</returns>
    public string Sign(string stubId)
    {
        ArgumentNullException.ThrowIfNull(stubId);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(stubId)));
    }

    /// <summary>
    /// Checks the signature of a stub in constant time.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <param name="signature">The signature received.</param>
    /// <returns>Whether it is the signature of that stub.</returns>
    public bool IsValid(string stubId, string? signature)
    {
        return signature is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(stubId)), Encoding.UTF8.GetBytes(signature));
    }

    private static byte[] LoadOrCreate(string path, ILogger<StrmSigner> logger)
    {
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllBytes(path);
                if (saved.Length == KeyLength)
                {
                    return saved;
                }

                logger.LogWarning("The .strm signing key is damaged. A new one is made, and the next sync signs every stub again");
            }

            var key = RandomNumberGenerator.GetBytes(KeyLength);
            Save(path, key);
            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stubs are then signed again at each restart, which costs little, while a throw here would stop all playback.
            logger.LogError("Cannot keep the .strm signing key: {Error}. A key for this run only is used", exception.GetType().Name);
            return RandomNumberGenerator.GetBytes(KeyLength);
        }
    }

    // A move, so that a crash never leaves half a key behind.
    private static void Save(string path, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(key);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
