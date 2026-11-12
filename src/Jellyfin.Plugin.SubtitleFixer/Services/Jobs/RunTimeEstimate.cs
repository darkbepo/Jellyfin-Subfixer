using Jellyfin.Plugin.SubtitleFixer.Worker;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>An expected run, in seconds from the start of SubSync.</summary>
/// <param name="Total">Whole run.</param>
/// <param name="ListenStart">When listening is expected to start (start-up plus reading the audio).</param>
/// <param name="ListenEnd">When listening is expected to end (matching follows).</param>
public sealed record RunEstimate(double Total, double ListenStart, double ListenEnd);

/// <summary>
/// Guesses how long SubSync will take for one item, for the progress bar only (D40). SubSync's time grows with the
/// video's length, so the guess is a start-up time plus length times a speed ratio, learned from finished runs on this
/// server; before the first one the ratio comes from the GPU/CPU mode of the last run, or assumes CPU.
/// Measured: an i7-13850HX on CPU takes about 0.3x the video's length; an RTX 3050 about 0.03x (README).
/// SubSync 1.3.0 reports per-step seconds, so it learns start-up (the run's wall time minus reading audio, listening and
/// matching), reading audio and listening per second of video, and matching separately. With 1.2.0 it learns one ratio
/// from the run's seconds minus <see cref="Overhead"/>. Runs whose transcript came from SubSync's cache teach nothing.
/// </summary>
public static class RunTimeEstimate
{
    /// <summary>Start-up (self-check, Python, model loading) until one is learned from a SubSync 1.3.0 run.</summary>
    public static readonly TimeSpan Overhead = TimeSpan.FromSeconds(15);

    /// <summary>The bar holds below this share until the item really ends.</summary>
    public const double MaxShare = 0.95;

    private const double CpuRatio = 0.33;
    private const double GpuRatio = 0.04;

    // Before reading audio is measured: its share of the speed ratio (9.8 s read for 58.1 s listened, README-CLI).
    private const double DefaultReadShare = 0.15;

    // 1.2.0 (or no timings): a run this fast used a cached transcript (D40).
    private const double FastRunShare = 0.01;
    private const double FastRunSeconds = 10;

    private static readonly object Gate = new();
    private static double? _legacyRatio;
    private static double? _startup;
    private static double? _readRatio;
    private static double? _listenRatio;
    private static double? _match;

    /// <summary>Expected run for a video of this length, or null when the length is unknown.</summary>
    /// <param name="runtime">Video length.</param>
    /// <returns>The estimate, or null.</returns>
    public static RunEstimate? Expect(TimeSpan? runtime)
    {
        if (runtime is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        var seconds = length.TotalSeconds;
        lock (Gate)
        {
            if (_listenRatio is { } listenRatio)
            {
                var startup = _startup ?? Overhead.TotalSeconds;
                var listenStart = startup + ((_readRatio ?? 0) * seconds);
                var listenEnd = listenStart + (listenRatio * seconds);
                return new RunEstimate(Math.Round(listenEnd + (_match ?? 0)), listenStart, listenEnd);
            }

            var ratio = _legacyRatio ?? (SpeechRecognition.LastMode == "GPU" ? GpuRatio : CpuRatio);
            var work = seconds * ratio;
            var start = Overhead.TotalSeconds;
            return new RunEstimate(Math.Round(start + work), start + (work * DefaultReadShare), start + work);
        }
    }

    /// <summary>
    /// The item's share of the bar while SubSync listens and says how far (1.3.0 <c>--progress</c>): listening's
    /// expected slice of the run, filled by SubSync's percent. Null in every other case; the page then uses elapsed time.
    /// </summary>
    /// <param name="estimate">The run's estimate.</param>
    /// <param name="stage">Current stage.</param>
    /// <param name="stagePercent">Percent within the stage.</param>
    /// <returns>0 to 95, or null.</returns>
    public static int? Percent(RunEstimate? estimate, string? stage, int? stagePercent)
    {
        if (estimate is not { Total: > 0 } e || stage != SubSyncStage.Listening || stagePercent is not { } p)
        {
            return null;
        }

        var at = e.ListenStart + ((e.ListenEnd - e.ListenStart) * Math.Clamp(p, 0, 100) / 100.0);
        return (int)Math.Round(Math.Clamp(at / e.Total, 0, MaxShare) * 100);
    }

    /// <summary>
    /// Learns from a finished run (synced or no_match: both listened to the whole soundtrack). Each new value counts as
    /// much as all earlier ones together. Skipped: clips under a minute, runs from SubSync's transcript cache (1.3.0
    /// says so), and, without 1.3.0 timings, runs under 1% of the video's length or under 10 s, which only a cached
    /// transcript explains (a Retry from the failed-syncs log, or a re-sync after a no-match).
    /// </summary>
    /// <param name="runtime">Video length.</param>
    /// <param name="result">The run.</param>
    public static void Record(TimeSpan? runtime, WorkerResult result)
    {
        if (runtime is not { } length || length < TimeSpan.FromMinutes(1))
        {
            return;
        }

        var timings = result.Timings;
        if (timings is { FromCache: true })
        {
            return;
        }

        var seconds = length.TotalSeconds;
        if (timings is { ReadAudio: { } read, Listen: { } listen })
        {
            var match = Math.Max(0, timings.Match ?? 0);
            double? startup = result.ProcessSeconds is { } wall
                ? wall - (read + listen + match)
                : result.ModelLoadSeconds;
            lock (Gate)
            {
                _readRatio = Average(_readRatio, Math.Clamp(read / seconds, 0, 3));
                _listenRatio = Average(_listenRatio, Math.Clamp(listen / seconds, 0.001, 3));
                _match = Average(_match, Math.Min(match, 600));
                if (startup is { } s)
                {
                    _startup = Average(_startup, Math.Clamp(s, 0, 600));
                }
            }

            return;
        }

        Record(runtime, result.Seconds);
    }

    /// <summary>Learns from a run that has only its total seconds (SubSync 1.2.0).</summary>
    /// <param name="runtime">Video length.</param>
    /// <param name="seconds">How long SubSync took.</param>
    public static void Record(TimeSpan? runtime, double? seconds)
    {
        if (runtime is not { } length || length < TimeSpan.FromMinutes(1) || seconds is not > 0)
        {
            return;
        }

        if (seconds.Value < Math.Max(FastRunSeconds, length.TotalSeconds * FastRunShare))
        {
            return;
        }

        var ratio = Math.Clamp((seconds.Value - Overhead.TotalSeconds) / length.TotalSeconds, 0.005, 3);
        lock (Gate)
        {
            _legacyRatio = Average(_legacyRatio, ratio);
        }
    }

    /// <summary>Forgets what was learned (tests).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _legacyRatio = null;
            _startup = null;
            _readRatio = null;
            _listenRatio = null;
            _match = null;
        }
    }

    private static double Average(double? old, double value) => old is { } o ? (o + value) / 2 : value;
}
