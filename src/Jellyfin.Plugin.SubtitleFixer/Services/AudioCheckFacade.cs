using System.Collections.Concurrent;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>
/// Stored audio-language checks in memory (D49): loaded from the state store at startup, updated by each Check. A
/// check is a few dozen bytes and only exists for items someone checked, so the whole set fits easily.
/// </summary>
public sealed class AudioCheckCache
{
    private readonly ConcurrentDictionary<Guid, AudioCheck> _checks = new();

    /// <summary>Gets how many checks are held.</summary>
    public int Count => _checks.Count;

    /// <summary>Replaces everything with the stored checks.</summary>
    /// <param name="checks">Checks.</param>
    public void Load(IEnumerable<AudioCheck> checks)
    {
        _checks.Clear();
        foreach (var check in checks)
        {
            _checks[check.ItemId] = check;
        }
    }

    /// <summary>Adds or replaces one check.</summary>
    /// <param name="check">Check.</param>
    public void Set(AudioCheck check) => _checks[check.ItemId] = check;

    /// <summary>Gets an item's check, or null.</summary>
    /// <param name="itemId">Item.</param>
    /// <returns>The check.</returns>
    public AudioCheck? Get(Guid itemId) => _checks.TryGetValue(itemId, out var c) ? c : null;
}

/// <summary>
/// Attaches stored audio-language checks to video facts (D49), so every rule that reads the library (browsing,
/// selection, the runner, Bazarr) treats a checked-English item the same way. Everything else passes through.
/// </summary>
public sealed class AudioCheckFacade : ILibraryFacade
{
    private readonly ILibraryFacade _inner;
    private readonly AudioCheckCache _checks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCheckFacade"/> class.
    /// </summary>
    /// <param name="inner">The facade to wrap.</param>
    /// <param name="checks">Stored checks.</param>
    public AudioCheckFacade(ILibraryFacade inner, AudioCheckCache checks)
    {
        _inner = inner;
        _checks = checks;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetLibraries(Guid userId) => _inner.GetLibraries(userId);

    /// <inheritdoc />
    public Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit, string? startsWith = null) =>
        _inner.GetChildren(userId, parentId, startIndex, limit, startsWith);

    /// <inheritdoc />
    public LibraryNode? GetNode(Guid userId, Guid itemId) => _inner.GetNode(userId, itemId);

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId) => _inner.GetBreadcrumb(userId, itemId);

    /// <inheritdoc />
    public IReadOnlyList<VideoFacts> GetSeasonEpisodes(Guid userId, Guid seasonId) =>
        _inner.GetSeasonEpisodes(userId, seasonId).Select(With).ToList();

    /// <inheritdoc />
    public VideoFacts? GetVideo(Guid userId, Guid episodeId) => _inner.GetVideo(userId, episodeId) is { } v ? With(v) : null;

    /// <inheritdoc />
    public Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit, IReadOnlyCollection<Guid>? libraryIds = null) =>
        _inner.Search(userId, term, startIndex, limit, libraryIds);

    private VideoFacts With(VideoFacts facts) => _checks.Get(facts.Node.Id) is { } check ? facts with { AudioCheck = check } : facts;
}
