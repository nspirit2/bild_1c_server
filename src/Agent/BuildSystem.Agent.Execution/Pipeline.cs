using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace BuildSystem.Agent.Execution;

public sealed class PipelineException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum ValueType { String, Int, Bool, Project, Infobase, Reservation, ArchiveVersion, TemplateVersion, Artifact }
public sealed record PipelineValue(ValueType Type, object Data)
{
    public static PipelineValue Parse(ValueType type, string text)
    {
        if (text.Length > 4096) throw new PipelineException("InvalidValue", "Value exceeds limit");
        object data = type switch
        {
            ValueType.Int when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => n,
            ValueType.Bool when text is "true" or "false" => text == "true",
            ValueType.String => text,
            ValueType.Project or ValueType.Infobase or ValueType.Reservation or ValueType.ArchiveVersion or ValueType.TemplateVersion or ValueType.Artifact
                when Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty => id,
            _ => throw new PipelineException("InvalidValue", "Invalid typed value")
        };
        return new(type, data);
    }
    public static PipelineValue From(ValueType type, object data) => data switch
    {
        string s => Parse(type, s),
        long n when type == ValueType.Int => new(type, n),
        bool b when type == ValueType.Bool => new(type, b),
        Guid id when id != Guid.Empty && type is >= ValueType.Project and <= ValueType.Artifact => new(type, id),
        _ => throw new PipelineException("InvalidValue", "Invalid typed value")
    };
}
public sealed record FieldSchema(ValueType Type, bool Required = true);
public sealed class StepDefinition
{
    public string Uses { get; }
    public ImmutableDictionary<string, FieldSchema> Inputs { get; }
    public ImmutableDictionary<string, FieldSchema> Outputs { get; }
    public ImmutableHashSet<string> Capabilities { get; }
    public bool RetrySafe { get; }
    public bool Mutating { get; }
    public string Fingerprint { get; }
    public StepDefinition(string uses, IEnumerable<KeyValuePair<string, FieldSchema>> inputs,
        IEnumerable<KeyValuePair<string, FieldSchema>> outputs, IEnumerable<string>? capabilities = null,
        bool retrySafe = false, bool mutating = true)
    {
        if (!PipelineCompiler.ValidUses(uses) || (retrySafe && mutating)) throw new ArgumentException("Invalid step definition");
        Uses = uses;
        Inputs = inputs.ToImmutableDictionary(StringComparer.Ordinal);
        Outputs = outputs.ToImmutableDictionary(StringComparer.Ordinal);
        Capabilities = (capabilities ?? []).ToImmutableHashSet(StringComparer.Ordinal);
        if (Inputs.Keys.Concat(Outputs.Keys).Concat(Capabilities).Any(x => !PipelineCompiler.ValidName(x))) throw new ArgumentException("Invalid schema identifier");
        RetrySafe = retrySafe; Mutating = mutating;
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            uses + "|" + retrySafe + "|" + mutating + "|" +
            string.Join(";", Inputs.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"I:{x.Key}:{x.Value.Type}:{x.Value.Required}")) + "|" +
            string.Join(";", Outputs.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"O:{x.Key}:{x.Value.Type}:{x.Value.Required}")) + "|" +
            string.Join(";", Capabilities.Order(StringComparer.Ordinal)))));
    }
}
public delegate ValueTask<IReadOnlyDictionary<string, PipelineValue>> StepHandler(
    IReadOnlyDictionary<string, PipelineValue> inputs, CancellationToken cancellationToken);
