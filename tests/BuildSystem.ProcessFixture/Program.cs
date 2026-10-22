using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
Console.Error.WriteLine(); // stderr is deliberately available even when no mode writes to it

if (args.Length == 0)
    return 64;

switch (args[0])
{
    case "echo":
        Console.Out.WriteLine("stdout:" + string.Join("|", args.Skip(1)));
        Console.Error.WriteLine("stderr:λ🌍");
        return 0;
    case "ordered":
        for (var i = 0; i < 20; i++)
        {
            Console.Out.WriteLine($"out:{i}");
            Console.Error.WriteLine($"err:{i}");
        }
        return 0;
    case "exit":
        return int.Parse(args[1]);
    case "cwd-env":
        Console.Out.WriteLine("cwd:" + Environment.CurrentDirectory);
        Console.Out.WriteLine("env:" + (Environment.GetEnvironmentVariable(args[1]) ?? "<unset>"));
        return 0;
    case "hang":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    case "tree":
        using (var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No process path"),
            ArgumentList = { typeof(Program).Assembly.Location, "child" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Child did not start"))
        {
            File.WriteAllText(args[1], child.Id.ToString());
            Console.Out.WriteLine("tree-ready");
            Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        return 0;
    case "child":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    case "flood":
        for (var i = 0; i < 2000; i++)
        {
            Console.Out.WriteLine($"out:{i:D4}:" + new string('x', 8192));
            Console.Error.WriteLine($"err:{i:D4}:" + new string('y', 8192));
        }
        return 0;
    case "secret":
        // A secret is deliberately fragmented into multiple writes and also placed in a second line.
        var secret = args[1];
        var halfway = secret.Length / 2;
        Console.Out.Write("before-");
        Console.Out.Flush();
        Console.Out.Write(secret[..halfway]);
        Console.Out.Flush();
        Console.Out.WriteLine(secret[halfway..] + "-after");
        Console.Out.WriteLine("again-" + secret + "-done");
        Console.Error.WriteLine("error-" + secret + "-done");
        return 0;
    default:
        return 65;
}
