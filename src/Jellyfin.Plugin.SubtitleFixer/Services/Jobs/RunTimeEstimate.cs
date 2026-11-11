using Jellyfin.Plugin.SubtitleFixer.Worker;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>
/// Guesses how long SubSync will take for one item, for the progress bar only (D40). SubSync's time grows with the
/// video's length, so the guess is length times a speed ratio. The ratio is learned from finished runs on this
/// server; before the first one it comes from the GPU/CPU mode of the last run, or assumes CPU.
/// Measured: an i7-13850HX on CPU takes about 0.3x the video's length; an RTX 3050 about 0.03x (README).
/// </summary>
public static class RunTimeEstimate
{
    /// <summary>Model loading and setup before SubSync starts listening.</summary>
    public static readonly TimeSpan Overhead = TimeSpan.FromSeconds(15);

    private const double CpuRatio = 0.33;
    private const double GpuRatio = 0.04;
    private static readonly object Gate = new();
    private static double? _learned;

    /// <summary>Expected seconds for a video of this length, or null when the length is unknown.</summary>
    /// <param name="runtime">Video length.</param>
    /// <returns>Seconds, or null.</returns>
    public static double? Expect(TimeSpan? runtime)
    {
        if (runtime is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        double ratio;
        lock (Gate)
        {
            ratio = _learned ?? (SpeechRecognition.LastMode == "GPU" ? GpuRatio : CpuRatio);
        }

        return Math.Round((length.TotalSeconds * ratio) + Overhead.TotalSeconds);
    }

    /// <summary>Learns from a finished run: the newer run counts as much as all earlier ones together.</summary>
    /// <param name="runtime">Video length.</param>
    /// <param name="seconds">How long SubSync took.</param>
    public static void Record(TimeSpan? runtime, double? seconds)
    {
        if (runtime is not { } length || length < TimeSpan.FromMinutes(1) || seconds is not > 0)
        {
            return;
        }

        var ratio = Math.Clamp((seconds.Value - Overhead.TotalSeconds) / length.TotalSeconds, 0.005, 3);
        lock (Gate)
        {
            _learned = _learned is { } old ? (old + ratio) / 2 : ratio;
        }
    }

    /// <summary>Forgets what was learned (tests).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _learned = null;
        }
    }
}
