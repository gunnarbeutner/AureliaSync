using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AureliaSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AureliaSync;

/// <summary>
/// The AureliaSync plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin's permanent identity. Aurelia's install flow selects the package by this GUID,
    /// and Jellyfin keys installed-plugin state on it. It must never change.
    /// </summary>
    public const string PluginGuid = "3fbf911d-ab0c-46dc-81d6-b3317bb8b176";

    /// <summary>
    /// Name of the directory, under the Jellyfin data path, holding the plugin's own database.
    /// </summary>
    public const string DataDirectoryName = "aureliasync";

    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Serializer used to persist plugin configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationPaths = applicationPaths;
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    /// <remarks>
    /// This is null until Jellyfin constructs the plugin, which happens during
    /// <c>InitializeServices</c> — after <c>IPluginServiceRegistrator.RegisterServices</c> has run.
    /// Never dereference it at service-registration time; it is safe inside hosted-service
    /// <c>StartAsync</c> and inside controllers.
    /// </remarks>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Aurelia Sync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Resumable snapshot and change-journal synchronisation of the music library for the Aurelia client.";

    /// <summary>
    /// Gets the directory holding the plugin's own SQLite database.
    /// </summary>
    /// <remarks>
    /// Deliberately derived from <see cref="IApplicationPaths.DataPath"/> rather than
    /// <c>BasePlugin.DataFolderPath</c>. The latter resolves under <c>plugins/</c> and appends
    /// <c>_&lt;version&gt;</c> when the directory does not already exist, which would orphan the
    /// database on every plugin upgrade and expose it to Jellyfin's plugin-directory cleanup.
    /// </remarks>
    public string DatabaseDirectory =>
        System.IO.Path.Combine(_applicationPaths.DataPath, DataDirectoryName);

    /// <summary>
    /// Gets the full path to the plugin's SQLite database file.
    /// </summary>
    public string DatabasePath =>
        System.IO.Path.Combine(DatabaseDirectory, "aureliasync.db");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
