namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// The result of an acknowledgement attempt.
/// </summary>
public enum AckResult
{
    /// <summary>The checkpoint advanced.</summary>
    Advanced = 0,

    /// <summary>
    /// This exact acknowledgement had already been applied; the stored result was returned again.
    /// </summary>
    AlreadyApplied = 1,

    /// <summary>
    /// The cursor was at or below the checkpoint. Accepted and ignored — a client retrying an
    /// acknowledgement it already made has done nothing wrong.
    /// </summary>
    NoOp = 2,

    /// <summary>The cursor was never issued to this session.</summary>
    BeyondIssued = 3,

    /// <summary>The cursor did not belong to this session's snapshot generation.</summary>
    WrongGeneration = 4,

    /// <summary>The session does not exist, is not the caller's, or has expired.</summary>
    SessionUnusable = 5
}

/// <summary>
/// The outcome of an acknowledgement, including the checkpoint the client should store.
/// </summary>
/// <param name="Result">What happened.</param>
/// <param name="AckedOrdinal">The checkpoint after the attempt.</param>
/// <param name="SnapshotComplete">Whether the whole snapshot is now acknowledged.</param>
public readonly record struct AckOutcome(AckResult Result, long AckedOrdinal, bool SnapshotComplete)
{
    /// <summary>Gets a value indicating whether the client should treat this as success.</summary>
    public bool IsSuccess =>
        Result is AckResult.Advanced or AckResult.AlreadyApplied or AckResult.NoOp;
}
