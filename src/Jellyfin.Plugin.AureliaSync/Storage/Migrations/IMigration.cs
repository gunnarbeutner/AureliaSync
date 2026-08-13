using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// One forward-only schema migration.
/// </summary>
/// <remarks>
/// Migrations are applied in ascending <see cref="Version"/> order inside a single transaction, and
/// are never rewritten once released — a released migration is part of the on-disk contract of
/// every server that has already run it.
/// </remarks>
public interface IMigration
{
    /// <summary>
    /// Gets the schema version this migration produces. Must be unique and start at one.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Gets a short human-readable name, used in logs and backup filenames.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Applies the migration.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">The enclosing transaction. Do not commit it.</param>
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
