using System;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Metadata about one materialised snapshot.
/// </summary>
public sealed record SnapshotInfo
{
    /// <summary>A snapshot being materialised. Not streamable.</summary>
    public const string StateBuilding = "building";

    /// <summary>A finished snapshot. The only state that may be streamed.</summary>
    public const string StateComplete = "complete";

    /// <summary>A snapshot whose build failed.</summary>
    public const string StateFailed = "failed";

    /// <summary>
    /// A snapshot abandoned before completion, typically because the server restarted mid-build.
    /// </summary>
    public const string StateInvalidated = "invalidated";

    /// <summary>Gets the generation, which is also the snapshot's identity.</summary>
    public long Generation { get; init; }

    /// <summary>Gets the owning user.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the current state.</summary>
    public string State { get; init; } = StateBuilding;

    /// <summary>Gets the journal position this snapshot was taken at, for the phase-3 hand-off.</summary>
    public long BaselineSequence { get; init; }

    /// <summary>Gets the wire schema the payloads were written for.</summary>
    public int WireSchema { get; init; }

    /// <summary>Gets the number of rows, once complete.</summary>
    public long RowCount { get; init; }

    /// <summary>Gets the total payload size, once complete.</summary>
    public long ByteCount { get; init; }

    /// <summary>Gets the phase currently being materialised.</summary>
    public string? Phase { get; init; }

    /// <summary>Gets how much of the current phase is done.</summary>
    public long PhaseDone { get; init; }

    /// <summary>Gets how much of the current phase there is in total.</summary>
    public long PhaseTotal { get; init; }

    /// <summary>Gets the failure code, when the build failed.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets the failure detail, when the build failed.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>Gets when the snapshot was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when the snapshot finished building.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets when the snapshot becomes eligible for cleanup.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Gets the highest ordinal that is safe to deliver.
    /// </summary>
    /// <remarks>
    /// Everything at or below this is final; everything above it may not have been materialised yet.
    /// The builder advances it as it writes, so a client can start applying the catalog long before
    /// the build finishes.
    /// </remarks>
    public long StreamableThrough { get; init; }

    /// <summary>
    /// Gets a value indicating whether the snapshot is finished, so that what has been delivered can
    /// be the whole of it.
    /// </summary>
    /// <remarks>
    /// This gates <c>caughtUp</c>, and nothing else. It is deliberately <i>not</i> what decides
    /// whether rows may be sent — see <see cref="HasDeliverableRows"/>. Conflating the two is what
    /// made a client wait for the entire build before receiving anything.
    /// </remarks>
    public bool IsComplete => string.Equals(State, StateComplete, StringComparison.Ordinal);

    /// <summary>Gets a value indicating whether any row can be delivered yet.</summary>
    public bool HasDeliverableRows => StreamableThrough > 0;

    /// <summary>
    /// Gets the instant a repair build covers from, or null for a full snapshot.
    /// </summary>
    /// <remarks>
    /// A repair projects only items Jellyfin has saved since this point and lists the identifiers of
    /// everything that survives, so a client that fell out of journal retention can be brought
    /// current without re-receiving a catalog it mostly already has.
    /// </remarks>
    public DateTimeOffset? RepairSince { get; init; }

    /// <summary>Gets a value indicating whether this build is a repair rather than a full snapshot.</summary>
    public bool IsRepair => RepairSince is not null;
}
