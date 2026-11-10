using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>Offline metadata only: this command never grants execution authority.</summary>
public static class TargetCreationOneShotReadinessCommand
{
    public static bool IsReadinessCommand(string[] args) =>
        args is { Length: 1 } && args[0] == "check-target-creation-readiness";

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter output)
    {
        var report = TargetCreationOneShotReadiness.Evaluate(configuration);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch (Exception) { return 5; } // Never retry output or disclose diagnostics.
        return report.Status switch
        {
            "DisabledNotAssessed" => 3,
            "InvalidConfiguration" => 4,
            "StaticChecksPassedRequiresOperationalVerification" => 2,
            _ => 4 // Fail closed; static checks never mean ready for execution.
        };
    }

    public static async Task<int> WriteConfigurationFailureAsync(TextWriter output) =>
        await RunAsync(null!, output);
}
