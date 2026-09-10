using System.Globalization;
using Jellyfin.Plugin.Reelink.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// Groups duplicate Jellyfin series by external provider identity.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Reelink";

    /// <inheritdoc />
    public override string Description => "Groups duplicate TV shows with matching external IDs, and merges or splits repeated movie and episode versions, without deleting library records or media files.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("56994efb-d240-459a-a992-4000cb41e758");

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public PluginConfiguration CurrentConfiguration => Configuration;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
