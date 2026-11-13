using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>
/// D50 (Danish subtitles, on English audio), D51 (Sync again) and the per-language state: eligibility, selection,
/// queue, the run with SubSync (language, reference, version), backups, the database and the page's subtitle list.
/// </summary>
[Collection(RunTimeEstimateTests.Collection)]
public sealed class DanishTests : IDisposable
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nHej.\r\n\r\n";

    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeWorker _worker = new();
    private readonly FakeRefresh _refresh = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly LibraryNode _season;

    public DanishTests()
    {
        var series = _library.AddSeries("Show");
        _season = _library.AddSeason(series, 1);
    }

    public void Dispose() => _dir.Dispose();

    private VideoFacts Episode(int n, bool en, bool da, string audio = "eng")
    {
        var video = _dir.File($"Show S01E{n:00}.mkv", "video bytes");
        if (en)
        {
            _dir.File($"Show S01E{n:00}.en.srt", Srt);
        }

        if (da)
        {
            _dir.File($"Show S01E{n:00}.da.srt", Srt);
        }

        return _library.AddEpisode(_season, n, video, audio);
    }

    private JobRunner Runner() => new(_library, _state, new BackupService(_state, TimeProvider.System, NullLogger<BackupService>.Instance), _worker, _refresh,
        () => new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), TimeProvider.System, NullLogger<JobRunner>.Instance);

    private JobItemSnapshot Job(VideoFacts e, string language, bool again = false) =>
        new(Guid.NewGuid(), e.Node.Id, "S01E01", "Show", _user, JobSource.Manual, null, JobItemStatus.Running, null, DateTime.UtcNow, DateTime.UtcNow, null, null,
            Language: language, Again: again);

    private SyncRecord Done(VideoFacts e, string language, string version = "1.5.0") =>
        Records.For(e.Node.Id, e.VideoPath!, SyncStatus.Done) with { Language = language, SubSyncVersion = version };

    // ---------- eligibility ----------
    [Theory]
    [InlineData("da")]
    [InlineData("dan")]
    [InlineData("da.hi")]
    [InlineData("dan.hi")]
    public void Danish_sidecars_are_found_by_their_exact_names(string tag)
    {
        var video = _dir.File("Film.mkv", "v");
        _dir.File("Film." + tag + ".srt", Srt);

        var found = EligibilityService.FindSidecar(video, SubtitleLanguage.Danish);

        Assert.NotNull(found);
        Assert.Equal(tag, found!.LanguageTag);
        Assert.Equal("da", found.Language);
        Assert.Null(EligibilityService.FindSidecar(video));  // no English one
    }

    [Fact]
    public void Danish_needs_english_audio_too()
    {
        var e = Episode(1, en: false, da: true, audio: "dan");
        Assert.Equal(IneligibleReason.NoEnglishAudio, EligibilityService.Evaluate(e, null, "da").Reason);
    }

    [Fact]
    public void Sync_again_lets_a_synced_subtitle_through()
    {
        var e = Episode(1, en: true, da: false);
        var done = Done(e, "en");
        Assert.Equal(IneligibleReason.AlreadySynced, EligibilityService.Evaluate(e, done).Reason);
        Assert.True(EligibilityService.Evaluate(e, done, "en", again: true).IsEligible);
    }

    // ---------- selection ----------
    [Fact]
    public async Task A_season_queues_every_subtitle_and_a_missing_danish_one_isnt_skipped()
    {
        var both = Episode(1, en: true, da: true);
        var onlyEn = Episode(2, en: true, da: false);
        var onlyDa = Episode(3, en: false, da: true);
        var synced = Episode(4, en: true, da: true);
        _state.Records[synced.Node.Id] = Done(synced, "en");
        _state.DanishRecords[synced.Node.Id] = Done(synced, "da");

        var result = await new SelectionValidator(_library, _state).ValidateAsync(_user, [_season.Id], 150, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(
            [(both.Node.Id, "en"), (both.Node.Id, "da"), (onlyEn.Node.Id, "en"), (onlyDa.Node.Id, "da")],
            result.Eligible.Select(c => (c.Episode.Node.Id, c.Language)).ToList());
        Assert.Equal(2, result.Skipped.AlreadySynced);  // both of episode 4's
        Assert.Equal(1, result.Skipped.NoSidecar);       // episode 3 has no English one; nobody is "missing" a Danish one
    }

    [Fact]
    public async Task Languages_limit_the_selection()
    {
        var both = Episode(1, en: true, da: true);

        var result = await new SelectionValidator(_library, _state).ValidateAsync(_user, [both.Node.Id], 150, CancellationToken.None, ["da"]);

        Assert.Equal("da", Assert.Single(result.Eligible).Language);
    }

    [Fact]
    public async Task Sync_again_is_only_for_one_episode()
    {
        var e = Episode(1, en: true, da: false);
        _state.Records[e.Node.Id] = Done(e, "en");
        var validator = new SelectionValidator(_library, _state);

        var one = await validator.ValidateAsync(_user, [e.Node.Id], 150, CancellationToken.None, ["en"], again: true);
        var season = await validator.ValidateAsync(_user, [_season.Id], 150, CancellationToken.None, ["en"], again: true);

        Assert.Equal("en", Assert.Single(one.Eligible).Language);
        Assert.Equal(ErrorCodes.UnsupportedItemType, season.Error!.Code);
    }

    [Fact]
    public async Task An_unknown_language_is_refused()
    {
        var e = Episode(1, en: true, da: false);
        var result = await new SelectionValidator(_library, _state).ValidateAsync(_user, [e.Node.Id], 150, CancellationToken.None, ["de"]);
        Assert.NotNull(result.Error);
    }

    // ---------- queue ----------
    [Fact]
    public void The_queue_holds_one_entry_per_episode_and_language()
    {
        using var queue = new JobQueue(TimeProvider.System);
        var id = Guid.NewGuid();

        var first = queue.Enqueue(_user, [new JobRequestItem(id, "E1", "Show"), new JobRequestItem(id, "E1", "Show", "da")], JobSource.Manual, null, 300);
        var again = queue.Enqueue(_user, [new JobRequestItem(id, "E1", "Show", "da")], JobSource.Manual, null, 300);

        Assert.Equal(2, first.Queued);
        Assert.Equal(0, again.Queued);
        Assert.Equal(["en", "da"], queue.Snapshot().Items.Select(i => i.Language).ToArray());
    }

    // ---------- the run ----------
    [Fact]
    public async Task A_danish_item_is_sent_with_its_language_and_the_english_subtitle_as_reference()
    {
        var e = Episode(1, en: true, da: true);
        var runner = Runner();

        var outcome = await runner.RunAsync(Job(e, "da"), CancellationToken.None);

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Equal("da", _worker.LastOptions!.SubtitleLanguage);
        Assert.EndsWith("Show S01E01.en.srt", _worker.LastOptions.Reference, StringComparison.Ordinal);
        Assert.True(_state.DanishRecords.ContainsKey(e.Node.Id));
        Assert.False(_state.Records.ContainsKey(e.Node.Id));  // the English record is its own
        Assert.Empty(_state.InFlight);
    }

    [Fact]
    public async Task A_danish_item_without_an_english_subtitle_has_no_reference()
    {
        var e = Episode(1, en: false, da: true);

        await Runner().RunAsync(Job(e, "da"), CancellationToken.None);

        Assert.Equal("da", _worker.LastOptions!.SubtitleLanguage);
        Assert.Null(_worker.LastOptions.Reference);
    }

    [Fact]
    public async Task Danish_needs_subsync_1_5_0_and_nothing_is_touched_before()
    {
        var e = Episode(1, en: true, da: true);
        _worker.Version = new Version(1, 4, 0);

        var outcome = await Runner().RunAsync(Job(e, "da"), CancellationToken.None);

        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Contains("1.5.0", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _worker.Calls);
        Assert.Empty(_state.DanishRecords);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "Show S01E01.da.srt.bak")));
    }

    [Fact]
    public async Task English_runs_without_a_language_or_reference_as_before()
    {
        var e = Episode(1, en: true, da: true);
        _worker.Version = new Version(1, 3, 1);

        var outcome = await Runner().RunAsync(Job(e, "en"), CancellationToken.None);

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Equal("en", _worker.LastOptions!.SubtitleLanguage);
        Assert.Null(_worker.LastOptions.Reference);
    }

    [Fact]
    public async Task A_failed_danish_run_restores_the_danish_subtitle()
    {
        var e = Episode(1, en: true, da: true);
        var danish = Path.Combine(_dir.Path, "Show S01E01.da.srt");
        _worker.Behavior = (_, sub, _) =>
        {
            File.WriteAllText(sub, "half");
            return Task.FromResult(FakeWorker.Result(WorkerStatus.Failed, "boom"));
        };

        var outcome = await Runner().RunAsync(Job(e, "da"), CancellationToken.None);

        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Equal(Srt, File.ReadAllText(danish));
        Assert.False(File.Exists(danish + ".bak"));
        Assert.Equal(SyncStatus.Failed, _state.DanishRecords[e.Node.Id].Status);
        Assert.Empty(_state.InFlight);
    }

    [Fact]
    public async Task Sync_again_runs_a_synced_subtitle()
    {
        var e = Episode(1, en: true, da: false);
        _state.Records[e.Node.Id] = Done(e, "en", "1.3.1");

        var skipped = await Runner().RunAsync(Job(e, "en"), CancellationToken.None);
        var again = await Runner().RunAsync(Job(e, "en", again: true), CancellationToken.None);

        Assert.Equal(JobItemStatus.Skipped, skipped.Status);
        Assert.Equal(JobItemStatus.Done, again.Status);
        Assert.Equal(1, _worker.Calls);
    }

    // ---------- SubSync calls ----------
    [Fact]
    public void The_command_line_gets_sub_lang_and_reference_for_danish_only()
    {
        var options = new WorkerOptions(true, false, TimeSpan.FromMinutes(1));
        var en = CliWorker.BuildArguments("v.mkv", "v.en.srt", "en", "r.json", options, inPlace: true);
        var da = CliWorker.BuildArguments("v.mkv", "v.da.srt", "da", "r.json", options with { SubtitleLanguage = "da", Reference = "v.en.srt" }, inPlace: true);

        Assert.DoesNotContain("--sub-lang", en);
        Assert.Equal(["--sub-lang", "da", "--reference", "v.en.srt"], da.Skip(da.Count - 4).ToArray());
    }

    [Fact]
    public async Task Serve_requests_for_danish_carry_subtitle_language_and_reference()
    {
        using var dir = new TempDir();
        var serve = new FakeServe([]);
        var session = (await ServeSession.StartAsync(serve, _ => true, TimeSpan.FromSeconds(5), NullLogger.Instance, CancellationToken.None)).Session!;
        var en = dir.File("E1.en.srt", Srt);
        var da = dir.File("E1.da.srt", Srt);

        await session.Send("en", dir.File("E1.mkv"), en, en, null).Result.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Send("da", dir.File("E1.mkv"), da, da, null, "da", en).Result.WaitAsync(TimeSpan.FromSeconds(5));

        using var first = JsonDocument.Parse(serve.RawLines[0]);
        using var second = JsonDocument.Parse(serve.RawLines[1]);
        Assert.False(first.RootElement.TryGetProperty("subtitle_language", out _));  // English as before
        Assert.Equal("da", second.RootElement.GetProperty("subtitle_language").GetString());
        Assert.Equal(en, second.RootElement.GetProperty("reference").GetString());
        session.Dispose();
    }

    // ---------- the database ----------
    [Fact]
    public async Task Records_and_journal_entries_are_kept_per_language()
    {
        using var dir = new TempDir();
        var store = new SqliteStateStore(Path.Combine(dir.Path, "state.db"), NullLogger<SqliteStateStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        var e = Episode(1, en: true, da: true);

        await store.UpsertAsync(Done(e, "en", "1.3.1"), CancellationToken.None);
        await store.UpsertAsync(Done(e, "da") with { Status = SyncStatus.Check }, CancellationToken.None);
        await store.AddInFlightAsync(new InFlightBackup(e.Node.Id, "a.en.srt", "a.en.srt.bak", DateTime.UtcNow), CancellationToken.None);
        await store.AddInFlightAsync(new InFlightBackup(e.Node.Id, "a.da.srt", "a.da.srt.bak", DateTime.UtcNow, "da"), CancellationToken.None);

        var en = await store.GetAsync([e.Node.Id], CancellationToken.None);
        var da = await store.GetAsync([e.Node.Id], "da", CancellationToken.None);
        Assert.Equal(SyncStatus.Done, en[e.Node.Id].Status);
        Assert.Equal("en", en[e.Node.Id].Language);
        Assert.Equal(SyncStatus.Check, da[e.Node.Id].Status);
        Assert.Equal(2, (await store.GetInFlightAsync(CancellationToken.None)).Count);

        await store.RemoveInFlightAsync(e.Node.Id, "da", CancellationToken.None);
        await store.DeleteAsync(e.Node.Id, "da", CancellationToken.None);
        Assert.Equal("a.en.srt", Assert.Single(await store.GetInFlightAsync(CancellationToken.None)).SubtitlePath);
        Assert.Empty(await store.GetAsync([e.Node.Id], "da", CancellationToken.None));
        Assert.Single(await store.GetAsync([e.Node.Id], CancellationToken.None));
    }

    // ---------- the page's subtitle list (D52) ----------
    [Fact]
    public async Task Episode_rows_list_both_languages_with_their_state()
    {
        var both = Episode(1, en: true, da: true);
        var onlyEn = Episode(2, en: true, da: false);
        _state.Records[both.Node.Id] = Done(both, "en", "1.3.1");

        var page = await new BrowseService(_library, _state).BrowseAsync(_user, _season.Id, 0, 50, CancellationToken.None);
        var row1 = page!.Items.Single(i => i.Id == both.Node.Id);
        var row2 = page.Items.Single(i => i.Id == onlyEn.Node.Id);

        Assert.Equal(["en", "da"], row1.Subtitles!.Select(s => s.Language).ToArray());
        var en1 = row1.Subtitles![0];
        Assert.True(en1.Present);
        Assert.Equal("done", en1.Status);
        Assert.True(en1.Outdated);  // synced with SubSync 1.3.1: lines could flash by
        Assert.Equal("1.3.1", en1.SyncedWith);
        Assert.True(row1.Subtitles[1].Eligible);
        Assert.True(row1.Eligible);       // the Danish one can still be synced
        Assert.Equal("none", row1.Status); // not every subtitle it has is synced
        Assert.False(row2.Subtitles![1].Present);
        Assert.Equal("no_sidecar", row2.Subtitles[1].Reason);
    }

    [Theory]
    [InlineData("1.3.1", true)]
    [InlineData("1.4.0", true)]
    [InlineData("1.4.1", false)]
    [InlineData("1.5.0", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Outdated_means_before_1_4_1(string? version, bool outdated) =>
        Assert.Equal(outdated, BrowseService.IsOutdated(version));
}
