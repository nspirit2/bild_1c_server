using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
// RAC-like invocations must have genuinely empty stderr on success. Preserve the
// original stderr behavior for every pre-existing fixture mode (including no args).
if (args.Length == 0 || args[0] != "infobase")
    Console.Error.WriteLine(); // stderr is deliberately available even when no mode writes to it

if (args.Length == 0)
    return 64;

switch (args[0])
{
    case "infobase":
        // Independent inventory mode precedes the existing info validation. Accept only
        // the fixed read-only command; the bridge may prepend the fixture DLL, nothing else.
        if (args.Length > 1 && args[1] == "summary")
        {
            if (args.Length != 7 || args[2] != "list" ||
                !args[3].StartsWith("--cluster=", StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[3]["--cluster=".Length..], "D", out _) ||
                !args[4].StartsWith("--cluster-user=", StringComparison.Ordinal) ||
                !args[5].StartsWith("--cluster-pwd=", StringComparison.Ordinal) ||
                args[6] is not ("inventory-ok" or "inventory-empty" or "inventory-stderr" or
                    "inventory-hang" or "inventory-malformed" or "inventory-casealias"))
                return 64;
            const string inventoryName = "bs_test_11111111111111111111111111111111";
            const string inventoryId = "22222222-2222-2222-2222-222222222222";
            var inventoryResponse = args[6] switch
            {
                "inventory-empty" => "",
                "inventory-malformed" => $"infobase : {inventoryId}\nname : {inventoryName}\nunexpected : value\n",
                "inventory-casealias" => $"infobase : {inventoryId}\nname : {inventoryName.ToUpperInvariant()}\n",
                _ => $"infobase : {inventoryId}\nname : {inventoryName}\ndescr : irrelevant λ🌍 description\n"
            };
            for (var offset = 0; offset < inventoryResponse.Length;)
            {
                var count = Math.Min(3, inventoryResponse.Length - offset);
                // Flush must not force the UTF-8 writer to encode half a surrogate pair.
                if (char.IsHighSurrogate(inventoryResponse[offset + count - 1])) count--;
                Console.Out.Write(inventoryResponse.Substring(offset, count));
                Console.Out.Flush();
                offset += count;
            }
            if (args[6] == "inventory-stderr")
            {
                foreach (var credential in new[] { args[4]["--cluster-user=".Length..], args[5]["--cluster-pwd=".Length..] })
                {
                    var middle = credential.Length / 2;
                    Console.Error.Write("private inventory diagnostic: " + credential[..middle]);
                    Console.Error.Flush();
                    Console.Error.WriteLine(credential[middle..]);
                }
            }
            if (args[6] == "inventory-hang")
                await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        // Accept the observer's real read-only RAC command, without a synthetic
        // mode argument inserted by the test bridge. Only the endpoint selects behavior.
        if (args.Length != 7 || args[1] != "info" ||
            !args[2].StartsWith("--cluster=", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[2]["--cluster=".Length..], "D", out _) ||
            !args[3].StartsWith("--infobase=", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[3]["--infobase=".Length..], "D", out var infobase) ||
            !args[4].StartsWith("--cluster-user=", StringComparison.Ordinal) ||
            !args[5].StartsWith("--cluster-pwd=", StringComparison.Ordinal) ||
            args[6] is not ("fixture-ok" or "fixture-stderr" or "fixture-hang" or "fixture-off"))
            return 64;
        var response = $"infobase : {infobase:D}\nscheduled-jobs-deny : {(args[6] == "fixture-off" ? "off" : "on")}\n";
        // Split field names, values and newlines across flushed writes. The runner's
        // tiny line limit also makes fragmentation deterministic at the callback boundary.
        for (var offset = 0; offset < response.Length; offset += 3)
        {
            Console.Out.Write(response.Substring(offset, Math.Min(3, response.Length - offset)));
            Console.Out.Flush();
        }
        if (args[6] == "fixture-stderr")
        {
            foreach (var credential in new[] { args[4]["--cluster-user=".Length..], args[5]["--cluster-pwd=".Length..] })
            {
                var middle = credential.Length / 2;
                Console.Error.Write("private diagnostic: " + credential[..middle]);
                Console.Error.Flush();
                Console.Error.WriteLine(credential[middle..]);
            }
        }
        if (args[6] == "fixture-hang")
            await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
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
