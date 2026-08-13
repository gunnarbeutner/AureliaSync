namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// One materialised record, stored ready to go on the wire.
/// </summary>
/// <remarks>
/// <paramref name="Payload"/> holds the exact UTF-8 JSON bytes the client will receive, so
/// streaming copies them through rather than deserialising and reserialising tens of thousands of
/// objects per segment.
/// </remarks>
/// <param name="Ordinal">Position within the snapshot.</param>
/// <param name="Kind">Record kind.</param>
/// <param name="EntityType">Entity type for item upserts, otherwise null.</param>
/// <param name="EntityId">The entity this record describes.</param>
/// <param name="Payload">Wire-ready JSON bytes.</param>
/// <param name="Checksum">Optional per-row digest.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1819:Properties should not return arrays",
    Justification = "The array is the point: this is a wire buffer read straight from SQLite and "
        + "written straight to the response. Exposing it as a copied collection would add an "
        + "allocation and a copy per record, tens of thousands of times per snapshot.")]
public sealed record SnapshotRow(
    long Ordinal,
    string Kind,
    string? EntityType,
    string EntityId,
    byte[] Payload,
    string? Checksum);
