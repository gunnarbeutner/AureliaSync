namespace Jellyfin.Plugin.AureliaSync.Journal;

/// <summary>
/// A serialised payload and the entity type it describes.
/// </summary>
/// <param name="Payload">Wire-ready JSON bytes, or null when the item has no wire representation.</param>
/// <param name="EntityType">The wire entity type, or null alongside a null payload.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1819:Properties should not return arrays",
    Justification = "A wire buffer handed straight to the journal; wrapping it would add a copy.")]
internal readonly record struct ProjectedPayload(byte[]? Payload, string? EntityType);
