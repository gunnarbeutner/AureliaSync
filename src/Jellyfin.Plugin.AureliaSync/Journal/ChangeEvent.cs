using System;

namespace Jellyfin.Plugin.AureliaSync.Journal;

/// <summary>
/// What kind of change was observed.
/// </summary>
public enum ChangeEventKind
{
    /// <summary>An item was created or edited.</summary>
    ItemChanged = 0,

    /// <summary>An item was deleted.</summary>
    ItemRemoved = 1,

    /// <summary>One user's state for an item changed.</summary>
    UserDataChanged = 2
}

/// <summary>
/// One observed change, before it is materialised into journal records.
/// </summary>
/// <remarks>
/// Deliberately holds identifiers rather than a <c>BaseItem</c> reference. Events are coalesced over
/// a short window, and an entity captured at event time could be several edits stale by the time it
/// is written; the item is re-read at materialisation instead. Deletions are the exception — there
/// is nothing left to re-read, so the entity type is captured here.
/// </remarks>
/// <param name="Kind">What happened.</param>
/// <param name="ItemId">The item affected.</param>
/// <param name="UserId">The user, for user-data changes; otherwise empty.</param>
/// <param name="EntityType">The wire entity type, captured for deletions.</param>
public readonly record struct ChangeEvent(
    ChangeEventKind Kind,
    Guid ItemId,
    Guid UserId,
    string? EntityType)
{
    /// <summary>
    /// Gets the coalescing key. Two events sharing one are the same change observed twice.
    /// </summary>
    public (ChangeEventKind Kind, Guid ItemId, Guid UserId) CoalesceKey => (Kind, ItemId, UserId);
}
