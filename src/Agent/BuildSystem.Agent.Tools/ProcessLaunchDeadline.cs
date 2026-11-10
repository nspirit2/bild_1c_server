using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildSystem.Agent.Tools;

/// <summary>
/// An optional, in-memory monotonic launch-budget check, not execution authority.
/// A future adapter must supply the pre-claim timestamp and server-derived TTL budget.
/// This finite boundary check is not atomic with process start under arbitrary pauses
/// and is not a physical fence. It neither grants nor renews authorization.
/// </summary>
[JsonConverter(typeof(ProcessLaunchDeadlineJsonConverter))]
public sealed class ProcessLaunchDeadline
{
    private readonly TimeProvider clock;
    private readonly long startedTimestamp;
    private readonly TimeSpan budget;

    public ProcessLaunchDeadline(TimeProvider timeProvider, long startedTimestamp, TimeSpan budget)
    {
        try
        {
            if (timeProvider is null || timeProvider.TimestampFrequency <= 0
                || budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException();
        }
        catch
        {
            throw new ProcessRunnerException("invalid_launch_deadline");
        }

        clock = timeProvider;
        this.startedTimestamp = startedTimestamp;
        this.budget = budget;
    }

    public void DemandNotExpired()
    {
        try
        {
            var elapsed = clock.GetElapsedTime(startedTimestamp);
            if (elapsed < TimeSpan.Zero || elapsed >= budget)
                throw new InvalidOperationException();
        }
        catch
        {
            throw new ProcessRunnerException("launch_deadline_expired");
        }
    }

    public override string ToString() => "ProcessLaunchDeadline [details omitted]";
}

/// <summary>Reject both reading and writing, including null values.</summary>
public sealed class ProcessLaunchDeadlineJsonConverter : JsonConverter<ProcessLaunchDeadline>
{
    public override bool HandleNull => true;

    public override ProcessLaunchDeadline Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new JsonException("Launch deadline serialization denied.");

    public override void Write(Utf8JsonWriter writer, ProcessLaunchDeadline value,
        JsonSerializerOptions options) => throw new JsonException("Launch deadline serialization denied.");
}
