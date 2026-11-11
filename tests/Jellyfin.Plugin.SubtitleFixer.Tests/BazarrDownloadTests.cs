using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>
/// Phase 2 download, with fault injection: "Bazarr" (the fake client) writes, overwrites, half-writes or writes
/// nothing, and every case must leave the folder's existing subtitles exactly as they were (hard rule 3).
/// </summary>
public sealed class BazarrDownloadTests : IDisposable
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nThere.\r\n";

    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeBazarr _bazarr = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeWorker _worker = new();
    private readonly BazarrResultStore _store = new();
    private readonly JobQueue _queue = new(TimeProvider.System);
    private readonly Guid _user = Guid.NewGuid();
    private readonly PluginConfiguration _config = new() { BazarrEnabled = true, BazarrUrl = "http://bazarr:6767", BazarrApiKey = "secret-key-123", MaxQueuedEpisodes = 300 };
    private readonly VideoFacts _movie;
    private readonly BazarrSearchResult _hit = FakeBazarr.Result("podnapisi", 88);
    private readonly string _resultId;

    public BazarrDownloadTests()
    {
        _movie = _library.AddMovie("Film", 2014, _dir.File("Film.mkv"), "eng");
        _resultId = _store.Add(_user, _movie.Node.Id, new BazarrTarget(true, 7, null, null, _movie.VideoPath, "path"), _hit);
        _state.Records[_movie.Node.Id] = Records.For(_movie.Node.Id, _movie.VideoPath!, SyncStatus.Done);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _dir.Dispose();
    }

    [Theory]
    [InlineData(false, "Film.en.srt")]
    [InlineData(true, "Film.en.hi.srt")]
    public async Task Good_download_clears_the_done_record_and_queues_a_bazarr_job(bool hi, string file)
    {
        var hit = FakeBazarr.Result("yifysubtitles", 91, hi: hi);
        var id = _store.Add(_user, _movie.Node.Id, new BazarrTarget(true, 7, null, null, _movie.VideoPath, "path"), hit);
        _bazarr.OnDownload = (_, _) => Write(file, Srt);

        var (result, error) = await Service().DownloadAsync(_user, _movie.Node.Id, id, CancellationToken.None);

        Assert.Null(error);
        Assert.True(result!.Queued);
        Assert.Equal(file, result.Subtitle);
        Assert.False(_state.Records.ContainsKey(_movie.Node.Id));
        var job = Assert.Single(_queue.Snapshot().Items);
        Assert.Equal(JobSource.Bazarr, job.Source);
        Assert.Equal("downloaded from yifysubtitles", job.Note);
        Assert.Equal("Film (2014)", job.Name);
        Assert.Equal(_user, job.UserId);
        var sent = Assert.Single(_bazarr.Downloads);
        Assert.Equal(7, sent.Target.RadarrId);
        Assert.Equal(hit.Subtitle, sent.Result.Subtitle);
    }

    [Fact]
    public async Task Nothing_saved_is_download_failed_and_keeps_the_record_and_queue_as_they_were()
    {
        var other = _dir.File("Film.da.srt", "danish");

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal(502, error!.Status);
        Assert.Equal("download_failed", error.Code);
        Assert.Contains("System > Logs", error.Message, StringComparison.Ordinal);
        Assert.True(_state.Records.ContainsKey(_movie.Node.Id));
        Assert.Empty(_queue.Snapshot().Items);
        Assert.Equal("danish", File.ReadAllText(other));
    }

    [Fact]
    public async Task Existing_subtitle_overwritten_by_a_failed_download_is_restored()
    {
        // Bazarr's "single language" mode saves <video>.srt and overwrites what's there.
        var plain = _dir.File("Film.srt", "the owner's own subtitle");
        var mtime = File.GetLastWriteTimeUtc(plain);
        _bazarr.OnDownload = (_, _) => Write("Film.srt", Srt);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("download_failed", error!.Code);
        Assert.Equal("the owner's own subtitle", File.ReadAllText(plain));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(plain));
    }

    [Fact]
    public async Task Existing_subtitle_overwritten_during_a_good_download_is_restored_too()
    {
        var danish = _dir.File("Film.da.srt", "danish");
        _bazarr.OnDownload = (_, _) =>
        {
            File.WriteAllText(danish, "clobbered");
            return Write("Film.en.srt", Srt);
        };

        var (result, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Null(error);
        Assert.True(result!.Queued);
        Assert.Equal("danish", File.ReadAllText(danish));
        Assert.Equal(Srt, File.ReadAllText(Path.Combine(_dir.Path, "Film.en.srt")));
    }

    [Fact]
    public async Task Invalid_downloaded_sidecar_is_removed_and_the_download_fails()
    {
        _bazarr.OnDownload = (_, _) => Write("Film.en.srt", string.Empty);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("download_failed", error!.Code);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "Film.en.srt")));
        Assert.Empty(_queue.Snapshot().Items);
    }

    [Fact]
    public async Task Subtitle_saved_under_a_name_the_fixer_ignores_is_named_in_the_error_and_left_alone()
    {
        _bazarr.OnDownload = (_, _) => Write("Film.en.cc.srt", Srt);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("download_failed", error!.Code);
        Assert.Contains("Film.en.cc.srt", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "Film.en.cc.srt")));
    }

    [Theory]
    [InlineData(BazarrFailure.Failed, 502, "download_failed")]
    [InlineData(BazarrFailure.NotFound, 404, "bazarr_not_found")]
    [InlineData(BazarrFailure.Unavailable, 503, "bazarr_unavailable")]
    [InlineData(BazarrFailure.Unauthorized, 503, "bazarr_unavailable")]
    public async Task Bazarr_refusing_the_download_is_reported_and_changes_nothing(BazarrFailure failure, int status, string code)
    {
        var plain = _dir.File("Film.srt", "keep");
        _bazarr.OnDownload = (_, _) =>
        {
            File.WriteAllText(plain, "changed before failing");
            return BazarrResult<bool>.Fail(failure, "Bazarr: Subtitle not found in cache. Please search again.");
        };

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal(status, error!.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal("keep", File.ReadAllText(plain));
        Assert.True(_state.Records.ContainsKey(_movie.Node.Id));
    }

    [Fact]
    public async Task Cancelled_request_still_restores_and_removes_a_half_written_sidecar()
    {
        var plain = _dir.File("Film.srt", "keep");
        using var cts = new CancellationTokenSource();
        _bazarr.OnDownload = (_, _) =>
        {
            File.WriteAllText(plain, "clobbered");
            File.WriteAllText(Path.Combine(_dir.Path, "Film.en.srt"), "1\r\n00:00:01,000 -->");
            cts.CancelAfter(TimeSpan.FromMilliseconds(30));
            return BazarrResult<bool>.Success(true);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().DownloadAsync(_user, _movie.Node.Id, _resultId, cts.Token));

        Assert.Equal("keep", File.ReadAllText(plain));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "Film.en.srt")));
        Assert.True(_state.Records.ContainsKey(_movie.Node.Id));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("eng.hi")]
    public async Task Item_that_has_a_subtitle_is_refused_before_bazarr_is_asked(string tag)
    {
        _dir.File("Film." + tag + ".srt", Srt);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal(409, error!.Status);
        Assert.Equal("has_subtitle", error.Code);
        Assert.Empty(_bazarr.Downloads);
    }

    [Fact]
    public async Task Expired_foreign_or_unknown_handles_are_410()
    {
        Assert.Equal(410, (await Service().DownloadAsync(_user, _movie.Node.Id, "nope", CancellationToken.None)).Error!.Status);
        Assert.Equal("result_expired", (await Service().DownloadAsync(Guid.NewGuid(), _movie.Node.Id, _resultId, CancellationToken.None)).Error!.Code);
        Assert.Empty(_bazarr.Downloads);
    }

    [Fact]
    public async Task Inaccessible_item_is_not_found()
    {
        _library.Hidden.Add(_movie.Node.Id);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("not_found", error!.Code);
        Assert.Empty(_bazarr.Downloads);
    }

    [Fact]
    public async Task Unconfigured_subsync_or_disabled_bazarr_refuses_before_downloading()
    {
        _worker.IsConfigured = false;
        Assert.Equal("subsync_unavailable", (await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None)).Error!.Code);

        _worker.IsConfigured = true;
        _config.BazarrEnabled = false;
        Assert.Equal("bazarr_unavailable", (await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None)).Error!.Code);
        Assert.Empty(_bazarr.Downloads);
    }

    [Fact]
    public async Task Full_queue_refuses_before_downloading()
    {
        _config.MaxQueuedEpisodes = 1;
        _queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "Other", string.Empty)], JobSource.Manual, null, 300);

        var (_, error) = await Service().DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("queue_full", error!.Code);
        Assert.Empty(_bazarr.Downloads);
    }

    [Fact]
    public async Task Second_download_for_the_same_item_while_one_runs_is_refused()
    {
        var service = Service();
        var first = service.DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None); // waits: nothing is written
        await Task.Delay(20);

        // Same user and handle (a handle only works for whoever searched); the item lock answers first.
        var (_, error) = await service.DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);

        Assert.Equal("download_in_progress", error!.Code);
        Assert.Equal("download_failed", (await first).Error!.Code);
        Assert.Single(_bazarr.Downloads);
    }

    [Fact]
    public async Task A_user_runs_one_download_at_a_time()
    {
        var other = _library.AddMovie("Other", 2001, _dir.File("Other.mkv"), "eng");
        var otherId = _store.Add(_user, other.Node.Id, new BazarrTarget(true, 8, null, null, other.VideoPath, "path"), _hit);
        var service = Service();
        var first = service.DownloadAsync(_user, _movie.Node.Id, _resultId, CancellationToken.None);
        await Task.Delay(20);

        var (_, error) = await service.DownloadAsync(_user, other.Node.Id, otherId, CancellationToken.None);

        Assert.Equal(429, error!.Status);
        Assert.Equal("rate_limited", error.Code);
        await first;
    }

    [Fact]
    public async Task Episode_download_sends_series_and_episode_ids_and_names_the_job_like_the_queue()
    {
        var series = _library.AddSeries("Show");
        var season = _library.AddSeason(series, 2);
        var episode = _library.AddEpisode(season, 5, _dir.File("Show S02E05.mkv"), "eng");
        var id = _store.Add(_user, episode.Node.Id, new BazarrTarget(false, null, 5, 77, episode.VideoPath, "path"), _hit);
        _bazarr.OnDownload = (_, _) => Write("Show S02E05.en.srt", Srt);

        var (result, error) = await Service().DownloadAsync(_user, episode.Node.Id, id, CancellationToken.None);

        Assert.Null(error);
        Assert.True(result!.Queued);
        Assert.Equal((5, 77), (_bazarr.Downloads[0].Target.SonarrSeriesId, _bazarr.Downloads[0].Target.SonarrEpisodeId));
        Assert.StartsWith("S02E05", Assert.Single(_queue.Snapshot().Items).Name, StringComparison.Ordinal);
    }

    private BazarrResult<bool> Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_dir.Path, name), content);
        return BazarrResult<bool>.Success(true);
    }

    private BazarrDownloadService Service() => new(
        _library,
        _store,
        _bazarr,
        _state,
        _queue,
        _worker,
        NullLogger<BazarrDownloadService>.Instance,
        () => _config,
        new BazarrDownloadTiming(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(15)));
}

