using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Owns the plugin's SQLite database: connection configuration, pragmas, and write serialisation.
/// </summary>
/// <remarks>
/// <para>
/// The database runs in WAL mode with a single writer and many concurrent readers. All writes are
/// serialised behind one semaphore rather than relying on retry-on-busy: with exactly one writer,
/// <c>SQLITE_BUSY</c> cannot arise from our own traffic at all, which removes a whole class of
/// intermittent failure.
/// </para>
/// <para>
/// Transactions are deliberately kept short. Bulk work (snapshot materialisation) batches roughly a
/// thousand rows per transaction rather than holding one open for the whole build.
/// </para>
/// </remarks>
public sealed class SyncDatabase : IDisposable
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly string _connectionString;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncDatabase"/> class.
    /// </summary>
    /// <param name="databasePath">Full path to the SQLite database file.</param>
    /// <param name="logger">Logger.</param>
    public SyncDatabase(string databasePath, ILogger<SyncDatabase> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>
    /// Gets the full path to the database file.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Opens a configured connection. The caller owns and must dispose it.
    /// </summary>
    /// <remarks>
    /// Suitable for reads without further coordination. Writers must go through
    /// <see cref="WriteAsync{T}"/> so that write serialisation is not bypassed.
    /// </remarks>
    /// <returns>An open, configured connection.</returns>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    /// <summary>
    /// Runs a unit of work inside an immediate transaction, serialised against all other writers.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">
    /// Work to perform. It is committed if it returns normally and rolled back if it throws.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whatever <paramref name="work"/> returned.</returns>
    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, T> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();

            // BEGIN IMMEDIATE takes the write lock up front rather than on first write, so a
            // conflicting writer fails at the start of the transaction instead of midway through.
            //
            // This is the synchronous overload deliberately: Microsoft.Data.Sqlite exposes the
            // 'deferred' flag only on BeginTransaction, never on BeginTransactionAsync, and
            // BEGIN IMMEDIATE is the point of the call. The statement itself does no I/O beyond
            // taking a local file lock, and we already hold the single-writer semaphore.
#pragma warning disable CA1849 // Call async methods when in an async method
            using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849

            var result = work(connection, transaction);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Runs a unit of work inside an immediate transaction, serialised against all other writers.
    /// </summary>
    /// <param name="work">Work to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the transaction has been committed.</returns>
    public Task WriteAsync(
        Action<SqliteConnection, SqliteTransaction> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return WriteAsync<object?>(
            (connection, transaction) =>
            {
                work(connection, transaction);
                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Folds the write-ahead log back into the main database file and truncates it.
    /// </summary>
    /// <remarks>
    /// Called after a snapshot build, which writes roughly 12 MB for a 30k-track library and would
    /// otherwise leave the <c>-wal</c> file sitting at that size indefinitely.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the checkpoint has run.</returns>
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException ex)
        {
            // A checkpoint is an optimisation; failing to take one is never worth failing a caller.
            _logger.LogWarning(ex, "AureliaSync: WAL checkpoint failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Executes a non-query statement.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">Statement to execute.</param>
    /// <param name="transaction">Optional transaction to enlist in.</param>
    /// <returns>Number of rows affected.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every caller passes a compile-time constant or a value interpolated from an "
            + "internal integer. No request-derived text ever reaches this method; all query "
            + "parameters are bound through SqliteParameter.")]
    public static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the database's schema version from <c>PRAGMA user_version</c>.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <returns>The current schema version; zero for a fresh database.</returns>
    public static int GetUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = command.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes the database's schema version.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="version">Version to record.</param>
    /// <param name="transaction">Optional transaction to enlist in.</param>
    public static void SetUserVersion(SqliteConnection connection, int version, SqliteTransaction? transaction = null)
    {
        // PRAGMA statements cannot be parameterised. The value is an int constant owned by the
        // migration list, never user input, so interpolation is safe here.
        Execute(
            connection,
            string.Format(CultureInfo.InvariantCulture, "PRAGMA user_version = {0};", version),
            transaction);
    }

    /// <summary>
    /// Ensures the directory holding the database exists.
    /// </summary>
    /// <param name="databasePath">Full path to the database file.</param>
    public static void EnsureDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();

        // Pooled connections keep the file handle (and the -wal file) alive past our last use.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        // journal_mode is a persistent property of the file; the rest are per-connection and so
        // must be reapplied on every open, including on connections handed back by the pool.
        Execute(
            connection,
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=67108864;
            """);
    }
}
