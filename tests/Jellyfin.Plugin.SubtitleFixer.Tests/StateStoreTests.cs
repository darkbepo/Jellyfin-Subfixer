using Jellyfin.Plugin.SubtitleFixer.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SqliteStateStore _store;

    public StateStoreTests()
    {
        _store = new SqliteStateStore(Path.Combine(_dir.Path, "data", "state.db"), NullLogger<SqliteStateStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static SyncRecord Record(Guid id, SyncStatus status = SyncStatus.Done) => new()
    {
        ItemId = id,
        Video = new VideoFingerprint(@"D:\Shows\Show\Season 1\Show S01E01 é.mkv", 123456789012, new DateTime(2026, 3, 4, 5, 6, 7, 891, DateTimeKind.Utc).AddTicks(2345)),
        SubtitlePath = @"D:\Shows\Show\Season 1\Show S01E01 é.en.srt",
        Status = status,
        Message = status == SyncStatus.Done ? null : "too few lines",
        AudioLanguage = "eng",
        SubSyncVersion = "1.2.0",
        Seconds = 83.5,
        UpdatedUtc = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Roundtrip_keeps_every_field_exactly()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var record = Record(Guid.NewGuid(), SyncStatus.Check);

        await _store.UpsertAsync(record, CancellationToken.None);
        var read = (await _store.GetAsync([record.ItemId], CancellationToken.None))[record.ItemId];

        Assert.Equal(record, read);
        Assert.True(record.Video.Matches(read.Video));
    }

    [Fact]
    public async Task Upsert_replaces_and_delete_removes()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await _store.UpsertAsync(Record(id, SyncStatus.Failed), CancellationToken.None);
        await _store.UpsertAsync(Record(id, SyncStatus.Done), CancellationToken.None);

        Assert.Equal(SyncStatus.Done, (await _store.GetAsync([id], CancellationToken.None))[id].Status);

        await _store.DeleteAsync(id, CancellationToken.None);
        Assert.Empty(await _store.GetAsync([id], CancellationToken.None));
    }

    [Fact]
    public async Task Batched_lookup_handles_more_ids_than_one_query()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var ids = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids.Where((_, i) => i % 3 == 0))
        {
            await _store.UpsertAsync(Record(id), CancellationToken.None);
        }

        var found = await _store.GetAsync(ids, CancellationToken.None);

        Assert.Equal(400, found.Count);
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_data_survives_reopen()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await _store.UpsertAsync(Record(id), CancellationToken.None);
        await _store.SetMetaAsync("k", "v1", CancellationToken.None);
        await _store.SetMetaAsync("k", "v2", CancellationToken.None);

        using var reopened = new SqliteStateStore(_store.Location, NullLogger<SqliteStateStore>.Instance);
        await reopened.InitializeAsync(CancellationToken.None);
        await reopened.InitializeAsync(CancellationToken.None);

        Assert.Single(await reopened.GetAsync([id], CancellationToken.None));
        Assert.Equal("v2", await reopened.GetMetaAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Newer_schema_is_refused()
    {
        await _store.InitializeAsync(CancellationToken.None);
        await using (var connection = new SqliteConnection("Data Source=" + _store.Location))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_version SET version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        using var newer = new SqliteStateStore(_store.Location, NullLogger<SqliteStateStore>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newer.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task In_flight_journal_roundtrips()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var entry = new InFlightBackup(Guid.NewGuid(), @"D:\Shows\A é.en.srt", @"D:\Shows\A é.en.srt.bak", new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc));

        await _store.AddInFlightAsync(entry, CancellationToken.None);
        Assert.Equal(entry, Assert.Single(await _store.GetInFlightAsync(CancellationToken.None)));

        await _store.RemoveInFlightAsync(entry.ItemId, CancellationToken.None);
        Assert.Empty(await _store.GetInFlightAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Version_1_database_is_migrated_to_the_current_version_keeping_rows()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await _store.UpsertAsync(Record(id), CancellationToken.None);
        await using (var connection = new SqliteConnection("Data Source=" + _store.Location))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE in_flight; DROP TABLE failed_syncs; DROP TABLE audio_checks; UPDATE schema_version SET version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        using var upgraded = new SqliteStateStore(_store.Location, NullLogger<SqliteStateStore>.Instance);
        await upgraded.InitializeAsync(CancellationToken.None);

        Assert.Single(await upgraded.GetAsync([id], CancellationToken.None));
        Assert.Empty(await upgraded.GetInFlightAsync(CancellationToken.None));
        Assert.Equal(0, (await upgraded.GetFailedSyncsAsync(null, 0, 10, CancellationToken.None)).TotalCount);
        Assert.Empty(await upgraded.GetAudioChecksAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Audio_checks_round_trip_and_are_replaced_per_item()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var mtime = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var video = new VideoFingerprint(@"C:\Film ü\Film.mp4", 1234, mtime);
        await _store.UpsertAudioCheckAsync(new AudioCheck(id, video, "da", 0.81, 60, mtime), CancellationToken.None);
        await _store.UpsertAudioCheckAsync(new AudioCheck(id, video, "en", 0.985, 90, mtime.AddMinutes(1)), CancellationToken.None);
        await _store.UpsertAudioCheckAsync(new AudioCheck(Guid.NewGuid(), video, null, null, 0, mtime), CancellationToken.None);

        var all = await _store.GetAudioChecksAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
        var check = all.Single(c => c.ItemId == id);
        Assert.Equal("en", check.Language);
        Assert.Equal(0.985, check.Probability);
        Assert.Equal(90, check.SpeechSeconds);
        Assert.True(check.Video.Matches(video));
        Assert.Equal(mtime.AddMinutes(1), check.CheckedUtc);
        Assert.Contains(all, c => c.Language is null && c.Probability is null);
    }

    [Fact]
    public async Task Empty_lookup_returns_empty()
    {
        await _store.InitializeAsync(CancellationToken.None);
        Assert.Empty(await _store.GetAsync([], CancellationToken.None));
        Assert.Null(await _store.GetMetaAsync("missing", CancellationToken.None));
    }
}
