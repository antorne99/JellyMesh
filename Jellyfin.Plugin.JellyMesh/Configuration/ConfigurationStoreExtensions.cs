using System;
using Jellyfin.Plugin.JellyMesh.Storage;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Reads for the lookups that run on every library change or stock request, which must not fail while the database cannot be used.
/// </summary>
public static class ConfigurationStoreExtensions
{
    /// <summary>
    /// Reads from the configuration, or gives a fallback while it cannot be read. Each caller picks the fallback that keeps it safe.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="store">The configuration store.</param>
    /// <param name="read">Projects the configuration to a detached result.</param>
    /// <param name="unavailable">The result while the configuration cannot be read.</param>
    /// <returns>The result of <paramref name="read"/>, or <paramref name="unavailable"/>.</returns>
    public static T ReadOr<T>(this IPluginConfigurationStore store, Func<PluginConfiguration, T> read, T unavailable)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Read(read);
        }
        catch (StoreSuspendedException)
        {
            return unavailable;
        }
    }
}
