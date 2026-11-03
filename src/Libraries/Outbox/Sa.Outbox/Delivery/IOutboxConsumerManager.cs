namespace Sa.Outbox.Delivery;

/// <summary>
/// Central manager for runtime control of consumer group settings.
/// Provides atomic snapshot swaps, pause/resume lifecycle, and change subscriptions.
/// All settings are immutable — updates create new instances, never mutate existing ones.
/// </summary>
public interface IOutboxConsumerManager
{
    /// <summary>
    /// Atomically applies a transformation to the current settings for a consumer group.
    /// The update is atomically swapped — active deliveries finish their current batch,
    /// then pick up the new settings on the next iteration.
    /// </summary>
    /// <param name="consumerGroupId">The consumer group identifier.</param>
    /// <param name="transform">
    /// A function that receives the current snapshot and returns the updated one. Use <c>with</c>
    /// expressions. It runs outside the manager lock and may be invoked again if a concurrent
    /// update lands first, so it must not assume it runs exactly once.
    /// </param>
    void Apply(string consumerGroupId, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
        => Apply(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId), transform);

    /// <summary>
    /// Atomically applies a transformation to the current settings of one provider's consumer group.
    /// </summary>
    /// <param name="key">The provider + group identity.</param>
    /// <param name="transform">The transformation; see the string overload for its contract.</param>
    void Apply(OutboxGroupKey key, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
        => throw new NotSupportedException();



    /// <summary>
    /// Attempts to register a consumer group only if it is not already registered.
    /// Thread-safe and idempotent — concurrent callers will see a consistent result.
    /// Returns true if the group was newly registered, false if it already existed.
    /// </summary>
    bool TryRegister(string consumerGroupId, OutboxConsumerSettings settings)
        => TryRegister(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId), settings);

    /// <summary>Registers one provider's consumer group. See the string overload.</summary>
    /// <param name="key">The provider + group identity.</param>
    /// <param name="settings">The initial snapshot.</param>
    /// <returns><see langword="true"/> when the group was newly registered.</returns>
    bool TryRegister(OutboxGroupKey key, OutboxConsumerSettings settings)
        => throw new NotSupportedException();

    /// <summary>
    /// Retrieves the current immutable settings snapshot. Thread-safe.
    /// </summary>
    OutboxConsumerSettings? Get(string consumerGroupId)
        => throw new NotSupportedException();

    /// <summary>Gets one provider's settings snapshot.</summary>
    /// <param name="key">The provider + group identity.</param>
    /// <returns>The snapshot, or <see langword="null"/> when the group is not registered.</returns>
    OutboxConsumerSettings? Get(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Temporarily pauses a consumer group without losing any settings.
    /// Active deliveries complete their current batch, then stop polling.
    /// Use <see cref="Resume"/> to restart processing.
    /// </summary>
    void Pause(string consumerGroupId)
        => Pause(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId));

    /// <summary>Pauses one provider's consumer group.</summary>
    /// <param name="key">The provider + group identity.</param>
    void Pause(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Resumes a paused consumer group. Processing continues with the latest settings.
    /// </summary>
    void Resume(string consumerGroupId)
        => Resume(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId));

    /// <summary>Resumes one provider's consumer group.</summary>
    /// <param name="key">The provider + group identity.</param>
    void Resume(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Returns true if the group exists and is currently paused.
    /// Returns false if the group is not registered or is not paused.
    /// </summary>
    bool IsPaused(string consumerGroupId)
        => throw new NotSupportedException();

    /// <summary>Whether one provider's consumer group is paused.</summary>
    /// <param name="key">The provider + group identity.</param>
    bool IsPaused(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Checks whether a consumer group is registered and managed.
    /// </summary>
    bool IsRegistered(string consumerGroupId)
        => throw new NotSupportedException();

    /// <summary>Whether one provider's consumer group is registered.</summary>
    /// <param name="key">The provider + group identity.</param>
    bool IsRegistered(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Unregisters a consumer group — removes settings AND detaches external control.
    /// Does NOT stop the underlying delivery job; use <see cref="Pause"/> for that.
    /// </summary>
    void Unregister(string consumerGroupId)
        => Unregister(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId));

    /// <summary>Unregisters one provider's consumer group.</summary>
    /// <param name="key">The provider + group identity.</param>
    void Unregister(OutboxGroupKey key)
        => throw new NotSupportedException();

    /// <summary>
    /// Returns a snapshot of all registered consumer group IDs. Thread-safe.
    /// </summary>
    IReadOnlyCollection<string> GetAllConsumerGroupIds();

    /// <summary>
    /// Subscribes to settings change notifications for a specific consumer group.
    /// The callback fires AFTER the atomic swap completes.
    /// Return an <see cref="IDisposable"/> to unsubscribe.
    /// </summary>
    IDisposable Subscribe(string consumerGroupId, Action<OutboxConsumerSettings> onChanged)
        => throw new NotSupportedException();

    /// <summary>Subscribes to change notifications of one provider's consumer group.</summary>
    /// <param name="key">The provider + group identity.</param>
    /// <param name="onChanged">The callback.</param>
    /// <returns>A subscription handle.</returns>
    IDisposable Subscribe(OutboxGroupKey key, Action<OutboxConsumerSettings> onChanged)
        => throw new NotSupportedException();
}
