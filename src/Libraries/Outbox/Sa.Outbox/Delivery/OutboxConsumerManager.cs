namespace Sa.Outbox.Delivery;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Thread-safe manager for runtime control of outbox consumer group settings.
/// Uses atomic immutable snapshots — no mutation during active delivery, no race conditions.
/// </summary>
internal sealed partial class OutboxConsumerManager : IOutboxConsumerManager
{
    private readonly Dictionary<string, OutboxConsumerSettings> _settings = [];
    private readonly Dictionary<string, List<Action<OutboxConsumerSettings>>> _listeners = [];
    private readonly Lock _lock = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Creates an empty manager (no delivery groups). Used by unit tests and anywhere a manager
    /// is constructed directly rather than through DI.
    /// </summary>
    internal OutboxConsumerManager(ILogger<OutboxConsumerManager>? logger = null)
    {
        _logger = logger ?? NullLogger<OutboxConsumerManager>.Instance;
    }

    /// <summary>
    /// DI entry point: pre-populates the manager with the per-group registration descriptors
    /// contributed by <c>AddDeliveryJob</c>.
    /// <para>
    /// For each group the stage-1 snapshot (built at registration time from
    /// <see cref="OutboxDefaults"/> and the user <c>configure</c> callback, without the
    /// configuration section) is registered first — so the group is known the moment the manager
    /// is resolved, even before the scheduler starts. The full chain is then built with a live
    /// provider (configuration section included, when a key was given) and, when it differs from
    /// the stage-1 snapshot, swapped in via <see cref="Apply(string, Func{OutboxConsumerSettings,
    /// OutboxConsumerSettings})"/> — which also runs the final <see cref="OutboxConsumerSettings.
    /// ThrowIfInvalid()"/> validation over the fully-composed settings.
    /// </para>
    /// <para>
    /// A group that is already known (e.g. two delivery registrations sharing one group id) is
    /// left as-is — the first registration wins, exactly as <see cref="TryRegister(string,
    /// OutboxConsumerSettings)"/> guarantees elsewhere.
    /// </para>
    /// </summary>
    internal OutboxConsumerManager(
        IEnumerable<DeliveryGroupSettingsRegistration> registrations,
        IServiceProvider sp,
        ILogger<OutboxConsumerManager>? logger = null) : this(logger)
    {
        foreach (var registration in registrations)
        {
            if (!TryRegister(registration.ConsumerGroupId, registration.RegistrationTimeSettings))
                continue;

            OutboxConsumerSettings full = registration.BuildFull(sp);
            if (full != registration.RegistrationTimeSettings)
                Apply(registration.ConsumerGroupId, _ => full);
        }
    }

    /// <inheritdoc/>
    public bool TryRegister(string consumerGroupId, OutboxConsumerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        ArgumentNullException.ThrowIfNull(settings);

        settings.ThrowIfInvalid();

        lock (_lock)
        {
            if (_settings.ContainsKey(consumerGroupId))
                return false;

            _settings[consumerGroupId] = settings;

            if (!_listeners.ContainsKey(consumerGroupId))
            {
                _listeners[consumerGroupId] = [];
            }
        }

        // Notify subscribers OUTSIDE the lock to avoid deadlocks
        NotifyListeners(consumerGroupId, settings);
        return true;
    }

    /// <summary>
    /// How many times <see cref="Apply"/> re-runs a transform that lost a race. Contention is
    /// bounded by a handful of writers in practice; the cap turns a pathological livelock into a
    /// loud failure instead of a call that never returns.
    /// </summary>
    private const int MaxApplyAttempts = 64;

