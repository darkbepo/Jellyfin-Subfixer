using Jellyfin.Plugin.SubtitleFixer.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>The backup could not be made; the CLI must not run.</summary>
public sealed class BackupException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="BackupException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Cause.</param>
    public BackupException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Never lose a subtitle (SPEC.md hard rule 7): journal, copy to <c>&lt;subtitle&gt;.bak</c>, and later either
/// discard the copy after a verified success or copy it back over the subtitle. The journal entry is written
/// before the copy and removed after the copy is gone, so a crash at any point leaves something recovery can act on.
/// </summary>
public sealed class BackupService
{
    /// <summary>Suffix Jellyfin ignores (M0 S4).</summary>
    public const string BackupSuffix = ".bak";

    private const int RestoreAttempts = 5;
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IStateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupService"/> class.
    /// </summary>
    /// <param name="state">State store (journal).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public BackupService(IStateStore state, TimeProvider time, ILogger<BackupService> logger)
    {
        _state = state;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the backup path for a subtitle.</summary>
    /// <param name="subtitlePath">Subtitle.</param>
    /// <returns><c>&lt;subtitle&gt;.bak</c>.</returns>
    public static string BackupPathFor(string subtitlePath) => subtitlePath + BackupSuffix;

    /// <summary>Journals and creates the backup. Throws <see cref="BackupException"/> and leaves nothing behind on failure.</summary>
    /// <param name="itemId">Episode.</param>
    /// <param name="subtitlePath">Subtitle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="language">The subtitle's language (D50): the journal is per item and language.</param>
    /// <returns>The journal entry.</returns>
    public async Task<InFlightBackup> CreateAsync(Guid itemId, string subtitlePath, CancellationToken cancellationToken, string language = Domain.SubtitleLanguage.English)
    {
        var backupPath = BackupPathFor(subtitlePath);
        if (File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            // Could be the only good copy left by something else; never overwrite it.
            throw new BackupException($"A backup already exists at {backupPath}. Remove or restore it by hand, then try again.");
        }

        var entry = new InFlightBackup(itemId, subtitlePath, backupPath, _time.GetUtcNow().UtcDateTime, Domain.SubtitleLanguage.Normalize(language));
        try
        {
            await _state.AddInFlightAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without a journal entry a crash could strand the .bak, so do not back up (and do not run).
            throw new BackupException("Cannot create backup: the state database is not available (" + ex.Message + ").", ex);
        }

        try
        {
            File.Copy(subtitlePath, backupPath, overwrite: false);
            if (new FileInfo(backupPath).Length != new FileInfo(subtitlePath).Length)
            {
                throw new IOException("The backup copy is incomplete.");
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(backupPath);
            await _state.RemoveInFlightAsync(itemId, entry.Language, CancellationToken.None).ConfigureAwait(false);
            throw new BackupException("Cannot create backup: " + ex.Message, ex);
        }
    }

    /// <summary>Copies the backup back over the subtitle, then removes the backup and the journal entry.</summary>
    /// <param name="entry">Journal entry.</param>
    /// <param name="cancellationToken">Cancellation token (only between attempts; a restore in progress is finished).</param>
    /// <returns>True when restored; false when the backup was missing.</returns>
    public async Task<bool> RestoreAsync(InFlightBackup entry, CancellationToken cancellationToken)
    {
        if (!File.Exists(entry.BackupPath))
        {
            _logger.LogWarning("Backup {Backup} is missing; nothing to restore for {Subtitle}", entry.BackupPath, entry.SubtitlePath);
            await _state.RemoveInFlightAsync(entry.ItemId, entry.Language, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(entry.BackupPath, entry.SubtitlePath, overwrite: true);
                if (new FileInfo(entry.SubtitlePath).Length != new FileInfo(entry.BackupPath).Length)
                {
                    throw new IOException("Restored file size does not match the backup.");
                }

                break;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < RestoreAttempts)
            {
                // The killed process tree may still hold the file for a moment.
                _logger.LogWarning(ex, "Restore of {Subtitle} failed (attempt {Attempt}); retrying", entry.SubtitlePath, attempt);
                await Task.Delay(RestoreRetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Only now is the backup redundant.
        File.Delete(entry.BackupPath);
        await _state.RemoveInFlightAsync(entry.ItemId, entry.Language, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes the backup after a verified success, then the journal entry.</summary>
    /// <param name="entry">Journal entry.</param>
    /// <returns>A task.</returns>
    public async Task DiscardAsync(InFlightBackup entry)
    {
        File.Delete(entry.BackupPath);
        await _state.RemoveInFlightAsync(entry.ItemId, entry.Language, CancellationToken.None).ConfigureAwait(false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