public sealed record StepRegistration(StepDefinition Definition, StepHandler Handler);
public interface IStepRegistry { StepRegistration? Resolve(string uses); }
public sealed class StepRegistry(IEnumerable<StepRegistration> registrations) : IStepRegistry
{
    private readonly ImmutableDictionary<string, StepRegistration> _entries = registrations.ToImmutableDictionary(x => x.Definition.Uses, StringComparer.Ordinal);
    public StepRegistration? Resolve(string uses) => _entries.GetValueOrDefault(uses);
}
public sealed record ValueSource(ValueType Type, PipelineValue? Literal, string? Parameter, string? StepId, string? Output);
public sealed class PlannedStep
{
    internal PlannedStep(string id, string uses, string fingerprint, ImmutableDictionary<string, ValueSource> inputs,
        TimeSpan timeout, int maxAttempts) => (Id, Uses, Fingerprint, Inputs, Timeout, MaxAttempts) =
        (id, uses, fingerprint, inputs, timeout, maxAttempts);
    public string Id { get; }
    public string Uses { get; }
    public string Fingerprint { get; }
    public ImmutableDictionary<string, ValueSource> Inputs { get; }
    public TimeSpan Timeout { get; }
    public int MaxAttempts { get; }
}
public sealed class ExecutionPlan
{
    internal ExecutionPlan(string name, string sha256, ImmutableDictionary<string, PipelineValue> parameters, ImmutableArray<PlannedStep> steps)
        => (Name, Sha256, Parameters, Steps) = (name, sha256, parameters, steps);
    public string Name { get; }
    public string Sha256 { get; }
    public ImmutableDictionary<string, PipelineValue> Parameters { get; }
    public ImmutableArray<PlannedStep> Steps { get; }
}
public interface IPipelineCompiler
{
    ExecutionPlan Compile(string yaml, IReadOnlyDictionary<string, PipelineValue> values, IReadOnlySet<string> capabilities);
}
public sealed class PipelineCompiler(IStepRegistry registry) : IPipelineCompiler
{
    private static readonly Regex Identifier = new("^[a-zA-Z][a-zA-Z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsesPattern = new("^[a-z][a-z0-9.-]{0,95}@[1-9][0-9]{0,8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParamRef = new(@"^\$\{\{ parameters\.([a-zA-Z][a-zA-Z0-9_-]{0,63}) \}\}$", RegexOptions.Compiled);
    private static readonly Regex OutputRef = new(@"^\$\{\{ steps\.([a-zA-Z][a-zA-Z0-9_-]{0,63})\.outputs\.([a-zA-Z][a-zA-Z0-9_-]{0,63}) \}\}$", RegexOptions.Compiled);
    public static bool ValidName(string s) => Identifier.IsMatch(s);
    public static bool ValidUses(string s) => UsesPattern.IsMatch(s);
    private static PipelineException Invalid(string message) => new("InvalidPipeline", message);
    private static YamlNode Parse(string yaml)
    {
        if (yaml.Length is 0 or > 65536) throw Invalid("YAML length out of bounds");
        try
        {
             // Event scan occurs before constructing the representation graph: no recursive alias expansion.
            var parser = new Parser(new StringReader(yaml));
            var depth = 0; var count = 0;
            while (parser.MoveNext())
            {
                var e = parser.Current;
                if (++count > 4096) throw Invalid("YAML node limit");
                if (e is AnchorAlias) throw Invalid("Aliases are forbidden");
                if (e is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)) throw Invalid("Anchors and tags are forbidden");
                if (e is MappingStart or SequenceStart && ++depth > 24) throw Invalid("YAML depth limit");
                if (e is MappingEnd or SequenceEnd) depth--;
            }
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1) throw Invalid("Exactly one document required");
            return stream.Documents[0].RootNode;
        }
        catch (YamlException) { throw Invalid("Malformed YAML"); }
    }
    private static Dictionary<string, YamlNode> Map(YamlNode node, params string[] allowed)
    {
        if (node is not YamlMappingNode mapping || mapping.Children.Count > 128) throw Invalid("Expected bounded mapping");
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value is null || !result.TryAdd(key.Value, pair.Value)) throw Invalid("Duplicate or non-scalar key");
            if (key.Value is "finally" or "when") throw new PipelineException("UnsupportedFeature", "finally and when are not supported");
            if (!allowed.Contains(key.Value, StringComparer.Ordinal)) throw Invalid("Unknown field");
        }
        return result;
    }
    private static string Text(YamlNode node) => node is YamlScalarNode { Value: not null } s && s.Value.Length <= 4096
        ? s.Value : throw Invalid("Expected scalar");
    private static YamlNode Require(Dictionary<string, YamlNode> map, string name) => map.GetValueOrDefault(name) ?? throw Invalid("Missing required field");
    private static ValueType Type(string name) => name switch
    {
        "string" => ValueType.String, "int" => ValueType.Int, "bool" => ValueType.Bool, "project" => ValueType.Project,
        "infobase" => ValueType.Infobase, "reservation" => ValueType.Reservation, "archiveVersion" => ValueType.ArchiveVersion,
        "templateVersion" => ValueType.TemplateVersion, "artifact" => ValueType.Artifact, _ => throw Invalid("Unknown parameter type")
    };
    public ExecutionPlan Compile(string yaml, IReadOnlyDictionary<string, PipelineValue> values, IReadOnlySet<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(yaml); ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(capabilities);
        var root = Map(Parse(yaml), "apiVersion", "kind", "metadata", "parameters", "steps");
        if (Text(Require(root, "apiVersion")) != "buildsystem/v1" || Text(Require(root, "kind")) != "Pipeline") throw Invalid("Unsupported version or kind");
        var metadata = Map(Require(root, "metadata"), "name");
        var name = Text(Require(metadata, "name")); if (!ValidName(name)) throw Invalid("Invalid pipeline name");
        var declared = new Dictionary<string, ValueType>(StringComparer.Ordinal);
        foreach (var (key, item) in Map(Require(root, "parameters"), values.Keys.Concat(["_"]).ToArray()))
        {
            if (!ValidName(key)) throw Invalid("Invalid parameter name");
            var schema = Map(item, "type", "required");
            if (schema.TryGetValue("required", out var required) && Text(required) != "true") throw Invalid("Parameters must be required");
            declared.Add(key, Type(Text(Require(schema, "type"))));
        }
        if (values.Count != declared.Count || values.Any(x => !declared.TryGetValue(x.Key, out var type) || x.Value is null || x.Value.Type != type)) throw Invalid("Missing, unknown or mistyped parameter value");
        var pinned = values.ToImmutableDictionary(x => x.Key, x => PipelineValue.From(x.Value.Type, x.Value.Data), StringComparer.Ordinal);
        if (Require(root, "steps") is not YamlSequenceNode sequence || sequence.Children.Count is 0 or > 64) throw Invalid("Steps must be bounded nonempty sequence");
        var steps = ImmutableArray.CreateBuilder<PlannedStep>();
        var previous = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);
        foreach (var node in sequence.Children)
        {
            var s = Map(node, "id", "uses", "with", "timeout", "retry");
            var id = Text(Require(s, "id")); var uses = Text(Require(s, "uses"));
            if (!ValidName(id) || !ValidUses(uses) || previous.ContainsKey(id)) throw Invalid("Invalid or duplicate step identifier");
            var definition = registry.Resolve(uses)?.Definition ?? throw Invalid("Unregistered step");
            if (!definition.Capabilities.All(capabilities.Contains)) throw Invalid("Missing capability");
            var inputNodes = s.TryGetValue("with", out var with) ? Map(with, definition.Inputs.Keys.ToArray()) : new Dictionary<string, YamlNode>();
            if (definition.Inputs.Any(x => x.Value.Required && !inputNodes.ContainsKey(x.Key))) throw Invalid("Required input missing");
            var inputs = ImmutableDictionary.CreateBuilder<string, ValueSource>(StringComparer.Ordinal);
            foreach (var (field, source) in inputNodes)
            {
                var expected = definition.Inputs[field].Type; var text = Text(source);
                var p = ParamRef.Match(text); var o = OutputRef.Match(text);
                ValueSource parsed;
                if (p.Success)
                {
                    var param = p.Groups[1].Value;
                    if (!declared.TryGetValue(param, out var actual) || actual != expected) throw Invalid("Invalid parameter reference type");
                    parsed = new(expected, null, param, null, null);
                }
                else if (o.Success)
                {
                    var step = o.Groups[1].Value; var output = o.Groups[2].Value;
                    if (!previous.TryGetValue(step, out var prior) || !prior.Outputs.TryGetValue(output, out var outputSchema) || !outputSchema.Required || outputSchema.Type != expected) throw Invalid("Invalid or future output reference");
                    parsed = new(expected, null, null, step, output);
                }
                else
                {
                    if (text.Contains("${{", StringComparison.Ordinal)) throw Invalid("Interpolation is forbidden");
                    parsed = new(expected, PipelineValue.Parse(expected, text), null, null, null);
                }
                inputs.Add(field, parsed);
            }
            var timeout = TimeSpan.FromMinutes(60);
            if (s.TryGetValue("timeout", out var timeoutNode))
            {
                var text = Text(timeoutNode);
                var match = Regex.Match(text, "^([1-9][0-9]{0,4})(s|m|h)$", RegexOptions.CultureInvariant);
                if (!match.Success) throw Invalid("Invalid timeout");
                var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                timeout = match.Groups[2].Value switch { "s" => TimeSpan.FromSeconds(number), "m" => TimeSpan.FromMinutes(number), _ => TimeSpan.FromHours(number) };
                if (timeout > TimeSpan.FromHours(24)) throw Invalid("Timeout exceeds 24 hours");
            }
            var attempts = 1;
            if (s.TryGetValue("retry", out var retry))
            {
                var r = Map(retry, "maxAttempts");
                if (!int.TryParse(Text(Require(r, "maxAttempts")), out attempts) || attempts is < 1 or > 3) throw Invalid("Invalid retry count");
            }
            if (attempts > 1 && (!definition.RetrySafe || definition.Mutating)) throw Invalid("Retry requires explicitly safe nonmutating handler");
            steps.Add(new(id, uses, definition.Fingerprint, inputs.ToImmutable(), timeout, attempts));
            previous.Add(id, definition);
        }
        var immutableSteps = steps.ToImmutable();
        var canonical = System.Text.Json.JsonSerializer.Serialize(new { yaml,
            Parameters = pinned.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
            Steps = immutableSteps.Select(s => new { s.Id, s.Uses, s.Fingerprint, s.Timeout, s.MaxAttempts,
                Inputs = s.Inputs.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() }).ToArray() });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(name, hash, pinned, immutableSteps);
    }
}

