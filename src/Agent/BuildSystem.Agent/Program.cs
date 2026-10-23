using BuildSystem.Agent;
using Microsoft.Extensions.DependencyInjection;
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
builder.Services.AddHostedService<ProbeWorker>();
// Avoid default HTTP request logging; credentials and server snapshots are never logged by this host.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
await builder.Build().RunAsync();
// end
