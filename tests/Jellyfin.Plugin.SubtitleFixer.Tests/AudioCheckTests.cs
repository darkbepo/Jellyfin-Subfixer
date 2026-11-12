using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>D49: the Check button. SubSync's <c>detect</c> is played back; the rules and the SubSync gate are real.</summary>
public sealed class AudioCheckTests : IDisposable
{
    private const string English = """{"video":"x","track":1,"audio_tracks":1,"track_language_tag":"und","language":"en","probability":0.985,"speech_seconds":90.0,"device":"cpu","seconds":63.1,"subsync_version":"1.4.0"}""";

    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly AudioCheckCache _cache = new();
    private readonly JobQueue _queue = new(TimeProvider.System);
    private readonly PluginConfiguration _config = new();
    private readonly List<IReadOnlyList<string>> _runs = new();
    private readonly Guid _user = Guid.NewGuid();
    private Version? _version = new(1, 4, 0);
    private Func<CancellationToken, Task<ProcessOutcome>> _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, "PROGRESS {}\n" + English + "\n", TimeSpan.FromSeconds(63)));

    public AudioCheckTests()
    {
        _dir.File("subsync-cli.exe", "fake");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _dir.Dispose();
    }

    private ILibraryFacade Facade => new AudioCheckFacade(_library, _cache);

    private AudioCheckService Service() => new(
        Facade,
        _state,
        _cache,
        _queue,
        () => _dir.Path,
        (_, _) => Task.FromResult(_version),
        (file, args, dir, timeout, onLine, ct) =>
        {
            _runs.Add(args);
            return _answer(ct);
        },
        () => _config,
        TimeProvider.System,
        NullLogger<AudioCheckService>.Instance);

    private VideoFacts Movie(string name, params string?[] audio)
    {
        var video = _dir.File(name + ".mp4");
        _dir.File(name + ".en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        return _library.AddMovie(name, 2026, video, audio);
    }

    private AudioCheck CheckOf(VideoFacts movie, string? language, double? probability, VideoFingerprint? video = null) =>
        new(movie.Node.Id, video ?? VideoFingerprint.FromFile(movie.VideoPath!)!, language, probability, 90, DateTime.UtcNow);

    // ---------- eligibility ----------
    [Fact]
    public void Untagged_audio_checked_as_english_is_english_for_the_same_video_only()
    {
        var movie = Movie("Other Mommy", "und");
        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(movie, null).Reason);

        _cache.Set(CheckOf(movie, "en", 0.985));
        Assert.True(EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).IsEligible);

        // A replaced video (another size) is checked again.
        _cache.Set(CheckOf(movie, "en", 0.985, new VideoFingerprint(movie.VideoPath!, 999_999, DateTime.UtcNow)));
        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).Reason);
    }

    [Theory]
    [InlineData("da", 0.95)]
    [InlineData("en", 0.4)]
    [InlineData(null, null)]
    public void A_check_that_did_not_hear_english_surely_changes_nothing(string? language, double? probability)
    {
        var movie = Movie("Film", "und");
        _cache.Set(CheckOf(movie, language, probability));

        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).Reason);
    }

    [Fact]
    public void A_check_never_overrides_a_language_tag()
    {
        var movie = Movie("Tagged", "ger");
        _cache.Set(CheckOf(movie, "en", 0.99));

        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).Reason);
    }

    // ---------- the service ----------
    [Fact]
    public async Task A_check_runs_detect_and_stores_and_remembers_what_it_heard()
    {
        var movie = Movie("Other Mommy", "und");
        _config.ForceCpu = true;

        var (result, error) = await Service().CheckAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.True(result!.English);
        Assert.Equal("en", result.Language);
        Assert.Contains("English", result.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "detect", movie.VideoPath!, "--cpu" }, Assert.Single(_runs));
        Assert.Equal("en", _state.AudioChecks[movie.Node.Id].Language);
        Assert.True(EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).IsEligible);
        Assert.True(_queue.TryBeginCheck());  // the gate was released
    }

    [Fact]
    public async Task Another_language_is_stored_and_described()
    {
        var movie = Movie("Dansk", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, """{"language":"da","probability":0.91,"speech_seconds":88}""", TimeSpan.FromSeconds(50)));

        var (result, _) = await Service().CheckAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.False(result!.English);
        Assert.Contains("Danish", result.Message, StringComparison.Ordinal);
        Assert.Contains("DA", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tagged_audio_and_an_old_subsync_are_refused_before_subsync_runs()
    {
        var english = Movie("English", "eng");
        var german = Movie("German", "ger");
        var untagged = Movie("Untagged", (string?)null);

        Assert.Equal("audio_tagged", (await Service().CheckAsync(_user, english.Node.Id, CancellationToken.None)).Error!.Code);
        Assert.Equal("audio_tagged", (await Service().CheckAsync(_user, german.Node.Id, CancellationToken.None)).Error!.Code);
        _version = new Version(1, 3, 1);
        var old = (await Service().CheckAsync(_user, untagged.Node.Id, CancellationToken.None)).Error!;
        Assert.Equal("subsync_too_old", old.Code);
        Assert.Contains("1.4.0", old.Message, StringComparison.Ordinal);
        Assert.Equal("not_found", (await Service().CheckAsync(_user, Guid.NewGuid(), CancellationToken.None)).Error!.Code);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task No_check_while_the_queue_runs_subsync()
    {
        var movie = Movie("Busy", "und");
        _queue.Enqueue(_user, [new JobRequestItem(Guid.NewGuid(), "Other", string.Empty)], JobSource.Manual, null, 300);
        await _queue.TakeAsync(CancellationToken.None);  // a queue run starts

        Assert.Equal("subsync_busy", (await Service().CheckAsync(_user, movie.Node.Id, CancellationToken.None)).Error!.Code);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task An_unreadable_video_is_reported_with_subsyncs_reason_and_stores_nothing()
    {
        var movie = Movie("Broken", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 3, """{"language":null,"reason":"can't read the audio: invalid data"}""", TimeSpan.FromSeconds(2)));

        var error = (await Service().CheckAsync(_user, movie.Node.Id, CancellationToken.None)).Error!;

        Assert.Equal("check_failed", error.Code);
        Assert.Contains("invalid data", error.Message, StringComparison.Ordinal);
        Assert.Empty(_state.AudioChecks);
        Assert.True(_queue.TryBeginCheck());
    }

    [Fact]
    public async Task No_speech_is_stored_as_unknown()
    {
        var movie = Movie("Silent", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, """{"language":null,"probability":null,"speech_seconds":1.2,"reason":"no speech found"}""", TimeSpan.FromSeconds(20)));

        var (result, _) = await Service().CheckAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(result!.Language);
        Assert.False(result.English);
        Assert.Contains("No speech", result.Message, StringComparison.Ordinal);
    }

    // ---------- the SubSync gate (hard rule 2) ----------
    [Fact]
    public async Task The_queue_waits_for_a_check_and_a_check_waits_for_the_queue_run()
    {
        Assert.True(_queue.TryBeginCheck());
        Assert.False(_queue.TryBeginCheck());
        _queue.Enqueue(_user, [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);

        var take = _queue.TakeAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(take.IsCompleted);

        _queue.EndCheck();
        var job = await take.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("E1", job.Snapshot.Name);

        Assert.False(_queue.TryBeginCheck());  // the run is active until its SubSync has closed
        _queue.Complete(job.Snapshot.JobId, JobItemStatus.Done, null, 1);
        Assert.False(_queue.TryBeginCheck());
        _queue.EndRun();
        Assert.True(_queue.TryBeginCheck());
    }

    [Theory]
    [InlineData(English, "en", 0.985)]
    [InlineData("noise\n{not json\n{\"language\":\"da\",\"probability\":0.5,\"speech_seconds\":3}", "da", 0.5)]
    public void Detect_answers_are_read_from_the_last_json_line(string output, string language, double probability)
    {
        var parsed = AudioCheckService.Parse(output)!.Value;
        Assert.Equal(language, parsed.Language);
        Assert.Equal(probability, parsed.Probability);
    }
}