public enum PipelineOutcome { Succeeded, Failed, Cancelled, NeedsAttention }
public sealed record ExecutionResult(PipelineOutcome Outcome, string? StepId, ImmutableDictionary<string, ImmutableDictionary<string, PipelineValue>> Outputs);
public interface IPipelineExecutor
{
    Task<ExecutionResult> ExecuteAsync(ExecutionPlan plan, Func<CancellationToken, ValueTask<bool>> leaseGuard, CancellationToken cancellationToken = default);
}
public sealed class PipelineExecutor(IStepRegistry registry) : IPipelineExecutor
{
    public async Task<ExecutionResult> ExecuteAsync(ExecutionPlan plan, Func<CancellationToken, ValueTask<bool>> leaseGuard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(leaseGuard);
        var outputs = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, PipelineValue>>(StringComparer.Ordinal);
        ExecutionResult Result(PipelineOutcome status, string? id) => new(status, id, outputs.ToImmutable());
         // Preflight all definitions before any side effect; also re-check immediately before each invocation.
        if (plan.Steps.Any(s => registry.Resolve(s.Uses)?.Definition.Fingerprint != s.Fingerprint)) return Result(PipelineOutcome.NeedsAttention, null);
        foreach (var step in plan.Steps)
        {
            for (var attempt = 0; attempt < step.MaxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) return Result(PipelineOutcome.Cancelled, step.Id);
                try { if (!await leaseGuard(cancellationToken)) return Result(PipelineOutcome.NeedsAttention, step.Id); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result(PipelineOutcome.Cancelled, step.Id); }
                catch { return Result(PipelineOutcome.NeedsAttention, step.Id); }
                if (cancellationToken.IsCancellationRequested) return Result(PipelineOutcome.Cancelled, step.Id);
                var registration = registry.Resolve(step.Uses);
                if (registration?.Definition.Fingerprint != step.Fingerprint) return Result(PipelineOutcome.NeedsAttention, step.Id);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(step.Timeout);
                try
                {
                    var input = step.Inputs.ToImmutableDictionary(x => x.Key, x => x.Value.Literal ??
                        (x.Value.Parameter is { } p ? plan.Parameters[p] : outputs[x.Value.StepId!][x.Value.Output!]), StringComparer.Ordinal);
                     // Cooperative deadline only: await handler completion; cancellation cannot forcibly kill an arbitrary operation.
                    var raw = await registration.Handler(input, timeout.Token);
                    if (cancellationToken.IsCancellationRequested) return Result(registration.Definition.Mutating ? PipelineOutcome.NeedsAttention : PipelineOutcome.Cancelled, step.Id);
                    if (timeout.IsCancellationRequested) return Result(registration.Definition.Mutating ? PipelineOutcome.NeedsAttention : PipelineOutcome.Failed, step.Id);
                    try { if (!await leaseGuard(cancellationToken)) return Result(PipelineOutcome.NeedsAttention, step.Id); }
                    catch { return Result(PipelineOutcome.NeedsAttention, step.Id); }
                    if (raw is null || raw.Count > 128 || raw.Any(x => !registration.Definition.Outputs.TryGetValue(x.Key, out var schema) ||
                        x.Value is null || x.Value.Type != schema.Type) ||
                        registration.Definition.Outputs.Any(x => x.Value.Required && !raw.ContainsKey(x.Key)))
                        return Result(PipelineOutcome.NeedsAttention, step.Id);
                    outputs.Add(step.Id, raw.ToImmutableDictionary(x => x.Key, x => PipelineValue.From(x.Value.Type, x.Value.Data), StringComparer.Ordinal));
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result(registration.Definition.Mutating ? PipelineOutcome.NeedsAttention : PipelineOutcome.Cancelled, step.Id); }
                catch
                {
                    if (cancellationToken.IsCancellationRequested) return Result(registration.Definition.Mutating ? PipelineOutcome.NeedsAttention : PipelineOutcome.Cancelled, step.Id);
                    if (registration.Definition.Mutating) return Result(PipelineOutcome.NeedsAttention, step.Id);
                    if (attempt + 1 == step.MaxAttempts) return Result(PipelineOutcome.Failed, step.Id);
                }
            }
        }
        return Result(PipelineOutcome.Succeeded, null);
    }
}
