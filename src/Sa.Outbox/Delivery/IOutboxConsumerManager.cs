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
    /// <param name="transform">A function that receives the current snapshot and returns the updated one. Use <c>this with { ... }</c> expressions.</param>
    void Apply(string consumerGroupId, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform);

    /// <summary>
    /// Registers a consumer group with initial settings.
    /// Unlike <see cref="Apply"/>, this does not require prior registration.
    /// </summary>
    /// <param name="consumerGroupId">The consumer group identifier.</param>
    /// <param name="settings">The initial immutable settings snapshot.</param>
    void Register(string consumerGroupId, OutboxConsumerSettings settings);

    /// <summary>
    /// Retrieves the current immutable settings snapshot. Thread-safe.
    /// </summary>
    OutboxConsumerSettings? Get(string consumerGroupId);

    /// <summary>
    /// Temporarily pauses a consumer group without losing any settings.
    /// Active deliveries complete their current batch, then stop polling.
    /// Use <see cref="Resume"/> to restart processing.
    /// </summary>
    void Pause(string consumerGroupId);

    /// <summary>
    /// Resumes a paused consumer group. Processing continues with the latest settings.
    /// </summary>
    void Resume(string consumerGroupId);

    /// <summary>
    /// Returns true if the group exists and is currently paused.
    /// Returns false if the group is not registered or is not paused.
    /// </summary>
    bool IsPaused(string consumerGroupId);

    /// <summary>
    /// Checks whether a consumer group is registered and managed.
    /// </summary>
    bool IsRegistered(string consumerGroupId);

    /// <summary>
    /// Unregisters a consumer group — removes settings AND detaches external control.
    /// Does NOT stop the underlying delivery job; use <see cref="Pause"/> for that.
    /// </summary>
    void Unregister(string consumerGroupId);

    /// <summary>
    /// Returns a snapshot of all registered consumer group IDs. Thread-safe.
    /// </summary>
    IReadOnlyCollection<string> GetAllConsumerGroupIds();

    /// <summary>
    /// Subscribes to settings change notifications for a specific consumer group.
    /// The callback fires AFTER the atomic swap completes.
    /// Return an <see cref="IDisposable"/> to unsubscribe.
    /// </summary>
    IDisposable Subscribe(string consumerGroupId, Action<OutboxConsumerSettings> onChanged);
}
