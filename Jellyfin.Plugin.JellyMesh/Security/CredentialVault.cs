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
/// decrypt. With nothing stored no key can be wrong, so removing the last server is the way out
/// after a key is lost for good.
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
        var secret = _keySource.GetSecretKey();
        if (string.IsNullOrWhiteSpace(secret))
        {
            return KeyState.Missing;
        }

        if (secret.Length < CredentialCipher.MinimumKeyLength)
        {
            return KeyState.TooShort;
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

        if (GetKeyState() != KeyState.Ok)
        {
            return null;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            return CredentialCipher.Encrypt(_keySource.GetSecretKey()!, payload, serverId.ToByteArray());
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
}
