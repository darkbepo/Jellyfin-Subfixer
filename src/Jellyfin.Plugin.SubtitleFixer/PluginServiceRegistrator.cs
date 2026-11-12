using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Web;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>File name of the state database inside the plugin's data folder.</summary>
    public const string DatabaseFileName = "state.db";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStateStore>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var folder = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(paths.PluginConfigurationsPath, "SubtitleFixer");
            return new SqliteStateStore(Path.Combine(folder, DatabaseFileName), sp.GetRequiredService<ILogger<SqliteStateStore>>());
        });

        serviceCollection.AddSingleton<StartupStatus>();
        serviceCollection.AddSingleton<FileTransformationRegistrar>();
        // Everything reads the library through the scope, so the "Libraries" setting applies everywhere (D43). Only the
        // settings page's library list (Admin/Libraries) reads the unrestricted facade.
        // Stored audio checks are attached on top, so untagged audio that was checked as English counts as English
        // everywhere (D49).
        serviceCollection.AddSingleton<JellyfinLibraryFacade>();
        serviceCollection.AddSingleton<AudioCheckCache>();
        serviceCollection.AddSingleton<ILibraryFacade>(sp => new AudioCheckFacade(
            new LibraryScopeFacade(sp.GetRequiredService<JellyfinLibraryFacade>(), () => Plugin.Instance?.Configuration.AllowedLibraryIds),
            sp.GetRequiredService<AudioCheckCache>()));
        serviceCollection.AddSingleton<AccessService>();
        serviceCollection.AddSingleton<SelectionValidator>();
        serviceCollection.AddSingleton<BrowseService>();
        serviceCollection.AddSingleton<SubSyncProbe>();
        serviceCollection.AddSingleton<IBazarrClient, BazarrClient>();
        serviceCollection.AddSingleton<IBazarrLookupSource, JellyfinBazarrLookupSource>();
        serviceCollection.AddSingleton(sp => new BazarrItemResolver(sp.GetRequiredService<IBazarrClient>(), sp.GetRequiredService<ILogger<BazarrItemResolver>>()));
        serviceCollection.AddSingleton(_ => new BazarrResultStore());
        serviceCollection.AddSingleton(_ => new BazarrRateLimiter());
        serviceCollection.AddSingleton(sp => new BazarrSearchService(
            sp.GetRequiredService<ILibraryFacade>(),
            sp.GetRequiredService<IBazarrLookupSource>(),
            sp.GetRequiredService<BazarrItemResolver>(),
            sp.GetRequiredService<IBazarrClient>(),
            sp.GetRequiredService<BazarrResultStore>(),
            sp.GetRequiredService<BazarrRateLimiter>(),
            sp.GetRequiredService<ILogger<BazarrSearchService>>()));
        serviceCollection.AddSingleton(sp => new BazarrDownloadService(
            sp.GetRequiredService<ILibraryFacade>(),
            sp.GetRequiredService<BazarrResultStore>(),
            sp.GetRequiredService<IBazarrClient>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<ISubSyncWorker>(),
            sp.GetRequiredService<ILogger<BazarrDownloadService>>()));

        // Job engine (M2).
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ISubSyncWorker>(sp => new CliWorker(
            () => Plugin.Instance?.Configuration.SubSyncFolder,
            sp.GetRequiredService<ILogger<CliWorker>>()));
        serviceCollection.AddSingleton<IRefreshService, RefreshService>();
        serviceCollection.AddSingleton<BackupService>();
        serviceCollection.AddSingleton<RecoveryService>();
        serviceCollection.AddSingleton<JobQueue>();
        serviceCollection.AddSingleton<VisibilityCache>();
        serviceCollection.AddSingleton(sp => new JobRunner(
            sp.GetRequiredService<ILibraryFacade>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<ISubSyncWorker>(),
            sp.GetRequiredService<IRefreshService>(),
            CurrentWorkerOptions,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<JobRunner>>()));

        // SubSync updates from the settings page (D46).
        serviceCollection.AddSingleton<ISubSyncReleaseSource, GitHubSubSyncReleases>();
        serviceCollection.AddSingleton<SubSyncInstaller>();
        serviceCollection.AddSingleton(sp => new SubSyncUpdateService(
            sp.GetRequiredService<ISubSyncReleaseSource>(),
            sp.GetRequiredService<SubSyncInstaller>(),
            sp.GetRequiredService<JobQueue>(),
            () => Plugin.Instance?.Configuration.SubSyncFolder,
            SubSyncVersion.Shared.GetAsync,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SubSyncUpdateService>>()));

        // The Check button (D49).
        serviceCollection.AddSingleton(sp => new AudioCheckService(
            sp.GetRequiredService<ILibraryFacade>(),
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<AudioCheckCache>(),
            sp.GetRequiredService<JobQueue>(),
            () => Plugin.Instance?.Configuration.SubSyncFolder,
            SubSyncVersion.Shared.GetAsync,
            AudioCheckService.DefaultRun,
            () => Plugin.Instance?.Configuration,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AudioCheckService>>()));

        serviceCollection.AddHostedService<StartupService>();
        serviceCollection.AddHostedService<JobProcessor>();
    }

    private static WorkerOptions CurrentWorkerOptions()
    {
        var config = Plugin.Instance?.Configuration;
        var minutes = Math.Max(1, config?.EpisodeTimeoutMinutes ?? 30);
        return new WorkerOptions(config?.TidyUpLines ?? true, config?.ForceCpu ?? false, TimeSpan.FromMinutes(minutes));
    }
}
