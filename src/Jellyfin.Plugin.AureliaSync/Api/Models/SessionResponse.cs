using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Response to opening a session.
/// </summary>
public class SessionResponse
{
    /// <summary>Gets or sets the session identifier. Appears in a URL path, so it is path-safe.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the delivery mode. Always <c>snapshot</c> in protocol v1.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "snapshot";

    /// <summary>Gets or sets the negotiated protocol version.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    /// <summary>Gets or sets the negotiated wire schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets where to resume from, or null to start at the beginning.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    /// <summary>Gets or sets the client's current durable position.</summary>
    [JsonPropertyName("checkpointToken")]
    public string? CheckpointToken { get; set; }

    /// <summary>
    /// Gets or sets the snapshot being delivered, as a string.
    /// </summary>
    /// <remarks>
    /// A string here while <c>status.snapshot.generation</c> is a number. The two client decoders
    /// disagree and both are strict, so the server matches each rather than breaking one.
    /// </remarks>
    [JsonPropertyName("snapshotGeneration")]
    public string? SnapshotGeneration { get; set; }

    /// <summary>Gets or sets the journal head, reserved for change sessions.</summary>
    [JsonPropertyName("journalHead")]
    public long? JournalHead { get; set; }

    /// <summary>Gets or sets when the session expires if left idle.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Gets or sets the session state, for diagnostics.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Gets or sets an explanatory message, for diagnostics.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
