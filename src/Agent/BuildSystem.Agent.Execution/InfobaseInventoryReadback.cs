namespace BuildSystem.Agent.Execution;

/// <summary>
/// Caller-supplied correlation, not authenticated RAC output or ownership evidence. No service epoch
/// is represented. ObservationId must be supplied by the caller for the requested observation.
/// </summary>
public sealed record InfobaseInventoryReadbackScope(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid ClusterId, Guid EndpointMappingId, Guid ReservationId,
    Guid ObservationId, string ExpectedInfobaseName)
{
    public override string ToString() => "InfobaseInventoryReadbackScope [details omitted]";
}

public sealed class InfobaseInventoryReadbackException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Only the exact reservation-generated name is observed; neither value grants authority.</summary>
public enum InfobaseInventoryObservation { Present, Absent }

/// <summary>A summary identifier and name. Descriptions are deliberately discarded.</summary>
public sealed class InfobaseInventoryEntry
{
    internal InfobaseInventoryEntry(Guid infobaseId, string name) => (InfobaseId, Name) = (infobaseId, name);
    public Guid InfobaseId { get; }
    public string Name { get; }
    public override string ToString() => "InfobaseInventoryEntry [details omitted]";
}

/// <summary>
/// One complete summary list, not ownership, a lock, freshness proof, create permission or Ready.
/// A trusted adapter must provide authentic, normally completed stdout for the pinned read-only
/// command and endpoint (no timeout, cancellation, read/callback failure or truncation). Exit zero
/// alone cannot establish those facts. Scope pins are not reported by summary output. Empty output
/// observes absence only; it does not authorize creation. Same-scope replay is not prevented here.
/// </summary>
public sealed class InfobaseInventoryReadbackResult
{
    internal InfobaseInventoryReadbackResult(InfobaseInventoryReadbackScope scope,
        InfobaseInventoryEntry[] entries)
    {
        Scope = scope;
        Entries = Array.AsReadOnly((InfobaseInventoryEntry[])entries.Clone());
        Observation = entries.Any(e => string.Equals(e.Name, scope.ExpectedInfobaseName, StringComparison.Ordinal))
            ? InfobaseInventoryObservation.Present : InfobaseInventoryObservation.Absent;
    }
    public InfobaseInventoryReadbackScope Scope { get; }
    public IReadOnlyList<InfobaseInventoryEntry> Entries { get; }
    public InfobaseInventoryObservation Observation { get; }
    public override string ToString() => "InfobaseInventoryReadbackResult [not authority; details omitted]";
}

/// <summary>Pure bounded RAC infobase summary list parser. No IO, commands or mutation.</summary>
public static class InfobaseInventoryReadback
{
    public const int MaxOutputCharacters = 65536;
    public const int MaxRecords = 256;
    public const int MaxNameCharacters = 128;
    public const int MaxDescriptionCharacters = 4096;

    public static InfobaseInventoryReadbackResult Parse(InfobaseInventoryReadbackScope scope,
        int exitCode, bool outputTruncated, string stdout)
    {
        Validate(scope);
        if (exitCode != 0 || outputTruncated) throw Error("InfobaseInventoryReadbackUnavailable");
        if (stdout is null || stdout.Length > MaxOutputCharacters ||
            stdout.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw Error("InvalidInfobaseInventoryReadback");
        var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) throw Error("InvalidInfobaseInventoryReadback");
        var entries = new List<InfobaseInventoryEntry>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in normalized.Split('\n'))
        {
            if (raw.All(c => c is ' ' or '\t'))
            {
                CompleteRecord();
                continue;
            }
            var colon = raw.IndexOf(':');
            if (colon < 1) throw Error("InvalidInfobaseInventoryReadback");
            var key = raw[..colon].Trim(' ', '\t');
            var rawValue = raw[(colon + 1)..];
            // RAC uses one optional ASCII space after ':'. Never trim a name into a different
            // identity (particularly the pinned target); ambiguous surrounding whitespace fails.
            var separatedValue = rawValue.StartsWith(' ') ? rawValue[1..] : rawValue;
            var value = key == "name" ? separatedValue : separatedValue.Trim(' ');
            if (key is not ("infobase" or "name" or "descr") || fields.Count >= 3 ||
                rawValue.Any(char.IsControl) ||
                (key == "name" && (value.Length == 0 || char.IsWhiteSpace(value[0]) ||
                    char.IsWhiteSpace(value[^1]))) || !fields.TryAdd(key, value))
                throw Error("InvalidInfobaseInventoryReadback");
        }
        CompleteRecord();
        return new(scope, entries.ToArray());

        void CompleteRecord()
        {
            if (fields.Count == 0) return;
            if (entries.Count >= MaxRecords || !fields.TryGetValue("infobase", out var id) ||
                !Guid.TryParseExact(id, "D", out var infobaseId) || infobaseId == Guid.Empty ||
                !fields.TryGetValue("name", out var name) || name.Length is 0 or > MaxNameCharacters ||
                (fields.TryGetValue("descr", out var descr) && descr.Length > MaxDescriptionCharacters) ||
                !ids.Add(infobaseId) || !names.Add(name))
                throw Error("InvalidInfobaseInventoryReadback");
            // A case-only alias of the pinned name must not be turned into an absence observation.
            if (string.Equals(name, scope.ExpectedInfobaseName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, scope.ExpectedInfobaseName, StringComparison.Ordinal))
                throw Error("AmbiguousInfobaseInventoryTarget");
            entries.Add(new(infobaseId, name));
            fields.Clear();
        }
    }

    /// <summary>Exact correlation only, not authentication, elapsed-time freshness or replay protection.</summary>
    public static void RequireMatch(InfobaseInventoryReadbackResult readback, InfobaseInventoryReadbackScope expected)
    {
        Validate(expected);
        if (readback is null || readback.Scope != expected) throw Error("InfobaseInventoryScopeMismatch");
    }

    private static void Validate(InfobaseInventoryReadbackScope scope)
    {
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ClusterId, scope.EndpointMappingId,
                scope.ReservationId, scope.ObservationId }.Any(x => x == Guid.Empty) ||
            !string.Equals(scope.ExpectedInfobaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal))
            throw Error("InvalidInfobaseInventoryScope");
    }

    private static InfobaseInventoryReadbackException Error(string code) => new(code);
}
