using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Web;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
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
        serviceCollection.AddHostedService<StartupService>();
    }
}
