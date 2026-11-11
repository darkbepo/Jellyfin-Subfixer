using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class EligibilityTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private EpisodeFacts Episode(string? videoPath, params string?[] audio) =>
        new(new LibraryNode(Guid.NewGuid(), "E1", NodeKind.Episode, Guid.NewGuid(), "Show"), videoPath, audio);

    [Fact]
    public void Eligible_with_en_sidecar_and_english_audio()
    {
        var video = _dir.File("Show S01E01.mkv");
        var sub = _dir.File("Show S01E01.en.srt");

        var result = EligibilityService.Evaluate(Episode(video, "eng"), null);

        Assert.True(result.IsEligible);
        Assert.Equal(sub, result.Sidecar!.Path);
        Assert.Equal("en", result.Sidecar.LanguageTag);
        Assert.Equal(EpisodeStatus.None, result.Status);
    }

    [Fact]
    public void Eng_sidecar_is_accepted_with_its_own_tag()
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File("Show S01E01.eng.srt");

        var result = EligibilityService.Evaluate(Episode(video, "en"), null);

        Assert.True(result.IsEligible);
        Assert.Equal("eng", result.Sidecar!.LanguageTag);
    }

    [Fact]
    public void When_both_exist_only_en_is_processed()
    {
        var video = _dir.File("Show S01E01.mkv");
        var en = _dir.File("Show S01E01.en.srt");
        _dir.File("Show S01E01.eng.srt");

        var result = EligibilityService.Evaluate(Episode(video, "eng"), null);

        Assert.Equal(en, result.Sidecar!.Path);
    }

    [Theory]
    [InlineData("Show S01E01.srt")]
    [InlineData("Show S01E01.english.srt")]
    [InlineData("Show S01E01.en.hi.srt")]
    [InlineData("Show S01E01.en.forced.srt")]
    [InlineData("Show S01E01.en.ass")]
    [InlineData("Show S01E01.en.vtt")]
    [InlineData("Show S01E01.en.srt.bak")]
    [InlineData("Other S01E01.en.srt")]
    public void Other_names_are_not_english_sidecars(string subtitleName)
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File(subtitleName);

        var result = EligibilityService.Evaluate(Episode(video, "eng"), null);

        Assert.Equal(IneligibleReason.NoSidecar, result.Reason);
    }

    [Theory]
    [InlineData("eng")]
    [InlineData("en")]
    [InlineData("ENG")]
    [InlineData(" En ")]
    public void English_audio_tags(string tag)
    {
        Assert.True(EligibilityService.IsEnglish(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("und")]
    [InlineData("jpn")]
    [InlineData("english")]
    public void Not_english_audio_tags(string? tag)
    {
        Assert.False(EligibilityService.IsEnglish(tag));
    }

    [Fact]
    public void No_english_audio_when_no_stream_is_english()
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File("Show S01E01.en.srt");

        var result = EligibilityService.Evaluate(Episode(video, "jpn", null), null);

        Assert.Equal(IneligibleReason.NoEnglishAudio, result.Reason);
        Assert.Equal("no_english_audio", EligibilityService.ReasonCode(result.Reason));
    }

    [Fact]
    public void One_english_stream_among_others_is_enough()
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File("Show S01E01.en.srt");

        Assert.True(EligibilityService.Evaluate(Episode(video, "jpn", "eng"), null).IsEligible);
    }

    [Fact]
    public void Missing_video_file()
    {
        var result = EligibilityService.Evaluate(Episode(Path.Combine(_dir.Path, "gone.mkv"), "eng"), null);
        Assert.Equal(IneligibleReason.NoVideoFile, result.Reason);

        Assert.Equal(IneligibleReason.NoVideoFile, EligibilityService.Evaluate(Episode(null, "eng"), null).Reason);
    }

    [Fact]
    public void Done_record_with_unchanged_video_is_already_synced()
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File("Show S01E01.en.srt");
        var episode = Episode(video, "eng");

        var result = EligibilityService.Evaluate(episode, Records.For(episode.Node.Id, video, SyncStatus.Done));

        Assert.Equal(IneligibleReason.AlreadySynced, result.Reason);
        Assert.Equal(EpisodeStatus.Done, result.Status);
    }

    [Fact]
    public void Replaced_video_makes_a_done_episode_eligible_again()
    {
        var video = _dir.File("Show S01E01.mkv", "old");
        _dir.File("Show S01E01.en.srt");
        var episode = Episode(video, "eng");
        var record = Records.For(episode.Node.Id, video, SyncStatus.Done);

        File.WriteAllText(video, "a different, longer file");

        var result = EligibilityService.Evaluate(episode, record);
        Assert.True(result.IsEligible);
        Assert.Equal(EpisodeStatus.None, result.Status);
    }

    [Fact]
    public void Touched_video_with_same_size_is_eligible_again()
    {
        var video = _dir.File("Show S01E01.mkv", "same");
        _dir.File("Show S01E01.en.srt");
        var episode = Episode(video, "eng");
        var record = Records.For(episode.Node.Id, video, SyncStatus.Done);

        File.SetLastWriteTimeUtc(video, record.Video.MtimeUtc.AddMinutes(5));

        Assert.True(EligibilityService.Evaluate(episode, record).IsEligible);
    }

    [Theory]
    [InlineData(SyncStatus.Check)]
    [InlineData(SyncStatus.Failed)]
    public void Check_and_failed_stay_selectable_as_attempted(SyncStatus status)
    {
        var video = _dir.File("Show S01E01.mkv");
        _dir.File("Show S01E01.en.srt");
        var episode = Episode(video, "eng");

        var result = EligibilityService.Evaluate(episode, Records.For(episode.Node.Id, video, status, "too few lines"));

        Assert.True(result.IsEligible);
        Assert.Equal(EpisodeStatus.Attempted, result.Status);
        Assert.Equal("too few lines", result.Message);
    }

    [Fact]
    public void Fingerprint_matches_case_insensitively_on_path()
    {
        var a = new VideoFingerprint(@"D:\Shows\A.mkv", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = a with { Path = @"d:\shows\a.MKV" };
        Assert.True(a.Matches(b));
        Assert.False(a.Matches(a with { Size = 11 }));
        Assert.False(a.Matches(a with { MtimeUtc = a.MtimeUtc.AddTicks(1) }));
        Assert.False(a.Matches(null));
    }
}
