using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AureliaSync.Configuration;

/// <summary>
/// AureliaSync plugin configuration.
/// </summary>
/// <remarks>
/// <para>
/// This type is persisted as XML by <c>BasePlugin&lt;T&gt;</c> through <c>IXmlSerializer</c>.
/// Every member must therefore be a public settable property of a parameterless-constructible type:
/// no <c>init</c> accessors, no records, no collections requiring custom construction.
/// </para>
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the sync protocol is enabled. When false, the
    /// status endpoint still responds (so Aurelia can report why) but session endpoints refuse.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how long a completed snapshot is retained after completion, in hours.
    /// </summary>
    public int SnapshotRetentionHours { get; set; } = 48;

    /// <summary>
    /// Gets or sets how long a delivery session survives without access, in hours.
    /// </summary>
    public int SessionIdleExpiryHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how long an inactive client subscription (and its checkpoint) is retained, in days.
    /// </summary>
    public int SubscriptionExpiryDays { get; set; } = 90;

    /// <summary>
    /// Gets or sets the default maximum number of records in one streamed segment.
    /// </summary>
    public int MaxRecordsPerSegment { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the default maximum uncompressed payload bytes in one streamed segment.
    /// </summary>
    public long MaxBytesPerSegment { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the wall-clock budget for producing one segment, in milliseconds.
    /// </summary>
    public int SegmentTimeBudgetMs { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets how many items are hydrated from Jellyfin per batch during snapshot
    /// materialisation. Hydrating a 30k-track library in one call would materialise gigabytes.
    /// </summary>
    public int SnapshotHydrationBatchSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets an optional delay between hydration batches, in milliseconds, to throttle
    /// snapshot builds on servers with slow storage. Zero disables the delay.
    /// </summary>
    public int SnapshotBatchDelayMs { get; set; }

    /// <summary>
    /// Gets or sets the window during which an existing completed snapshot is reused for a new
    /// session belonging to the same user, in minutes. This is what makes a second device cheap.
    /// </summary>
    public int SnapshotReuseWindowMinutes { get; set; } = 360;

    /// <summary>
    /// Gets or sets the maximum number of snapshot builds allowed to run at once.
    /// </summary>
    public int MaxConcurrentSnapshotBuilds { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether only audio playlists are synchronised.
    /// </summary>
    public bool AudioPlaylistsOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether segments are gzip-compressed when the client asks.
    /// Jellyfin's own response compression does not cover <c>application/x-ndjson</c>, so this is
    /// applied by the streaming endpoint itself.
    /// </summary>
    public bool EnableGzip { get; set; } = true;

    /// <summary>
    /// Gets or sets the default checksum mode: <c>none</c>, <c>segment</c>, or <c>record</c>.
    /// Per-record checksums cost roughly 20% of the wire size and are redundant over TLS plus a
    /// segment digest, so <c>segment</c> is the default.
    /// </summary>
    public string DefaultChecksumMode { get; set; } = "segment";

    /// <summary>
    /// Gets or sets how long a stream request waits for a snapshot that is still being built,
    /// in seconds, before answering with a valid but empty segment.
    /// </summary>
    /// <remarks>
    /// Bounded well below the client's between-packets timeout. Waiting is what lets "still
    /// building" be reported as an ordinary empty segment instead of an error the client has no
    /// retry path for.
    /// </remarks>
    public int StreamWaitSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum number of snapshot builds one user may trigger per hour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is on <b>builds</b>, not on sessions. Opening a session is cheap — it usually
    /// reuses an existing snapshot or returns changes — whereas building a snapshot crawls the
    /// whole library. An earlier version limited session creation instead and blocked a real client
    /// doing nothing unusual: six sessions in two minutes is ordinary behaviour for an app that
    /// syncs on launch, on foreground, on pull-to-refresh and on a timer.
    /// </para>
    /// <para>
    /// Reuse and change sessions are never rate limited, so a client in a retry loop is slowed only
    /// when it would actually cost the server work.
    /// </para>
    /// </remarks>
    public int MaxSnapshotBuildsPerUserPerHour { get; set; } = 4;

    /// <summary>
    /// Gets or sets the size, in megabytes, beyond which the plugin stops building new snapshots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero disables the check. A snapshot of a 30,000-track library is roughly 12 MB, so the
    /// default leaves room for several generations plus the journal before anything is refused.
    /// </para>
    /// <para>
    /// Only <b>builds</b> are refused under pressure, never acknowledgements. Refusing a build costs
    /// a client a delayed sync; refusing an acknowledgement costs it correctness, because the client
    /// has already committed the data locally and would be forced to replay work it has done. Given
    /// a choice between a client that waits and a client that is wrong, the plugin makes the client
    /// wait.
    /// </para>
    /// </remarks>
    public int StoragePressureMegabytes { get; set; } = 2048;

    /// <summary>
    /// Gets or sets a value indicating whether verbose per-record diagnostics are logged.
    /// Never logs tokens or payload contents.
    /// </summary>
    public bool VerboseDiagnostics { get; set; }
}
