using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>D47: the letter picker on library pages, poster tags, and the per-season no-subtitle count.</summary>
public sealed class BrowseCardsTests : IDisposable
{
    private readonly Guid _user = Guid.NewGuid();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly TempDir _dir = new();
    private readonly LibraryNode _tv;

    public BrowseCardsTests()
    {
        _tv = _library.AddLibrary("Shows");
        foreach (var name in new[] { "The Acolyte", "Abbott Elementary", "Bluey", "1883", "30 Rock", "Adolescence" })
        {
            _library.PutIn(_tv, _library.AddSeries(name).Id);
        }
    }

    public void Dispose() => _dir.Dispose();

    private BrowseService Service() => new(_library, _state);

    private async Task<IReadOnlyList<string>> Names(string? startsWith)
    {
        var page = await Service().BrowseAsync(_user, _tv.Id, 0, 50, CancellationToken.None, startsWith);
        return page!.Items.Select(i => i.Name).ToList();
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("Z", "Z")]
    [InlineData("#", "#")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_letter_or_hash_is_accepted(string? raw, string? expected)
    {
        Assert.True(LetterFilter.TryParse(raw, out var letter));
        Assert.Equal(expected, letter);
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("1")]
    [InlineData("Æ")]
    [InlineData("%")]
    public void Anything_else_is_refused(string raw) => Assert.False(LetterFilter.TryParse(raw, out _));

    [Fact]
    public async Task A_letter_keeps_the_names_that_sort_under_it()
    {
        // "The Acolyte" sorts as "Acolyte", as in Jellyfin, so it is under A and not under T.
        Assert.Equal(new[] { "Abbott Elementary", "The Acolyte", "Adolescence" }, await Names("A"));
        Assert.Equal("A", _library.LastStartsWith);
        Assert.Empty(await Names("T"));
    }

    [Fact]
    public async Task Hash_keeps_names_that_start_with_a_digit()
    {
        Assert.Equal(new[] { "1883", "30 Rock" }, await Names(LetterFilter.Other));
    }

    [Fact]
    public async Task No_letter_lists_everything_and_the_total_follows_the_filter()
    {
        Assert.Equal(6, (await Names(null)).Count);
        var page = await Service().BrowseAsync(_user, _tv.Id, 0, 1, CancellationToken.None, "A");
        Assert.Single(page!.Items);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task The_letter_only_applies_to_a_library_page()
    {
        var series = _library.AddSeries("Bluey 2");
        _library.PutIn(_tv, series.Id);
        _library.AddSeason(series, 1);
        _library.AddSeason(series, 2);

        var page = await Service().BrowseAsync(_user, series.Id, 0, 50, CancellationToken.None, "Q");

        Assert.Equal(2, page!.Items.Count);
    }

    [Fact]
    public async Task Posters_come_with_the_items_and_the_breadcrumb()
    {
        var series = _library.AddSeries("Poster Show");
        _library.PutIn(_tv, series.Id);
        var season = _library.AddSeason(series, 1);
        _library.SetImage(series.Id, "tag-series");

        var library = await Service().BrowseAsync(_user, _tv.Id, 0, 50, CancellationToken.None, "P");
        Assert.Equal("tag-series", Assert.Single(library!.Items).ImageTag);

        var seasonPage = await Service().BrowseAsync(_user, season.Id, 0, 50, CancellationToken.None);
        var crumb = seasonPage!.Parent!.Breadcrumb.Single(c => c.Type == "Series");
        Assert.Equal("tag-series", crumb.ImageTag);
    }

    [Fact]
    public async Task Untagged_audio_is_still_not_eligible_but_says_it_is_untagged()
    {
        var series = _library.AddSeries("Tags");
        var season = _library.AddSeason(series, 1);
        _dir.File("U1.en.srt");
        _dir.File("U2.en.srt");
        _dir.File("U3.en.srt");
        _library.AddEpisode(season, 1, _dir.File("U1.mkv"), "und");
        _library.AddEpisode(season, 2, _dir.File("U2.mkv"), (string?)null);
        _library.AddEpisode(season, 3, _dir.File("U3.mkv"), "ger");

        var rows = (await Service().BrowseAsync(_user, season.Id, 0, 50, CancellationToken.None))!.Items;

        Assert.All(rows, r => Assert.Equal("no_english_audio", r.Reason));
        Assert.Equal(new bool?[] { true, true, false }, rows.Select(r => r.AudioUntagged));
    }

    [Fact]
    public async Task A_hearing_impaired_subtitle_on_disk_is_flagged_also_when_synced()
    {
        var series = _library.AddSeries("HI");
        var season = _library.AddSeason(series, 1);
        _dir.File("H1.en.hi.srt");
        _dir.File("H2.en.srt");
        var synced = _library.AddEpisode(season, 1, _dir.File("H1.mkv"), "eng");
        _library.AddEpisode(season, 2, _dir.File("H2.mkv"), "eng");
        _state.Records[synced.Node.Id] = Records.For(synced.Node.Id, synced.VideoPath!, State.SyncStatus.Done);

        var rows = (await Service().BrowseAsync(_user, season.Id, 0, 50, CancellationToken.None))!.Items;

        Assert.Equal("already_synced", rows[0].Reason);
        Assert.True(rows[0].SubtitleHi);
        Assert.False(rows[1].SubtitleHi);
    }

    [Fact]
    public async Task A_season_counts_its_episodes_without_an_English_subtitle()
    {
        var series = _library.AddSeries("Counted");
        _library.PutIn(_tv, series.Id);
        var season = _library.AddSeason(series, 1);
        var withSub = _dir.File("E1.mkv");
        _dir.File("E1.en.srt");
        _library.AddEpisode(season, 1, withSub, "eng");
        _library.AddEpisode(season, 2, _dir.File("E2.mkv"), "eng");
        _library.AddEpisode(season, 3, _dir.File("E3.mkv"), "eng");

        var page = await Service().BrowseAsync(_user, series.Id, 0, 50, CancellationToken.None);
        var row = Assert.Single(page!.Items);

        Assert.Equal(1, row.EligibleCount);
        Assert.Equal(2, row.NoSubtitleCount);
    }
}
