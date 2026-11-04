using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// AES-256-GCM encryption of credential payloads.
/// </summary>
/// <remarks>
/// The 32-byte key is HKDF-SHA256 of the UTF-8 bytes of the secret string, taken exactly as set,
/// with no salt and a fixed info label. HKDF does not slow down guessing, so the secret has to be
/// high-entropy: strings under <see cref="MinimumKeyLength"/> characters are refused.
/// A value is the Base64 of: version byte, 12-byte nonce, 16-byte tag, ciphertext. The context
/// is authenticated, so a value does not decrypt for another server. A wrong key, an altered
/// value and another server's value all fail in the same way, with a <see cref="CryptographicException"/>.
/// </remarks>
public static class CredentialCipher
{
    /// <summary>
    /// The shortest accepted secret, in characters. <c>openssl rand -base64 32</c> gives 44.
    /// </summary>
    public const int MinimumKeyLength = 32;

    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private static readonly byte[] _keyInfo = Encoding.UTF8.GetBytes("JellyMesh/credentials/v1");

    /// <summary>
    /// Encrypts a payload.
    /// </summary>
    /// <param name="secret">The secret key string.</param>
    /// <param name="plaintext">The payload.</param>
    /// <param name="context">The value the result is bound to.</param>
    /// <returns>The encrypted value, as Base64.</returns>
    public static string Encrypt(string secret, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var blob = new byte[HeaderSize + plaintext.Length];
        blob[0] = FormatVersion;
        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);

        var key = DeriveKey(secret);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, blob.AsSpan(HeaderSize), tag, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a value.
    /// </summary>
    /// <param name="secret">The secret key string.</param>
    /// <param name="value">The value from <see cref="Encrypt"/>.</param>
    /// <param name="context">The value it was bound to.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="CryptographicException">The value cannot be authenticated under this key and context.</exception>
    public static byte[] Decrypt(string secret, string value, ReadOnlySpan<byte> context)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(value);

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new CryptographicException("The stored value is malformed.");
        }

        if (blob.Length < HeaderSize || blob[0] != FormatVersion)
        {
            throw new CryptographicException("The stored value is malformed.");
        }

        var plaintext = new byte[blob.Length - HeaderSize];
        var key = DeriveKey(secret);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(HeaderSize), blob.AsSpan(1 + NonceSize, TagSize), plaintext, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string secret)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), KeySize, info: _keyInfo);
    }
}
