namespace Sa.Outbox.Delivery;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Thread-safe manager for runtime control of outbox consumer group settings.
/// Uses atomic immutable snapshots — no mutation during active delivery, no race conditions.
/// </summary>
/// <remarks>
/// Settings are keyed by <see cref="OutboxGroupKey"/> (provider + group), so two providers may use
/// the same group id. The legacy string overloads address the default provider and keep working
/// while the id is unambiguous.
/// </remarks>
internal sealed partial class OutboxConsumerManager : IOutboxConsumerManager
{
    private readonly Dictionary<OutboxGroupKey, OutboxConsumerSettings> _settings = [];
    private readonly Dictionary<OutboxGroupKey, List<Action<OutboxConsumerSettings>>> _listeners = [];
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
    /// the stage-1 snapshot, swapped in via <see cref="Apply(OutboxGroupKey, Func{OutboxConsumerSettings, OutboxConsumerSettings})"/> —
    /// which also runs the final <see cref="OutboxConsumerSettings.ThrowIfInvalid()"/> validation
    /// over the fully-composed settings.
    /// </para>
    /// <para>
    /// A group that is already known (the same provider + group id registered twice) is left as-is
    /// — the first registration wins, exactly as <see cref="TryRegister(OutboxGroupKey, OutboxConsumerSettings)"/>
    /// guarantees elsewhere. The same group id under a different provider is a separate group.
    /// </para>
    /// </summary>
    internal OutboxConsumerManager(
        IEnumerable<DeliveryGroupSettingsRegistration> registrations,
        IServiceProvider sp,
        ILogger<OutboxConsumerManager>? logger = null) : this(logger)
    {
        foreach (var registration in registrations)
        {
            var key = new OutboxGroupKey(registration.ProviderKey, registration.ConsumerGroupId);

            if (!TryRegister(key, registration.RegistrationTimeSettings))
                continue;

            OutboxConsumerSettings full = registration.BuildFull(sp);
            if (full != registration.RegistrationTimeSettings)
                Apply(key, _ => full);
        }
    }

