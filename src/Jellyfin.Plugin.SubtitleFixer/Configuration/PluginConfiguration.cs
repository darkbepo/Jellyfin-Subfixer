using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleFixer.Configuration;

/// <summary>
/// Who may use the Subtitle Fixer page and API.
/// </summary>
public enum AllowedUsers
{
    /// <summary>Administrators only (default).</summary>
    AdminOnly = 0,

    /// <summary>Any signed-in user.</summary>
    AllAuthenticated = 1,
}

/// <summary>
/// Admin settings (SPEC.md section 6).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the folder that contains <c>subsync-cli.exe</c>. Empty means not configured.</summary>
    public string SubSyncFolder { get; set; } = string.Empty;

    /// <summary>Gets or sets who may use the plugin.</summary>
    public AllowedUsers AllowedUsers { get; set; } = AllowedUsers.AdminOnly;

    /// <summary>Gets or sets the most eligible episodes one request may queue.</summary>
    public int MaxEpisodesPerRequest { get; set; } = 150;

    /// <summary>Gets or sets the most episodes the global queue may hold.</summary>
    public int MaxQueuedEpisodes { get; set; } = 300;

    /// <summary>Gets or sets a value indicating whether SubSync tidies lines. Off passes <c>--keep-layout</c>.</summary>
    public bool TidyUpLines { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether SubSync is forced onto the CPU (<c>--cpu</c>).</summary>
    public bool ForceCpu { get; set; }

    /// <summary>Gets or sets the per-episode timeout in minutes.</summary>
    public int EpisodeTimeoutMinutes { get; set; } = 30;
}
