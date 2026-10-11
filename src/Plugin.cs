using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ContinueWatchingTweaks.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ContinueWatchingTweaks;

/// <summary>
/// Plugin entry point. Jellyfin discovers this via MEF on startup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Continue Watching Tweaks";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("747ee9a0-1b72-42aa-8b62-d9135eb53438");

    /// <inheritdoc />
    public override string Description =>
        "Merges Continue Watching and Next Up into a single resumable list for Infuse (and other clients).";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name                 = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }}
