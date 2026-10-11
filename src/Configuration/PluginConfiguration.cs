using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NextUpMerge.Configuration;

/// <summary>
/// Plugin configuration – kept minimal for now.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool ApplyToInfuseOnly { get; set; } = false;
    public bool BlockNextUpEndpoint { get; set; } = false;
    public bool HideNextUpForSeriesInProgress { get; set; } = true;
    public bool SortNextUpByEpisodeAddedDate { get; set; } = true;
}