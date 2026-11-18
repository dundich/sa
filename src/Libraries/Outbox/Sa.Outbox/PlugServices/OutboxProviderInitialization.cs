using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Runs each provider's <see cref="IOutboxProviderInitializer"/> once and shares the result with
/// every consumer group of that provider.
/// </summary>
/// <remarks>
/// <para>
/// The initialization runs at most once per provider key: concurrent groups await the same task.
/// A failed (or cancelled) attempt is removed from the cache, so the next delivery tick retries it
/// instead of replaying the same exception forever.
/// </para>
/// <para>
/// The task is published in the cache <b>before</b> the initializer is started. The failure path
/// then always removes the entry it owns, so a fast fault (a refused connection, a synchronously
/// faulted initializer) cannot be cached before the entry exists and stay there forever.
/// </para>
/// <para>
/// Callers await with their own token (<see cref="Task.WaitAsync(CancellationToken)"/>), so a
/// shutdown never waits for a connection attempt; the attempt itself uses the provider's own
/// connection timeouts and is not tied to a single group's lifetime.
/// </para>
/// </remarks>
internal sealed class OutboxProviderInitialization(IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);

    /// <summary>
    /// Ensures the provider is initialized. A provider without an initializer is a completed no-op.
    /// </summary>
    /// <param name="providerKey">The provider key (outbox scope key).</param>
    /// <param name="cancellationToken">The caller's token; it bounds the wait, not the warm-up.</param>
    /// <returns>A task that completes when the provider is ready.</returns>
    public Task EnsureAsync(string providerKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        // Fast path: once the provider is initialized (or known to have no initializer) a delivery
        // tick costs one dictionary lookup and no keyed-service resolution.
        if (_tasks.TryGetValue(providerKey, out Task? cached))
            return cached.WaitAsync(cancellationToken);

        IOutboxProviderInitializer? initializer =
            services.GetKeyedService<IOutboxProviderInitializer>(providerKey);

        if (initializer is null)
        {
            // A provider without a warm-up is a permanent no-op; remember it so the keyed lookup is
            // not repeated on every tick.
            _tasks.TryAdd(providerKey, Task.CompletedTask);
            return Task.CompletedTask;
        }

        return StartAsync(providerKey, initializer).WaitAsync(cancellationToken);
    }

    private Task StartAsync(string providerKey, IOutboxProviderInitializer initializer)
    {
        while (true)
        {
            if (_tasks.TryGetValue(providerKey, out Task? existing))
                return existing;

            // Publish before starting: the initializer may fault synchronously, and the failure
            // path removes this entry. Inserting after the start (the old Task.Yield approach) left
            // a window where the removal found nothing and the faulted task was cached forever.
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_tasks.TryAdd(providerKey, completion.Task))
                continue;

            _ = RunAsync(providerKey, initializer, completion);
            return completion.Task;
        }
    }

    private async Task RunAsync(
        string providerKey,
        IOutboxProviderInitializer initializer,
        TaskCompletionSource completion)
    {
        try
        {
            await initializer.InitializeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            // Back to "not initialized": the next tick retries the whole warm-up.
            _tasks.TryRemove(providerKey, out _);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            _tasks.TryRemove(providerKey, out _);
            completion.TrySetException(exception);
        }
    }
}
