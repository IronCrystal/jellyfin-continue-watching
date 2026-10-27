using System;
using Jellyfin.Plugin.ContinueWatchingPlus.Api;
using Jellyfin.Plugin.ContinueWatchingPlus.Caching;
using Jellyfin.Plugin.ContinueWatchingPlus.Merging;
using Jellyfin.Plugin.ContinueWatchingPlus.Preferences;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<NextUpPreferenceStore>();
        serviceCollection.AddSingleton<MergedListBuilder>();
        serviceCollection.AddSingleton(serviceProvider => new MergedListCache(
            serviceProvider.GetRequiredService<MergedListBuilder>().Build,
            () => TimeSpan.FromMinutes(Math.Max(1, Plugin.Instance?.Configuration.CacheMinutes ?? 5)),
            TimeProvider.System,
            serviceProvider.GetRequiredService<ILogger<MergedListCache>>()));
        serviceCollection.AddSingleton<ContinueWatchingActionFilter>();
        serviceCollection.AddHostedService<CacheInvalidationService>();

        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ContinueWatchingActionFilter>());
    }
}
