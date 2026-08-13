using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AureliaSync.Wire;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Response of the capability probe.
/// </summary>
/// <remarks>
/// This is Aurelia's feature probe and is called on every connect, so it deliberately runs no
/// Jellyfin library queries — only cheap reads of the plugin's own small database. It also reports
/// nothing about any user other than the caller.
/// </remarks>
public class StatusResponse
{
    /// <summary>Gets or sets the plugin identifier.</summary>
    [JsonPropertyName("plugin")]
    public string PluginName { get; set; } = "AureliaSync";

    /// <summary>Gets or sets the plugin version.</summary>
    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the supported protocol version range.</summary>
    [JsonPropertyName("protocolVersions")]
    public VersionRange ProtocolVersions { get; set; } = new VersionRange();

    /// <summary>Gets or sets the supported wire-schema version range.</summary>
    [JsonPropertyName("wireSchemaVersions")]
    public VersionRange WireSchemaVersions { get; set; } = new VersionRange();

    /// <summary>Gets or sets the Jellyfin server version.</summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin ABI this build targets.</summary>
    [JsonPropertyName("targetAbi")]
    public string TargetAbi { get; set; } = WireSchema.TargetAbi;

    /// <summary>
    /// Gets or sets overall health: <c>ok</c>, <c>starting</c>, <c>degraded</c>, or <c>unavailable</c>.
    /// </summary>
    [JsonPropertyName("health")]
    public string Health { get; set; } = "starting";

    /// <summary>Gets or sets an administrator-facing explanation when health is not <c>ok</c>.</summary>
    [JsonPropertyName("healthDetail")]
    public string? HealthDetail { get; set; }

    /// <summary>Gets or sets a value indicating whether synchronisation is enabled by the administrator.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the plugin database's schema version.</summary>
    [JsonPropertyName("databaseSchemaVersion")]
    public int DatabaseSchemaVersion { get; set; }

    /// <summary>Gets or sets journal extents.</summary>
    [JsonPropertyName("journal")]
    public JournalStatus Journal { get; set; } = new JournalStatus();

    /// <summary>Gets or sets the caller's snapshot state.</summary>
    [JsonPropertyName("snapshot")]
    public SnapshotStatus Snapshot { get; set; } = new SnapshotStatus();

    /// <summary>Gets or sets the caller's subscription state.</summary>
    [JsonPropertyName("user")]
    public UserStatus User { get; set; } = new UserStatus();

    /// <summary>Gets or sets the delivery limits a client should plan around.</summary>
    [JsonPropertyName("limits")]
    public LimitsStatus Limits { get; set; } = new LimitsStatus();

    /// <summary>Gets or sets the server's current time, so clients need not trust their own clock.</summary>
    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }

    /// <summary>Gets or sets the correlation identifier for this response.</summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;
}
