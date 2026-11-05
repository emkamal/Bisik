using System.Text.Json;
using Bisik.Core;

internal static class Program
{
    private const string ProbeText = "Bisik verification — Selamat pagi, Indonesia!\r\nSecond paragraph: café 😀.";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--benchmark")
            return LatencyDiagnostics.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--cleanup-benchmark")
            return LatencyDiagnostics.RunAsync(["synthetic-corpus", .. args[1..]], cleanupOnly: true).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--local-costs")
            return LocalPersistenceDiagnostics.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] is "--azure-file" or "--azure-once")
            return AzureDiagnostics.RunAsync(args[1..], singleRequest: args[0] == "--azure-once").GetAwaiter().GetResult();

        if (!args.Contains("--insert") && !args.Contains("--capture-only"))
        {
            Console.WriteLine("Use --insert or --capture-only. Optional: --capture-delay-ms 5000 --after-capture-delay-ms 1000 --keep-clipboard --output report.json");
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var context = new ApplicationContext();
        using var synchronization = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(synchronization);
        var exitCode = 1;
        _ = RunAsync(args, context, code => exitCode = code);
        Application.Run(context);
        return exitCode;
    }

    private static async Task RunAsync(string[] args, ApplicationContext context, Action<int> setExitCode)
    {
        await Task.Yield(); // Ensure even zero-delay probes run after the message loop starts.
        try
        {
            var captureDelay = ReadMilliseconds(args, "--capture-delay-ms", 5000);
            var insertionDelay = ReadMilliseconds(args, "--after-capture-delay-ms", 1000);
            await Task.Delay(captureDelay);
            var focusDetails = args.Contains("--focus-details") ? FocusDiagnostics.Capture() : null;
            using var output = new WindowsTextOutput();
            using var target = output.CaptureTarget();
            var targetVerified = target.IsCurrent;
            await Task.Delay(insertionDelay);
            var targetStillCurrent = target.IsCurrent;
            var outcome = args.Contains("--capture-only")
                ? new OutputDispatchResult(OutputDisposition.Skipped, target.UnsafeReason)
                : await output.InsertAsync(ProbeText, target, args.Contains("--keep-clipboard"), CancellationToken.None);

            await WriteReportAsync(args, new
            {
                TargetVerified = targetVerified,
                TargetStillCurrent = targetStillCurrent,
                Disposition = outcome.Disposition.ToString(),
                outcome.Message,
                FocusDetails = focusDetails,
            });
            setExitCode(0);
        }
        catch (Exception exception)
        {
            // Reports deliberately omit window titles, user text and connection details.
            await WriteReportAsync(args, new { Error = exception.GetType().Name });
        }
        finally { context.ExitThread(); }
    }

    private static int ReadMilliseconds(string[] args, string name, int fallback)
    {
        var value = ReadOption(args, name);
        return value is null ? fallback : Math.Clamp(int.Parse(value), 0, 60000);
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1]
            : throw new ArgumentException("Missing option value.");
    }

    private static async Task WriteReportAsync<T>(string[] args, T report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var path = ReadOption(args, "--output");
        if (path is null) Console.WriteLine(json);
        else await File.WriteAllTextAsync(path, json);
    }
}
