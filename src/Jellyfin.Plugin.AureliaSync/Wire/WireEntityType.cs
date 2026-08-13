namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// Values of the <c>entityType</c> field on an <see cref="WireKind.ItemUpsert"/> record.
/// </summary>
/// <remarks>
/// An unrecognised entity type aborts the client's sync, exactly as an unrecognised record kind
/// does, so this list is closed for protocol v1.
/// </remarks>
public static class WireEntityType
{
    /// <summary>A track.</summary>
    public const string Track = "track";

    /// <summary>An album.</summary>
    public const string Album = "album";

    /// <summary>An artist.</summary>
    public const string Artist = "artist";

    /// <summary>A playlist.</summary>
    public const string Playlist = "playlist";

    /// <summary>A genre.</summary>
    public const string Genre = "genre";
}
