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

        IOutboxProviderInitializer? initializer =
            services.GetKeyedService<IOutboxProviderInitializer>(providerKey);

        if (initializer is null) return Task.CompletedTask;

        Task task = _tasks.GetOrAdd(providerKey, _ => RunAsync(initializer, providerKey));

        return task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync(IOutboxProviderInitializer initializer, string providerKey)
    {
        // Yield first: the dictionary insertion (GetOrAdd) must publish this task before the
        // initializer can fault, otherwise the failure path would remove a not-yet-inserted entry
        // and the faulted task would stay cached.
        await Task.Yield();

        try
        {
            await initializer.InitializeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Back to "not initialized": the next tick retries the whole warm-up.
            _tasks.TryRemove(providerKey, out _);
            throw;
        }
    }
}
