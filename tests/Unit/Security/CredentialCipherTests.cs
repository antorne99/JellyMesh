using System;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public class CredentialCipherTests
{
    private static readonly byte[] _plaintext = Encoding.UTF8.GetBytes("payload");
    private static readonly byte[] _context = [1, 2, 3];

    [Fact]
    public void DecryptReturnsWhatWasEncrypted()
    {
        var value = CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context);

        Assert.Equal(_plaintext, CredentialCipher.Decrypt(TestValues.Key, value, _context));
    }

    [Fact]
    public void EachEncryptionUsesAFreshNonce()
    {
        Assert.NotEqual(
            CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context),
            CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context));
    }

    [Fact]
    public void WrongKeyFails()
    {
        var value = CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context);

        Assert.ThrowsAny<CryptographicException>(() => CredentialCipher.Decrypt(TestValues.OtherKey, value, _context));
    }

    [Fact]
    public void AnotherContextFails()
    {
        var value = CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context);

        Assert.ThrowsAny<CryptographicException>(() => CredentialCipher.Decrypt(TestValues.Key, value, [9]));
    }

    [Theory]
    [InlineData(0)]   // version byte
    [InlineData(1)]   // nonce
    [InlineData(13)]  // tag
    [InlineData(29)]  // ciphertext
    public void AnAlteredByteFails(int index)
    {
        var blob = Convert.FromBase64String(CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context));
        blob[index] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(
            () => CredentialCipher.Decrypt(TestValues.Key, Convert.ToBase64String(blob), _context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AQID")]
    public void AMalformedValueFails(string value)
    {
        Assert.ThrowsAny<CryptographicException>(() => CredentialCipher.Decrypt(TestValues.Key, value, _context));
    }

    [Fact]
    public void TheKeyStringIsTakenExactly()
    {
        var value = CredentialCipher.Encrypt(TestValues.Key, _plaintext, _context);

        Assert.ThrowsAny<CryptographicException>(() => CredentialCipher.Decrypt(TestValues.Key + " ", value, _context));
    }
}
