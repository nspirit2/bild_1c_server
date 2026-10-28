using System.Security.Cryptography;
using System.Text;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

/// <summary>
/// Pure inspection of a retirement plan's shape and deterministic artifact identity.
/// Passing validation is not cleanup authority, ownership proof, or evidence that bytes may be deleted.
/// </summary>
public static class RetirementPlanValidation
{
    public static void Validate(StageRetirementPlan plan)
    {
        if (plan is null || plan.RetirementId == Guid.Empty || plan.EnvironmentId == Guid.Empty ||
            plan.JobId == Guid.Empty || plan.ArtifactId == Guid.Empty || plan.ReservationId == Guid.Empty ||
            plan.ArchiveVersionId == Guid.Empty || plan.FencingToken <= 0 || plan.SizeBytes <= 0 ||
            plan.Format is not ("dt" or "bak") || plan.Sha256 is null || plan.Sha256.Length != 64 ||
            !plan.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            throw new ArgumentException("Invalid retirement plan.", nameof(plan));

        // Same archive-stage-v1 namespace and UUID-v8 big-endian layout as ArchiveStaging.
        // No filesystem, lease settlement, or cleanup operation takes place here.
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"archive-stage-v1:{plan.ReservationId:N}:{plan.ArchiveVersionId:N}"));
        identity[6] = (byte)((identity[6] & 0x0f) | 0x80);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        if (plan.ArtifactId != new Guid(identity.AsSpan(0, 16), bigEndian: true))
            throw new ArgumentException("Retirement artifact identity mismatch.", nameof(plan));
    }
}
// end
