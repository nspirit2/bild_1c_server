# Deterministic complete-response timeout regression

Windows Build41982223176 for a14b14f failed ScheduledJobsObserverProcessTests.RealTimeoutRejectsEvenACompleteBlockedResponse. Ubuntu and SQL succeeded. Public failure annotation identifies the test only; download of TRX returned HTTP401. Exact failed assertion/body is unavailable; do not claim the old CI root cause was proved. Local real-wall-clock reproduction passed.

The original test structurally races child .NET startup plus fragmented output/callback delivery against a two-second process deadline, while requiring the complete blocked response. Killing before startup/output finishes can legitimately produce timeout but cannot satisfy that combined test scenario. Arbitrary sleeps/warm-up/ready files do not make this ordering deterministic.

The fix preserves the original ProcessRunner(options) constructor and production TimeProvider.System deadline placement after process.Start. New overload accepts trusted in-process TimeProvider, not command/pipeline/remote config. CTS timeout uses that provider; no process outcome is fabricated. The controlled-deadline test runs a real child process through the real runner and observer, waits until the exact full stdout has been consumed by the observer callback, then advances the clock to the same two-second deadline. It still requires ProcessCompletion.TimedOut, process PID, exact complete stdout and the same redacted observer rejection. Existing real-wall-clock timeout/kill tests remain unchanged. Wall-clock watchdog and finally-cancellation bound cleanup if barriers fail.

This test proves real-process rejection at a controlled timeout after complete output, not completion of Windows process startup within two wall-clock seconds. No fixture output weakening, accepting partial readbacks, timeout increase, test retry/skip, or cancellation-as-timeout.

No live RAC/SQL/1C execution or deployment in this increment. Scheduled jobs prohibition remains mandatory; security profile requirements are not added.

## Validation

Targeted complete-response/real-wall-timeout/null-clock cases passed; complete-response test passed five additional repeats. Full local Release1241passed/0failed, 166SQLexplicitlyskippedбезopt-in (неSQLvalidation). Process210passed, Agent580, Pipeline158. Scoped diffcheckpassed. New Windows/Linux/SQL CI required after push before closing; no claim that old failure is precisely diagnosed.
