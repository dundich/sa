namespace Sa.Outbox.Delivery;

/// <summary>
/// Registry of <see cref="OutboxIdleState"/> per consumer group, so the delivery job and the
/// adaptive schedule timing share the same instance.
/// </summary>
internal interface IOutboxIdleStateRegistry
{
    /// <summary>Gets the state for a group, creating it on first use.</summary>
    OutboxIdleState GetOrAdd(string consumerGroupId, TimeSpan baseInterval, TimeSpan maxInterval);

    /// <summary>Gets the state for a group, or <c>null</c> if it was never created.</summary>
    OutboxIdleState? Get(string consumerGroupId);

    /// <summary>Resets every tracked group's back-off to its base interval.</summary>
    void ResetAll();
}

internal sealed class OutboxIdleStateRegistry(IEmptyQueueWaitStrategy strategy) : IOutboxIdleStateRegistry
{
    private readonly Dictionary<string, OutboxIdleState> _states = [];
    private readonly Lock _lock = new();

    public OutboxIdleState GetOrAdd(string consumerGroupId, TimeSpan baseInterval, TimeSpan maxInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroupId);

        lock (_lock)
        {
            if (_states.TryGetValue(consumerGroupId, out var existing))
                return existing;

            var state = new OutboxIdleState(strategy, baseInterval, maxInterval);
            _states[consumerGroupId] = state;
            return state;
        }
    }

    public OutboxIdleState? Get(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId)) return null;

        lock (_lock)
        {
            return _states.TryGetValue(consumerGroupId, out var state) ? state : null;
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
