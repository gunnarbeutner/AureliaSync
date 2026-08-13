using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Applies pending schema migrations, transactionally and fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// The contract is that a failed migration must leave the previous database usable, or else refuse
/// to serve at all. It must never leave a half-migrated database that looks healthy.
/// </para>
/// <para>
/// A database newer than the running plugin is treated as fatal rather than something to downgrade:
/// that state means the administrator has rolled the plugin back, and the newer schema may hold
/// checkpoints this build cannot interpret. Refusing loudly is safer than silently resyncing every
/// client.
/// </para>
/// </remarks>
public sealed class MigrationRunner
{
    /// <summary>
    /// How many pre-migration backups to keep.
    /// </summary>
    public const int BackupsToKeep = 3;

    private readonly ILogger _logger;
    private readonly List<IMigration> _migrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationRunner"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="migrations">
    /// Migrations to consider. Defaults to the full built-in set when null.
    /// </param>
    public MigrationRunner(ILogger logger, IEnumerable<IMigration>? migrations = null)
    {
        _logger = logger;
        _migrations = (migrations ?? All()).OrderBy(m => m.Version).ToList();
    }

    /// <summary>
    /// Gets the schema version this build expects.
    /// </summary>
    public int TargetVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <summary>
    /// Gets the complete built-in migration set, in ascending version order.
    /// </summary>
    /// <returns>All known migrations.</returns>
    public static IReadOnlyList<IMigration> All() =>
        new ReadOnlyCollection<IMigration>(new IMigration[]
        {
            new M001Initial(),
            new M002SnapshotRowGroups(),
            new M003JournalGroups(),
            new M004SessionCounters()
        });

    /// <summary>
    /// Brings the database at <paramref name="databasePath"/> up to <see cref="TargetVersion"/>.
    /// </summary>
    /// <param name="databasePath">Full path to the database file.</param>
    /// <exception cref="InvalidOperationException">
    /// The database is newer than this build, or a migration failed.
    /// </exception>
    public void Run(string databasePath)
    {
        SyncDatabase.EnsureDirectory(databasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ToString();

        int current;
        using (var probe = new SqliteConnection(connectionString))
        {
            probe.Open();

            // WAL must be set before anything else so the backup below folds in a complete file.
            SyncDatabase.Execute(probe, "PRAGMA journal_mode=WAL;");
            current = SyncDatabase.GetUserVersion(probe);
        }

        var target = TargetVersion;

        if (current == target)
        {
            _logger.LogInformation(
                "AureliaSync: database schema is current at version {Version}",
                current);
            return;
        }

        if (current > target)
        {
            // Fail closed. Do not downgrade, and do not pretend to be healthy.
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Database schema version {0} is newer than this plugin build supports ({1}). "
                    + "Install the matching or a newer AureliaSync version, or remove {2} to start over. "
                    + "Refusing to downgrade.",
                    current,
                    target,
                    databasePath));
        }

        var pending = _migrations.Where(m => m.Version > current).ToList();
        _logger.LogInformation(
            "AureliaSync: migrating database from version {From} to {To} ({Count} migration(s))",
            current,
            target,
            pending.Count);

        string? backupPath = null;
        if (current > 0 && File.Exists(databasePath))
        {
            backupPath = CreateBackup(databasePath, current, connectionString);
        }

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            SyncDatabase.Execute(connection, "PRAGMA foreign_keys=ON;");

            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                foreach (var migration in pending)
                {
                    _logger.LogInformation(
                        "AureliaSync: applying migration {Version} ({Name})",
                        migration.Version,
                        migration.Name);
                    migration.Apply(connection, transaction);
                }

                SyncDatabase.SetUserVersion(connection, target, transaction);
                transaction.Commit();
            }

            VerifyIntegrity(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AureliaSync: migration to version {Target} failed; restoring pre-migration state",
                target);

            SqliteConnection.ClearAllPools();
            RestoreBackup(databasePath, backupPath);

            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "AureliaSync database migration to version {0} failed. The previous database has been restored.",
                    target),
                ex);
        }

        PruneBackups(databasePath);

        _logger.LogInformation("AureliaSync: database migrated to version {Version}", target);
    }

    private static string BackupDirectory(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "backups");

    private string CreateBackup(string databasePath, int currentVersion, string connectionString)
    {
        // Fold the WAL into the main file first, so a plain file copy is a complete database.
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            SyncDatabase.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        SqliteConnection.ClearAllPools();

        var directory = BackupDirectory(databasePath);
        Directory.CreateDirectory(directory);

        var backupPath = Path.Combine(
            directory,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}.pre-v{1}",
                Path.GetFileName(databasePath),
                currentVersion));

        File.Copy(databasePath, backupPath, overwrite: true);
        _logger.LogInformation("AureliaSync: wrote pre-migration backup to {Path}", backupPath);
        return backupPath;
    }

    private void RestoreBackup(string databasePath, string? backupPath)
    {
        if (backupPath is null || !File.Exists(backupPath))
        {
            // Nothing to restore: this was a fresh database, so the partially created file is
            // simply removed and the next start builds it again.
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
            return;
        }

        try
        {
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
            File.Copy(backupPath, databasePath, overwrite: true);
            _logger.LogInformation("AureliaSync: restored database from {Path}", backupPath);
        }
        catch (IOException ex)
        {
            _logger.LogCritical(
                ex,
                "AureliaSync: could not restore the database from {Path}. The backup is intact; restore it by hand",
                backupPath);
        }
    }

    private void PruneBackups(string databasePath)
    {
        var directory = BackupDirectory(databasePath);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles(Path.GetFileName(databasePath) + ".pre-v*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(BackupsToKeep);

            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "AureliaSync: could not prune old database backups");
        }
    }

    private void VerifyIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = command.ExecuteScalar() as string;

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Integrity check failed after migration: {0}",
                    result ?? "(no result)"));
        }

        _logger.LogDebug("AureliaSync: post-migration integrity check passed");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "AureliaSync: could not delete {Path}", path);
        }
    }
}
