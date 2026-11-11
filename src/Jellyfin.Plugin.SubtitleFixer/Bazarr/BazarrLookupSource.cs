using System.Globalization;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>Builds the <see cref="BazarrLookup"/> for a video the caller can access. Tests use a fake.</summary>
public interface IBazarrLookupSource
{
    /// <summary>Gets what Bazarr can match the item on.</summary>
    /// <param name="userId">User; access was already checked through <see cref="ILibraryFacade.GetVideo"/>.</param>
    /// <param name="video">The episode or movie.</param>
    /// <returns>The lookup, or null when the item has no video path.</returns>
    BazarrLookup? GetLookup(Guid userId, VideoFacts video);
}

/// <summary>
/// Reads provider ids and the series folder from Jellyfin 12.4 (<c>BaseItem.ProviderIds</c>, <c>Episode.Series</c>,
/// <c>ProviderIdsExtensions.GetProviderId</c>, checked in the 12.4 package docs). The item is fetched for the user,
/// so Jellyfin applies library access again.
/// </summary>
public sealed class JellyfinBazarrLookupSource : IBazarrLookupSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinBazarrLookupSource"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    public JellyfinBazarrLookupSource(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public BazarrLookup? GetLookup(Guid userId, VideoFacts video)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (string.IsNullOrWhiteSpace(video.VideoPath))
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(video.Node.Id, user);
        switch (item)
        {
            case Movie movie:
                return new BazarrLookup(true, video.VideoPath, movie.GetProviderId(MetadataProvider.Imdb), null, null, null, null);
            case Episode episode:
                var series = episode.Series;
                return new BazarrLookup(
                    false,
                    video.VideoPath,
                    series?.GetProviderId(MetadataProvider.Imdb),
                    int.TryParse(series?.GetProviderId(MetadataProvider.Tvdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdb) ? tvdb : null,
                    series?.Path,
                    episode.ParentIndexNumber ?? video.Node.ParentIndexNumber,
                    episode.IndexNumber ?? video.Node.IndexNumber);
            default:
                return null;
        }
    }
}
