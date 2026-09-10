using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Reelink.Configuration;

/// <summary>
/// Configuration for duplicate-series grouping and movie/episode version merging.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the comma-separated provider priority list used for series grouping.
    /// </summary>
    public string ProviderIds { get; set; } = "Tvdb,Tmdb,Imdb";

    /// <summary>
    /// Gets or sets the number of matching IDs required on every series in a group.
    /// </summary>
    public int MinimumProviderMatches { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether grouping runs after library scans.
    /// </summary>
    public bool RunAfterLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether series grouping changes are only logged.
    /// </summary>
    public bool PreviewOnly { get; set; }

    /// <summary>
    /// Gets or sets the library locations excluded from movie and episode version merging.
    /// </summary>
    public string[] LocationsExcluded { get; set; } = [];
}
