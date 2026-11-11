using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>A temp folder with spaces and non-ASCII in its name, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "subfixer tests ü " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content = "x")
    {
        var full = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>In-memory library: one or more series, each with seasons of episodes.</summary>
public sealed class FakeLibrary : ILibraryFacade
{
    private readonly Dictionary<Guid, LibraryNode> _nodes = new();
    private readonly Dictionary<Guid, List<EpisodeFacts>> _seasonEpisodes = new();
    private readonly Dictionary<Guid, EpisodeFacts> _episodes = new();

    public HashSet<Guid> Hidden { get; } = new();

    public LibraryNode AddLibrary(string name)
    {
        var node = new LibraryNode(Guid.NewGuid(), name, NodeKind.Library);
        _nodes[node.Id] = node;
        return node;
    }

    public LibraryNode AddSeries(string name)
    {
        var id = Guid.NewGuid();
        var node = new LibraryNode(id, name, NodeKind.Series, id, name);
        _nodes[id] = node;
        return node;
    }

    public LibraryNode AddSeason(LibraryNode series, int number)
    {
        var id = Guid.NewGuid();
        var node = new LibraryNode(id, "Season " + number, NodeKind.Season, series.Id, series.Name, id, number);
        _nodes[id] = node;
        _seasonEpisodes[id] = new List<EpisodeFacts>();
        return node;
    }

    public EpisodeFacts AddEpisode(LibraryNode season, int number, string? videoPath, params string?[] audio)
    {
        var id = Guid.NewGuid();
        var node = new LibraryNode(id, "Episode " + number, NodeKind.Episode, season.SeriesId, season.SeriesName, season.Id, number, season.IndexNumber);
        var facts = new EpisodeFacts(node, videoPath, audio);
        _nodes[id] = node;
        _episodes[id] = facts;
        _seasonEpisodes[season.Id].Add(facts);
        return facts;
    }

    public LibraryNode AddOther(string name)
    {
        var node = new LibraryNode(Guid.NewGuid(), name, NodeKind.Other);
        _nodes[node.Id] = node;
        return node;
    }

    public IReadOnlyList<LibraryNode> GetTvLibraries(Guid userId) => _nodes.Values.Where(n => n.Kind == NodeKind.Library).ToList();

    public Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit) => null;

    public LibraryNode? GetNode(Guid userId, Guid itemId) =>
        !Hidden.Contains(itemId) && _nodes.TryGetValue(itemId, out var n) ? n : null;

    public IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId) => [];

    public IReadOnlyList<EpisodeFacts> GetSeasonEpisodes(Guid userId, Guid seasonId) =>
        _seasonEpisodes.TryGetValue(seasonId, out var list) ? list.Where(e => !Hidden.Contains(e.Node.Id)).ToList() : [];

    public EpisodeFacts? GetEpisode(Guid userId, Guid episodeId) =>
        !Hidden.Contains(episodeId) && _episodes.TryGetValue(episodeId, out var e) ? e : null;

    public Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit) => new([], 0);
}

/// <summary>In-memory state store.</summary>
public sealed class FakeStateStore : IStateStore
{
    public Dictionary<Guid, SyncRecord> Records { get; } = new();

    public string Location => "memory";

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, SyncRecord>>(itemIds.Where(Records.ContainsKey).Distinct().ToDictionary(id => id, id => Records[id]));

    public Task UpsertAsync(SyncRecord record, CancellationToken cancellationToken)
    {
        Records[record.ItemId] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid itemId, CancellationToken cancellationToken)
    {
        Records.Remove(itemId);
        return Task.CompletedTask;
    }

    public Dictionary<Guid, InFlightBackup> InFlight { get; } = new();

    public Task AddInFlightAsync(InFlightBackup entry, CancellationToken cancellationToken)
    {
        InFlight[entry.ItemId] = entry;
        return Task.CompletedTask;
    }

    public Task RemoveInFlightAsync(Guid itemId, CancellationToken cancellationToken)
    {
        InFlight.Remove(itemId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InFlightBackup>> GetInFlightAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InFlightBackup>>(InFlight.Values.ToList());

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class Records
{
    public static SyncRecord For(Guid itemId, string videoPath, SyncStatus status, string? message = null)
    {
        var fp = VideoFingerprint.FromFile(videoPath) ?? throw new InvalidOperationException("Video missing: " + videoPath);
        return new SyncRecord
        {
            ItemId = itemId,
            Video = fp,
            SubtitlePath = Path.ChangeExtension(videoPath, ".en.srt"),
            Status = status,
            Message = message,
            UpdatedUtc = DateTime.UtcNow,
        };
    }
}
