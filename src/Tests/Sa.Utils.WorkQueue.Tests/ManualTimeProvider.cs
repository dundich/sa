namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// A clock that moves only when a test moves it.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c>: the queue needs four timer-backed
/// waits and nothing else, and a 30-second test dependency for that is a poor trade.
/// <para>
/// Time here is virtual, so <see cref="Advance"/> can cross the default
/// <c>ShutdownTimeout</c> in microseconds. Firing happens outside the lock, and the
/// clock is re-checked after every callback so a periodic timer that re-arms itself is
/// picked up by the same <see cref="Advance"/> call.
/// </para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    /// <summary>Virtual time since this provider was created.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync) return _elapsed;
        }
    }

    /// <summary>
    /// How many timers are currently due to fire. Lets a test wait until the code under
    /// test has actually parked on a wait, instead of guessing with a sleep — otherwise
    /// the advance could land before the timer exists and be lost.
    /// </summary>
    public int ArmedCount
    {
        get
        {
            lock (_sync)
            {
                var count = 0;
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is not null) count++;
                }

                return count;
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync) return _elapsed.Ticks;
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;

    /// <summary>How many timers have ever been created on this provider.</summary>
    public int CreatedCount { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, period);

        lock (_sync)
        {
            CreatedCount++;
            _timers.Add(timer);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = _elapsed + dueTime;
            }
        }

        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that comes due.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "Cannot move the clock backwards.");
        }

        lock (_sync)
        {
            _elapsed += by;
        }

        while (TryTakeNextDue(out var due))
        {
            due!.Fire();
        }
    }

    /// <summary>
    /// Claims the earliest armed timer, disarming it before the callback runs so a
    /// re-arm from inside the callback is visible as a fresh due time.
    /// </summary>
    private bool TryTakeNextDue(out ManualTimer? due)
    {
        lock (_sync)
        {
            due = null;
            var earliest = TimeSpan.MaxValue;

            foreach (var timer in _timers)
            {
                if (timer.DueAt is not { } at || at > earliest) continue;
                earliest = at;
                due = timer;
            }

            if (due is null) return false;

            due.DueAt = null;
            return true;
        }
    }

    private void Arm(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_sync)
        {
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _elapsed + dueTime;
        }
    }

    private void Forget(ManualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
            timer.DueAt = null;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
        }

        /// <summary>Virtual time this timer is due, or <see langword="null"/> when disarmed.</summary>
        public TimeSpan? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;

            _period = period;
            _owner.Arm(this, dueTime);
            return true;
        }

        public void Fire()
        {
            if (_disposed) return;

            _callback(_state);

            // Re-arm only if the callback left us disarmed: a Change() from inside the
            // callback has already set the next due time, and must not be doubled.
            if (!_disposed && DueAt is null && _period != Timeout.InfiniteTimeSpan)
            {
                _owner.Arm(this, _period);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
