using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Key/value settings that belong to the database rather than to plugin configuration.
/// </summary>
/// <remarks>
/// Configuration lives in Jellyfin's XML and is an administrator's to edit. This is for values the
/// database owns and must keep in step with its own contents — currently the checkpoint signing
/// key, whose rotation would invalidate every issued token.
/// </remarks>
public sealed class MetaStore
{
    /// <summary>Key holding the checkpoint-token signing key, base64 encoded.</summary>
    public const string CheckpointSigningKey = "checkpoint.signingKey";

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetaStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public MetaStore(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads a value.
    /// </summary>
    /// <param name="key">Setting key.</param>
    /// <returns>The value, or null when unset.</returns>
    public string? Get(string key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Writes a value.
    /// </summary>
    /// <param name="key">Setting key.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the value is committed.</returns>
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                INSERT INTO meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """,
                ("$key", key),
                ("$value", value)),
            cancellationToken);

    /// <summary>
    /// Returns the checkpoint signing key, generating and persisting one on first use.
    /// </summary>
    /// <remarks>
    /// Generated once and never rotated automatically: every outstanding checkpoint token is
    /// signed with it, and rotating would force every client into a fresh snapshot.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A 32-byte key.</returns>
    public async Task<byte[]> GetOrCreateSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        var existing = Get(CheckpointSigningKey);
        if (!string.IsNullOrEmpty(existing))
        {
            try
            {
                return Convert.FromBase64String(existing);
            }
            catch (FormatException)
            {
                // Unreadable key: fall through and mint a new one. Outstanding tokens stop
                // validating, which costs affected clients one fresh snapshot — strictly better
                // than refusing to serve at all.
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        await SetAsync(CheckpointSigningKey, Convert.ToBase64String(key), cancellationToken)
            .ConfigureAwait(false);
        return key;
    }
}
