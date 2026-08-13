namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// The <c>kind</c> discriminator on every NDJSON line, and the <c>entityType</c> values.
/// </summary>
/// <remarks>
/// The client treats an unrecognised record kind as fatal rather than skipping it, so nothing
/// outside this list may ever be emitted on the wire.
/// </remarks>
public static class WireKind
{
    /// <summary>First line of a segment.</summary>
    public const string SegmentBegin = "segment.begin";

    /// <summary>Last line of a segment. Its presence is what makes the segment valid.</summary>
    public const string SegmentEnd = "segment.end";

    /// <summary>An in-band failure, only ever written once the response body has started.</summary>
    public const string Error = "error";

    /// <summary>Create or replace one entity.</summary>
    public const string ItemUpsert = "item.upsert";

    /// <summary>One playlist entry, carrying the full track payload for that entry.</summary>
    public const string PlaylistReplace = "playlist.replace";

    /// <summary>Per-user state for one item.</summary>
    public const string UserDataUpsert = "userData.upsert";

    /// <summary>Tombstone. Reserved: never emitted in snapshot mode.</summary>
    public const string ItemDelete = "item.delete";

    /// <summary>
    /// One chunk of the identifiers a repaired catalog should retain. Only emitted in repair mode.
    /// </summary>
    public const string CatalogManifest = "catalog.manifest";

    /// <summary>Reserved no-op the client accepts and ignores.</summary>
    public const string RelationshipReplace = "relationship.replace";

    /// <summary>Reserved no-op the client accepts and ignores.</summary>
    public const string ControlReconcile = "control.reconcile";
}
