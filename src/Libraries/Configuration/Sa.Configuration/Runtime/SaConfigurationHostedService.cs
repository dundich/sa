using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Configuration;

/// <summary>
/// Runs the optional start-up load and the optional periodic reload of the dynamic layers.
/// </summary>
/// <remarks>
/// The service is registered only when <see cref="SaConfigurationRuntimeOptions.InitialLoad"/> is not
/// <see cref="SaDynamicInitialLoad.Explicit"/> or
/// <see cref="SaConfigurationRuntimeOptions.DynamicReload"/> is on; a plain
/// <c>AddSaConfigurationRuntime</c> call registers no hosted service at all.
/// <para>
/// The start-up load runs in <see cref="StartingAsync"/>, i.e. before any
/// <see cref="IHostedService.StartAsync"/> — including the web host — so the database layer is in
/// place before the first request, and a failure fails the host start.
/// </para>
/// </remarks>
internal sealed class SaConfigurationHostedService(IServiceProvider services, SaConfigurationRuntimeOptions options)
    : IHostedLifecycleService
{
    private CancellationTokenSource? _polling;
    private Task? _pollingLoop;

    private ISaConfigurationReloader Reloader => services.GetRequiredService<ISaConfigurationReloader>();

    private ILogger Logger
        => services.GetService<ILogger<SaConfigurationHostedService>>() ?? NullLogger<SaConfigurationHostedService>.Instance;

    public Task StartingAsync(CancellationToken cancellationToken) => options.InitialLoad switch
    {
        SaDynamicInitialLoad.Hosted => InitializeAsync(cancellationToken),
        SaDynamicInitialLoad.Background => StartBackgroundInitialization(),
        _ => Task.CompletedTask
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.DynamicReload || _pollingLoop is not null)
            return Task.CompletedTask;

        _polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingLoop = PollAsync(_polling.Token);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_polling is null)
            return;

        await _polling.CancelAsync().ConfigureAwait(false);

        if (_pollingLoop is not null)
        {
            try
            {
                await _pollingLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: the loop observes the same cancellation.
            }
        }

        _polling.Dispose();
        _polling = null;
        _pollingLoop = null;
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Reloader.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "The initial load of the dynamic configuration layers failed.");
            throw;
        }
    }

    private Task StartBackgroundInitialization()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Reloader.EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    exception,
                    "The background initial load of the dynamic configuration layers failed; "
                    + "the static configuration stays in effect.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (options.DynamicReloadInitialDelay is { } delay)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            using PeriodicTimer timer = new(options.DynamicReloadInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await Reloader.ReloadDynamicAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // The provider keeps its previous snapshot; one failed poll must not kill the loop.
                    Logger.LogError(
                        exception,
                        "Periodic reload of the dynamic configuration layers failed; "
                        + "the previous snapshot stays in effect.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
