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
        serviceCollection.AddSingleton<ILibraryFacade, JellyfinLibraryFacade>();
        serviceCollection.AddSingleton<AccessService>();
        serviceCollection.AddSingleton<SelectionValidator>();
        serviceCollection.AddSingleton<BrowseService>();
        serviceCollection.AddSingleton<SubSyncProbe>();
        serviceCollection.AddSingleton<IBazarrClient, BazarrClient>();

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
