using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace BuildSystem.Agent.Execution;

/// <summary>Review-only pins. This request is not a grant, a local path or ownership evidence.</summary>
public sealed record DtRestoreReviewRequest(Guid EnvironmentId, Guid JobId, long FencingToken,
    Guid ReservationId, Guid ArtifactId, Guid ArchiveVersionId, Guid TargetId,
    Guid ClusterId, Guid SqlServerId, string Sha256, long SizeBytes, string Format,
    string PreparationProfileVersion)
{
    public override string ToString() => "DtRestoreReviewRequest [details omitted]";
}

/// <summary>Operator-owned mapping; never populate from YAML or an untrusted request.</summary>
public sealed record DtRestoreTarget(Guid TargetId, Guid ClusterId, Guid SqlServerId,
    string InfobaseName, string SqlDatabaseName)
{
    public override string ToString() => "DtRestoreTarget [details omitted]";
}

public sealed class DtRestorePlanningException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Required future phases, not accomplished checks or executable step registrations.</summary>
public enum DtRestoreReviewPhase
{
    VerifyArtifact, VerifyFreshAuthority, VerifyOwnedEmptyTarget, VerifyScheduledJobsBlocked,
    RestoreDt, PrepareIsolation, Verify, Publish
}

/// <summary>
/// Inert review descriptor. It has no executable argv, credentials, filesystem path, authority
/// assertion or Ready outcome. Names only identify planned slots, never external ownership.
/// </summary>
public sealed class ReviewOnlyDtRestorePlan
{
    internal ReviewOnlyDtRestorePlan(DtRestoreReviewRequest request, DtRestoreTarget target, string sha256)
        => (Request, Target, RecipeSha256) = (request, target, sha256);
    public bool ReviewOnly => true;
    public string RecipeVersion => "dt-restore-review-v1";
    public DtRestoreReviewRequest Request { get; }
    public DtRestoreTarget Target { get; }
    public string RecipeSha256 { get; }
    public ImmutableArray<DtRestoreReviewPhase> RequiredPhases { get; } =
        [DtRestoreReviewPhase.VerifyArtifact, DtRestoreReviewPhase.VerifyFreshAuthority,
         DtRestoreReviewPhase.VerifyOwnedEmptyTarget, DtRestoreReviewPhase.VerifyScheduledJobsBlocked,
         DtRestoreReviewPhase.RestoreDt, DtRestoreReviewPhase.PrepareIsolation,
         DtRestoreReviewPhase.Verify, DtRestoreReviewPhase.Publish];
    public override string ToString() => "ReviewOnlyDtRestorePlan [not executable; details omitted]";
}

/// <summary>
/// Pure planner for the conservative ASCII MVP target subset. No IO, process execution,
/// registration, resource creation or authorization is performed. All future phases require
/// trusted adapters and durable reconciliation before an executor can be introduced.
/// </summary>
public sealed class DtRestoreReviewPlanner
{
    public const int MaxTargetMappings = 1024;
    private readonly ImmutableDictionary<Guid, DtRestoreTarget> targets;

    public DtRestoreReviewPlanner(IEnumerable<DtRestoreTarget> operatorTargets)
    {
        if (operatorTargets is null) throw Invalid("InvalidTargetMapping");
        var map = ImmutableDictionary.CreateBuilder<Guid, DtRestoreTarget>();
        foreach (var target in operatorTargets)
        {
            if (map.Count >= MaxTargetMappings || target is null || target.TargetId == Guid.Empty || target.ClusterId == Guid.Empty ||
                target.SqlServerId == Guid.Empty || !ValidSlot(target.InfobaseName) ||
                !ValidSlot(target.SqlDatabaseName) || map.ContainsKey(target.TargetId))
                throw Invalid("InvalidTargetMapping");
            map.Add(target.TargetId, target with { });
        }
        if (map.Count == 0) throw Invalid("InvalidTargetMapping");
        targets = map.ToImmutable();
    }

    public ReviewOnlyDtRestorePlan Plan(DtRestoreReviewRequest request)
    {
        if (request is null) throw Invalid("InvalidRestorePins");
        if (new[] { request.EnvironmentId, request.JobId, request.ReservationId, request.ArtifactId,
                request.ArchiveVersionId, request.TargetId, request.ClusterId, request.SqlServerId }.Any(id => id == Guid.Empty) ||
            request.FencingToken <= 0 || request.SizeBytes <= 0 || request.Sha256 is null || request.Sha256.Length != 64 ||
            !request.Sha256.All(Uri.IsHexDigit) || request.PreparationProfileVersion is null ||
            request.PreparationProfileVersion.Length is 0 or > 128 ||
            request.PreparationProfileVersion.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw Invalid("InvalidRestorePins");
        if (!string.Equals(request.Format, "dt", StringComparison.Ordinal)) throw Invalid("UnsupportedRestoreFormat");
        if (!targets.TryGetValue(request.TargetId, out var target)) throw Invalid("RestoreTargetUnavailable");
        if (request.ClusterId != target.ClusterId || request.SqlServerId != target.SqlServerId)
            throw Invalid("RestoreTargetMismatch");
        var slot = "bs_test_" + request.ReservationId.ToString("N");
        if (!string.Equals(target.InfobaseName, slot, StringComparison.Ordinal) ||
            !string.Equals(target.SqlDatabaseName, slot, StringComparison.Ordinal))
            throw Invalid("RestoreTargetMismatch");
        var pinned = request with { Sha256 = request.Sha256.ToUpperInvariant() };
        // Fixed property order and fixed recipe version make every pin participate in identity.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RecipeVersion = "dt-restore-review-v1", pinned.EnvironmentId, pinned.JobId, pinned.FencingToken,
            pinned.ReservationId, pinned.ArtifactId, pinned.ArchiveVersionId, pinned.TargetId,
            pinned.ClusterId, pinned.SqlServerId, pinned.Sha256, pinned.SizeBytes, pinned.Format,
            pinned.PreparationProfileVersion, target.InfobaseName, target.SqlDatabaseName
        });
        return new(pinned, target, Convert.ToHexString(SHA256.HashData(canonical)));
    }

    private static bool ValidSlot(string? value) => value is { Length: 40 } &&
        value.StartsWith("bs_test_", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[8..], "N", out var id) && id != Guid.Empty &&
        value == "bs_test_" + id.ToString("N");

    private static DtRestorePlanningException Invalid(string code) => new(code);
}
