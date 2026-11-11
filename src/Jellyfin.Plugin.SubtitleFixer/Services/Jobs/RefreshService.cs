using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>Asks Jellyfin to refresh one item after its subtitle changed.</summary>
public interface IRefreshService
{
    /// <summary>Queues the refresh; returns at once.</summary>
    /// <param name="itemId">Item.</param>
    void RequestRefresh(Guid itemId);
}

/// <summary>
/// The same call Jellyfin makes after a subtitle upload (M0 S4): default refresh mode, no replace flags,
/// high priority. External <c>.srt</c> files are read per request, so this is insurance.
/// </summary>
public sealed class RefreshService : IRefreshService
{
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshService"/> class.
    /// </summary>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    public RefreshService(IProviderManager providerManager, IFileSystem fileSystem)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public void RequestRefresh(Guid itemId)
    {
        _providerManager.QueueRefresh(itemId, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
    }
}
