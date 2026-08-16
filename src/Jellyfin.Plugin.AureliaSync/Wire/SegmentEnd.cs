using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// The last line of every segment.
/// </summary>
/// <remarks>
/// <b>Its presence is what makes a segment valid.</b> A client that reaches the end of the body
/// without seeing this line discards everything it read and retries from its last acknowledged
/// cursor, which is what lets the server abandon a response at any point without coordinating.
/// </remarks>
public class SegmentEnd
{
    /// <summary>Gets the line discriminator.</summary>
    [JsonPropertyName("kind")]
    public string Kind => WireKind.SegmentEnd;

    /// <summary>
    /// Gets or sets the cursor through which this segment delivered.
    /// </summary>
    /// <remarks>
    /// This is the value the client acknowledges and resumes from; per-record cursors are not used
    /// for paging. When a segment carries no records this echoes the requested position.
    /// </remarks>
    [JsonPropertyName("cursor")]
    public string Cursor { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of record lines, excluding this line and the opening one.</summary>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    /// <summary>Gets or sets the uncompressed byte count of the record payloads.</summary>
    [JsonPropertyName("byteCount")]
    public long ByteCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether delivery is complete.
    /// </summary>
    /// <remarks>
    /// True on exactly one segment — the final one, which may itself carry records. The client
    /// promotes its staged catalog on this signal, so setting it early truncates the library and
    /// never setting it leaves the client looping forever.
    /// </remarks>
    [JsonPropertyName("caughtUp")]
    public bool CaughtUp { get; set; }

    /// <summary>Gets or sets the highest position this session will deliver.</summary>
    [JsonPropertyName("sessionUpperBound")]
    public long SessionUpperBound { get; set; }

    /// <summary>
    /// Gets or sets the journal's head at the moment this segment was written.
    /// </summary>
    /// <remarks>
    /// Advisory, and deliberately distinct from <see cref="SessionUpperBound"/>, which is fixed
    /// when the session opens so that a session's work is finite. <c>caughtUp</c> therefore means
    /// "caught up to that bound", not "caught up to now" — during a library scan the two diverge,
    /// and without this a client cannot tell that more is already waiting.
    /// </remarks>
    [JsonPropertyName("journalHead")]
    public long JournalHead { get; set; }

    /// <summary>
    /// Gets or sets why the segment ended: <c>maxRecords</c>, <c>maxBytes</c>, <c>timeBudget</c>,
    /// <c>upperBound</c>, or <c>clientAbort</c>.
    /// </summary>
    [JsonPropertyName("stopReason")]
    public string? StopReason { get; set; }

    /// <summary>Gets or sets the cursor to pass as <c>after</c> on the next request.</summary>
    [JsonPropertyName("nextAfter")]
    public string? NextAfter { get; set; }
}
