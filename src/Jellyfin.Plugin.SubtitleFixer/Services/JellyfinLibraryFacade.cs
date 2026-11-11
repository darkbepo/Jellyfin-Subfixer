using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>
/// <see cref="ILibraryFacade"/> over Jellyfin 12.4's library APIs (M0-SPIKES.md S3). Every query passes the
/// user, so Jellyfin applies library access and parental rules. Never loads a whole library.
/// </summary>
public sealed class JellyfinLibraryFacade : ILibraryFacade
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryFacade"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userViewManager">User view manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    public JellyfinLibraryFacade(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserViewManager userViewManager,
        IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userViewManager = userViewManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetLibraries(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        return _userViewManager.GetUserViews(new UserViewQuery { User = user })
            .Where(IsSupportedLibrary)
            .Select(ToNode)
            .ToList();
    }

    /// <inheritdoc />
    public Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit)
    {
        var user = _userManager.GetUserById(userId);
        var parent = user is null ? null : _libraryManager.GetItemById<BaseItem>(parentId, user);
        if (user is null || parent is null)
        {
            return null;
        }

        var dto = new DtoOptions(false);
        switch (parent)
        {
            case Series series:
                return Page(series.GetSeasons(user, dto).Select(ToNode).ToList(), startIndex, limit);
            case Season season:
                return Page(season.GetEpisodes(user, dto, false).Where(e => !e.IsVirtualItem).Select(ToNode).ToList(), startIndex, limit);
            case Folder folder when IsSupportedLibrary(folder):
                var result = folder.GetItems(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = IsMovieLibrary(folder) ? [BaseItemKind.Movie] : [BaseItemKind.Series],
                    IsVirtualItem = false,
                    Recursive = true,
                    StartIndex = startIndex,
                    Limit = limit,
                    EnableTotalRecordCount = true,
                    OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                    DtoOptions = dto,
                });
                return new Paged<LibraryNode>(result.Items.Select(ToNode).ToList(), result.TotalRecordCount);
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public LibraryNode? GetNode(Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        return item is null ? null : ToNode(item);
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return [];
        }

        if (item is Folder folder && IsSupportedLibrary(folder))
        {
            return [ToNode(folder)];
        }

        if (item is Movie movie)
        {
            var movieLibrary = _libraryManager.GetCollectionFolders(movie).FirstOrDefault(IsSupportedLibrary);
            return movieLibrary is null ? [ToNode(movie)] : [ToNode(movieLibrary), ToNode(movie)];
        }

        var series = item switch
        {
            Series s => s,
            Season s => s.Series,
            Episode e => e.Series,
            _ => null,
        };
        if (series is null)
        {
            return [];
        }

        var season = item switch
        {
            Season s => s,
            Episode e => e.Season,
            _ => null,
        };

        var trail = new List<LibraryNode>(4);
        var library = _libraryManager.GetCollectionFolders(series).FirstOrDefault(IsSupportedLibrary);
        if (library is not null)
        {
            trail.Add(ToNode(library));
        }

        trail.Add(ToNode(series));
        if (season is not null)
        {
            trail.Add(ToNode(season));
        }

        if (item is Episode episode)
        {
            trail.Add(ToNode(episode));
        }

        return trail;
    }

    /// <inheritdoc />
    public IReadOnlyList<VideoFacts> GetSeasonEpisodes(Guid userId, Guid seasonId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || _libraryManager.GetItemById<BaseItem>(seasonId, user) is not Season season)
        {
            return [];
        }

        return season.GetEpisodes(user, new DtoOptions(false), false)
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem)
            .Select(ToFacts)
            .ToList();
    }

    /// <inheritdoc />
    public VideoFacts? GetVideo(Guid userId, Guid episodeId)
    {
        var user = _userManager.GetUserById(userId);
        return user is not null
            && _libraryManager.GetItemById<BaseItem>(episodeId, user) is Video { IsVirtualItem: false } video
            && video is Episode or Movie
            ? ToFacts(video)
            : null;
    }

    /// <inheritdoc />
    public Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || string.IsNullOrWhiteSpace(term))
        {
            return new Paged<LibraryNode>([], 0);
        }

        var result = _libraryManager.GetItemsResult(new InternalItemsQuery(user)
        {
            SearchTerm = term.Trim(),
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            StartIndex = startIndex,
            Limit = limit,
            EnableTotalRecordCount = true,
            DtoOptions = new DtoOptions(false),
        });

        return new Paged<LibraryNode>(result.Items.Select(ToNode).ToList(), result.TotalRecordCount);
    }

    private static bool IsSupportedLibrary(BaseItem item) =>
        item is IHasCollectionType { CollectionType: CollectionType.tvshows or CollectionType.movies };

    private static bool IsMovieLibrary(BaseItem item) =>
        item is IHasCollectionType { CollectionType: CollectionType.movies };

    private static Paged<LibraryNode> Page(IReadOnlyList<LibraryNode> all, int startIndex, int limit) =>
        new(all.Skip(startIndex).Take(limit).ToList(), all.Count);

    private static LibraryNode ToNode(BaseItem item) => item switch
    {
        Series s => new LibraryNode(s.Id, s.Name, NodeKind.Series, s.Id, s.Name),
        Season s => new LibraryNode(s.Id, s.Name, NodeKind.Season, s.SeriesId, s.SeriesName, s.Id, s.IndexNumber),
        Episode e => new LibraryNode(e.Id, e.Name, NodeKind.Episode, e.SeriesId, e.SeriesName, e.SeasonId, e.IndexNumber, e.ParentIndexNumber),
        Movie m => new LibraryNode(m.Id, m.Name, NodeKind.Movie, ProductionYear: m.ProductionYear),
        Folder f when IsSupportedLibrary(f) => new LibraryNode(f.Id, f.Name, NodeKind.Library, LibraryType: IsMovieLibrary(f) ? "movies" : "tvshows"),
        _ => new LibraryNode(item.Id, item.Name, NodeKind.Other),
    };

    private VideoFacts ToFacts(Video episode)
    {
        var languages = _mediaSourceManager.GetMediaStreams(episode.Id)
            .Where(s => s.Type == MediaStreamType.Audio)
            .Select(s => s.Language)
            .ToList();
        var runtime = episode.RunTimeTicks is > 0 ? TimeSpan.FromTicks(episode.RunTimeTicks.Value) : (TimeSpan?)null;
        return new VideoFacts(ToNode(episode), string.IsNullOrEmpty(episode.Path) ? null : episode.Path, languages, runtime);
    }
}
