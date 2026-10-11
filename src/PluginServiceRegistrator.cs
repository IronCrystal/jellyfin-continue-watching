using Jellyfin.Plugin.ContinueWatchingTweaks.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ContinueWatchingTweaks;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// Jellyfin scans for IPluginServiceRegistrator on startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<IStartupFilter, ResumeStartupFilter>();
    }
}
