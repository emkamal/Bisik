using System.Globalization;
using System.Text;
using System.Text.Json;
using Bisik.Core;

internal static class ComparisonDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[1] != "--output") return 2;
        var reports = new List<SessionTimingReport>();
        var legacyRun = 0;
        foreach (var file in Directory.EnumerateFiles(args[0], "*.json"))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Reports", out var rows))
            {
                var run = rows.Deserialize<SessionTimingReport[]>() ?? [];
                if (!document.RootElement.TryGetProperty("Features", out _))
                {
                    // Older receipts lack feature identity; never aggregate distinct experiments as one configuration.
                    legacyRun++;
                    run = run.Select(r => r with { Configuration = r.Configuration + "-legacy" + legacyRun }).ToArray();
                }
                reports.AddRange(run);
            }
        }
        var text = new StringBuilder("Single samples do not establish a distribution or causation. Quality is reviewed in separate synthetic quality receipts.\n\n");
        text.AppendLine("| Configuration | Audio ms | Samples | Median ready ms | p95 ms | Failures | Fallback rate | Quality |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        string Number(double? value) => value?.ToString("F1", CultureInfo.InvariantCulture) ?? "N/A";
        foreach (var row in TimingAggregation.Compare(reports))
            text.AppendLine($"| {row.Configuration.Replace('|', '_')} | {Number(row.AudioDurationMs)} | {row.Summary.Samples} | {Number(row.Summary.MedianMs)} | {Number(row.Summary.P95Ms)} | {row.Summary.Failures} | {row.FallbackRate.ToString("P0", CultureInfo.InvariantCulture)} | Separate receipt / untested | ");
        await File.WriteAllTextAsync(args[2], text.ToString());
        return 0;
    }
}
