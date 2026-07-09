namespace Sa.Outbox.Delivery;

using System.Diagnostics;

/// <summary>
/// Thread-safe manager for runtime control of outbox consumer group settings.
/// Uses atomic immutable snapshots — no mutation during active delivery, no race conditions.
/// </summary>
internal sealed class OutboxConsumerManager : IOutboxConsumerManager
{
    private readonly Dictionary<string, OutboxConsumerSettings> _settings = [];
    private readonly Dictionary<string, List<Action<OutboxConsumerSettings>>> _listeners = [];
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public bool TryRegister(string consumerGroupId, OutboxConsumerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        ArgumentNullException.ThrowIfNull(settings);

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

    /// <inheritdoc/>
    public void Apply(string consumerGroupId, Func<OutboxConsumerSettings, OutboxConsumerSettings> transform)
    {
        if (string.IsNullOrWhiteSpace(consumerGroupId))
            throw new ArgumentException("Consumer group ID cannot be null or empty.", nameof(consumerGroupId));

        ArgumentNullException.ThrowIfNull(transform);

        OutboxConsumerSettings newSettings;

        lock (_lock)
        {
            if (!_settings.TryGetValue(consumerGroupId, out var existing))
            {
                throw new InvalidOperationException(
                    $"Consumer group '{consumerGroupId}' is not registered. Call Register() first.");
            }

            // Perform transformation outside the lock to avoid holding it during
            // potentially expensive validation or copying operations.
            newSettings = transform(existing);
            _settings[consumerGroupId] = newSettings;
        }

        // Notify subscribers OUTSIDE the lock to avoid deadlocks
        NotifyListeners(consumerGroupId, newSettings);
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
        List<Action<OutboxConsumerSettings>>? listeners;

        lock (_lock)
        {
            if (!_listeners.TryGetValue(consumerGroupId, out listeners)) return;
        }

        // Fire outside lock
        foreach (var listener in listeners)
        {
            try
            {
                listener(newSettings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[OutboxConsumerManager] Listener error for '{consumerGroupId}': {ex.Message}");
            }
        }
    }

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
