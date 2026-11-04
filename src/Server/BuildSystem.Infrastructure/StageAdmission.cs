using System.Text.Json;

namespace BuildSystem.Infrastructure;

/// <summary>Classifies staging intent only. Never confers CREATE or restore authority.</summary>
internal static class StageAdmission
{
    internal static bool IsStageJob(EnvironmentJob job)
    {
        if (job.Kind is not ("ProvisionArchive" or "ObserveArchiveStage")) return false;
        try
        {
            using var document = JsonDocument.Parse(job.SnapshotJson);
            var snapshot = document.RootElement;
            if (snapshot.ValueKind != JsonValueKind.Object) return false;
            // Reject duplicate or alternate-case safety keys rather than choosing a parser's winner.
            var keys = new[] { "admissionMode", "stageOnly", "executable", "scheduledJobsMustRemainBlocked" };
            foreach (var key in keys)
            {
                var properties = snapshot.EnumerateObject().Where(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (properties.Length > 1 || properties.Length == 1 && properties[0].Name != key) return false;
            }
            // Preserve historical snapshots only when the entire new discriminator is absent.
            if (job.Kind == "ProvisionArchive" && keys.All(key => !snapshot.TryGetProperty(key, out _))) return true;
            return snapshot.GetProperty("admissionMode").GetString() ==
                    (job.Kind == "ObserveArchiveStage" ? "archive-stage-only" : "archive") &&
                snapshot.GetProperty("stageOnly").ValueKind ==
                    (job.Kind == "ObserveArchiveStage" ? JsonValueKind.True : JsonValueKind.False) &&
                snapshot.GetProperty("executable").ValueKind == JsonValueKind.False &&
                snapshot.GetProperty("scheduledJobsMustRemainBlocked").ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
// end
