using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Independent operator opt-in; enabling is neither a grant nor worker registration.</summary>
public sealed class TargetCreationDeadlineInvocationAdapterOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationDeadlineInvocationAdapterOptions [details omitted]";
}

/// <summary>Fixed-code failure without credentials, invocation diagnostics, or an inner exception.</summary>
public sealed class TargetCreationDeadlineInvocationException : Exception
{
    internal TargetCreationDeadlineInvocationException() : base("TargetCreationDeadlineInvocationUnavailable") { }
    public string Code => Message;
    public override string ToString() => Code;
}

/// <summary>
/// Isolated, default-off bridge for one invocation from a trusted, fresh one-shot coordinator.
/// This is NOT a standalone authorization boundary: neither enabling, public deadline construction,
/// nor matching scope/configuration values establishes a fresh permit or held namespace. The caller
/// must be the trusted coordinator that just checked the grant, journal, namespace and fresh absence.
/// The bound recipe validates canonical identities and endpoint mapping; the trusted runner retains
/// responsibility for production process policy and its final launch-budget check. No production
/// host registration or physical endpoint mapping is supplied here. Explicitly enabling all layers
/// with a real runner CAN launch real RAC; this is not a dry run. There are no retries, authority
/// renewal, namespace revalidation, or success promotion here. Managed secrets cannot be erased and
/// must not be retained, logged, serialized, or persisted as command recipes.
/// </summary>
public sealed class TargetCreationDeadlineInvocationAdapter : ITargetCreationSingleInvocation
{
    private readonly RacInfobaseCreationRecipe _recipe;
    private readonly RacInfobaseCreationInvocation _invocation;
    private readonly bool _enabled;

    public TargetCreationDeadlineInvocationAdapter(RacInfobaseCreationRecipe recipe,
        RacInfobaseCreationInvocation invocation, TargetCreationDeadlineInvocationAdapterOptions options)
    {
        if (recipe is null || invocation is null || options is null)
            throw new TargetCreationDeadlineInvocationException();
        _recipe = recipe;
        _invocation = invocation;
        _enabled = options.Enabled;
    }

    public async Task<RacInfobaseCreationOutcome> InvokeAsync(RacInfobaseCreationScope scope,
        TargetCreationDeliveredSecretValues secrets, ProcessLaunchDeadline launchDeadline,
        CancellationToken cancellationToken = default)
    {
        // Gate before touching invocation inputs, secrets, the deadline clock, or downstream code.
        if (!_enabled) throw new TargetCreationDeadlineInvocationException();
        try
        {
            if (scope is null || secrets is null || launchDeadline is null)
                throw new TargetCreationDeadlineInvocationException();
            launchDeadline.DemandNotExpired();
            cancellationToken.ThrowIfCancellationRequested();
            var commandRecipe = _recipe.Prepare(scope, new RacInfobaseCreationCredentials(
                secrets.ClusterUser, secrets.ClusterPassword, secrets.SqlUser, secrets.SqlPassword));
            // Pass the exact guard, including time consumed while preparing the recipe.
            return await _invocation.RunWithLaunchDeadlineAsync(commandRecipe, launchDeadline,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failure may follow physical launch. The coordinator quarantines; never retry or
            // leak arbitrary recipe/runner/clock diagnostics (including cancellation diagnostics).
            throw new TargetCreationDeadlineInvocationException();
        }
    }

    public override string ToString() => "TargetCreationDeadlineInvocationAdapter [details omitted]";
}
