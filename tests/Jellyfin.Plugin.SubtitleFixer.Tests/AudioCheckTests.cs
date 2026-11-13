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

    // ---------- the service: a check is a queue item ----------
    private JobItemSnapshot Queued(VideoFacts movie) =>
        new(Guid.NewGuid(), movie.Node.Id, movie.Node.Name, string.Empty, _user, JobSource.AudioCheck, AudioCheckService.QueueNote, JobItemStatus.Running, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

    [Fact]
    public async Task Check_audio_goes_into_the_queue_as_a_check_item()
    {
        var movie = Movie("Other Mommy", "und");

        var (result, error) = await Service().EnqueueAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.True(result!.Queued);
        var item = Assert.Single(_queue.Snapshot().Items);
        Assert.Equal(JobSource.AudioCheck, item.Source);
        Assert.Equal("Other Mommy (2026)", item.Name);
        Assert.Equal("audio_check", JobQueue.SourceCode(item.Source));
        Assert.Empty(_runs);

        var again = (await Service().EnqueueAsync(_user, movie.Node.Id, CancellationToken.None)).Result!;
        Assert.False(again.Queued);
    }

    [Fact]
    public async Task A_queued_check_runs_detect_stores_what_it_heard_and_finishes_done()
    {
        var movie = Movie("Other Mommy", "und");
        _config.ForceCpu = true;
        var stages = new List<string>();
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, English, TimeSpan.FromSeconds(53)));

        var outcome = await Service().RunAsync(Queued(movie), CancellationToken.None, (stage, _) => stages.Add(stage));

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Contains("Sounds like English (99% sure)", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "detect", movie.VideoPath!, "--progress", "--cpu" }, Assert.Single(_runs));
        Assert.Equal("en", _state.AudioChecks[movie.Node.Id].Language);
        Assert.True(EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).IsEligible);
        Assert.Contains(SubSyncStage.ReadingAudio, stages);
    }

    [Fact]
    public async Task Another_language_is_stored_and_described()
    {
        var movie = Movie("Dansk", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, """{"language":"da","probability":0.91,"speech_seconds":88}""", TimeSpan.FromSeconds(50)));

        var outcome = await Service().RunAsync(Queued(movie), CancellationToken.None);

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Contains("Danish", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("DA", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(Facade.GetVideo(_user, movie.Node.Id)!, null).Reason);
    }

    [Fact]
    public async Task Tagged_audio_and_an_old_subsync_are_refused_before_anything_is_queued()
    {
        var english = Movie("English", "eng");
        var german = Movie("German", "ger");
        var untagged = Movie("Untagged", (string?)null);

        Assert.Equal("audio_tagged", (await Service().EnqueueAsync(_user, english.Node.Id, CancellationToken.None)).Error!.Code);
        Assert.Equal("audio_tagged", (await Service().EnqueueAsync(_user, german.Node.Id, CancellationToken.None)).Error!.Code);
        _version = new Version(1, 3, 1);
        var old = (await Service().EnqueueAsync(_user, untagged.Node.Id, CancellationToken.None)).Error!;
        Assert.Equal("subsync_too_old", old.Code);
        Assert.Contains("1.4.0", old.Message, StringComparison.Ordinal);
        Assert.Equal("not_found", (await Service().EnqueueAsync(_user, Guid.NewGuid(), CancellationToken.None)).Error!.Code);
        Assert.Empty(_queue.Snapshot().Items);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task An_item_tagged_while_it_waited_is_skipped_without_running_subsync()
    {
        var movie = Movie("Retagged", "und");
        var waiting = Queued(movie);
        var tagged = movie with { AudioLanguages = ["eng"] };

        // Same item, now tagged English: GetVideo returns the new facts.
        var service = new AudioCheckService(new StubFacade(tagged), _state, _cache, _queue, () => _dir.Path, (_, _) => Task.FromResult(_version), (f, a, d, t, l, c) => { _runs.Add(a); return _answer(c); }, () => _config, TimeProvider.System, NullLogger<AudioCheckService>.Instance);
        var outcome = await service.RunAsync(waiting, CancellationToken.None);

        Assert.Equal(JobItemStatus.Skipped, outcome.Status);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task An_unreadable_video_fails_with_subsyncs_reason_and_stores_nothing()
    {
        var movie = Movie("Broken", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 3, """{"language":null,"reason":"can't read the audio: invalid data"}""", TimeSpan.FromSeconds(2)));

        var outcome = await Service().RunAsync(Queued(movie), CancellationToken.None);

        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Contains("invalid data", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_state.AudioChecks);
    }

    [Fact]
    public async Task Exit_7_stops_the_queue_like_a_sync_and_a_cancel_is_cancelled()
    {
        var movie = Movie("Busy", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 7, string.Empty, TimeSpan.FromSeconds(1)));
        Assert.Equal(WorkerStatus.Unavailable, (await Service().RunAsync(Queued(movie), CancellationToken.None)).Worker);

        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Cancelled, null, string.Empty, TimeSpan.FromSeconds(1)));
        Assert.Equal(JobItemStatus.Cancelled, (await Service().RunAsync(Queued(movie), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task No_speech_is_stored_as_unknown()
    {
        var movie = Movie("Silent", "und");
        _answer = _ => Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, 0, """{"language":null,"probability":null,"speech_seconds":1.2,"reason":"no speech found"}""", TimeSpan.FromSeconds(20)));

        var outcome = await Service().RunAsync(Queued(movie), CancellationToken.None);

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Contains("No speech", outcome.Message, StringComparison.Ordinal);
        Assert.Null(_state.AudioChecks[movie.Node.Id].Language);
    }

    // ---------- one SubSync at a time (hard rule 2) ----------
    [Fact]
    public void Nothing_is_sent_ahead_past_a_check_into_a_running_serve()
    {
        _queue.Enqueue(_user, [new JobRequestItem(Guid.NewGuid(), "Check", string.Empty)], JobSource.AudioCheck, AudioCheckService.QueueNote, 300);
        _queue.Enqueue(_user, [new JobRequestItem(Guid.NewGuid(), "E2", "Show")], JobSource.Manual, null, 300);

        Assert.Null(_queue.TryTake(syncOnly: true));  // the check is next: serve must close first
        Assert.Equal("Check", _queue.TryTake()!.Snapshot.Name);
        Assert.Equal("E2", _queue.TryTake(syncOnly: true)!.Snapshot.Name);
    }

    private sealed class StubFacade : ILibraryFacade
    {
        private readonly VideoFacts _video;

        public StubFacade(VideoFacts video) => _video = video;

        public IReadOnlyList<LibraryNode> GetLibraries(Guid userId) => [];

        public Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit, string? startsWith = null) => null;

        public LibraryNode? GetNode(Guid userId, Guid itemId) => itemId == _video.Node.Id ? _video.Node : null;

        public IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId) => [];

        public IReadOnlyList<VideoFacts> GetSeasonEpisodes(Guid userId, Guid seasonId) => [];

        public VideoFacts? GetVideo(Guid userId, Guid episodeId) => episodeId == _video.Node.Id ? _video : null;

        public Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit, IReadOnlyCollection<Guid>? libraryIds = null) => new([], 0);
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
