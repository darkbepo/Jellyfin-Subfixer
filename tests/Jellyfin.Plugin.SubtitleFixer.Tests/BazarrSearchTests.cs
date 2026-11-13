using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>In-memory Bazarr: movies, series, episodes and search results; counts calls.</summary>
internal sealed class FakeBazarr : IBazarrClient
{
    public List<BazarrMovie> Movies { get; } = [];

    public List<BazarrSeries> Series { get; } = [];

    public List<BazarrEpisode> Episodes { get; } = [];

    public Dictionary<int, IReadOnlyList<BazarrSearchResult>> MovieResults { get; } = [];

    public Dictionary<int, IReadOnlyList<BazarrSearchResult>> EpisodeResults { get; } = [];

    public BazarrFailure? FailWith { get; set; }

    public int MovieListCalls { get; private set; }

    public int SeriesListCalls { get; private set; }

    public int EpisodeListCalls { get; private set; }

    public List<int> MovieSearches { get; } = [];

    public List<int> EpisodeSearches { get; } = [];

    public Task<BazarrStatusResult> GetStatusAsync(BazarrConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(new BazarrStatusResult(true, "1.6.2", "ok"));

    public Task<bool?> GetUseSubsyncAsync(BazarrConnection connection, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);

    public Task<BazarrResult<BazarrPage<BazarrMovie>>> GetMoviesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken)
    {
        MovieListCalls++;
        return Task.FromResult(Page(Movies, start, length));
    }

    public Task<BazarrResult<BazarrPage<BazarrSeries>>> GetSeriesAsync(BazarrConnection connection, int start, int length, CancellationToken cancellationToken)
    {
        SeriesListCalls++;
        return Task.FromResult(Page(Series, start, length));
    }

    public Task<BazarrResult<IReadOnlyList<BazarrEpisode>>> GetEpisodesAsync(BazarrConnection connection, int sonarrSeriesId, CancellationToken cancellationToken)
    {
        EpisodeListCalls++;
        return Task.FromResult(FailWith is { } f
            ? BazarrResult<IReadOnlyList<BazarrEpisode>>.Fail(f, "fail")
            : BazarrResult<IReadOnlyList<BazarrEpisode>>.Success(Episodes.Where(e => e.SonarrSeriesId == sonarrSeriesId).ToList()));
    }

    public Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchMovieAsync(BazarrConnection connection, int radarrId, CancellationToken cancellationToken)
    {
        MovieSearches.Add(radarrId);
        return Task.FromResult(MovieResults.TryGetValue(radarrId, out var r)
            ? BazarrResult<IReadOnlyList<BazarrSearchResult>>.Success(r)
            : BazarrResult<IReadOnlyList<BazarrSearchResult>>.Fail(BazarrFailure.Failed, "Bazarr: Movie file not found. Path mapping issue?"));
    }

    public Task<BazarrResult<IReadOnlyList<BazarrSearchResult>>> SearchEpisodeAsync(BazarrConnection connection, int sonarrEpisodeId, CancellationToken cancellationToken)
    {
        EpisodeSearches.Add(sonarrEpisodeId);
        return Task.FromResult(EpisodeResults.TryGetValue(sonarrEpisodeId, out var r)
            ? BazarrResult<IReadOnlyList<BazarrSearchResult>>.Success(r)
            : BazarrResult<IReadOnlyList<BazarrSearchResult>>.Fail(BazarrFailure.NotFound, "nf"));
    }

    /// <summary>What "Bazarr" does when asked to download: write files, fail, throw. Default: accept and do nothing.</summary>
    public Func<BazarrTarget, BazarrSearchResult, BazarrResult<bool>>? OnDownload { get; set; }

    public List<(BazarrTarget Target, BazarrSearchResult Result)> Downloads { get; } = [];

