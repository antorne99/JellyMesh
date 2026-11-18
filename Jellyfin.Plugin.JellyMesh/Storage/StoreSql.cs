using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Small helpers for the stores' commands: constant texts, every value a parameter.
/// </summary>
internal static class StoreSql
{
    /// <summary>
    /// Makes a command in the transaction with its parameters.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="text">A constant command text.</param>
    /// <param name="parameters">The values, by name.</param>
    /// <returns>The command.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant text; values go in as parameters.")]
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>
    /// Runs a command that returns no rows.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="text">A constant command text.</param>
    /// <param name="parameters">The values, by name.</param>
    /// <returns>The rows changed.</returns>
    public static int Execute(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, text, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Writes a GUID as the stores keep it.</summary>
    /// <param name="id">The GUID.</param>
    /// <returns>Its "N" form.</returns>
    public static string Text(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="column">The column.</param>
    /// <returns>The text, or null.</returns>
    public static string? TextOrNull(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    /// <summary>Reads a nullable integer column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="column">The column.</param>
    /// <returns>The number, or null.</returns>
    public static int? IntOrNull(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

    /// <summary>Reads a nullable 64-bit integer column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="column">The column.</param>
    /// <returns>The number, or null.</returns>
    public static long? LongOrNull(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
}
