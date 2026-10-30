namespace Sa.Outbox.Delivery;

/// <summary>
/// Per-consumer-group idle state that feeds the adaptive poll schedule.
/// <para>
/// One instance lives for the lifetime of a consumer group (see
/// <see cref="IOutboxIdleStateRegistry"/>). <see cref="DeliveryJob{TMessage}"/> reports the
/// outcome of every run here, and <see cref="AdaptiveIdleTiming"/> reads
/// <see cref="CurrentInterval"/> to decide when the next run should happen.
/// </para>
/// </summary>
internal sealed class OutboxIdleState
{
    private readonly Lock _lock = new();
    private readonly IEmptyQueueWaitStrategy _strategy;
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxInterval;

    private int _consecutiveEmptyPolls;
    private TimeSpan _currentInterval;

    public OutboxIdleState(IEmptyQueueWaitStrategy strategy, TimeSpan baseInterval, TimeSpan maxInterval)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        _strategy = strategy;
        _baseInterval = baseInterval;
        _maxInterval = maxInterval;
        _currentInterval = baseInterval;
    }

    /// <summary>The delay the next poll should use. Starts at the base interval.</summary>
    public TimeSpan CurrentInterval
    {
        get
        {
            lock (_lock) return _currentInterval;
        }
    }

    /// <summary>Number of consecutive polls that found nothing. Exposed for diagnostics/tests.</summary>
    public int ConsecutiveEmptyPolls
    {
        get
        {
            lock (_lock) return _consecutiveEmptyPolls;
        }
    }

    /// <summary>
    /// Reports the number of messages a completed run handled. Any work resets the back-off to
    /// the base interval; an empty run advances it one step.
    /// </summary>
    public void Report(long processed)
    {
        lock (_lock)
        {
            if (processed > 0)
            {
                _consecutiveEmptyPolls = 0;
                _currentInterval = _baseInterval;
                return;
            }

            _consecutiveEmptyPolls++;
            _currentInterval = _strategy.GetNextWait(_baseInterval, _maxInterval, _consecutiveEmptyPolls);
        }
    }

    /// <summary>Resets the back-off to the base interval (e.g. on resume from pause).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _consecutiveEmptyPolls = 0;
            _currentInterval = _baseInterval;
        }
    }
}
