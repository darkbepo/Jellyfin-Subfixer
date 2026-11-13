using System.Collections.Concurrent;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>
/// Items a Bazarr download is working on right now (D52). A sync of the same item waits until the download is
/// finished, so the download's snapshot and restore of the subtitle files can never undo or mix with a sync. The
/// download refuses an item that is queued or syncing; this closes the gap of a job starting during a download.
/// </summary>
public sealed class DownloadGuard
{
    private readonly ConcurrentDictionary<Guid, byte> _active = new();

    /// <summary>Gets the instance shared by the download service and the job runner.</summary>
    public static DownloadGuard Shared { get; } = new();

    /// <summary>Marks an item as being downloaded for.</summary>
    /// <param name="itemId">Item.</param>
    /// <returns>False when a download for it is already running.</returns>
    public bool TryEnter(Guid itemId) => _active.TryAdd(itemId, 0);

    /// <summary>Ends <see cref="TryEnter"/>.</summary>
    /// <param name="itemId">Item.</param>
    public void Exit(Guid itemId) => _active.TryRemove(itemId, out _);

    /// <summary>Gets a value indicating whether a download is working on the item.</summary>
    /// <param name="itemId">Item.</param>
    /// <returns>True while it is.</returns>
    public bool IsActive(Guid itemId) => _active.ContainsKey(itemId);

    /// <summary>Waits until no download is working on the item (downloads are bounded: about two minutes).</summary>
    /// <param name="itemId">Item.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task.</returns>
    public async Task WaitAsync(Guid itemId, CancellationToken cancellationToken)
    {
        while (IsActive(itemId))
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }
}
