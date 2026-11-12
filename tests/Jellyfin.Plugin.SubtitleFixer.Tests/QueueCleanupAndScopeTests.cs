using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>Finished entries leave the queue list after a minute (D44).</summary>
public sealed class QueueRetentionTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly JobQueue _queue;

    public QueueRetentionTests()
    {
        _queue = new JobQueue(_time);
    }

    public void Dispose() => _queue.Dispose();

    private static JobRequestItem Item(string name) => new(Guid.NewGuid(), name, "Show");

    [Fact]
    public async Task Finished_entries_drop_out_a_minute_after_they_end_and_pending_ones_stay()
    {
        _queue.Enqueue(Guid.NewGuid(), [Item("E1"), Item("E2"), Item("E3")], JobSource.Manual, null, 300);
        var first = await _queue.TakeAsync(CancellationToken.None);
        _queue.Complete(first.Snapshot.JobId, JobItemStatus.Failed, "why", 5);
        var second = await _queue.TakeAsync(CancellationToken.None);

        _time.Advance(JobQueue.FinishedRetention - TimeSpan.FromSeconds(1));
        Assert.Equal(3, _queue.Snapshot().Items.Count);

        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = _queue.Snapshot();
        Assert.Equal(2, snapshot.Items.Count);
        Assert.DoesNotContain(snapshot.Items, i => i.JobId == first.Snapshot.JobId);
        Assert.Equal(second.Snapshot.JobId, snapshot.Current?.JobId);

        // A running entry never expires, however long it runs.
        _time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(second.Snapshot.JobId, _queue.Snapshot().Current?.JobId);
    }

    [Fact]
    public void Cancelled_entries_expire_too_and_the_stop_reason_stays_until_the_next_start()
    {
        _queue.Enqueue(Guid.NewGuid(), [Item("E1")], JobSource.Manual, null, 300);
        _queue.Stop("SubSync is busy");

        _time.Advance(JobQueue.FinishedRetention + TimeSpan.FromSeconds(1));
        var snapshot = _queue.Snapshot();

        Assert.Empty(snapshot.Items);
        Assert.Equal(QueueState.Idle, snapshot.State);
        Assert.Equal("SubSync is busy", snapshot.StopReason);
    }
}

/// <summary>The "Libraries" setting (D43), checked on the server for every lookup.</summary>
public sealed class LibraryScopeTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeLibrary _inner = new();
    private readonly LibraryNode _tv;
    private readonly LibraryNode _kids;
    private readonly LibraryNode _movies;
    private readonly LibraryNode _allowedSeason;
    private readonly LibraryNode _blockedSeason;
    private readonly VideoFacts _allowedEpisode;
    private readonly VideoFacts _blockedEpisode;
    private readonly VideoFacts _movie;
    private string[]? _setting;

    public LibraryScopeTests()
    {
        _tv = _inner.AddLibrary("TV");
        _kids = _inner.AddLibrary("Kids");
        _movies = _inner.AddLibrary("Movies");

        var show = _inner.AddSeries("Grown-up Show");
        _inner.PutIn(_tv, show.Id);
        _allowedSeason = _inner.AddSeason(show, 1);
        _allowedEpisode = _inner.AddEpisode(_allowedSeason, 1, null, "eng");

        var cartoon = _inner.AddSeries("Cartoon Show");
        _inner.PutIn(_kids, cartoon.Id);
        _blockedSeason = _inner.AddSeason(cartoon, 1);
        _blockedEpisode = _inner.AddEpisode(_blockedSeason, 1, null, "eng");

        _movie = _inner.AddMovie("A Show Movie", 2001, null, "eng");
        _inner.PutIn(_movies, _movie.Node.Id);
    }

    private LibraryScopeFacade Scope() => new(_inner, () => _setting);

    [Fact]
    public void No_libraries_chosen_means_every_library()
    {
        _setting = [];
        var scope = Scope();

        Assert.Equal(3, scope.GetLibraries(User).Count);
        Assert.NotNull(scope.GetVideo(User, _blockedEpisode.Node.Id));
        Assert.Single(scope.GetSeasonEpisodes(User, _blockedSeason.Id));
        scope.Search(User, "show", 0, 30);
        Assert.Null(_inner.LastSearchLibraries);
    }

    [Fact]
    public void Items_outside_the_chosen_libraries_are_not_found()
    {
        _setting = [_tv.Id.ToString("D"), _movies.Id.ToString("N")];
        var scope = Scope();

        Assert.Equal(new[] { _tv.Id, _movies.Id }.Order(), scope.GetLibraries(User).Select(l => l.Id).Order());
        Assert.DoesNotContain(scope.GetLibraries(User), l => l.Id == _kids.Id);

        Assert.NotNull(scope.GetNode(User, _allowedEpisode.Node.Id));
        Assert.NotNull(scope.GetVideo(User, _allowedEpisode.Node.Id));
        Assert.NotNull(scope.GetVideo(User, _movie.Node.Id));
        Assert.Single(scope.GetSeasonEpisodes(User, _allowedSeason.Id));

        Assert.Null(scope.GetNode(User, _blockedEpisode.Node.Id));
        Assert.Null(scope.GetNode(User, _kids.Id));
        Assert.Null(scope.GetVideo(User, _blockedEpisode.Node.Id));
        Assert.Empty(scope.GetSeasonEpisodes(User, _blockedSeason.Id));
        Assert.Empty(scope.GetBreadcrumb(User, _blockedSeason.Id));
        Assert.Null(scope.GetChildren(User, _kids.Id, 0, 50));
    }

    [Fact]
    public void Search_only_looks_in_the_chosen_libraries()
    {
        _setting = [_tv.Id.ToString()];

        var page = Scope().Search(User, "show", 0, 30);

        Assert.Equal([_tv.Id], _inner.LastSearchLibraries);
        Assert.Equal([_allowedEpisode.Node.SeriesId!.Value], page.Items.Select(i => i.Id));
    }

    [Fact]
    public void An_item_without_a_known_library_is_out_of_scope_when_libraries_are_chosen()
    {
        var orphan = _inner.AddMovie("Loose Movie", 1999, null, "eng");
        _setting = [_movies.Id.ToString()];

        Assert.Null(Scope().GetVideo(User, orphan.Node.Id));
    }

    [Fact]
    public void A_setting_with_only_unreadable_ids_allows_nothing()
    {
        _setting = ["not-a-guid", "  "];
        var scope = Scope();

        Assert.Empty(scope.GetLibraries(User));
        Assert.Null(scope.GetVideo(User, _allowedEpisode.Node.Id));
        Assert.Empty(scope.Search(User, "show", 0, 30).Items);
    }

    [Fact]
    public async Task Selecting_an_episode_from_another_library_is_refused()
    {
        _setting = [_tv.Id.ToString()];
        var validator = new SelectionValidator(Scope(), new FakeStateStore());

        var result = await validator.ValidateAsync(User, [_blockedEpisode.Node.Id], 150, CancellationToken.None);

        Assert.NotNull(result.Error);
    }
}

