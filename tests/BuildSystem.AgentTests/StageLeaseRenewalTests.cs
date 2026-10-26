using System.Net;
using System.Security.Cryptography;
using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageLeaseRenewalTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PeriodicAcceptedHeartbeatAllowsCopyBeyondInitialDeadline()
    {
        using var f = new Fixture();
        var clock = new ControlledClock();
        var initialDeadline = clock.GetUtcNow().AddSeconds(1);
        var copying = Signal(); var releaseCopy = Signal(); var renewed = Signal(); var calls = 0;
        var coordinator = new StageCoordinator(f.Journal, async (request, ct) =>
        {
            copying.TrySetResult();
            await releaseCopy.Task.WaitAsync(Timeout, ct);
            return await f.Staging.StageAsync(request, ct);
        }, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call > 1) renewed.TrySetResult();
            return Task.FromResult(new LeaseStatus(call == 1 ? initialDeadline : clock.GetUtcNow().AddMinutes(5)));
        }, clock, Interval);

        var execution = coordinator.ExecuteAsync(f.Intent, default);
        try
        {
            await copying.Task.WaitAsync(Timeout);
            await clock.TickArmed.Task.WaitAsync(Timeout);
            clock.Advance(Interval);
            await renewed.Task.WaitAsync(Timeout);
            await clock.DeadlineExtended.Task.WaitAsync(Timeout); // Wait for persisted renewal and timer reset.
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(clock.GetUtcNow() > initialDeadline);
        }
        finally { releaseCopy.TrySetResult(); }
        Assert.Equal(StageOutcome.Enqueued, await execution.WaitAsync(Timeout));
        Assert.True(calls >= 3); // Initial, at least one renewal, final guard.
        Assert.Single(f.Journal.PendingArtifacts());
        Assert.Single(f.Journal.Unsettled());
        Assert.Null(f.Failure);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("regressed")]
    [InlineData("malformed")]
    [InlineData("io")]
    public async Task PeriodicRejectionCancelsAndObservesCopyBeforeReturning(string rejection)
    {
        using var f = new Fixture();
        var entered = Signal(); var cleaned = Signal(); var calls = 0;
        var coordinator = f.Coordinator(async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            finally { cleaned.TrySetResult(); }
            throw new InvalidOperationException("Cancelled copy unexpectedly continued.");
        }, async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5));
            await entered.Task.WaitAsync(Timeout);
            if (rejection == "http") throw new HttpRequestException("not persisted", null, HttpStatusCode.Conflict);
            if (rejection == "timeout") throw new OperationCanceledException();
            if (rejection == "malformed") throw new InvalidOperationException("Malformed guard response.");
            if (rejection == "io") throw new IOException("Broken response stream.");
            return new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(rejection == "regressed" ? 2 : -1));
        });

        Assert.Equal(StageOutcome.Blocked, await coordinator.ExecuteAsync(f.Intent, default).WaitAsync(Timeout));
        Assert.True(cleaned.Task.IsCompletedSuccessfully);
        Assert.Empty(f.Journal.PendingArtifacts());
        Assert.Equal("LeaseRejected", f.Failure);
        var completedCalls = calls;
        await Task.Delay(Interval * 3);
        Assert.Equal(completedCalls, calls);
    }

    [Fact]
    public async Task StalledPeriodicHeartbeatCannotReviveExpiredAuthority()
    {
        using var f = new Fixture();
        var guardEntered = Signal(); var copyCleaned = Signal(); var calls = 0;
        var releaseGuard = new TaskCompletionSource<LeaseStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = f.Coordinator(async (_, ct) =>
        {
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            finally { copyCleaned.TrySetResult(); }
            throw new InvalidOperationException();
        }, (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(1)));
            guardEntered.TrySetResult();
            return releaseGuard.Task; // Simulate a guard ignoring cancellation until its response arrives.
        });

        var execution = coordinator.ExecuteAsync(f.Intent, default);
        try
        {
            await guardEntered.Task.WaitAsync(Timeout);
            await copyCleaned.Task.WaitAsync(Timeout); // Expiry must cancel copy even while HTTP is stalled.
        }
        finally { releaseGuard.TrySetResult(new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5))); }
        Assert.Equal(StageOutcome.Blocked, await execution.WaitAsync(Timeout));
        Assert.Empty(f.Journal.PendingArtifacts());
        Assert.Equal("LeaseExpired", f.Failure);
    }

    [Fact]
    public async Task CallerCancellationWaitsForCopyCleanupAndStopsRenewal()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var cleaned = Signal(); var renewal = Signal(); var calls = 0;
        var coordinator = f.Coordinator(async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            finally { cleaned.TrySetResult(); }
            throw new InvalidOperationException();
        }, (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) renewal.TrySetResult();
            return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5)));
        });
        var execution = coordinator.ExecuteAsync(f.Intent, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        await renewal.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        Assert.Equal(StageOutcome.Cancelled, await execution.WaitAsync(Timeout));
        Assert.True(cleaned.Task.IsCompletedSuccessfully);
        Assert.Empty(f.Journal.PendingArtifacts());
        Assert.Equal("Cancelled", f.Failure);
        var completedCalls = calls;
        await Task.Delay(Interval * 3);
        Assert.Equal(completedCalls, calls);
    }

    [Fact]
    public async Task StageFaultStopsRenewalWithoutReceiptOrFinalGuard()
    {
        using var f = new Fixture(); var calls = 0; var renewal = Signal();
        var coordinator = f.Coordinator(async (_, ct) =>
        {
            await renewal.Task.WaitAsync(Timeout, ct);
            throw new ArchiveException(ArchiveFailure.ArchiveChanged, "not persisted");
        }, (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) renewal.TrySetResult();
            return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5)));
        });
        Assert.Equal(StageOutcome.Blocked, await coordinator.ExecuteAsync(f.Intent, default).WaitAsync(Timeout));
        Assert.Equal("ArchiveChanged", f.Failure);
        Assert.Empty(f.Journal.PendingArtifacts());
        var completedCalls = calls;
        await Task.Delay(Interval * 3);
        Assert.Equal(completedCalls, calls);
    }

    [Fact]
    public async Task FinalGuardRejectionLeavesRealPublishedArtifactWithoutReceipt()
    {
        using var f = new Fixture(); var calls = 0;
        var prepared = await f.Staging.StageAsync(f.Request());
        var coordinator = new StageCoordinator(f.Journal, (_, _) => Task.FromResult(prepared), (_, _) =>
            Task.FromResult(new LeaseStatus(Interlocked.Increment(ref calls) == 1
                ? DateTimeOffset.UtcNow.AddSeconds(5) : DateTimeOffset.UtcNow.AddSeconds(-1))),
            TimeProvider.System, TimeSpan.FromSeconds(30));
        Assert.Equal(StageOutcome.Blocked, await coordinator.ExecuteAsync(f.Intent, default).WaitAsync(Timeout));
        Assert.Equal(2, calls);
        Assert.Equal("LeaseRejected", f.Failure);
        Assert.Empty(f.Journal.PendingArtifacts());
        Assert.Single(Directory.GetDirectories(f.StagingDirectory));
    }

    [Fact]
    public async Task QuickCopyUsesOnlyInitialAndFinalGuards()
    {
        using var f = new Fixture(); var calls = 0;
        var prepared = await f.Staging.StageAsync(f.Request());
        var coordinator = new StageCoordinator(f.Journal, (_, _) => Task.FromResult(prepared), (_, _) =>
        {
            Assert.NotNull(f.Journal.GetStageIntent(f.Intent.JobId, f.Intent.FencingToken));
            calls++;
            return Task.FromResult(new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5)));
        }, TimeProvider.System, TimeSpan.FromSeconds(30));
        Assert.Equal(StageOutcome.Enqueued, await coordinator.ExecuteAsync(f.Intent, default).WaitAsync(Timeout));
        Assert.Equal(2, calls);
        Assert.Single(f.Journal.PendingArtifacts());
    }

    [Fact]
    public async Task GuardCallbacksNeverOverlapAndStopBeforeSuccessfulReturn()
    {
        using var f = new Fixture(); var calls = 0; var active = 0; var releaseCopy = Signal();
        var coordinator = f.Coordinator(async (request, ct) =>
        {
            await releaseCopy.Task.WaitAsync(Timeout, ct);
            return await f.Staging.StageAsync(request, ct);
        }, async (_, ct) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                Assert.Single(f.Journal.Unsettled());
                if (Interlocked.Increment(ref calls) >= 3) releaseCopy.TrySetResult();
                await Task.Delay(Interval, ct);
                return new LeaseStatus(DateTimeOffset.UtcNow.AddSeconds(5));
            }
            finally { Interlocked.Decrement(ref active); }
        });
        Assert.Equal(StageOutcome.Enqueued, await coordinator.ExecuteAsync(f.Intent, default).WaitAsync(Timeout));
        Assert.Equal(0, active);
        var completedCalls = calls;
        await Task.Delay(Interval * 3);
        Assert.Equal(completedCalls, calls);
        Assert.Single(f.Journal.PendingArtifacts());
    }

    // This test clock drives Task.Delay and CTS timers together; wall time is only a hang watchdog.
    private sealed class ControlledClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<ControlledTimer> timers = [];
        public TaskCompletionSource DeadlineExtended { get; } = Signal();
        public TaskCompletionSource TickArmed { get; } = Signal();
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new ControlledTimer(this, callback, state);
                timers.Add(timer);
                timer.Set(dueTime, period);
                if (dueTime == Interval) TickArmed.TrySetResult();
                return timer;
            }
        }
        public void Advance(TimeSpan delta)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (gate)
            {
                now += delta;
                foreach (var timer in timers)
                {
                    if (timer.Disposed || timer.Due is not { } due || due > now) continue;
                    callbacks.Add((timer.Callback, timer.State));
                    timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : null;
                }
            }
            foreach (var callback in callbacks) callback.Callback(callback.State);
        }
        private sealed class ControlledTimer(ControlledClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset? Due;
            public TimeSpan Period;
            public bool Disposed;
            public void Set(TimeSpan due, TimeSpan period)
            {
                Due = due == global::System.Threading.Timeout.InfiniteTimeSpan ? null : owner.now + due;
                Period = period;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Set(dueTime, period);
                    if (dueTime > TimeSpan.FromMinutes(1)) owner.DeadlineExtended.TrySetResult();
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) { Disposed = true; Due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stage-renewal-tests", Guid.NewGuid().ToString("N"));
        public AgentJournal Journal { get; }
        public ArchiveStaging Staging { get; }
        public string StagingDirectory { get; }
        public StageIntent Intent { get; }
        public string? Failure => Journal.StageIntentFailure(Intent.JobId, Intent.FencingToken);
        public Fixture()
        {
            var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(Path.Combine(source, "archive.dt"), bytes);
            var root = Guid.NewGuid();
            StagingDirectory = Path.Combine(directory, "stage");
            Staging = new ArchiveStaging([new(root, source, true)], StagingDirectory, 100000, 1000000);
            Journal = new AgentJournal(Path.Combine(directory, "journal"));
            Intent = new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), root, "archive.dt",
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "dt");
            Journal.StoreGrant(new(Intent.JobId, Intent.FencingToken, DateTimeOffset.UtcNow.AddMinutes(1), "ArchiveStageOnly", "{}"));
        }
        public StageRequest Request() => new(Intent.ReservationId, Intent.ArchiveVersionId,
            new(Intent.RootId, Intent.RelativePath, Intent.Sha256, Intent.SizeBytes, ArchiveFormat.Dt));
        public StageCoordinator Coordinator(Func<StageRequest, CancellationToken, Task<VerifiedArtifact>> stage,
            Func<LeaseHeartbeat, CancellationToken, Task<LeaseStatus>> guard) => new(Journal, stage, guard, TimeProvider.System, Interval);
        public void Dispose() { Journal.Dispose(); Directory.Delete(directory, true); }
    }
}
// end
