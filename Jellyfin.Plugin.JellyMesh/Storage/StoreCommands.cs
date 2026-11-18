using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Commands and column conversions shared by the tables of the plugin database.
/// </summary>
internal static class StoreCommands
{
    /// <summary>
    /// Creates a command with a constant text inside a transaction; values always go in as parameters.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="text">The constant SQL text.</param>
    /// <returns>The command.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant text; values go in as parameters.")]
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    /// <summary>
    /// Runs constant statements that take no values, such as a schema step.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="text">The constant SQL text.</param>
    public static void Execute(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        using var command = Command(connection, transaction, text);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates a command with its parameters declared, to be run once per row with new values.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="text">The constant SQL text.</param>
    /// <param name="names">The parameter names, in the order <see cref="Run"/> takes their values.</param>
    /// <returns>The command.</returns>
    public static SqliteCommand Prepared(SqliteConnection connection, SqliteTransaction transaction, string text, params string[] names)
    {
        var command = Command(connection, transaction, text);
        foreach (var name in names)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        return command;
    }

    /// <summary>
    /// Runs a prepared command with the values of one row, null as SQL NULL.
    /// </summary>
    /// <param name="command">The command from <see cref="Prepared"/>.</param>
    /// <param name="values">The values, in the order of its parameters.</param>
    public static void Run(this SqliteCommand command, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            command.Parameters[index].Value = values[index] ?? DBNull.Value;
        }

        command.ExecuteNonQuery();
    }

    /// <summary>Converts an ID to its column text.</summary>
    /// <param name="id">The ID.</param>
    /// <returns>The text.</returns>
    public static string Text(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Converts a nullable ID to its column text.</summary>
    /// <param name="id">The ID.</param>
    /// <returns>The text, null for null.</returns>
    public static string? Text(Guid? id) => id is { } value ? Text(value) : null;

    /// <summary>Converts a time to its column text, which keeps its offset.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The text, null for null.</returns>
    public static string? Text(DateTimeOffset? time) => time?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Reads an ID column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The ID.</returns>
    public static Guid GetId(this SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Reads a nullable ID column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The ID, null for NULL.</returns>
    public static Guid? GetIdOrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetId(ordinal);

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The text, null for NULL.</returns>
    public static string? GetTextOrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable integer column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The number, null for NULL.</returns>
    public static int? GetInt32OrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>Reads a nullable time column written by <see cref="Text(DateTimeOffset?)"/>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The time, null for NULL.</returns>
    public static DateTimeOffset? GetTimeOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.ParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}