    public Task<BazarrResult<bool>> DownloadAsync(BazarrConnection connection, BazarrTarget target, BazarrSearchResult result, CancellationToken cancellationToken)
    {
        Downloads.Add((target, result));
        return Task.FromResult(OnDownload?.Invoke(target, result) ?? BazarrResult<bool>.Success(true));
    }

    public static BazarrSearchResult Result(string provider, int score, string language = "en", bool hi = false, bool forced = false) =>
        new(provider, "opaque-" + provider + "-" + score, language, hi, forced, false, score, ["Release." + provider], "uploader", ["source"], ["release_group"]);

    private BazarrResult<BazarrPage<T>> Page<T>(List<T> all, int start, int length) =>
        FailWith is { } f
            ? BazarrResult<BazarrPage<T>>.Fail(f, "Could not reach Bazarr at http://bazarr:6767/.")
            : BazarrResult<BazarrPage<T>>.Success(new BazarrPage<T>(all.Skip(start).Take(length).ToList(), all.Count));
}

internal sealed class FakeLookups : IBazarrLookupSource
{
    public Dictionary<Guid, (string? Imdb, int? Tvdb, string? SeriesPath)> Ids { get; } = [];

    public BazarrLookup? GetLookup(Guid userId, VideoFacts video)
    {
        if (string.IsNullOrWhiteSpace(video.VideoPath))
        {
            return null;
        }

        Ids.TryGetValue(video.Node.Id, out var ids);
        return video.Node.Kind == NodeKind.Movie
            ? new BazarrLookup(true, video.VideoPath, ids.Imdb, null, null, null, null)
            : new BazarrLookup(false, video.VideoPath, ids.Imdb, ids.Tvdb, ids.SeriesPath, video.Node.ParentIndexNumber, video.Node.IndexNumber);
    }
}

public sealed class BazarrItemResolverTests
{
    private static readonly BazarrConnection Conn = Make("http://bazarr:6767");

