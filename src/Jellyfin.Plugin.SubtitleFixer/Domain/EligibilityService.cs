using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>Why an episode cannot be queued. Wire names are in <see cref="EligibilityService.ReasonCode"/>.</summary>
public enum IneligibleReason
{
    /// <summary>Eligible.</summary>
    None,

    /// <summary>Jellyfin has no video path, or the file is gone.</summary>
    NoVideoFile,

    /// <summary>A <c>done</c> record matches the current video.</summary>
    AlreadySynced,

    /// <summary>No audio stream tagged <c>en</c> or <c>eng</c>.</summary>
    NoEnglishAudio,

    /// <summary>No <c>&lt;video&gt;.en.srt</c>, <c>.eng.srt</c>, <c>.en.hi.srt</c> or <c>.eng.hi.srt</c>.</summary>
    NoSidecar,
}

/// <summary>Display status of an episode.</summary>
public enum EpisodeStatus
{
    /// <summary>Never attempted, or a stale <c>done</c> record (video changed).</summary>
    None,

    /// <summary>Synced, and the video is unchanged.</summary>
    Done,

    /// <summary>Last attempt ended in <c>check</c> or <c>failed</c>.</summary>
    Attempted,
}

/// <summary>A sidecar subtitle that may be processed.</summary>
/// <param name="Path">Full path of the subtitle.</param>
/// <param name="LanguageTag">Tag in the file name: <c>en</c>, <c>eng</c>, <c>en.hi</c> or <c>eng.hi</c>. Passed to SubSync as <c>--suffix</c>, so the result keeps the same name.</param>
public sealed record Sidecar(string Path, string LanguageTag);

/// <summary>Result of evaluating one episode.</summary>
/// <param name="Reason">Why it is not eligible, or <see cref="IneligibleReason.None"/>.</param>
/// <param name="Status">Display status.</param>
/// <param name="Message">Message of the last attempt, for <see cref="EpisodeStatus.Attempted"/>.</param>
/// <param name="Sidecar">The subtitle to process, when found.</param>
/// <param name="Video">Current video fingerprint, when the file exists.</param>
public sealed record Eligibility(IneligibleReason Reason, EpisodeStatus Status, string? Message, Sidecar? Sidecar, VideoFingerprint? Video)
{
    /// <summary>Gets a value indicating whether the episode may be queued.</summary>
    public bool IsEligible => Reason == IneligibleReason.None;
}

/// <summary>
/// Eligibility rules (SPEC.md section 4, CLAUDE.md non-negotiables 3 and 5).
/// </summary>
public static class EligibilityService
{
    /// <summary>
    /// Sidecar tags in priority order; only the first that exists is processed (one subtitle per episode).
    /// Plain English before hearing-impaired, <c>en</c> before <c>eng</c> (D6).
    /// </summary>
    private static readonly string[] SidecarTags = ["en", "eng", "en.hi", "eng.hi"];

    /// <summary>Gets the API wire name of a reason (API.md), or null for eligible.</summary>
    /// <param name="reason">Reason.</param>
    /// <returns>Wire name.</returns>
    public static string? ReasonCode(IneligibleReason reason) => reason switch
    {
        IneligibleReason.None => null,
        IneligibleReason.NoVideoFile => "no_video_file",
        IneligibleReason.AlreadySynced => "already_synced",
        IneligibleReason.NoEnglishAudio => "no_english_audio",
        IneligibleReason.NoSidecar => "no_sidecar",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>Gets the API wire name of a status.</summary>
    /// <param name="status">Status.</param>
    /// <returns>Wire name.</returns>
    public static string StatusCode(EpisodeStatus status) => status switch
    {
        EpisodeStatus.None => "none",
        EpisodeStatus.Done => "done",
        EpisodeStatus.Attempted => "attempted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>Whether a Jellyfin language tag means English.</summary>
    /// <param name="language">Language tag.</param>
    /// <returns>True for <c>en</c> or <c>eng</c>, any case.</returns>
    public static bool IsEnglish(string? language)
    {
        var tag = language?.Trim();
        return string.Equals(tag, "eng", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the sidecar named exactly <c>&lt;videoBase&gt;.en.srt</c>, <c>.eng.srt</c>, <c>.en.hi.srt</c> or <c>.eng.hi.srt</c>.
    /// No other names are considered (D6).
    /// </summary>
    /// <param name="videoPath">Video path.</param>
    /// <returns>The sidecar, or null.</returns>
    public static Sidecar? FindSidecar(string videoPath)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName))
        {
            return null;
        }

        foreach (var tag in SidecarTags)
        {
            var candidate = Path.Combine(folder, baseName + "." + tag + ".srt");
            if (File.Exists(candidate))
            {
                return new Sidecar(candidate, tag);
            }
        }

        return null;
    }

    /// <summary>Evaluates one episode against the rules and its stored record.</summary>
    /// <param name="episode">Episode facts from Jellyfin.</param>
    /// <param name="record">Stored record, if any.</param>
    /// <returns>The evaluation.</returns>
    public static Eligibility Evaluate(EpisodeFacts episode, SyncRecord? record)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var video = string.IsNullOrWhiteSpace(episode.VideoPath) ? null : VideoFingerprint.FromFile(episode.VideoPath);
        var status = StatusOf(record, video);
        var message = status == EpisodeStatus.Attempted ? record?.Message : null;

        if (video is null)
        {
            return new Eligibility(IneligibleReason.NoVideoFile, status, message, null, null);
        }

        if (status == EpisodeStatus.Done)
        {
            return new Eligibility(IneligibleReason.AlreadySynced, status, null, null, video);
        }

        if (!episode.AudioLanguages.Any(IsEnglish))
        {
            return new Eligibility(IneligibleReason.NoEnglishAudio, status, message, null, video);
        }

        var sidecar = FindSidecar(video.Path);
        if (sidecar is null)
        {
            return new Eligibility(IneligibleReason.NoSidecar, status, message, null, video);
        }

        return new Eligibility(IneligibleReason.None, status, message, sidecar, video);
    }

    private static EpisodeStatus StatusOf(SyncRecord? record, VideoFingerprint? video)
    {
        if (record is null)
        {
            return EpisodeStatus.None;
        }

        return record.Status switch
        {
            // A replaced video makes the episode eligible again (SPEC.md section 5).
            SyncStatus.Done => record.Video.Matches(video) ? EpisodeStatus.Done : EpisodeStatus.None,
            SyncStatus.Check or SyncStatus.Failed => EpisodeStatus.Attempted,
            _ => EpisodeStatus.None,
        };
    }
}
