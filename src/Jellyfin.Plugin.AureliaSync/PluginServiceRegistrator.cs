using Jellyfin.Plugin.AureliaSync.Snapshots;
using Jellyfin.Plugin.AureliaSync.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AureliaSync;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
/// <remarks>
/// This runs during <c>ApplicationHost.Init</c>, before any <c>IServiceProvider</c> exists and
/// before Jellyfin constructs the <see cref="Plugin"/> instance. Nothing here may resolve a service
/// or touch <c>Plugin.Instance</c> — registration only.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SyncRuntime>();

        // Registered as a singleton and then handed to AddHostedService by resolution, so that the
        // controller and the hosted service share one instance rather than getting two.
        serviceCollection.AddSingleton<SnapshotCoordinator>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<SnapshotCoordinator>());
    }
}
