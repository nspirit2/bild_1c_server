using BuildSystem.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "BuildSystem.Agent");
var directory = builder.Configuration["Agent:DataDirectory"];
if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
    throw new InvalidOperationException("Agent__DataDirectory must be an existing service-only ACL protected absolute directory.");
var tokenPath = Path.Combine(directory, "agent-token.dpapi");
if (args.Length == 1 && args[0] == "import-token")
{
    Console.Error.WriteLine("Token (hidden terminal input or protected stdin):");
    string? token;
    if (Console.IsInputRedirected) token = Console.ReadLine();
    else
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        token = text.ToString();
    }
    TokenStore.Import(tokenPath, token ?? throw new InvalidOperationException("No token supplied."));
    Console.WriteLine("Credential protected for current Windows service account. Token not printed.");
    return;
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This worker requires Windows DPAPI.");
var origin = builder.Configuration["Agent:ApiOrigin"] ?? throw new InvalidOperationException("Configure Agent__ApiOrigin.");
builder.Services.AddSingleton(new AgentTransport(new Uri(origin), TokenStore.Load(tokenPath)));
builder.Services.AddSingleton<IAgentTransport>(sp => sp.GetRequiredService<AgentTransport>());
builder.Services.AddSingleton<IArtifactTransport>(sp => sp.GetRequiredService<AgentTransport>());
builder.Services.AddSingleton(new AgentJournal(directory));
builder.Services.AddSingleton<ArtifactDelivery>();
if (builder.Configuration.GetValue<bool>("Agent:ArchiveStageEnabled"))
{
    // A separate loop, not a provisioning capability on the general probe dispatcher.
    var rootSection = builder.Configuration.GetSection("Agent:ArchiveRoots").GetChildren();
    var roots = rootSection.Select(section => new BuildSystem.Agent.Archives.ArchiveRoot(
        Guid.Parse(section["Id"] ?? throw new InvalidOperationException("Configure archive root ID.")),
        section["AbsolutePath"] ?? throw new InvalidOperationException("Configure approved archive root path."),
        section.GetValue<bool>("AtomicPublication"))).ToArray();
    var staging = builder.Configuration["Agent:StagingDirectory"] ?? throw new InvalidOperationException("Configure Agent__StagingDirectory.");
    builder.Services.AddSingleton(new BuildSystem.Agent.Archives.ArchiveStaging(roots, staging,
        builder.Configuration.GetValue<long>("Agent:MaxArchiveBytes"), builder.Configuration.GetValue<long>("Agent:MaxStagingBytes")));
    builder.Services.AddSingleton<IStageTransport>(sp => sp.GetRequiredService<AgentTransport>());
    builder.Services.AddSingleton(sp => new StageCoordinator(sp.GetRequiredService<AgentJournal>(),
        sp.GetRequiredService<BuildSystem.Agent.Archives.ArchiveStaging>(), sp.GetRequiredService<IStageTransport>().HeartbeatStageAsync));
    builder.Services.AddSingleton<StageWorker>();
    builder.Services.AddHostedService<StageHostedWorker>();
}
else builder.Services.AddHostedService<ProbeWorker>();
// Avoid default HTTP request logging; credentials and server snapshots are never logged by this host.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
await builder.Build().RunAsync();
// end
