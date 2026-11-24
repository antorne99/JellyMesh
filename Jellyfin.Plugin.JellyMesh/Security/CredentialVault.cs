using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// <see cref="ICredentialVault"/> keyed by <see cref="ISecretKeySource"/>.
/// </summary>
/// <remarks>
/// There is no separate key check value: a key is wrong exactly when a stored credential does not
/// decrypt. The way out is to enter the password of each such server again, which a key that is
/// there always allows. Credentials once decrypted stay in memory for the life of the process, with the
/// key and the stored value they came from, and are used again only while both are the same (D39).
/// </remarks>
public sealed class CredentialVault : ICredentialVault
{
    private readonly ISecretKeySource _keySource;
    private readonly IPluginConfigurationStore _store;
    private readonly ILegacySecretKeySource? _legacy;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Decrypted> _decrypted = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CredentialVault"/> class.
    /// </summary>
    /// <param name="keySource">The key source.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="legacy">The key of versions before 0.4, if any.</param>
    /// <param name="logger">The logger.</param>
    public CredentialVault(ISecretKeySource keySource, IPluginConfigurationStore store, ILegacySecretKeySource? legacy = null, ILogger<CredentialVault>? logger = null)
    {
        _keySource = keySource;
        _store = store;
        _legacy = legacy;
        _logger = logger ?? NullLogger<CredentialVault>.Instance;

        // Here, before any caller can hold the configuration lock or read a wrong key that is about to be moved.
        Migrate();
    }

    /// <inheritdoc />
    public bool IsLegacyKeySet => !string.IsNullOrWhiteSpace(_legacy?.GetSecretKey());

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

        Forget(secret, stored);
        return stored.All(entry => CanDecrypt(secret, entry.Key, entry.Value, keep: true)) ? KeyState.Ok : KeyState.Wrong;
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
        if (GetKeyState() != KeyState.Ok || UsableKey() is not { } secret)
        {
            return null;
        }

        return Decrypt(secret, serverId, encryptedCredentials, keep: false);
    }

    /// <inheritdoc />
    public bool CanDecrypt(Guid serverId, string encryptedCredentials)
    {
        return UsableKey() is { } secret && CanDecrypt(secret, serverId, encryptedCredentials, keep: false);
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

    private static string? Usable(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret) || secret.Length < CredentialCipher.MinimumKeyLength ? null : secret;
    }

    private static string Reencrypt(string from, string to, Guid serverId, string value)
    {
        var payload = CredentialCipher.Decrypt(from, value, serverId.ToByteArray());
        try
        {
            return CredentialCipher.Encrypt(to, payload, serverId.ToByteArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private string? UsableKey() => Usable(_keySource.GetSecretKey());

    // Whether the value decrypts under the key; keep says whether the credentials stay in memory.
    private bool CanDecrypt(string secret, Guid serverId, string value, bool keep)
    {
        try
        {
            Decrypt(secret, serverId, value, keep);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // The credentials of a server, from an earlier decryption of the same value under the same key when one was kept.
    // Only values just read from the store are kept, so nothing outside it stays in memory.
    private RemoteCredentials? Decrypt(string secret, Guid serverId, string value, bool keep)
    {
        if (_decrypted.TryGetValue(serverId, out var kept) && kept.Matches(secret, value))
        {
            return kept.Credentials;
        }

        var payload = CredentialCipher.Decrypt(secret, value, serverId.ToByteArray());
        try
        {
            var credentials = JsonSerializer.Deserialize<RemoteCredentials>(payload);
            if (keep && credentials is not null)
            {
                _decrypted[serverId] = new Decrypted(secret, value, credentials);
            }

            return credentials;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    // Drops the credentials of servers that are gone or whose key or stored value changed.
    private void Forget(string secret, List<KeyValuePair<Guid, string>> stored)
    {
        foreach (var (serverId, kept) in _decrypted)
        {
            if (!stored.Any(entry => entry.Key == serverId && kept.Matches(secret, entry.Value)))
            {
                _decrypted.TryRemove(KeyValuePair.Create(serverId, kept));
            }
        }
    }

    // All or nothing: a password the old key cannot read leaves every password as it was, and the page asks for them
    // again. A failure here must not stop the plugin from starting.
    private void Migrate()
    {
        try
        {
            if (UsableKey() is not { } key || Usable(_legacy?.GetSecretKey()) is not { } legacy)
            {
                return;
            }

            var stale = _store.Read(config => config.Servers
                .Where(server => !CanDecrypt(key, server.Id, server.EncryptedCredentials))
                .Select(server => (server.Id, Value: server.EncryptedCredentials))
                .ToList());
            if (stale.Count == 0)
            {
                return;
            }

            if (!stale.All(server => CanDecrypt(legacy, server.Id, server.Value)))
            {
                _logger.LogWarning("JELLYMESH_SECRET_KEY does not decrypt every saved password, so none was moved. Enter the password of each marked server again");
                return;
            }

            var values = stale.ToDictionary(server => server.Id, server => (Old: server.Value, New: Reencrypt(legacy, key, server.Id, server.Value)));
            var moved = 0;
            _store.Update(config =>
            {
                // A password changed meanwhile means someone entered it again: then nothing is moved at all.
                var servers = config.Servers.Where(server => values.ContainsKey(server.Id)).ToList();
                if (servers.Count != values.Count || servers.Any(server => server.EncryptedCredentials != values[server.Id].Old))
                {
                    return;
                }

                foreach (var server in servers)
                {
                    server.EncryptedCredentials = values[server.Id].New;
                }

                moved = servers.Count;
            });

            if (moved > 0)
            {
                _logger.LogInformation("Moved the credentials of {Count} servers to the key file. JELLYMESH_SECRET_KEY is no longer used and can be removed", moved);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Moving the credentials to the key file failed: {Error}{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
        }
    }

    // Credentials with the key and the stored value they were decrypted from. A class, so that no generated ToString prints them.
    private sealed class Decrypted(string secret, string value, RemoteCredentials credentials)
    {
        public RemoteCredentials Credentials { get; } = credentials;

        public bool Matches(string key, string stored)
            => string.Equals(secret, key, StringComparison.Ordinal) && string.Equals(value, stored, StringComparison.Ordinal);
    }
}
