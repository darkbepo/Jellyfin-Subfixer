using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>
/// Opens the state store and registers the menu transformation. Runs in the background so it never blocks
/// Jellyfin's startup; registration retries in case File Transformation is still starting.
/// </summary>
public sealed class StartupService : IHostedService
{
    private const int RegistrationAttempts = 10;
    private static readonly TimeSpan RegistrationDelay = TimeSpan.FromSeconds(3);

    private readonly IStateStore _state;
    private readonly RecoveryService _recovery;
    private readonly FileTransformationRegistrar _registrar;
    private readonly StartupStatus _status;
    private readonly ILogger<StartupService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _work;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    /// <param name="state">State store.</param>
    /// <param name="recovery">Startup recovery.</param>
    /// <param name="registrar">File Transformation registrar.</param>
    /// <param name="status">Startup status.</param>
    /// <param name="logger">Logger.</param>
    public StartupService(IStateStore state, RecoveryService recovery, FileTransformationRegistrar registrar, StartupStatus status, ILogger<StartupService> logger)
    {
        _state = state;
        _recovery = recovery;
        _registrar = registrar;
        _status = status;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _work = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_work is not null)
        {
            await Task.WhenAny(_work, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _state.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _status.StateReady = true;
            _status.Recovered = await _recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or TypeLoadException or DllNotFoundException)
        {
            _status.StateError = ex.Message;
            _logger.LogError(ex, "Subtitle Fixer could not open its state database at {Path}", _state.Location);
        }
        finally
        {
            // Without a working state store every run fails safely at the journal step, so the queue may start.
            _status.MarkRecoveryDone();
        }

        for (var attempt = 1; attempt <= RegistrationAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (_registrar.TryRegister(out var error))
            {
                _status.MenuRegistered = true;
                _status.MenuError = null;
                return;
            }

            _status.MenuError = error;
            try
            {
                await Task.Delay(RegistrationDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _logger.LogError("Subtitle Fixer could not add its menu entry: {Error}", _status.MenuError);
    }
}
