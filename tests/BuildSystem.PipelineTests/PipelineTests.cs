using BuildSystem.Agent.Execution;
using Xunit;
using Type = BuildSystem.Agent.Execution.ValueType;

namespace BuildSystem.PipelineTests;

public class PipelineTests
{
    private static readonly Dictionary<string, PipelineValue> Empty = [];
    private static readonly HashSet<string> Caps = [];
    private const string Header = "apiVersion: buildsystem/v1\nkind: Pipeline\nmetadata: { name: test }\nparameters: {}\nsteps:\n";
    private const string Step = "  - id: first\n    uses: echo@1\n    with: { message: hello }\n";
    private static StepDefinition Definition(bool safe = true, bool mutating = false, string[]? caps = null) => new("echo@1",
        new Dictionary<string, FieldSchema> { ["message"] = new(Type.String) },
        new Dictionary<string, FieldSchema> { ["text"] = new(Type.String) }, caps, safe, mutating);
    private static StepRegistry Registry(StepHandler? handler = null, StepDefinition? definition = null) => new([
        new(definition ?? Definition(), handler ?? ((input, _) => ValueTask.FromResult<IReadOnlyDictionary<string, PipelineValue>>(
            new Dictionary<string, PipelineValue> { ["text"] = input["message"] }))) ]);
    private static ExecutionPlan Compile(string yaml, IStepRegistry? registry = null, IReadOnlyDictionary<string, PipelineValue>? values = null) =>
        new PipelineCompiler(registry ?? Registry()).Compile(yaml, values ?? Empty, Caps);
    private static ValueTask<bool> Lease(CancellationToken _) => ValueTask.FromResult(true);

    [Fact]
    public async Task CancellationDuringGuardCannotStartHandler()
    {
        using var cancelled = new CancellationTokenSource();
        var called = false;
        var registry = Registry((input, _) =>
        {
            called = true;
            return ValueTask.FromResult<IReadOnlyDictionary<string, PipelineValue>>(
                new Dictionary<string, PipelineValue> { ["text"] = input["message"] });
        });
        var plan = Compile(Header + Step, registry);
        var result = await new PipelineExecutor(registry).ExecuteAsync(plan, _ =>
        {
            cancelled.Cancel();
            return ValueTask.FromResult(true);
        }, cancelled.Token);
        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        Assert.False(called);
    }

    [Theory]
    [InlineData("metadata: { name: one, name: two }")]
    [InlineData("metadata: &x { name: one }")]
    [InlineData("metadata: *x")]
    [InlineData("metadata: !!map { name: one }")]
    [InlineData("metadata: { name: one, secret: x }")]
    public void StrictParser(string replacement)
    {
        Assert.Throws<PipelineException>(() => Compile((Header + Step).Replace("metadata: { name: test }", replacement)));
    }
    [Theory]
    [InlineData("finally: []\n")]
    [InlineData("    when: safe-to-unlock\n")]
    public void UnsupportedNotIgnored(string extra) => Assert.Equal("UnsupportedFeature",
        Assert.Throws<PipelineException>(() => Compile(Header + Step + extra)).Code);

    [Theory]
    [InlineData("    unknown: no\n")]
    [InlineData("    timeout: 25h\n")]
    [InlineData("    timeout: 0s\n")]
    [InlineData("    retry: { maxAttempts: 4 }\n")]
    public void BoundedSchema(string extra) => Assert.Throws<PipelineException>(() => Compile(Header + Step + extra));

    [Theory]
    [InlineData("${{ steps.later.outputs.text }}")]
    [InlineData("${{ steps.first.outputs.missing }}")]
    [InlineData("prefix ${{ parameters.message }}")]
    [InlineData("${{ parameters.unknown }}")]
    public void InvalidReferences(string reference) => Assert.Throws<PipelineException>(() => Compile(Header + Step +
        "  - id: second\n    uses: echo@1\n    with: { message: '" + reference + "' }\n"));