/// <summary>The failed-syncs log in state.db (D44).</summary>
public sealed class FailedSyncLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SqliteStateStore _store;

    public FailedSyncLogTests()
    {
        _store = new SqliteStateStore(Path.Combine(_dir.Path, "state.db"), NullLogger<SqliteStateStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static FailedSync Entry(Guid user, string name, SyncStatus status = SyncStatus.Failed) =>
        new(0, Guid.NewGuid(), name, "Show é", user, status, "SubSync could not read the audio.", "bazarr", "downloaded from x", new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Entries_come_back_newest_first_per_user_and_the_log_keeps_only_the_newest()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _store.AddFailedSyncAsync(Entry(alice, "A1"), 3, CancellationToken.None);
        await _store.AddFailedSyncAsync(Entry(bob, "B1", SyncStatus.Check), 3, CancellationToken.None);
        await _store.AddFailedSyncAsync(Entry(alice, "A2"), 3, CancellationToken.None);
        await _store.AddFailedSyncAsync(Entry(alice, "A3"), 3, CancellationToken.None);

        var all = await _store.GetFailedSyncsAsync(null, 0, 10, CancellationToken.None);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(["A3", "A2", "B1"], all.Items.Select(i => i.Name));

        var bobs = await _store.GetFailedSyncsAsync(bob, 0, 10, CancellationToken.None);
        var b1 = Assert.Single(bobs.Items);
        Assert.Equal(SyncStatus.Check, b1.Status);
        Assert.Equal("Show é", b1.SeriesName);
        Assert.Equal("downloaded from x", b1.Note);
        Assert.Equal(DateTimeKind.Utc, b1.FinishedUtc.Kind);

        var page = await _store.GetFailedSyncsAsync(alice, 1, 1, CancellationToken.None);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal("A2", Assert.Single(page.Items).Name);

        Assert.Equal(b1, await _store.GetFailedSyncAsync(b1.Id, CancellationToken.None));
        Assert.Null(await _store.GetFailedSyncAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task Entries_can_be_removed_one_at_a_time_per_user_or_all()
    {
        await _store.InitializeAsync(CancellationToken.None);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await _store.AddFailedSyncAsync(Entry(alice, "A1"), 500, CancellationToken.None);
        await _store.AddFailedSyncAsync(Entry(alice, "A2"), 500, CancellationToken.None);
        await _store.AddFailedSyncAsync(Entry(bob, "B1"), 500, CancellationToken.None);
        var a1 = (await _store.GetFailedSyncsAsync(alice, 0, 10, CancellationToken.None)).Items.Single(i => i.Name == "A1");

        Assert.Equal(0, await _store.RemoveFailedSyncsAsync(bob, a1.Id, CancellationToken.None));
        Assert.Equal(1, await _store.RemoveFailedSyncsAsync(null, a1.Id, CancellationToken.None));
        Assert.Equal(1, await _store.RemoveFailedSyncsAsync(alice, null, CancellationToken.None));
        Assert.Equal(1, (await _store.GetFailedSyncsAsync(null, 0, 10, CancellationToken.None)).TotalCount);
        Assert.Equal(1, await _store.RemoveFailedSyncsAsync(null, null, CancellationToken.None));
        Assert.Equal(0, (await _store.GetFailedSyncsAsync(null, 0, 10, CancellationToken.None)).TotalCount);
    }

    [Fact]
    public void Another_users_entry_in_a_library_the_viewer_cannot_see_is_hidden()
    {
        var viewer = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = Entry(viewer, "Mine") with { Id = 1 };
        var hidden = Entry(other, "Secret") with { Id = 2 };
        var visible = Entry(other, "Shared") with { Id = 3 };

        var dto = JobsController.BuildFailedSyncs([mine, hidden, visible], 3, viewer, id => id == visible.ItemId);

        Assert.Equal(3, dto.TotalCount);
        Assert.True(dto.Items[0].Mine);
        Assert.Equal("Mine", dto.Items[0].Name);
        Assert.Null(dto.Items[1].ItemId);
        Assert.Equal(JobsController.HiddenName, dto.Items[1].Name);
        Assert.Null(dto.Items[1].Message);
        Assert.Null(dto.Items[1].SeriesName);
        Assert.Equal("Shared", dto.Items[2].Name);
        Assert.Equal("failed", dto.Items[2].Status);
    }
}
