namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// One change, materialised and ready to deliver.
/// </summary>
/// <remarks>
/// The payload is captured at write time rather than hydrated later. By the time a client asks for
/// this change the item may have changed again or been deleted, and the journal's job is to say
/// what happened, not what is currently true.
/// </remarks>
/// <param name="Scope">
/// The user this record is for, in 32-character hexadecimal form, or
/// <see cref="JournalStore.BroadcastScope"/> for records every user receives.
/// </param>
/// <param name="Kind">Record kind.</param>
/// <param name="EntityType">Entity type for item upserts, otherwise null.</param>
/// <param name="EntityId">The entity this record describes.</param>
/// <param name="WireSchema">The wire schema the payload was written for.</param>
/// <param name="Payload">Wire-ready JSON bytes.</param>
/// <param name="GroupKey">
/// Records sharing a non-null key must be delivered in one segment. Used for playlist membership.
/// </param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1819:Properties should not return arrays",
    Justification = "The array is a wire buffer written straight to SQLite and later straight to "
        + "the response; wrapping it would add a copy per record.")]
public sealed record JournalRecord(
    string Scope,
    string Kind,
    string? EntityType,
    string EntityId,
    int WireSchema,
    byte[] Payload,
    string? GroupKey = null);
