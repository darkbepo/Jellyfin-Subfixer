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

    /// <summary>Gets or sets how many subtitles startup recovery restored.</summary>
    public int Recovered { get; set; }

    /// <summary>Gets a task that completes once startup recovery has run (or the state store failed). The queue waits for it.</summary>
    public Task RecoveryDone => _recoveryDone.Task;

    private readonly TaskCompletionSource _recoveryDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Releases the queue.</summary>
    public void MarkRecoveryDone() => _recoveryDone.TrySetResult();
}
