namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>
/// The Jellyfin library, seen through the signed-in user's access. The only place that touches Jellyfin's
/// library APIs; tests use a fake. Every method applies the user's library access.
/// </summary>
public interface ILibraryFacade
{
    /// <summary>Gets the TV libraries the user can see.</summary>
    /// <param name="userId">User.</param>
    /// <returns>Libraries.</returns>
    IReadOnlyList<LibraryNode> GetTvLibraries(Guid userId);

    /// <summary>Gets a page of children: library to series, series to seasons, season to episodes.</summary>
    /// <param name="userId">User.</param>
    /// <param name="parentId">Parent item.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>The page, or null when the parent is unknown or not accessible.</returns>
    Paged<LibraryNode>? GetChildren(Guid userId, Guid parentId, int startIndex, int limit);

    /// <summary>Gets one item, or null when unknown or not accessible.</summary>
    /// <param name="userId">User.</param>
    /// <param name="itemId">Item.</param>
    /// <returns>The node.</returns>
    LibraryNode? GetNode(Guid userId, Guid itemId);

    /// <summary>Gets the path from the library down to the item (inclusive).</summary>
    /// <param name="userId">User.</param>
    /// <param name="itemId">Item.</param>
    /// <returns>Breadcrumb, library first.</returns>
    IReadOnlyList<LibraryNode> GetBreadcrumb(Guid userId, Guid itemId);

    /// <summary>Gets all real (not missing/virtual) episodes of a season with their facts.</summary>
    /// <param name="userId">User.</param>
    /// <param name="seasonId">Season.</param>
    /// <returns>Episodes in order.</returns>
    IReadOnlyList<EpisodeFacts> GetSeasonEpisodes(Guid userId, Guid seasonId);

    /// <summary>Gets one episode with its facts, or null.</summary>
    /// <param name="userId">User.</param>
    /// <param name="episodeId">Episode.</param>
    /// <returns>Facts.</returns>
    EpisodeFacts? GetEpisode(Guid userId, Guid episodeId);

    /// <summary>Searches series and episodes by name.</summary>
    /// <param name="userId">User.</param>
    /// <param name="term">Search term.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <returns>The page.</returns>
    Paged<LibraryNode> Search(Guid userId, string term, int startIndex, int limit);
}
