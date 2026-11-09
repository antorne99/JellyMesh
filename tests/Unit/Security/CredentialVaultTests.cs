using System;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public class CredentialVaultTests
{
    private readonly InMemoryConfigurationStore _store = new();
    private readonly FixedKeySource _key = new(TestValues.Key);

    private CredentialVault Vault => new(_key, _store);

    [Theory]
    [InlineData(null, KeyState.Missing)]
    [InlineData("", KeyState.Missing)]
    [InlineData("   ", KeyState.Missing)]
    [InlineData("0123456789012345678901234567890", KeyState.Missing)]
    [InlineData("01234567890123456789012345678901", KeyState.Ok)]
    public void KeyStateFollowsTheKeyWithNothingStored(string? key, KeyState expected)
    {
        _key.Key = key;

        Assert.Equal(expected, Vault.GetKeyState());
    }

    [Fact]
    public void CredentialsRoundTrip()
    {
        var id = Guid.NewGuid();
        var encrypted = Vault.Protect(id, TestValues.Credentials);

        var credentials = Vault.Unprotect(id, encrypted!);

        Assert.Equal(TestValues.Username, credentials!.Username);
        Assert.Equal(TestValues.Password, credentials.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public void WithoutAUsableKeyNothingIsProtected(string? key)
    {
        _key.Key = key;

        Assert.Null(Vault.Protect(Guid.NewGuid(), TestValues.Credentials));
    }

    [Fact]
    public void AChangedKeyIsDetectedAndBlocksUse()
    {
        var server = StoreServer();
        Assert.Equal(KeyState.Ok, Vault.GetKeyState());

        _key.Key = TestValues.OtherKey;

        Assert.Equal(KeyState.Wrong, Vault.GetKeyState());
        Assert.False(Vault.CanDecrypt(server.Id, server.EncryptedCredentials));
        Assert.Null(Vault.Unprotect(server.Id, server.EncryptedCredentials));
    }

    [Fact]
    public void CredentialsEnteredAgainWithAChangedKeyClearTheState()
    {
        var first = StoreServer();
        var second = StoreServer();
        _key.Key = TestValues.OtherKey;

        first.EncryptedCredentials = Vault.Protect(first.Id, TestValues.Credentials)!;

        Assert.True(Vault.CanDecrypt(first.Id, first.EncryptedCredentials));
        Assert.Equal(KeyState.Wrong, Vault.GetKeyState());

        second.EncryptedCredentials = Vault.Protect(second.Id, TestValues.Credentials)!;

        Assert.Equal(KeyState.Ok, Vault.GetKeyState());
        Assert.Equal(TestValues.Password, Vault.Unprotect(second.Id, second.EncryptedCredentials)!.Password);
    }

    [Fact]
    public void TheRightKeyComingBackClearsTheState()
    {
        StoreServer();
        _key.Key = TestValues.OtherKey;
        Assert.Equal(KeyState.Wrong, Vault.GetKeyState());

        _key.Key = TestValues.Key;

        Assert.Equal(KeyState.Ok, Vault.GetKeyState());
    }

    [Fact]
    public void RemovingTheLastCredentialClearsAWrongKey()
    {
        var server = StoreServer();
        _key.Key = TestValues.OtherKey;

        _store.Configuration.Servers.Remove(server);

        Assert.Equal(KeyState.Ok, Vault.GetKeyState());
    }

    [Fact]
    public void AValueCopiedFromAnotherServerIsWrong()
    {
        var first = StoreServer();
        var second = StoreServer();

        second.EncryptedCredentials = first.EncryptedCredentials;

        Assert.Equal(KeyState.Wrong, Vault.GetKeyState());
    }

    [Fact]
    public void OneAlteredValueSuspendsEverything()
    {
        StoreServer();
        var altered = StoreServer();

        altered.EncryptedCredentials = "AA" + altered.EncryptedCredentials[2..];

        Assert.Equal(KeyState.Wrong, Vault.GetKeyState());
    }

    [Fact]
    public void UnprotectWithAValueOfAnotherServerThrows()
    {
        var encrypted = Vault.Protect(Guid.NewGuid(), TestValues.Credentials);

        Assert.ThrowsAny<CryptographicException>(() => Vault.Unprotect(Guid.NewGuid(), encrypted!));
    }

    private const string ThirdKey = "01234567890123456789012345678901";

    private readonly FixedLegacyKeySource _legacy = new(null);

    private CredentialVault VaultWithLegacy(string fileKey) => new(new FixedKeySource(fileKey), _store, _legacy);

    private RemoteServerConfig StoreServerWith(string key)
    {
        var id = Guid.NewGuid();
        var server = new RemoteServerConfig { Id = id, EncryptedCredentials = new CredentialVault(new FixedKeySource(key), new InMemoryConfigurationStore()).Protect(id, TestValues.Credentials)! };
        _store.Configuration.Servers.Add(server);
        return server;
    }

    [Fact]
    public void TheOldVariableMovesEveryPasswordToTheKeyFileOnce()
    {
        var first = StoreServerWith(TestValues.Key);
        var second = StoreServerWith(TestValues.Key);
        _legacy.Key = TestValues.Key;

        var vault = VaultWithLegacy(TestValues.OtherKey);

        Assert.Equal(KeyState.Ok, vault.GetKeyState());
        Assert.Equal(TestValues.Password, vault.Unprotect(first.Id, first.EncryptedCredentials)!.Password);
        Assert.Equal(TestValues.Password, vault.Unprotect(second.Id, second.EncryptedCredentials)!.Password);
        Assert.True(new CredentialVault(new FixedKeySource(TestValues.OtherKey), _store).GetKeyState() == KeyState.Ok);
        Assert.True(vault.IsLegacyKeySet);
    }

    [Fact]
    public void APasswordTheOldVariableCannotReadMovesNothing()
    {
        var first = StoreServerWith(TestValues.Key);
        var second = StoreServerWith(ThirdKey);
        var before = (first.EncryptedCredentials, second.EncryptedCredentials);
        _legacy.Key = TestValues.Key;

        var vault = VaultWithLegacy(TestValues.OtherKey);

        Assert.Equal(KeyState.Wrong, vault.GetKeyState());
        Assert.Equal(before, (first.EncryptedCredentials, second.EncryptedCredentials));
    }

    [Fact]
    public void AfterTheMoveTheOldVariableIsIgnored()
    {
        var server = StoreServerWith(TestValues.OtherKey);
        var before = server.EncryptedCredentials;
        _legacy.Key = TestValues.Key;

        var vault = VaultWithLegacy(TestValues.OtherKey);

        Assert.Equal(KeyState.Ok, vault.GetKeyState());
        Assert.Equal(before, server.EncryptedCredentials);
        Assert.True(vault.IsLegacyKeySet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("short")]
    public void WithoutAUsableOldVariableNothingMoves(string? legacy)
    {
        var server = StoreServerWith(TestValues.Key);
        var before = server.EncryptedCredentials;
        _legacy.Key = legacy;

        var vault = VaultWithLegacy(TestValues.OtherKey);

        Assert.Equal(KeyState.Wrong, vault.GetKeyState());
        Assert.Equal(before, server.EncryptedCredentials);
        Assert.Equal(legacy == "short", vault.IsLegacyKeySet);
    }

    private RemoteServerConfig StoreServer()
    {
        var id = Guid.NewGuid();
        var server = new RemoteServerConfig { Id = id, EncryptedCredentials = Vault.Protect(id, TestValues.Credentials)! };
        _store.Configuration.Servers.Add(server);
        return server;
    }
}