    /// <inheritdoc/>
    public void Apply(string consumerGroupId, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        ArgumentNullException.ThrowIfNull(transform);

        // The transform runs outside the lock. Lock is not reentrant: a callback that called
        // Get/Pause/Apply used to deadlock. Snapshots are immutable, so a lost race is a
        // reference mismatch and the transform is retried against the new snapshot. It must be
        // safe to run more than once.
        for (int attempt = 1; ; attempt++)
        {
            if (attempt > MaxApplyAttempts)
            {
                throw new InvalidOperationException(
                    $"Could not apply settings for consumer group '{consumerGroupId}': " +
                    $"{MaxApplyAttempts} consecutive updates landed while the transform was running.");
            }

            OutboxConsumerSettings existing;

            lock (_lock)
            {
                if (!_settings.TryGetValue(consumerGroupId, out existing!))
                {
                    throw new InvalidOperationException(
                        $"Consumer group '{consumerGroupId}' is not registered. Call Register() first.");
                }
            }

            OutboxConsumerSettings newSettings = transform(existing);
            newSettings.ThrowIfInvalid();

            lock (_lock)
            {
                if (!_settings.TryGetValue(consumerGroupId, out var current))
                {
                    throw new InvalidOperationException(
                        $"Consumer group '{consumerGroupId}' is not registered. Call Register() first.");
                }

                if (!ReferenceEquals(current, existing))
                    continue;

                _settings[consumerGroupId] = newSettings;
            }

            // Notify subscribers OUTSIDE the lock to avoid deadlocks
            NotifyListeners(consumerGroupId, newSettings);
            return;
        }
    }

    /// <inheritdoc/>
    public OutboxConsumerSettings? Get(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        lock (_lock)
        {
            return _settings.TryGetValue(consumerGroupId, out var settings)
                ? settings
                : null;
        }
    }

    /// <inheritdoc/>
    public bool IsRegistered(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId)) return false;

        lock (_lock)
        {
            return _settings.ContainsKey(consumerGroupId);
        }
    }

    /// <inheritdoc/>
    public bool IsPaused(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId)) return false;

        lock (_lock)
        {
            return _settings.TryGetValue(consumerGroupId, out var settings) && settings.Paused;
        }
    }

    /// <inheritdoc/>
    public void Pause(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        Apply(consumerGroupId, s => s with { Paused = true });
    }

    /// <inheritdoc/>
    public void Resume(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        Apply(consumerGroupId, s => s with { Paused = false });
    }

    /// <inheritdoc/>
    public void Unregister(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        lock (_lock)
        {
            _settings.Remove(consumerGroupId);
            _listeners.Remove(consumerGroupId);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> GetAllConsumerGroupIds()
    {
        lock (_lock)
        {
            // Must be a copy: returning the live KeyCollection would let the caller observe
            // concurrent Register/Unregister mutations (and throw on enumeration) long after
            // the lock has been released.
            return _settings.Keys.ToList().AsReadOnly();
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(string consumerGroupId, Action<OutboxConsumerSettings> onChanged)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        ArgumentNullException.ThrowIfNull(onChanged);

        var subscription = new Subscription(this, consumerGroupId, onChanged);

        lock (_lock)
        {
            if (!_listeners.TryGetValue(consumerGroupId, out var list))
            {
                _listeners[consumerGroupId] = list = [];
            }

            list.Add(onChanged);
        }

        return subscription;
    }

    internal void NotifyListeners(string consumerGroupId, OutboxConsumerSettings newSettings)
    {
        Action<OutboxConsumerSettings>[] listeners;

        lock (_lock)
        {
            // Snapshot. The list is mutated by Subscribe/Dispose, and firing outside the lock
            // used to enumerate that live list — InvalidOperationException, or a skipped callback.
            if (!_listeners.TryGetValue(consumerGroupId, out var list) || list.Count == 0)
                return;

            listeners = list.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(newSettings);
            }
            catch (Exception ex)
            {
                // Debug.WriteLine is compiled out of Release, so a throwing listener used to vanish.
                LogListenerError(_logger, ex, consumerGroupId);
            }
        }
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Listener error for consumer group '{ConsumerGroupId}'")]
    private static partial void LogListenerError(ILogger logger, Exception exception, string consumerGroupId);

    private sealed class Subscription : IDisposable
    {
        private readonly OutboxConsumerManager _manager;
        private readonly string _consumerGroupId;
        private readonly Action<OutboxConsumerSettings> _callback;
        private bool _disposed;

        internal Subscription(OutboxConsumerManager manager, string consumerGroupId, Action<OutboxConsumerSettings> callback)
        {
            _manager = manager;
            _consumerGroupId = consumerGroupId;
            _callback = callback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_manager._lock)
            {
                if (_manager._listeners.TryGetValue(_consumerGroupId, out var list))
                {
                    list.Remove(_callback);
                }
            }
        }
    }
}