    [Fact]
    public async Task Movie_matches_by_mapped_path_ignoring_case_and_separators()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(7, "/movies/Other (2000)/o.mkv", "tt1", "Other"));
        bazarr.Movies.Add(new BazarrMovie(9, "/movies/22 Jump Street (2014)/22.mkv", "tt2294449", "22 Jump Street"));
        var mapper = BazarrPathMapper.FromSetting(@"D:\Film => /movies");

        var r = await Resolver(bazarr).ResolveAsync(Conn, mapper, new BazarrLookup(true, @"d:\film\22 Jump Street (2014)\22.MKV", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(9, r.Value!.RadarrId);
        Assert.Equal("path", r.Value.MatchedBy);
        Assert.Equal("/movies/22 Jump Street (2014)/22.mkv", r.Value.Path);
    }

    [Fact]
    public async Task Movie_falls_back_to_imdb_when_paths_differ()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(9, "/elsewhere/22.mkv", "tt2294449", "22 Jump Street"));

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, @"C:\notporn\22.mkv", "TT2294449", null, null, null, null), CancellationToken.None);

        Assert.Equal(9, r.Value!.RadarrId);
        Assert.Equal("ids", r.Value.MatchedBy);
    }

    [Fact]
    public async Task Ambiguous_imdb_is_no_match()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(1, "/a.mkv", "tt1", "A"));
        bazarr.Movies.Add(new BazarrMovie(2, "/b.mkv", "tt1", "A again"));

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, @"C:\x.mkv", "tt1", null, null, null, null), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Unknown_movie_is_no_match_and_rereads_a_cached_list_once()
    {
        var bazarr = new FakeBazarr();
        var resolver = Resolver(bazarr);
        var lookup = new BazarrLookup(true, @"C:\x.mkv", null, null, null, null, null);

        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None); // fresh read: 1 call
        var r = await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None); // cache miss: re-read once

        Assert.Null(r.Value);
        Assert.Equal(2, bazarr.MovieListCalls);
    }

    [Fact]
    public async Task Movie_added_after_the_list_was_cached_is_still_found()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(1, "/a.mkv", null, "A"));
        var resolver = Resolver(bazarr);
        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, "/a.mkv", null, null, null, null, null), CancellationToken.None);

        bazarr.Movies.Add(new BazarrMovie(2, "/b.mkv", null, "B"));
        var r = await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, "/b.mkv", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(2, r.Value!.RadarrId);
    }

    [Fact]
    public async Task Cached_list_is_reused_until_it_expires()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(1, "/a.mkv", null, "A"));
        var time = new ManualTime();
        var resolver = Resolver(bazarr, time);
        var lookup = new BazarrLookup(true, "/a.mkv", null, null, null, null, null);

        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None);
        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None);
        Assert.Equal(1, bazarr.MovieListCalls);

        time.Advance(BazarrItemResolver.CacheLifetime + TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None);
        Assert.Equal(2, bazarr.MovieListCalls);
    }

    [Fact]
    public async Task Long_lists_are_read_in_pages()
    {
        var bazarr = new FakeBazarr();
        for (var i = 1; i <= (BazarrItemResolver.PageSize * 2) + 3; i++)
        {
            bazarr.Movies.Add(new BazarrMovie(i, $"/m/{i}.mkv", null, null));
        }

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, "/m/503.mkv", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(503, r.Value!.RadarrId);
        Assert.Equal(3, bazarr.MovieListCalls);
    }

    [Fact]
    public async Task Episode_matches_by_path_within_the_series_found_by_path()
    {
        var bazarr = new FakeBazarr();
        bazarr.Series.Add(new BazarrSeries(5, "/tv/Schitt's Creek", 287247, "tt3526078", "Schitt's Creek"));
        bazarr.Episodes.Add(new BazarrEpisode(51, 5, 1, 1, "/tv/Schitt's Creek/Season 01/S01E01.mkv"));
        bazarr.Episodes.Add(new BazarrEpisode(52, 5, 1, 2, "/tv/Schitt's Creek/Season 01/S01E02.mkv"));
        var mapper = BazarrPathMapper.FromSetting(@"C:\notporn2 => /tv");

        var r = await Resolver(bazarr).ResolveAsync(Conn, mapper, new BazarrLookup(false, @"C:\notporn2\Schitt's Creek\Season 01\S01E02.mkv", null, null, @"C:\notporn2\Schitt's Creek", 1, 2), CancellationToken.None);

        Assert.Equal(52, r.Value!.SonarrEpisodeId);
        Assert.Equal(5, r.Value.SonarrSeriesId);
        Assert.Equal("path", r.Value.MatchedBy);
    }

    [Fact]
    public async Task Episode_falls_back_to_tvdb_and_season_episode_numbers()
    {
        var bazarr = new FakeBazarr();
        bazarr.Series.Add(new BazarrSeries(5, "/somewhere/else", 287247, null, "Schitt's Creek"));
        bazarr.Episodes.Add(new BazarrEpisode(51, 5, 1, 1, "/somewhere/else/a.mkv"));
        bazarr.Episodes.Add(new BazarrEpisode(52, 5, 1, 2, "/somewhere/else/b.mkv"));

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(false, @"C:\tv\S\x.mkv", null, 287247, @"C:\tv\S", 1, 2), CancellationToken.None);

        Assert.Equal(52, r.Value!.SonarrEpisodeId);
        Assert.Equal("ids", r.Value.MatchedBy);
    }

    [Fact]
    public async Task Episode_with_no_matching_series_is_no_match_without_reading_episodes()
    {
        var bazarr = new FakeBazarr();
        bazarr.Series.Add(new BazarrSeries(5, "/tv/Other", 1, null, "Other"));

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(false, @"C:\tv\S\x.mkv", null, 287247, @"C:\tv\S", 1, 2), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Null(r.Value);
        Assert.Equal(0, bazarr.EpisodeListCalls);
    }

    [Fact]
    public async Task Bazarr_failure_is_passed_on()
    {
        var bazarr = new FakeBazarr { FailWith = BazarrFailure.Unavailable };

        var r = await Resolver(bazarr).ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), new BazarrLookup(true, "/a.mkv", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(BazarrFailure.Unavailable, r.Failure);
    }

    [Fact]
    public async Task Changing_the_bazarr_address_drops_the_cache()
    {
        var bazarr = new FakeBazarr();
        bazarr.Movies.Add(new BazarrMovie(1, "/a.mkv", null, "A"));
        var resolver = Resolver(bazarr);
        var lookup = new BazarrLookup(true, "/a.mkv", null, null, null, null, null);

        await resolver.ResolveAsync(Conn, BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None);
        await resolver.ResolveAsync(Make("http://other:6767"), BazarrPathMapper.FromSetting(null), lookup, CancellationToken.None);

        Assert.Equal(2, bazarr.MovieListCalls);
    }

    internal static BazarrConnection Make(string url)
    {
        Assert.True(BazarrConnection.TryCreate(url, "key", out var c, out _));
        return c!;
    }

    private static BazarrItemResolver Resolver(FakeBazarr bazarr, TimeProvider? time = null) =>
        new(bazarr, NullLogger<BazarrItemResolver>.Instance, time);
}

