using Jellyfin.Plugin.SubtitleFixer.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>
/// Startup recovery (JOB-FLOW.md step 8): anything still journaled means Jellyfin stopped mid-sync. Restore the
/// subtitle from its backup, delete the backup, and mark the episode not synced. Uses the journal instead of
/// scanning the library for <c>.bak</c> files (D23).
/// </summary>
public sealed class RecoveryService
{
    private readonly IStateStore _state;
    private readonly BackupService _backup;
    private readonly ILogger<RecoveryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryService"/> class.
    /// </summary>
    /// <param name="state">State store.</param>
    /// <param name="backup">Backup service.</param>
    /// <param name="logger">Logger.</param>
    public RecoveryService(IStateStore state, BackupService backup, ILogger<RecoveryService> logger)
    {
        _state = state;
        _backup = backup;
        _logger = logger;
    }

    /// <summary>Restores every journaled backup.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many subtitles were restored.</returns>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var entry in await _state.GetInFlightAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await _backup.RestoreAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    restored++;
                    _logger.LogWarning("Restored {Subtitle} from its backup after an interrupted sync", entry.SubtitlePath);
                }

                await _state.DeleteAsync(entry.ItemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the journal entry and the .bak in place; the next start tries again.
                _logger.LogError(ex, "Could not restore {Subtitle} from {Backup}; both are left as they are", entry.SubtitlePath, entry.BackupPath);
            }
        }

        return restored;
    }
}
