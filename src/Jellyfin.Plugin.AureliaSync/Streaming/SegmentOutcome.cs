namespace Jellyfin.Plugin.AureliaSync.Streaming;

/// <summary>
/// What a written segment ended up containing.
/// </summary>
/// <param name="RecordCount">Records written, excluding the two control lines.</param>
/// <param name="PayloadBytes">Uncompressed payload bytes.</param>
/// <param name="TotalBytes">Every byte written, including framing and newlines.</param>
/// <param name="LastOrdinal">The highest ordinal delivered, or the requested position when empty.</param>
/// <param name="CaughtUp">Whether delivery is complete.</param>
/// <param name="StopReason">Why the segment ended.</param>
public readonly record struct SegmentOutcome(
    int RecordCount,
    long PayloadBytes,
    long TotalBytes,
    long LastOrdinal,
    bool CaughtUp,
    string StopReason)
{
    /// <summary>The record limit was reached.</summary>
    public const string StopMaxRecords = "maxRecords";

    /// <summary>The byte budget was reached.</summary>
    public const string StopMaxBytes = "maxBytes";

    /// <summary>The time budget was reached.</summary>
    public const string StopTimeBudget = "timeBudget";

    /// <summary>Everything available was delivered.</summary>
    public const string StopUpperBound = "upperBound";
}
