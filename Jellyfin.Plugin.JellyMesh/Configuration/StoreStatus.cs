using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The state of the plugin database as the page shows it (rows 427, 466, 469).
/// </summary>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the database is not healthy, or why the old files cannot be read; <c>null</c> when neither.</param>
/// <param name="Recovery">The last restore of a damaged database, until the admin dismisses it; <c>null</c> when there is none.</param>
/// <param name="MigratedKeptUntil">Until when the files of the older version are kept; <c>null</c> when there are none.</param>
public sealed record StoreStatus(StoreState State, string? Reason, StoreRecovery? Recovery, DateTimeOffset? MigratedKeptUntil)
{
    /// <summary>
    /// Reads the state of the database, leaving out the old files while it is suspended. A file or SQLite error reading
    /// them shows as the reason, so that it does not fail the status.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state.</returns>
    internal static async Task<StoreStatus> ReadAsync(IPluginDatabase database, CancellationToken cancellationToken)
    {
        var health = database.Health;
        var reason = health.Reason;
        DateTimeOffset? kept = null;
        if (!health.Suspended)
        {
            try
            {
                kept = await database.MigratedKeptUntilAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (StoreSuspendedException)
            {
                // Suspended since the health was read: the state is the new one, and the old files are not shown.
                health = database.Health;
                reason = health.Reason;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                reason = "Until when the files of the older version are kept cannot be read: " + PluginDatabase.ReasonOf(exception);
            }
        }

        return new StoreStatus(health.State, reason, database.Recovery, kept);
    }
}
