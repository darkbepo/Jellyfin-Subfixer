using System.Globalization;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleFixer;

/// <summary>
/// The Subtitle Fixer plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin id. Never change it: Jellyfin keys settings and installs on it.</summary>
    public static readonly Guid PluginId = Guid.Parse("aca97159-ff3b-469e-a552-db44d9b77806");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Subtitle Fixer";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    // Jellyfin writes this into the installed meta.json, so keep it in line with build\plugin-info.ps1.
    public override string Description => "Re-times English subtitles on English-audio episodes and movies with SubSync, from a page in the avatar menu. Windows servers only. Requires the File Transformation plugin and a SubSync install on the server.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "SubtitleFixer",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),

                // Listed in the dashboard's plugin section, so admins reach the settings in one click
                // (jellyfin-web 12.4 PluginDrawerSection; MenuIcon is a Material Icons ligature).
                EnableInMainMenu = true,
                DisplayName = "Subtitle Fixer",
                MenuIcon = "subtitles",
            },
        ];
    }
}
