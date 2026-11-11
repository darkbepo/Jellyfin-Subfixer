namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>What happened at startup, for the diagnostics page and logs.</summary>
public sealed class StartupStatus
{
    /// <summary>Gets or sets a value indicating whether the state store opened and migrated.</summary>
    public bool StateReady { get; set; }

    /// <summary>Gets or sets the state store error, if any.</summary>
    public string? StateError { get; set; }

    /// <summary>Gets or sets a value indicating whether the menu transformation is registered.</summary>
    public bool MenuRegistered { get; set; }

    /// <summary>Gets or sets the registration error, if any.</summary>
    public string? MenuError { get; set; }
}
