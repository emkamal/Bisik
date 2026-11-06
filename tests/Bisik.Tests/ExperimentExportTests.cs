using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public sealed class ExperimentExportTests
{
    [Fact]
    public void UnicodeMultilineExportsHaveSafeIdentitiesAndUnavailableValues()
    {
        var definition = ExperimentModelTests.Definition();
        var run = new ExperimentRun(definition, new(Guid.NewGuid(), "raw\nΔ", 1000, null, definition.InputIdentity), ExperimentState.Completed, 1,
            [new() { Id = "raw", Candidate = ExperimentCandidate.Baseline, Generation = ExperimentGenerationStatus.Succeeded,
                Output = "edited\nΔ\t<script>", CleanupMs = 500, Factors = new(0, null, null, null, null) }]);
        var csv = ExperimentExport.Format(run, new(0, 0, 0, 0, 100), HistoryCopyFormat.Csv);
        Assert.Contains("\"edited\nΔ\t<script>\"", csv); Assert.Contains("1500", csv);
        var html = ExperimentExport.HtmlClipboard(run, new(0, 0, 0, 0, 100));
        Assert.Contains("&lt;script&gt;", html); Assert.Contains("mso-data-placement:same-cell", html);
        Assert.Contains("StartFragment:", html);
        Assert.DoesNotContain("Endpoint", ExperimentExport.Format(run, new(), HistoryCopyFormat.Json));
    }
}
