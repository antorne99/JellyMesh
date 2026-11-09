using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// <see cref="ICredentialVault"/> keyed by <see cref="ISecretKeySource"/>.
/// </summary>
/// <remarks>
/// There is no separate key check value: a key is wrong exactly when a stored credential does not
/// decrypt. The way out is to enter the password of each such server again, which a key that is
/// there always allows.
/// </remarks>
public sealed class CredentialVault : ICredentialVault
{
    private readonly ISecretKeySource _keySource;
    private readonly IPluginConfigurationStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="CredentialVault"/> class.
    /// </summary>
    /// <param name="keySource">The key source.</param>
    /// <param name="store">The configuration store.</param>
    public CredentialVault(ISecretKeySource keySource, IPluginConfigurationStore store)
    {
        _keySource = keySource;
        _store = store;
    }

    /// <inheritdoc />
    public KeyState GetKeyState()
    {
        if (UsableKey() is not { } secret)
        {
            return KeyState.Missing;
        }

        var stored = _store.Read(config => config.Servers
            .Select(server => KeyValuePair.Create(server.Id, server.EncryptedCredentials))
            .ToList());

        return stored.All(entry => CanDecrypt(secret, entry.Key, entry.Value)) ? KeyState.Ok : KeyState.Wrong;
    }

    /// <inheritdoc />
    public string? Protect(Guid serverId, RemoteCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // A key that is there is enough: credentials entered again are how a changed key is fixed.
        if (UsableKey() is not { } secret)
        {
            return null;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            return CredentialCipher.Encrypt(secret, payload, serverId.ToByteArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public RemoteCredentials? Unprotect(Guid serverId, string encryptedCredentials)
    {
        if (GetKeyState() != KeyState.Ok)
        {
            return null;
        }

        var payload = CredentialCipher.Decrypt(_keySource.GetSecretKey()!, encryptedCredentials, serverId.ToByteArray());
        try
        {
            return JsonSerializer.Deserialize<RemoteCredentials>(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public bool CanDecrypt(Guid serverId, string encryptedCredentials)
    {
        return UsableKey() is { } secret && CanDecrypt(secret, serverId, encryptedCredentials);
    }

    private static bool CanDecrypt(string secret, Guid serverId, string value)
    {
        try
        {
            CryptographicOperations.ZeroMemory(CredentialCipher.Decrypt(secret, value, serverId.ToByteArray()));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private string? UsableKey()
    {
        var secret = _keySource.GetSecretKey();
        return string.IsNullOrWhiteSpace(secret) || secret.Length < CredentialCipher.MinimumKeyLength ? null : secret;
    }
}
