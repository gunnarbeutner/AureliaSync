using System;

namespace Jellyfin.Plugin.AureliaSync.Projection;

/// <summary>
/// One user's state for one item.
/// </summary>
public sealed record UserDataFacts
{
    /// <summary>Gets a value indicating whether the user favourited the item.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets how many times the user has played it.</summary>
    public int PlayCount { get; init; }

    /// <summary>Gets when the user last played it.</summary>
    public DateTimeOffset? LastPlayedAt { get; init; }

    /// <summary>Gets the stored resume position, in Jellyfin ticks.</summary>
    public long PlaybackPositionTicks { get; init; }

    /// <summary>Gets a value indicating whether the item is marked played.</summary>
    public bool Played { get; init; }

    /// <summary>
    /// Gets a value indicating whether this state is worth sending.
    /// </summary>
    /// <remarks>
    /// A snapshot writes into an empty staging area, so an item the user has never touched needs
    /// no record at all. Skipping them keeps tens of thousands of empty rows off the wire.
    /// </remarks>
    public bool IsWorthSending =>
        IsFavorite || Played || PlayCount > 0 || PlaybackPositionTicks > 0 || LastPlayedAt is not null;
}
