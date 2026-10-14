namespace Sa.Data.TempFolderTests;

/// <summary>
/// A clock that moves only when a test moves it — hand-written (as in Sa.Utils.WorkQueue.Tests)
/// rather than pulled from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the debounce tests
/// need virtual time and one-shot timers and nothing else.
/// </summary>
/// <remarks>
/// Time here is virtual: <see cref="Advance"/> fires every timer that comes due, outside the lock,
/// so a callback that re-arms itself is picked up by the same <see cref="Advance"/> call.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset baseTime) : TimeProvider
{
    private readonly Lock _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    /// <summary>Virtual "now": the base time plus everything a test has advanced.</summary>
    public override DateTimeOffset GetUtcNow() => baseTime + _elapsed;

    /// <summary>
    /// Local time is pinned to UTC, so date-based naming tests assert against an exact clock
    /// instead of the machine's time zone.
    /// </summary>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long GetTimestamp() => _elapsed.Ticks;

    /// <summary>How many timers are currently armed — lets a test wait instead of guessing.</summary>
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

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, period);

        lock (_sync)
        {
            _timers.Add(timer);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = _elapsed + dueTime;
            }
        }

        return timer;
    }

    /// <summary>Moves the virtual clock forward, firing every timer that comes due.</summary>
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

    private bool TryTakeNextDue(out ManualTimer? due)
    {
        lock (_sync)
        {
            due = null;
            var earliest = TimeSpan.MaxValue;

            foreach (var timer in _timers)
            {
                // Only timers whose due time has actually been reached by this Advance.
                if (timer.DueAt is not { } at || at > _elapsed || at > earliest) continue;
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
