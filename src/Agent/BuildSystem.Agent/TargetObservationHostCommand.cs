using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>One explicitly addressed invocation; never constructs or starts a hosted worker.</summary>
public static class TargetObservationHostCommand
{
    public static async Task<int> RunAsync(Guid requestId, IConfiguration configuration,
        TextWriter output, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty) { await output.WriteLineAsync("UnsupportedAgentCommand"); return 64; }
        try
        {
            ct.ThrowIfCancellationRequested();
            var options = TargetObservationHostOptions.Bind(configuration);
            if (!options.Enabled) { await output.WriteLineAsync("TargetObservationDisabled"); return 4; }
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var directory = configuration["Agent:DataDirectory"];
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
                directory.Any(char.IsControl) || !Directory.Exists(directory)) throw new InvalidOperationException();
            var origin = configuration["Agent:ApiOrigin"];
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var apiOrigin)) throw new InvalidOperationException();
            // Serialize explicit observation invocations sharing this data directory. Other worker/command
            // processes must be stopped by the operator; they do not participate in this exclusive lock.
            using var exclusive = new FileStream(Path.Combine(directory, "observation-command.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var transport = new TargetObservationHttpClient(new()
            { BaseUri = apiOrigin, AgentId = options.AgentId, TimeoutSeconds = options.TimeoutSeconds },
                () => TokenStore.Load(Path.Combine(directory, "agent-token.dpapi")));
            var executor = TargetObservationHostFactory.CreateExecutor(options, directory);
            using var journal = new AgentJournal(directory);
            var coordinator = new TargetObservationCoordinator(journal, transport, executor);
            var delivery = new TargetObservationOutboxDelivery(journal, transport);
            return await TargetObservationCommand.RunAsync(requestId, coordinator, delivery, output, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await output.WriteLineAsync("TargetObservationCancelled"); return 130; }
        catch (Exception)
        { await output.WriteLineAsync("TargetObservationConfigurationOrStorageFailure"); return 5; }
    }
}
// End.
