using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Storage.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public MigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aureliasync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "aureliasync.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leaked temp directory must never fail a test run.
        }
    }

    private MigrationRunner NewRunner(IEnumerable<IMigration>? migrations = null) =>
        new MigrationRunner(NullLogger.Instance, migrations);

    private int ReadUserVersion()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return SyncDatabase.GetUserVersion(connection);
    }

    private List<string> ReadTableNames()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public void FreshDatabaseMigratesToTargetVersion()
    {
        var runner = NewRunner();
        runner.Run(_databasePath);

        Assert.True(File.Exists(_databasePath));
        Assert.Equal(runner.TargetVersion, ReadUserVersion());
        Assert.Equal(1, runner.TargetVersion);
    }

    [Fact]
    public void InitialMigrationCreatesEveryExpectedTable()
    {
        NewRunner().Run(_databasePath);

        var expected = new[]
        {
            "ack_requests", "inventory", "journal", "meta",
            "sessions", "snapshot_rows", "snapshots", "subscriptions"
        };

        var actual = ReadTableNames();
        foreach (var table in expected)
        {
            Assert.Contains(table, actual);
        }
    }

    [Fact]
    public void InitialMigrationCreatesTheIndexesTheHotPathsDependOn()
    {
        NewRunner().Run(_databasePath);

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();

        var indexes = new List<string>();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }

        // Session lookup by owner, expiry sweeps, and snapshot reuse are the queries that run on
        // every request or every timer tick; a missing index here degrades silently.
        Assert.Contains("ix_sessions_owner", indexes);
        Assert.Contains("ix_sessions_expires", indexes);
        Assert.Contains("ix_snapshots_user_state", indexes);
        Assert.Contains("ix_subscriptions_expires", indexes);
        Assert.Contains("ix_ack_requests_age", indexes);
        Assert.Contains("ix_journal_scope", indexes);
    }

    [Fact]
    public void RunningTwiceIsANoOp()
    {
        var runner = NewRunner();
        runner.Run(_databasePath);
        var tablesAfterFirst = ReadTableNames();

        // Must not throw, must not re-apply, must not disturb the schema.
        runner.Run(_databasePath);

        Assert.Equal(runner.TargetVersion, ReadUserVersion());
        Assert.Equal(tablesAfterFirst, ReadTableNames());
    }

    [Fact]
    public void DatabaseNewerThanThePluginFailsClosed()
    {
        NewRunner().Run(_databasePath);

        // Simulate a plugin downgrade: the file on disk is ahead of what this build knows.
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            SyncDatabase.SetUserVersion(connection, 99);
        }

        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => NewRunner().Run(_databasePath));
        Assert.Contains("newer than this plugin build", ex.Message, StringComparison.Ordinal);

        // Crucially: it must not have downgraded the database.
        Assert.Equal(99, ReadUserVersion());
    }

    [Fact]
    public void FailingMigrationRestoresThePreviousDatabase()
    {
        // Establish a v1 database holding a row we can look for after the failed upgrade.
        NewRunner().Run(_databasePath);
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            SyncDatabase.Execute(connection, "INSERT INTO meta (key, value) VALUES ('canary', 'present');");
        }

        SqliteConnection.ClearAllPools();

        var migrations = MigrationRunner.All().ToList();
        migrations.Add(new ThrowingMigration());

        var ex = Assert.Throws<InvalidOperationException>(() => NewRunner(migrations).Run(_databasePath));
        Assert.Contains("failed", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The database must still be at v1 and must still hold the canary row.
        Assert.Equal(1, ReadUserVersion());

        using var check = new SqliteConnection($"Data Source={_databasePath}");
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'canary';";
        Assert.Equal("present", command.ExecuteScalar() as string);
    }

    [Fact]
    public void FailingMigrationWritesABackupThatCanBeInspected()
    {
        NewRunner().Run(_databasePath);
        SqliteConnection.ClearAllPools();

        var migrations = MigrationRunner.All().ToList();
        migrations.Add(new ThrowingMigration());
        Assert.Throws<InvalidOperationException>(() => NewRunner(migrations).Run(_databasePath));

        var backups = Directory.GetFiles(Path.Combine(_directory, "backups"), "*.pre-v*");
        Assert.Single(backups);
    }

    [Fact]
    public void ForeignKeysAndWalAreEnabledOnOpenedConnections()
    {
        NewRunner().Run(_databasePath);

        using var database = new SyncDatabase(_databasePath, NullLogger<SyncDatabase>.Instance);
        using var connection = database.Open();

        using var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (journalMode.ExecuteScalar() as string)?.ToLowerInvariant());

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(foreignKeys.ExecuteScalar()));
    }

    private sealed class ThrowingMigration : IMigration
    {
        public int Version => 999;

        public string Name => "deliberately-broken";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            // Do real work first, so the rollback has something to undo.
            SyncDatabase.Execute(connection, "CREATE TABLE should_not_survive (x INTEGER);", transaction);
            throw new InvalidOperationException("simulated migration failure");
        }
    }
}
