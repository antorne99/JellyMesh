using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Names a device of a user to a JellyMesh server without telling which it is (row 511): a hash with a key of this server,
/// different for every server it is sent to, so that two of them cannot recognize the same device.
/// </summary>
public sealed class DeviceHasher
{
    /// <summary>The label the key is derived with.</summary>
    public const string KeyLabel = "JellyMesh stream device";

    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceHasher"/> class.
    /// </summary>
    /// <param name="key">The key, which never leaves this server.</param>
    public DeviceHasher(byte[] key)
    {
        _key = key;
    }

    /// <summary>
    /// Hashes a device of a user for a server.
    /// </summary>
    /// <param name="serverId">The server the hash is sent to.</param>
    /// <param name="userId">The local user.</param>
    /// <param name="deviceId">The device of the local access.</param>
    /// <returns>The hash in lowercase hexadecimal, null without a device.</returns>
    public string? Hash(Guid serverId, Guid userId, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        // The fields of fixed length go first, so that no two inputs are the same bytes.
        byte[] input = [.. serverId.ToByteArray(), .. userId.ToByteArray(), .. Encoding.UTF8.GetBytes(deviceId)];
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, input));
    }
}
