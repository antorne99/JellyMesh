using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// One step of the schema, which brings the database to <paramref name="Version"/> inside its own transaction.
/// </summary>
/// <param name="Version">The <c>user_version</c> the database has after this step.</param>
/// <param name="Apply">Changes the schema on the writer connection, inside the step's transaction.</param>
public sealed record SchemaMigration(int Version, Action<SqliteConnection, SqliteTransaction> Apply);
