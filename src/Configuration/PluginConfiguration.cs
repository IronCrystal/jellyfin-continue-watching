using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool ApplyToInfuseOnly { get; set; }

    public bool BlockNextUpEndpoint { get; set; }

    public bool OneEntryPerSeries { get; set; } = true;

    public bool SortNextUpByEpisodeAddedDate { get; set; } = true;

    public bool RequireFinishedEpisodeForNextUp { get; set; } = true;

    public int CacheMinutes { get; set; } = 5;

    // Used for users whose web client hasn't reported its own Next Up settings yet.
    // Defaults match jellyfin-web's.
    public int DefaultMaxDaysInNextUp { get; set; } = 365;

    public bool DefaultEnableRewatching { get; set; }
}
