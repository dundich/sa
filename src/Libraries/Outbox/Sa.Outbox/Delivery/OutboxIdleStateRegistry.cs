namespace Sa.Outbox.Delivery;

/// <summary>
/// Registry of <see cref="OutboxIdleState"/> per consumer group, so the delivery job and the
/// adaptive schedule timing share the same instance.
/// </summary>
internal interface IOutboxIdleStateRegistry
{
    /// <summary>Gets the state for a group, creating it on first use.</summary>
    OutboxIdleState GetOrAdd(OutboxGroupKey key, TimeSpan baseInterval, TimeSpan maxInterval);

    /// <summary>Gets the state for a group, or <c>null</c> if it was never created.</summary>
    OutboxIdleState? Get(OutboxGroupKey key);

    /// <summary>Resets every tracked group's back-off to its base interval.</summary>
    void ResetAll();
}

internal sealed class OutboxIdleStateRegistry(IEmptyQueueWaitStrategy strategy) : IOutboxIdleStateRegistry
{
    private readonly Dictionary<OutboxGroupKey, OutboxIdleState> _states = [];
    private readonly Lock _lock = new();

    public OutboxIdleState GetOrAdd(OutboxGroupKey key, TimeSpan baseInterval, TimeSpan maxInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ProviderKey);

        lock (_lock)
        {
            if (_states.TryGetValue(key, out var existing))
                return existing;

            var state = new OutboxIdleState(strategy, baseInterval, maxInterval);
            _states[key] = state;
            return state;
        }
    }

    public OutboxIdleState? Get(OutboxGroupKey key)
    {
        if (string.IsNullOrWhiteSpace(key.GroupId)) return null;

        lock (_lock)
        {
            return _states.TryGetValue(key, out var state) ? state : null;
        }
    }

    public void ResetAll()
    {
        OutboxIdleState[] states;
        lock (_lock)
        {
            states = [.. _states.Values];
        }

        foreach (var state in states)
        {
            state.Reset();
        }
    }
}
