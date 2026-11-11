using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class SelectionValidatorTests : IDisposable
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();

    public void Dispose() => _dir.Dispose();

    private SelectionValidator Validator() => new(_library, _state);

    private VideoFacts EligibleEpisode(LibraryNode season, int n)
    {
        var name = $"{season.SeriesName} S{season.IndexNumber:00}E{n:00}";
        var video = _dir.File(name + ".mkv");
        _dir.File(name + ".en.srt");
        return _library.AddEpisode(season, n, video, "eng");
    }

    private Task<SelectionResult> Validate(int cap, params Guid[] ids) => Validator().ValidateAsync(User, ids, cap, CancellationToken.None);

    [Fact]
    public async Task Season_expands_to_its_eligible_episodes()
    {
        var series = _library.AddSeries("Show");
        var season = _library.AddSeason(series, 1);
        EligibleEpisode(season, 1);
        EligibleEpisode(season, 2);
        _library.AddEpisode(season, 3, _dir.File("Show S01E03.mkv"), "eng"); // no sidecar

        var result = await Validate(150, season.Id);

        Assert.Null(result.Error);
        Assert.Equal(series.Id, result.SeriesId);
        Assert.Equal(3, result.Selected);
        Assert.Equal(2, result.Eligible.Count);
        Assert.Equal(1, result.Skipped.NoSidecar);
    }

    [Fact]
    public async Task Multiple_seasons_of_one_series_are_allowed_and_deduplicated()
    {
        var series = _library.AddSeries("Show");
        var s1 = _library.AddSeason(series, 1);
        var s2 = _library.AddSeason(series, 2);
        var e1 = EligibleEpisode(s1, 1);
        EligibleEpisode(s2, 1);

        var result = await Validate(150, s1.Id, s2.Id, e1.Node.Id);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Selected);
        Assert.Equal(2, result.Eligible.Count);
    }

    [Fact]
    public async Task Two_series_are_rejected()
    {
        var a = _library.AddSeason(_library.AddSeries("A"), 1);
        var b = _library.AddSeason(_library.AddSeries("B"), 1);
        EligibleEpisode(a, 1);
        EligibleEpisode(b, 1);

        var result = await Validate(150, a.Id, b.Id);

        Assert.Equal(ErrorCodes.MultipleSeries, result.Error?.Code);
    }

    [Fact]
    public async Task Episode_of_another_series_is_rejected()
    {
        var a = _library.AddSeason(_library.AddSeries("A"), 1);
        var b = _library.AddSeason(_library.AddSeries("B"), 1);
        EligibleEpisode(a, 1);
        var other = EligibleEpisode(b, 1);

        var result = await Validate(150, a.Id, other.Node.Id);

        Assert.Equal(ErrorCodes.MultipleSeries, result.Error?.Code);
    }

    [Fact]
    public async Task Series_ids_are_rejected()
    {
        var series = _library.AddSeries("Show");
        Assert.Equal(ErrorCodes.UnsupportedItemType, (await Validate(150, series.Id)).Error?.Code);
    }

    [Fact]
    public async Task Library_ids_are_rejected()
    {
        var library = _library.AddLibrary("TV");
        Assert.Equal(ErrorCodes.UnsupportedItemType, (await Validate(150, library.Id)).Error?.Code);
    }

    [Fact]
    public async Task Other_item_types_are_rejected()
    {
        var movie = _library.AddOther("A movie");
        Assert.Equal(ErrorCodes.UnsupportedItemType, (await Validate(150, movie.Id)).Error?.Code);
    }

    [Fact]
    public async Task Unknown_or_inaccessible_ids_are_rejected()
    {
        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        _library.Hidden.Add(season.Id);

        Assert.Equal(ErrorCodes.UnsupportedItemType, (await Validate(150, Guid.NewGuid())).Error?.Code);
        Assert.Equal(ErrorCodes.UnsupportedItemType, (await Validate(150, season.Id)).Error?.Code);
    }

    [Fact]
    public async Task Empty_selection_is_rejected()
    {
        Assert.Equal(ErrorCodes.NothingEligible, (await Validate(150)).Error?.Code);
    }

    [Fact]
    public async Task Cap_counts_eligible_episodes_only()
    {
        var series = _library.AddSeries("Show");
        var season = _library.AddSeason(series, 1);
        var synced = EligibleEpisode(season, 1);
        EligibleEpisode(season, 2);
        EligibleEpisode(season, 3);
        _library.AddEpisode(season, 4, _dir.File("Show S01E04.mkv"), "jpn");
        _state.Records[synced.Node.Id] = Records.For(synced.Node.Id, synced.VideoPath!, SyncStatus.Done);

        var fits = await Validate(2, season.Id);
        Assert.Null(fits.Error);
        Assert.Equal(4, fits.Selected);
        Assert.Equal(2, fits.Eligible.Count);
        Assert.Equal(1, fits.Skipped.AlreadySynced);
        Assert.Equal(1, fits.Skipped.NoEnglishAudio);

        var tooMany = await Validate(1, season.Id);
        Assert.Equal(ErrorCodes.CapExceeded, tooMany.Error?.Code);
    }

    [Fact]
    public async Task Previously_attempted_episodes_are_eligible_and_counted()
    {
        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        var e = EligibleEpisode(season, 1);
        _state.Records[e.Node.Id] = Records.For(e.Node.Id, e.VideoPath!, SyncStatus.Check);

        var result = await Validate(150, season.Id);

        Assert.Single(result.Eligible);
        Assert.Equal(1, result.AttemptedBefore);
    }

    [Fact]
    public async Task Nothing_eligible_still_previews_with_counts()
    {
        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        var e = EligibleEpisode(season, 1);
        _state.Records[e.Node.Id] = Records.For(e.Node.Id, e.VideoPath!, SyncStatus.Done);

        var result = await Validate(150, season.Id);

        Assert.Null(result.Error);
        Assert.Empty(result.Eligible);
        Assert.Equal(1, result.Skipped.AlreadySynced);
    }

    private VideoFacts EligibleMovie(string name, int year)
    {
        var video = _dir.File($"{name} ({year}).mkv");
        _dir.File($"{name} ({year}).en.srt");
        return _library.AddMovie(name, year, video, "eng");
    }

    [Fact]
    public async Task Movies_from_anywhere_are_accepted_without_a_series_lock()
    {
        var a = EligibleMovie("Alpha", 2001);
        var b = EligibleMovie("Beta", 2002);
        var noSub = _library.AddMovie("Gamma", 2003, _dir.File("Gamma (2003).mkv"), "eng");

        var result = await Validate(150, a.Node.Id, b.Node.Id, noSub.Node.Id);

        Assert.Null(result.Error);
        Assert.Equal(SelectionKind.Movies, result.Kind);
        Assert.Null(result.SeriesId);
        Assert.Equal(3, result.Selected);
        Assert.Equal(2, result.Eligible.Count);
        Assert.Equal(1, result.Skipped.NoSidecar);
    }

    [Fact]
    public async Task Movies_and_episodes_cannot_be_mixed()
    {
        var movie = EligibleMovie("Alpha", 2001);
        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        var episode = EligibleEpisode(season, 1);

        Assert.Equal(ErrorCodes.MixedSelection, (await Validate(150, movie.Node.Id, episode.Node.Id)).Error?.Code);
        Assert.Equal(ErrorCodes.MixedSelection, (await Validate(150, season.Id, movie.Node.Id)).Error?.Code);
    }

    [Fact]
    public async Task Movie_cap_uses_a_movie_message()
    {
        var a = EligibleMovie("Alpha", 2001);
        var b = EligibleMovie("Beta", 2002);

        var result = await Validate(1, a.Node.Id, b.Node.Id);

        Assert.Equal(ErrorCodes.CapExceeded, result.Error?.Code);
        Assert.Contains("movies", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Episode_selection_reports_its_kind_and_series()
    {
        var series = _library.AddSeries("Show");
        var season = _library.AddSeason(series, 1);
        EligibleEpisode(season, 1);

        var result = await Validate(150, season.Id);

        Assert.Equal(SelectionKind.Episodes, result.Kind);
        Assert.Equal(series.Id, result.SeriesId);
    }

    [Fact]
    public async Task Huge_requests_are_rejected()
    {
        var ids = Enumerable.Range(0, SelectionValidator.MaxIdsPerRequest + 1).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(ErrorCodes.CapExceeded, (await Validate(150, ids)).Error?.Code);
    }
}