public sealed class BazarrSearchServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeLookups _lookups = new();
    private readonly FakeBazarr _bazarr = new();
    private readonly ManualTime _time = new();
    private readonly Guid _user = Guid.NewGuid();
    private PluginConfiguration _config = new() { BazarrEnabled = true, BazarrUrl = "http://bazarr:6767", BazarrApiKey = "secret-key-123" };

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Movie_without_subtitle_gets_non_forced_results_in_every_language_best_first_with_our_own_ids()
    {
        var movie = Movie("22 Jump Street.mkv");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, "22 Jump Street"));
        _bazarr.MovieResults[1] =
        [
            FakeBazarr.Result("podnapisi", 80),
            FakeBazarr.Result("yifysubtitles", 91, hi: true),
            FakeBazarr.Result("embeddedsubtitles", 95, language: "da"),
            FakeBazarr.Result("opensubtitles", 99, forced: true),
        ];

        var (result, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("movie", result!.Kind);
        Assert.Equal(movie.VideoPath, result.Resource);
        // D48: every language Bazarr returns, forced ones aside; only English is marked for re-timing.
        Assert.Equal(["embeddedsubtitles", "yifysubtitles", "podnapisi"], result.Results.Select(r => r.Provider));
        Assert.Equal("DA", result.Results[0].Language);
        Assert.Equal("da", result.Results[0].LanguageCode);
        Assert.False(result.Results[0].English);
        Assert.Equal("EN:HI", result.Results[1].Language);
        Assert.True(result.Results[1].HearingImpaired);
        Assert.True(result.Results[1].English);
        Assert.Equal("EN", result.Results[2].Language);
        Assert.Equal("en", result.Results[2].LanguageCode);
        Assert.Equal(1, result.Hidden);

        var json = JsonSerializer.Serialize(result, Api.ApiJson.Options);
        Assert.DoesNotContain("opaque-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-123", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_ids_resolve_only_for_the_same_user_and_item_until_they_expire()
    {
        var movie = Movie("m.mkv");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, null));
        _bazarr.MovieResults[1] = [FakeBazarr.Result("podnapisi", 80)];
        var store = new BazarrResultStore(_time);

        var (result, _) = await Service(store).SearchAsync(_user, movie.Node.Id, CancellationToken.None);
        var id = result!.Results[0].Id;

        Assert.Equal("opaque-podnapisi-80", store.Get(id, _user, movie.Node.Id)!.Result.Subtitle);
        Assert.Null(store.Get(id, Guid.NewGuid(), movie.Node.Id));
        Assert.Null(store.Get(id, _user, Guid.NewGuid()));
        _time.Advance(BazarrResultStore.Lifetime);
        Assert.Null(store.Get(id, _user, movie.Node.Id));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("eng")]
    [InlineData("en.hi")]
    [InlineData("eng.hi")]
    public async Task Item_with_an_english_subtitle_is_searched_for_other_languages_only(string tag)
    {
        // D50: an English subtitle no longer blocks the search; it only takes English out of the results.
        var movie = Movie("m.mkv");
        _dir.File("m." + tag + ".srt");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, null));
        _bazarr.MovieResults[1] = [FakeBazarr.Result("podnapisi", 90), FakeBazarr.Result("podnapisi", 80, language: "da")];

        var (result, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.False(result!.EnglishWanted);
        Assert.Equal(["da"], result.Results.Select(r => r.LanguageCode));
        Assert.Contains("EN", result.Have!);
        Assert.Equal(1, result.Hidden);
    }

    [Fact]
    public async Task Languages_already_there_are_left_out_in_both_spellings()
    {
        var movie = Movie("m.mkv");
        _dir.File("m.dan.srt");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, null));
        _bazarr.MovieResults[1] = [FakeBazarr.Result("podnapisi", 90), FakeBazarr.Result("podnapisi", 80, language: "da"), FakeBazarr.Result("podnapisi", 70, language: "sv")];

        var (result, _) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.True(result!.EnglishWanted);
        Assert.Equal(["en", "sv"], result.Results.Select(r => r.LanguageCode));
        Assert.Equal(["DA"], result.Have!);
    }

    [Fact]
    public async Task Other_sidecar_names_do_not_count_as_a_subtitle()
    {
        var movie = Movie("m.mkv");
        _dir.File("m.en.forced.srt");
        _dir.File("m.da.srt");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, null));
        _bazarr.MovieResults[1] = [];

        var (result, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.Empty(result!.Results);
    }

    [Fact]
    public async Task Inaccessible_item_is_not_found_and_bazarr_is_never_asked()
    {
        var movie = Movie("m.mkv");
        _library.Hidden.Add(movie.Node.Id);

        var (_, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Equal(404, error!.Status);
        Assert.Equal("not_found", error.Code);
        Assert.Equal(0, _bazarr.MovieListCalls);
    }

    [Fact]
    public async Task Item_without_english_audio_gets_only_other_languages()
    {
        // An English subtitle couldn't be re-timed to French audio, so English isn't offered; Danish still is.
        var movie = _library.AddMovie("M", 2000, _dir.File("m.mkv"), "fre");
        _bazarr.Movies.Add(new BazarrMovie(1, movie.VideoPath, null, null));
        _bazarr.MovieResults[1] = [FakeBazarr.Result("podnapisi", 90), FakeBazarr.Result("podnapisi", 80, language: "da")];

        var (result, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.False(result!.EnglishWanted);
        Assert.Equal(["da"], result.Results.Select(r => r.LanguageCode));
    }

    [Fact]
    public async Task Disabled_or_unconfigured_bazarr_is_unavailable()
    {
        var movie = Movie("m.mkv");
        _config = new PluginConfiguration { BazarrEnabled = false, BazarrUrl = "http://bazarr:6767", BazarrApiKey = "k" };
        Assert.False(Service().Enabled);
        Assert.Equal("bazarr_unavailable", (await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None)).Error!.Code);

        _config = new PluginConfiguration { BazarrEnabled = true, BazarrUrl = string.Empty, BazarrApiKey = "k" };
        Assert.False(Service().Enabled);
        Assert.Equal(503, (await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None)).Error!.Status);
    }

    [Fact]
    public async Task Unknown_to_bazarr_is_404_bazarr_not_found()
    {
        var movie = Movie("m.mkv");

        var (_, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Equal(404, error!.Status);
        Assert.Equal("bazarr_not_found", error.Code);
        Assert.Contains("Radarr", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bazarr_search_error_is_502_with_bazarrs_message()
    {
        var movie = Movie("m.mkv");
        _bazarr.Movies.Add(new BazarrMovie(3, movie.VideoPath, null, null)); // no results registered: Bazarr answers 500

        var (_, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Equal(502, error!.Status);
        Assert.Equal("search_failed", error.Code);
        Assert.Contains("Path mapping issue", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_bazarr_is_503_and_the_message_has_no_key()
    {
        var movie = Movie("m.mkv");
        _bazarr.FailWith = BazarrFailure.Unavailable;

        var (_, error) = await Service().SearchAsync(_user, movie.Node.Id, CancellationToken.None);

        Assert.Equal(503, error!.Status);
        Assert.DoesNotContain("secret-key-123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_search_within_five_seconds_is_rate_limited_per_user()
    {
        var a = Movie("a.mkv");
        _bazarr.Movies.Add(new BazarrMovie(1, a.VideoPath, null, null));
        _bazarr.MovieResults[1] = [];
        var service = Service();

        Assert.Null((await service.SearchAsync(_user, a.Node.Id, CancellationToken.None)).Error);
        var limited = (await service.SearchAsync(_user, a.Node.Id, CancellationToken.None)).Error;
        Assert.Equal(429, limited!.Status);
        Assert.Equal("rate_limited", limited.Code);
        Assert.InRange(limited.RetryAfterSeconds!.Value, 1, 5);

        Assert.Null((await service.SearchAsync(Guid.NewGuid(), a.Node.Id, CancellationToken.None)).Error);

        _time.Advance(BazarrRateLimiter.MinInterval);
        Assert.Null((await service.SearchAsync(_user, a.Node.Id, CancellationToken.None)).Error);
        Assert.Equal(3, _bazarr.MovieSearches.Count);
    }

    [Fact]
    public async Task Refused_items_do_not_use_up_the_rate_limit()
    {
        // Only a missing video refuses a search now (D50).
        var noVideo = _library.AddMovie("Gone", 2000, Path.Combine(_dir.Path, "gone.mkv"), "eng");
        var without = Movie("b.mkv");
        _bazarr.Movies.Add(new BazarrMovie(2, without.VideoPath, null, null));
        _bazarr.MovieResults[2] = [];
        var service = Service();

        Assert.Equal("no_video_file", (await service.SearchAsync(_user, noVideo.Node.Id, CancellationToken.None)).Error!.Code);
        Assert.Null((await service.SearchAsync(_user, without.Node.Id, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Episode_search_uses_the_sonarr_episode_id()
    {
        var series = _library.AddSeries("Show");
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(season, 3, _dir.File("S01E03.mkv"), "eng");
        _lookups.Ids[episode.Node.Id] = (null, 42, _dir.Path);
        _bazarr.Series.Add(new BazarrSeries(5, _dir.Path, 42, null, "Show"));
        _bazarr.Episodes.Add(new BazarrEpisode(77, 5, 1, 3, episode.VideoPath));
        _bazarr.EpisodeResults[77] = [FakeBazarr.Result("podnapisi", 70)];

        var (result, error) = await Service().SearchAsync(_user, episode.Node.Id, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("episode", result!.Kind);
        Assert.Equal([77], _bazarr.EpisodeSearches);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("EN", true)]
    [InlineData("eng", true)]
    [InlineData("en-GB", true)]
    [InlineData("en:hi", true)]
    [InlineData("da", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void English_language_codes(string? code, bool expected) => Assert.Equal(expected, BazarrSearchService.IsEnglish(code));

    private VideoFacts Movie(string file) => _library.AddMovie(Path.GetFileNameWithoutExtension(file), 2014, _dir.File(file), "eng");

    private BazarrSearchService Service(BazarrResultStore? store = null) => new(
        _library,
        _lookups,
        new BazarrItemResolver(_bazarr, NullLogger<BazarrItemResolver>.Instance, _time),
        _bazarr,
        store ?? new BazarrResultStore(_time),
        _limiter ??= new BazarrRateLimiter(_time),
        NullLogger<BazarrSearchService>.Instance,
        () => _config);

    private BazarrRateLimiter? _limiter;
}

public sealed class BazarrClientSearchParsingTests
{
    [Fact]
    public async Task Movie_search_reads_the_live_1_6_2_shape()
    {
        // Field names and types as returned live by Bazarr 1.6.2 (BAZARR-API.md).
        const string body = """
        {"data":[{"dont_matches":["release_group","streaming_service"],"forced":"False","hearing_impaired":"True","language":"en",
          "matches":["source","title","year"],"original_format":"False","orig_score":165,"provider":"yifysubtitles",
          "release_info":["22.Jump.Street.2014.1080p.BluRay.H264.AAC-RARBG"],"score":91,"score_without_hash":165,
          "subtitle":"05270e7f-d2e6-43e4-bf77-e60a786764c2","uploader":"FUNKSW","url":"https://example/sub"},
          {"provider":"broken"}]}
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        var r = await client.SearchMovieAsync(BazarrItemResolverTests.Make("http://host:6767"), 1, CancellationToken.None);

        var row = Assert.Single(r.Value!);
        Assert.Equal("yifysubtitles", row.Provider);
        Assert.Equal("05270e7f-d2e6-43e4-bf77-e60a786764c2", row.Subtitle);
        Assert.True(row.HearingImpaired);
        Assert.False(row.Forced);
        Assert.Equal(91, row.Score);
        Assert.Equal(["22.Jump.Street.2014.1080p.BluRay.H264.AAC-RARBG"], row.Releases);
        Assert.Equal("http://host:6767/api/providers/movies?radarrid=1", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Movie_list_reads_the_live_1_6_2_shape_and_pages()
    {
        const string body = """{"data":[{"radarrId":1,"path":"C:\\notporn\\22 Jump Street (2014)\\22.mp4","imdbId":"tt2294449","title":"22 Jump Street","subtitles":[]}],"total":1}""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        var r = await client.GetMoviesAsync(BazarrItemResolverTests.Make("http://host:6767"), 0, 250, CancellationToken.None);

        Assert.Equal(1, r.Value!.Total);
        Assert.Equal(new BazarrMovie(1, @"C:\notporn\22 Jump Street (2014)\22.mp4", "tt2294449", "22 Jump Street"), Assert.Single(r.Value.Items));
        Assert.Equal("http://host:6767/api/movies?start=0&length=250", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Episodes_are_asked_for_one_series()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"sonarrEpisodeId":9,"sonarrSeriesId":5,"season":1,"episode":2,"path":"/tv/a.mkv"}]}""", Encoding.UTF8, "application/json") });
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        var r = await client.GetEpisodesAsync(BazarrItemResolverTests.Make("http://host:6767"), 5, CancellationToken.None);

        Assert.Equal(new BazarrEpisode(9, 5, 1, 2, "/tv/a.mkv"), Assert.Single(r.Value!));
        Assert.Equal("http://host:6767/api/episodes?seriesid%5B%5D=5", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "\"Movie not found\"", BazarrFailure.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, "\"Movie file not found. Path mapping issue?\"", BazarrFailure.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", BazarrFailure.Unauthorized)]
    [InlineData(HttpStatusCode.OK, "<html>", BazarrFailure.Unavailable)]
    public async Task Search_failures_are_classified(HttpStatusCode status, string body, BazarrFailure expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var client = new BazarrClient(new StubFactory(handler), NullLogger<BazarrClient>.Instance);

        var r = await client.SearchMovieAsync(BazarrItemResolverTests.Make("http://host:6767"), 1, CancellationToken.None);

        Assert.Equal(expected, r.Failure);
        if (expected == BazarrFailure.Failed)
        {
            Assert.Equal("Bazarr: Movie file not found. Path mapping issue?", r.Message);
        }
    }
}