    /// <inheritdoc/>
    public bool TryRegister(string consumerGroupId, OutboxConsumerSettings settings)
    {
        EnsureGroupId(consumerGroupId);
        return TryRegister(new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId), settings);
    }

    /// <inheritdoc/>
    public bool TryRegister(OutboxGroupKey key, OutboxConsumerSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ProviderKey);
        ArgumentNullException.ThrowIfNull(settings);

        settings.ThrowIfInvalid();

        lock (_lock)
        {
            if (_settings.ContainsKey(key))
                return false;

            _settings[key] = settings;

            if (!_listeners.ContainsKey(key))
            {
                _listeners[key] = [];
            }
        }

        // Notify subscribers OUTSIDE the lock to avoid deadlocks
        NotifyListeners(key, settings);
        return true;
    }

    /// <summary>
    /// How many times <see cref="Apply(OutboxGroupKey, Func{OutboxConsumerSettings, OutboxConsumerSettings})"/>
    /// re-runs a transform that lost a race. Contention is bounded by a handful of writers in
    /// practice; the cap turns a pathological livelock into a loud failure instead of a call that
    /// never returns.
    /// </summary>
    private const int MaxApplyAttempts = 64;

    /// <inheritdoc/>
    public void Apply(string consumerGroupId, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
    {
        EnsureGroupId(consumerGroupId);
        ArgumentNullException.ThrowIfNull(transform);

        Apply(ResolveUnique(consumerGroupId), transform);
    }

    /// <inheritdoc/>
    public void Apply(OutboxGroupKey key, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ProviderKey);
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
                    $"Could not apply settings for consumer group '{key}': " +
                    $"{MaxApplyAttempts} consecutive updates landed while the transform was running.");
            }

            OutboxConsumerSettings existing;

            lock (_lock)
            {
                if (!_settings.TryGetValue(key, out existing!))
                {
                    throw new InvalidOperationException(
                        $"Consumer group '{key}' is not registered. Call Register() first.");
                }
            }

            OutboxConsumerSettings newSettings = transform(existing);
            newSettings.ThrowIfInvalid();

            lock (_lock)
            {
                if (!_settings.TryGetValue(key, out var current))
                {
                    throw new InvalidOperationException(
                        $"Consumer group '{key}' is not registered. Call Register() first.");
                }

                if (!ReferenceEquals(current, existing))
                    continue;

                _settings[key] = newSettings;
            }

            // Notify subscribers OUTSIDE the lock to avoid deadlocks
            NotifyListeners(key, newSettings);
            return;
        }
    }

    /// <inheritdoc/>
    public OutboxConsumerSettings? Get(string consumerGroupId)
    {
        EnsureGroupId(consumerGroupId);

        lock (_lock)
        {
            OutboxGroupKey? key = ResolveOrNull(consumerGroupId);
            if (key is not { } resolved) return null;

            return _settings.TryGetValue(resolved, out var settings) ? settings : null;
        }
    }

    /// <inheritdoc/>
    public OutboxConsumerSettings? Get(OutboxGroupKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);

        lock (_lock)
        {
            return _settings.TryGetValue(key, out var settings) ? settings : null;
        }
    }

    /// <inheritdoc/>
    public bool IsRegistered(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId)) return false;

        lock (_lock)
        {
            OutboxGroupKey? key = ResolveOrNull(consumerGroupId);
            return key is { } resolved && _settings.ContainsKey(resolved);
        }
    }

    /// <inheritdoc/>
    public bool IsRegistered(OutboxGroupKey key)
    {
        if (string.IsNullOrWhiteSpace(key.GroupId)) return false;

        lock (_lock)
        {
            return _settings.ContainsKey(key);
        }
    }

    /// <inheritdoc/>
    public bool IsPaused(string consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId)) return false;

        lock (_lock)
        {
            OutboxGroupKey? key = ResolveOrNull(consumerGroupId);
            return key is { } resolved && _settings.TryGetValue(resolved, out var settings) && settings.Paused;
        }
    }

    /// <inheritdoc/>
    public bool IsPaused(OutboxGroupKey key)
    {
        if (string.IsNullOrWhiteSpace(key.GroupId)) return false;

        lock (_lock)
        {
            return _settings.TryGetValue(key, out var settings) && settings.Paused;
        }
    }

    /// <inheritdoc/>
    public void Pause(string consumerGroupId)
    {
        EnsureGroupId(consumerGroupId);
        Pause(ResolveUnique(consumerGroupId));
    }

    /// <inheritdoc/>
    public void Pause(OutboxGroupKey key)
        => Apply(key, s => s with { Paused = true });

    /// <inheritdoc/>
    public void Resume(string consumerGroupId)
    {
        EnsureGroupId(consumerGroupId);
        Resume(ResolveUnique(consumerGroupId));
    }

    /// <inheritdoc/>
    public void Resume(OutboxGroupKey key)
        => Apply(key, s => s with { Paused = false });

    /// <inheritdoc/>
    public void Unregister(string consumerGroupId)
    {
        EnsureGroupId(consumerGroupId);

        OutboxGroupKey key;

        lock (_lock)
        {
            // Unregistering an unknown group has always been a no-op; with composite keys there is
            // nothing to remove, and the default provider is the only sensible guess.
            key = ResolveOrNull(consumerGroupId) ?? new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId);
        }

        Unregister(key);
    }

    /// <inheritdoc/>
    public void Unregister(OutboxGroupKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);

        lock (_lock)
        {
            _settings.Remove(key);
            _listeners.Remove(key);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> GetAllConsumerGroupIds()
    {
        lock (_lock)
        {
            // Must be a copy: returning the live collection would let the caller observe concurrent
            // Register/Unregister mutations (and throw on enumeration) long after the lock has been
            // released. Partition names are keyed by the plain group id, so duplicates across
            // providers collapse here on purpose.
            return _settings.Keys
                .Select(static key => key.GroupId)
                .Distinct(StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(string consumerGroupId, Action<OutboxConsumerSettings> onChanged)
    {
        EnsureGroupId(consumerGroupId);
        ArgumentNullException.ThrowIfNull(onChanged);

        OutboxGroupKey key;

        lock (_lock)
        {
            // Subscribing before the group is registered has always been allowed (the listener
            // fires once the group appears). With composite keys an unknown id maps to the default
            // provider, which is what the legacy API addresses.
            key = ResolveOrNull(consumerGroupId) ?? new OutboxGroupKey(OutboxScope.DefaultKey, consumerGroupId);
        }

        return Subscribe(key, onChanged);
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(OutboxGroupKey key, Action<OutboxConsumerSettings> onChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.GroupId);
        ArgumentNullException.ThrowIfNull(onChanged);

        var subscription = new Subscription(this, key, onChanged);

        lock (_lock)
        {
            if (!_listeners.TryGetValue(key, out var list))
            {
                _listeners[key] = list = [];
            }

            list.Add(onChanged);
        }

        return subscription;
    }

    internal void NotifyListeners(OutboxGroupKey key, OutboxConsumerSettings newSettings)
    {
        Action<OutboxConsumerSettings>[] listeners;

        lock (_lock)
        {
            // Snapshot. The list is mutated by Subscribe/Dispose, and firing outside the lock
            // used to enumerate that live list — InvalidOperationException, or a skipped callback.
            if (!_listeners.TryGetValue(key, out var list) || list.Count == 0)
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
                LogListenerError(_logger, ex, key.ToString());
            }
        }
    }

    /// <summary>
    /// The legacy guard: a plain <see cref="ArgumentException"/> for null, empty and whitespace,
    /// which is what the string API always threw.
    /// </summary>
    private static void EnsureGroupId(string? consumerGroupId)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));
    }

    /// <summary>
    /// Resolves the single key carrying <paramref name="consumerGroupId"/>. The caller must hold
    /// <see cref="_lock"/>. Throws when two providers use the same id: the legacy string API cannot
    /// pick one, and silently picking the first would apply the change to the wrong storage.
    /// </summary>
    private OutboxGroupKey ResolveUnique(string consumerGroupId)
    {
        lock (_lock)
        {
            return ResolveOrNull(consumerGroupId) ?? throw new InvalidOperationException(
                $"Consumer group '{consumerGroupId}' is not registered.");
        }
    }

    /// <summary>
    /// Finds the key for a group id, or <see langword="null"/> when none is registered. The caller
    /// must hold <see cref="_lock"/>. Throws when the id is ambiguous across providers.
    /// </summary>
    private OutboxGroupKey? ResolveOrNull(string consumerGroupId)
    {
        lock (_lock)
        {
            OutboxGroupKey? found = null;

            foreach (OutboxGroupKey key in _settings.Keys)
            {
                if (!string.Equals(key.GroupId, consumerGroupId, StringComparison.Ordinal)) continue;

                if (found is { } existing)
                {
                    throw new InvalidOperationException(
                        $"Consumer group id '{consumerGroupId}' is registered by providers " +
                        $"'{existing.ProviderKey}' and '{key.ProviderKey}'. Use the OutboxGroupKey " +
                        "overload of IOutboxConsumerManager to address one of them.");
                }

                found = key;
            }

            return found;
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
        private readonly OutboxGroupKey _key;
        private readonly Action<OutboxConsumerSettings> _callback;
        private bool _disposed;

        internal Subscription(OutboxConsumerManager manager, OutboxGroupKey key, Action<OutboxConsumerSettings> callback)
        {
            _manager = manager;
            _key = key;
            _callback = callback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_manager._lock)
            {
                if (_manager._listeners.TryGetValue(_key, out var list))
                {
                    list.Remove(_callback);
                }
            }
        }
    }
}
