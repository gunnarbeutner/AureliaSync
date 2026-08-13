namespace Jellyfin.Plugin.AureliaSync.Projection;

/// <summary>
/// The parts of an album a track needs to describe itself.
/// </summary>
/// <remarks>
/// Collected once while projecting albums and reused across every track, so that rendering album
/// art never requires the client to have already received the album record, and the server never
/// re-reads an album per track.
/// </remarks>
/// <param name="Name">The album's name.</param>
/// <param name="ImageTag">The album's Primary image cache tag.</param>
public readonly record struct AlbumSummary(string? Name, string? ImageTag);
