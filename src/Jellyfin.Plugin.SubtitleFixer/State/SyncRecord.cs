namespace Jellyfin.Plugin.SubtitleFixer.State;

/// <summary>Outcome stored for an episode.</summary>
public enum SyncStatus
{
    /// <summary>Synced and verified.</summary>
    Done,

    /// <summary>SubSync matched too few lines and wrote nothing.</summary>
    Check,

    /// <summary>Failed; the original subtitle was restored.</summary>
    Failed,
}

/// <summary>
/// Identity of a video file without hashing it: path, size and last write time (UTC). D12.
/// </summary>
/// <param name="Path">Full path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="MtimeUtc">Last write time, UTC.</param>
public sealed record VideoFingerprint(string Path, long Size, DateTime MtimeUtc)
{
    /// <summary>Reads the fingerprint of a file from disk.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The fingerprint, or null when the file does not exist.</returns>
    public static VideoFingerprint? FromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        return new VideoFingerprint(info.FullName, info.Length, DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
    }

    /// <summary>Whether this fingerprint describes the same file content as <paramref name="other"/>.</summary>
    /// <param name="other">Other fingerprint.</param>
    /// <returns>True when path (case-insensitive, Windows), size and mtime all match.</returns>
    public bool Matches(VideoFingerprint? other)
    {
        return other is not null
            && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && Size == other.Size
            && MtimeUtc.ToUniversalTime().Ticks == other.MtimeUtc.ToUniversalTime().Ticks;
    }
}

/// <summary>A backup that exists on disk while an episode syncs (table <c>in_flight</c>, D23).</summary>
/// <param name="ItemId">Episode.</param>
/// <param name="SubtitlePath">The subtitle being synced.</param>
/// <param name="BackupPath">Its backup, <c>&lt;subtitle&gt;.bak</c>.</param>
/// <param name="StartedUtc">When the backup was journaled.</param>
public sealed record InFlightBackup(Guid ItemId, string SubtitlePath, string BackupPath, DateTime StartedUtc, string Language = Domain.SubtitleLanguage.English);

/// <summary>One row of <c>synced_items</c> (ARCHITECTURE.md, state schema).</summary>
public sealed record SyncRecord
{
    /// <summary>Gets the Jellyfin item id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the video fingerprint at the time of the attempt.</summary>
    public required VideoFingerprint Video { get; init; }

    /// <summary>Gets the subtitle path that was processed.</summary>
    public required string SubtitlePath { get; init; }

    /// <summary>Gets the outcome.</summary>
    public required SyncStatus Status { get; init; }

    /// <summary>Gets the reason or note.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the audio language SubSync used.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>Gets the SubSync version.</summary>
    public string? SubSyncVersion { get; init; }

    /// <summary>Gets the run time in seconds.</summary>
    public double? Seconds { get; init; }

    /// <summary>Gets when the row was written (UTC).</summary>
    public required DateTime UpdatedUtc { get; init; }

    /// <summary>Gets the subtitle language this record is about (D50); English for records from before 0.7.0.0.</summary>
    public string Language { get; init; } = Domain.SubtitleLanguage.English;
}
