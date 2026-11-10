using Xunit;

namespace BuildSystem.AgentTests;

// Bound actual-agent subprocess concurrency without disabling parallelism for unrelated tests.
[CollectionDefinition("Agent CLI subprocesses", DisableParallelization = true)]
public sealed class AgentCliSubprocessCollection;
