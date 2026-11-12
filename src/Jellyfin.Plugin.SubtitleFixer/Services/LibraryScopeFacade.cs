using System.Globalization;
using Jellyfin.Plugin.SubtitleFixer.Domain;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>
/// Applies the <c>AllowedLibraryIds</c> setting on top of the user's own library access (D43). Everything that reads
/// the library goes through this, so an item outside the allowed libraries is "not found" for browsing, search,
/// selection, Bazarr and the runner alike. With no libraries chosen, every library is allowed and this adds nothing.
/// </summary>
public sealed class LibraryScopeFacade : ILibraryFacade
{
    private readonly ILibraryFacade _inner;
    private readonly Func<IReadOnlyCollection<string>?> _allowed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScopeFacade"/> class.
    /// </summary>
    /// <param name="inner">The unrestricted facade.</param>
    /// <param name="allowed">Reads the allowed library ids from the settings (read on every call, so a save applies at once).</param>
    public LibraryScopeFacade(ILibraryFacade inner, Func<IReadOnlyCollection<string>?> allowed)
    {
        _inner = inner;
        _allowed = allowed;
    }

    /// <summary>Parses the setting. Unparsable entries are ignored; an empty or missing list means every library.</summary>
    /// <param name="ids">Setting value.</param>
    /// <returns>The allowed ids, or null for every library.</returns>
    public static IReadOnlySet<Guid>? Parse(IReadOnlyCollection<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return null;
        }

        var set = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (Guid.TryParse(id?.Trim(), CultureInfo.InvariantCulture, out var guid) && guid != Guid.Empty)
            {
                set.Add(guid);
            }
        }

        // Every entry unreadable: fail closed rather than silently opening every library.
        return set;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetLibraries(Guid userId)
    {
        var allowed = Allowed();
        var all = _inner.GetLibraries(userId);
        return allowed is null ? all : all.Where(l => allowed.Contains(l.Id)).ToList();
    }

    /// <inheritdoc />
    public Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit) =>
        InScope(userId, parentId, Allowed()) ? _inner.GetChildren(userId, parentId, startIndex, limit) : null;

    /// <inheritdoc />
    public LibraryNode? GetNode(Guid userId, Guid itemId) =>
        InScope(userId, itemId, Allowed()) ? _inner.GetNode(userId, itemId) : null;

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId) =>
        InScope(userId, itemId, Allowed()) ? _inner.GetBreadcrumb(userId, itemId) : [];

    /// <inheritdoc />
    public IReadOnlyList<VideoFacts> GetSeasonEpisodes(Guid userId, Guid seasonId) =>
        InScope(userId, seasonId, Allowed()) ? _inner.GetSeasonEpisodes(userId, seasonId) : [];

    /// <inheritdoc />
    public VideoFacts? GetVideo(Guid userId, Guid episodeId) =>
        InScope(userId, episodeId, Allowed()) ? _inner.GetVideo(userId, episodeId) : null;

    /// <inheritdoc />
    public Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit, IReadOnlyCollection<Guid>? libraryIds = null)
    {
        var allowed = Allowed();
        if (allowed is null)
        {
            return _inner.Search(userId, term, startIndex, limit, libraryIds);
        }

        var scope = libraryIds is null ? allowed.ToList() : libraryIds.Where(allowed.Contains).ToList();
        return scope.Count == 0 ? new Paged<LibraryNode>([], 0) : _inner.Search(userId, term, startIndex, limit, scope);
    }

    private IReadOnlySet<Guid>? Allowed() => Parse(_allowed());

    // The breadcrumb starts at the item's library; an item whose library can't be found is out of scope.
    private bool InScope(Guid userId, Guid itemId, IReadOnlySet<Guid>? allowed)
    {
        if (allowed is null)
        {
            return true;
        }

        if (allowed.Contains(itemId))
        {
            return true;
        }

        var trail = _inner.GetBreadcrumb(userId, itemId);
        return trail.Count > 0 && trail[0].Kind == NodeKind.Library && allowed.Contains(trail[0].Id);
    }
}
