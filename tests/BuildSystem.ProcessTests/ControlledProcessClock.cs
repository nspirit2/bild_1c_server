namespace BuildSystem.ProcessTests;

// Test-only clock: real time is reserved for hang watchdogs, not deadline ordering.
internal sealed class ControlledProcessClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    private readonly List<ControlledTimer> timers = [];

    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (gate)
        {
            var timer = new ControlledTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (gate)
        {
            now += delta;
            foreach (var timer in timers)
            {
                if (timer.Due is not { } due || due > now) continue;
                // Disarm a one-shot before invoking it: CTS may dispose it in its callback.
                timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : null;
                callbacks.Add((timer.Callback, timer.State));
            }
        }
        foreach (var callback in callbacks) callback.Callback(callback.State);
    }

    private sealed class ControlledTimer(ControlledProcessClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period));
            lock (owner.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                Due = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
