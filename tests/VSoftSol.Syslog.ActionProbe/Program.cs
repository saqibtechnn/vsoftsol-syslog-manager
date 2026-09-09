// action-probe — a fixture for the Phase 7 RunScript sandbox tests.
//
//   action-probe [args...]
//
// Behaviour:
//   * prints "ARGS:" then one line per argument, verbatim (so a test can prove that a
//     shell-metacharacter argument was passed as ONE literal token, not interpreted).
//   * prints "ENV COUNT: <n>" — the number of environment variables visible to the child
//     (so a test can prove the parent's environment was NOT inherited).
//   * if an argument is "--exit=N", exits with code N.
//   * if an argument is "--sleep=SECONDS", sleeps that long first (timeout test).
//   * if an argument is "--spew=BYTES", writes that many bytes to stdout (oversize test).

int exitCode = 0;

foreach (string arg in args)
{
    if (arg.StartsWith("--exit=", StringComparison.Ordinal)
        && int.TryParse(arg.AsSpan(7), out int code))
    {
        exitCode = code;
    }
    else if (arg.StartsWith("--sleep=", StringComparison.Ordinal)
        && double.TryParse(arg.AsSpan(8), out double seconds))
    {
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }
    else if (arg.StartsWith("--spew=", StringComparison.Ordinal)
        && int.TryParse(arg.AsSpan(7), out int bytes))
    {
        var chunk = new string('x', 4096);
        for (int written = 0; written < bytes; written += chunk.Length)
        {
            Console.Out.Write(chunk);
        }

        Console.Out.WriteLine();
    }
}

Console.WriteLine("ARGS:");
foreach (string arg in args)
{
    Console.WriteLine(arg);
}

Console.WriteLine($"ENV COUNT: {Environment.GetEnvironmentVariables().Count}");
Console.WriteLine($"SYSLOG_RULE: {Environment.GetEnvironmentVariable("SYSLOG_RULE")}");

return exitCode;