    [Fact]
    public async Task TypedReferencesAndFrozenValues()
    {
        var yaml = (Header + Step).Replace("parameters: {}", "parameters: { p: { type: string, required: true } }")
            .Replace("message: hello", "message: '${{ parameters.p }}'") +
            "  - id: second\n    uses: echo@1\n    with: { message: '${{ steps.first.outputs.text }}' }\n";
        var values = new Dictionary<string, PipelineValue> { ["p"] = new(Type.String, "original") };
        var plan = Compile(yaml, values: values);
        values["p"] = new(Type.String, "changed");
        var result = await new PipelineExecutor(Registry()).ExecuteAsync(plan, Lease);
        Assert.Equal(PipelineOutcome.Succeeded, result.Outcome);
        Assert.Equal("original", result.Outputs["second"]["text"].Data);
        Assert.NotEqual(plan.Sha256, Compile(yaml, values: values).Sha256);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, PipelineValue>)plan.Parameters).Add("bad", new(Type.String, "x")));
    }
    [Fact]
    public void ParameterValuesStrict()
    {
        var yaml = (Header + Step).Replace("parameters: {}", "parameters: { p: { type: string } }");
        Assert.Throws<PipelineException>(() => Compile(yaml));
        Assert.Throws<PipelineException>(() => Compile(yaml, values: new Dictionary<string, PipelineValue> { ["p"] = new(Type.Int, 1L) }));
        Assert.Throws<PipelineException>(() => Compile(Header + Step, values: new Dictionary<string, PipelineValue> { ["extra"] = new(Type.String, "x") }));
        Assert.Throws<PipelineException>(() => Compile(yaml, values: new Dictionary<string, PipelineValue> { ["p"] = new(Type.String, new object()) }));
        Assert.NotEqual(PipelineValue.Parse(Type.Project, Guid.NewGuid().ToString()), PipelineValue.Parse(Type.Artifact, Guid.NewGuid().ToString()));
    }
    [Fact]
    public void SemanticGuidMismatchAndCapabilities()
    {
        var yaml = (Header + Step).Replace("parameters: {}", "parameters: { p: { type: project } }").Replace("message: hello", "message: '${{ parameters.p }}'");
        Assert.Throws<PipelineException>(() => Compile(yaml, values: new Dictionary<string, PipelineValue> { ["p"] = new(Type.Project, Guid.NewGuid()) }));
        Assert.Throws<PipelineException>(() => Compile(Header + Step, Registry(definition: Definition(caps: ["ProvisionArchive"]))));
    }
    [Fact]
    public async Task RetryOnlySafeAndGuardEveryAttempt()
    {
        var calls = 0; var leases = 0;
        var registry = Registry((_, _) => { calls++; throw new InvalidOperationException("secret"); });
        var plan = Compile(Header + Step + "    retry: { maxAttempts: 3 }\n", registry);
        var result = await new PipelineExecutor(registry).ExecuteAsync(plan, _ => ValueTask.FromResult(++leases < 2));
        Assert.Equal(PipelineOutcome.NeedsAttention, result.Outcome); Assert.Equal(1, calls); Assert.Equal(2, leases);
        Assert.Throws<PipelineException>(() => Compile(Header + Step + "    retry: { maxAttempts: 2 }\n", Registry(definition: Definition(false, true))));
        Assert.Throws<ArgumentException>(() => Definition(true, true));
    }
    [Fact]
    public async Task UnsafeFailureAttentionAndDefinitionSwapRejected()
    {
        var registry = Registry((_, _) => throw new Exception("secret"), Definition(false, true));
        var plan = Compile(Header + Step, registry);
        Assert.Equal(PipelineOutcome.NeedsAttention, (await new PipelineExecutor(registry).ExecuteAsync(plan, Lease)).Outcome);
        Assert.Equal(PipelineOutcome.NeedsAttention, (await new PipelineExecutor(Registry()).ExecuteAsync(plan, Lease)).Outcome);
    }
    [Fact]
    public async Task CancellationStopsFutureSteps()
    {
        using var cts = new CancellationTokenSource(); var calls = 0;
        var registry = Registry((input, _) => { calls++; cts.Cancel(); return ValueTask.FromResult<IReadOnlyDictionary<string, PipelineValue>>(new Dictionary<string, PipelineValue> { ["text"] = input["message"] }); });
        var plan = Compile(Header + Step + Step.Replace("first", "second"), registry);
        var result = await new PipelineExecutor(registry).ExecuteAsync(plan, Lease, cts.Token);
        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome); Assert.Equal(1, calls);
        Assert.Equal(PipelineOutcome.NeedsAttention, (await new PipelineExecutor(registry).ExecuteAsync(plan, _ => throw new Exception("secret"))).Outcome);
    }
    [Fact]
    public async Task TimeoutAwaitsCooperationAndDoesNotStartNextStep()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var registry = Registry(async (input, token) =>
        {
            calls++; started.SetResult();
            using var subscription = token.Register(() => deadlineObserved.TrySetResult());
            await release.Task; // deliberately ignores cancellation until host releases it
            return new Dictionary<string, PipelineValue> { ["text"] = input["message"] };
        });
        var plan = Compile(Header + Step + "    timeout: 1s\n" + Step.Replace("first", "second"), registry);
        var task = new PipelineExecutor(registry).ExecuteAsync(plan, Lease);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await deadlineObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted); Assert.Equal(1, calls);
        release.SetResult(); Assert.Equal(PipelineOutcome.Failed, (await task).Outcome); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task LeaseRevokedDuringHandlerDoesNotPublishOutputs()
    {
        var count = 0; var registry = Registry();
        var result = await new PipelineExecutor(registry).ExecuteAsync(Compile(Header + Step, registry),
            _ => ValueTask.FromResult(++count == 1));
        Assert.Equal(PipelineOutcome.NeedsAttention, result.Outcome);
        Assert.Empty(result.Outputs);
    }
    [Fact]
    public async Task UnsafeCancellationIsUncertainAndRetriesAreBounded()
    {
        using var cts = new CancellationTokenSource();
        var unsafeRegistry = Registry((_, _) => { cts.Cancel(); throw new OperationCanceledException(); }, Definition(false, true));
        Assert.Equal(PipelineOutcome.NeedsAttention, (await new PipelineExecutor(unsafeRegistry).ExecuteAsync(Compile(Header + Step, unsafeRegistry), Lease, cts.Token)).Outcome);
        var calls = 0;
        var safeRegistry = Registry((_, _) => { calls++; throw new Exception("secret"); });
        Assert.Equal(PipelineOutcome.Failed, (await new PipelineExecutor(safeRegistry).ExecuteAsync(Compile(Header + Step + "    retry: { maxAttempts: 3 }\n", safeRegistry), Lease)).Outcome);
        Assert.Equal(3, calls);
    }
    [Fact]
    public async Task InvalidOutputsAndBoundedInput()
    {
        var registry = Registry((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, PipelineValue>>(new Dictionary<string, PipelineValue> { ["text"] = new(Type.String, new string('x', 5000)) }));
        Assert.Equal(PipelineOutcome.Failed, (await new PipelineExecutor(registry).ExecuteAsync(Compile(Header + Step, registry), Lease)).Outcome);
        Assert.Throws<PipelineException>(() => Compile(new string('x', 65537)));
        Assert.Throws<PipelineException>(() => Compile(Header + Step + "    with: " + new string('[', 25) + "x" + new string(']', 25)));
    }
}
