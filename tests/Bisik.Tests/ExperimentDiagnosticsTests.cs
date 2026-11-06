using System.Text.Json;
using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public class ExperimentDiagnosticsTests
{
    [Theory]
    [InlineData("HAPPI calls FetchClassData, not CommitClassData.")]
    [InlineData("Please bring the blue folder tomorrow.")]
    [InlineData("Jangan hapus dua konfigurasi; pompa belum berhenti.")]
    [InlineData("The valve is closed, tetapi sensor masih aktif.")]
    [InlineData("Keep 12 objects, not 21. Do not restart the server.")]
    public async Task HeldOutTextUsesSameRunnerAndContentFreeSummary(string text)
    {
        var settings = new AppSettings { PostProcessor = TranscriptionServiceTextTests.Settings() };
        var definition = ExperimentRunnerTests.Definition(settings.PostProcessor) with { Reference = text };
        var calls = 0;
        var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((input, config, _) =>
        {
            if (config.Model == "judge") return Task.FromResult("{\"meaning\":100,\"readability\":100,\"warnings\":[]}");
            Assert.Equal(text, input); Assert.DoesNotContain(text, config.SystemPrompt); calls++; return Task.FromResult(text);
        }));
        var run = await ExperimentDiagnostics.ExecuteAsync(definition, new(text, 1234, ExperimentFixtureKind.SyntheticText), settings, service, CancellationToken.None);
        Assert.Equal(4, calls); Assert.Equal(1234, run.Take.SharedStopToRawMs);
        var summary = JsonSerializer.Serialize(ExperimentDiagnostics.Summary(run, "heldout", ExperimentFixtureKind.SyntheticText));
        Assert.DoesNotContain(text, summary); Assert.DoesNotContain("example.invalid", summary); Assert.Contains("SyntheticText", summary);
        var direct = await new ExperimentRunner(service, new(service)).RunAsync(definition, run.Take, settings.PostProcessor,
            CancellationToken.None, orderSeed: run.OrderSeed);
        Assert.Equal(run.Trials.Select(t => t.Id), direct.Trials.Select(t => t.Id));
        Assert.Equal(ExperimentScoring.Rank(run, definition.Weights).Select(r => r.Factors.Wording),
            ExperimentScoring.Rank(direct, definition.Weights).Select(r => r.Factors.Wording));
    }

    [Fact]
    public void OptionsRejectUnknownDuplicateAndOverwritingSource()
    {
        var args = new[] { "--definition", "setup.json", "--fixture", "raw.json", "--settings", "encrypted.json", "--output", "timing.json" };
        Assert.Equal("fixture", ExperimentOptions.Parse(args).Label);
        Assert.Throws<ArgumentException>(() => ExperimentOptions.Parse([.. args, "--unknown", "x"]));
        Assert.Throws<ArgumentException>(() => ExperimentOptions.Parse([.. args, "--output", "x"]));
        Assert.Throws<ArgumentException>(() => ExperimentOptions.Parse([.. args[..6], "--output", "raw.json"]));
        Assert.Throws<ArgumentException>(() => new ExperimentFixture("raw", null, ExperimentFixtureKind.Human).Validate(false));
    }
}