public sealed class BazarrClientDownloadTests
{
    [Fact]
    public async Task Movie_download_posts_query_parameters_with_original_format_false()
    {
        var handler = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);
        var hit = new BazarrSearchResult("yifysubtitles", "05270e7f-d2e6", "en", true, false, true, 91, [], null, [], []);

        var r = await client.DownloadAsync(BazarrItemResolverTests.Make("http://host:6767"), new BazarrTarget(true, 1, null, null, null, "path"), hit, CancellationToken.None);

        Assert.True(r.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(System.Net.Http.HttpMethod.Post, request.Method);
        Assert.Equal("http://host:6767/api/providers/movies?radarrid=1&hi=True&forced=False&original_format=False&provider=yifysubtitles&subtitle=05270e7f-d2e6", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Episode_download_sends_series_and_episode_ids()
    {
        var handler = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        await client.DownloadAsync(BazarrItemResolverTests.Make("http://host:6767"), new BazarrTarget(false, null, 5, 77, null, "path"), FakeBazarr.Result("podnapisi", 80), CancellationToken.None);

        Assert.StartsWith("http://host:6767/api/providers/episodes?seriesid=5&episodeid=77&hi=False", Assert.Single(handler.Requests).RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bazarr_error_message_is_passed_on()
    {
        var handler = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new System.Net.Http.StringContent("\"Unable to save subtitles file\""),
        });
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        var r = await client.DownloadAsync(BazarrItemResolverTests.Make("http://host:6767"), new BazarrTarget(true, 1, null, null, null, "path"), FakeBazarr.Result("p", 1), CancellationToken.None);

        Assert.Equal(BazarrFailure.Failed, r.Failure);
        Assert.Equal("Bazarr: Unable to save subtitles file", r.Message);
    }
}
