using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public sealed class ExperimentDetailsViewTests
{
    [Fact]
    public Task RepeatSelectionKeepsOutputEvidenceAndMetadataPaired() => StaTestHost.RunAsync(() =>
    {
        var definition = ExperimentRunnerTests.Definition(TranscriptionServiceTextTests.Settings()) with
        {
            Reference = "Reference with & identifiers.\r\n" + new string('x', 4000),
            Prompts = [new("Saved prompt", "Preserve the reference.")],
        };
        var candidate = new ExperimentCandidate("candidate", "Candidate", "deployment", "model", definition.Prompts[0].SystemPrompt,
            "low", "low", ExperimentVerification.Verified, "test");
        var first = new ExperimentTrial
        {
            Id = "first", Candidate = candidate, Generation = ExperimentGenerationStatus.Succeeded,
            Output = "First result", CleanupMs = 12.5, EvaluationStatus = ExperimentEvaluationStatus.Succeeded,
            Evaluation = new(90, 30, 80, [new(ExperimentWarningCategory.Negation, ExperimentSeverity.Major, "not", "is", "Negation lost.")], "first"),
            TermEvidence = [new("VtrinLib", 2, 1, 50)],
        };
        var second = first with { Id = "second", RepeatIndex = 1, Output = "Second result", CleanupMs = 24,
            Evaluation = new(100, 100, 95, [], "second"), TermEvidence = [] };
        var run = new ExperimentRun(definition, new(Guid.NewGuid(), "Shared input", 56, null, definition.InputIdentity),
            ExperimentState.Completed, 1, [second, first]);
        using var form = new Form { Size = new(900, 700) };
        using var details = new ExperimentDetailsView(); form.Controls.Add(details); form.Show();
        details.ShowCandidate(run, candidate.Id);
        RichTextBox Reader(string name) => ExperimentsFormTests.All(details).OfType<RichTextBox>().Single(t => t.AccessibleName == name);
        var repeat = ExperimentsFormTests.All(details).OfType<ComboBox>().Single();
        Assert.Equal(definition.Reference.Replace("\r\n", "\n"), Reader("Reference text").Text); Assert.True(Reader("Reference text").ReadOnly);
        Assert.Equal("Shared input", Reader("Shared raw").Text); Assert.Equal("Preserve the reference.", Reader("Result prompt").Text);
        Assert.Equal("First result", Reader("Cleanup result").Text);
        Assert.Contains("Negation lost.", Reader("AI judge result").Text); Assert.Contains("VtrinLib: 2 correct / 1 wrong", Reader("AI judge result").Text);
        Assert.Contains("Cleanup time: 12", Reader("Metadata").Text);
        repeat.SelectedIndex = 1;
        Assert.Equal("Second result", Reader("Cleanup result").Text); Assert.DoesNotContain("Negation lost.", Reader("AI judge result").Text);
        Assert.Contains("Cleanup time: 24", Reader("Metadata").Text);
        details.ShowCandidate(run with { BatchElapsedMs = 100 }, candidate.Id);
        Assert.Equal(1, repeat.SelectedIndex); Assert.Equal("Second result", Reader("Cleanup result").Text);
        details.Clear(); Assert.Empty(Reader("Cleanup result").Text); Assert.Empty(repeat.Items); Assert.False(repeat.Enabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task BaselineAndUnavailableResultsAreExplicitAndStaleEvidenceClears() => StaTestHost.RunAsync(() =>
    {
        var definition = ExperimentRunnerTests.Definition(TranscriptionServiceTextTests.Settings());
        var baseline = new ExperimentTrial { Id = "raw", Candidate = ExperimentCandidate.Baseline,
            Generation = ExperimentGenerationStatus.Succeeded, EvaluationStatus = ExperimentEvaluationStatus.Skipped, Output = "raw" };
        var failed = baseline with { Id = "failed", Candidate = baseline.Candidate with { Id = "failed", IsBaseline = false },
            Generation = ExperimentGenerationStatus.Failed, EvaluationStatus = ExperimentEvaluationStatus.Skipped,
            Output = null, FailureCode = "timeout", LocalWarnings = ["Generation failed."] };
        var run = new ExperimentRun(definition, new(Guid.NewGuid(), "raw", 0, null, definition.InputIdentity), ExperimentState.Completed, 1, [baseline, failed]);
        using var form = new Form { Size = new(760, 540) };
        using var details = new ExperimentDetailsView(); form.Controls.Add(details); form.Show();
        RichTextBox Reader(string name) => ExperimentsFormTests.All(details).OfType<RichTextBox>().Single(t => t.AccessibleName == name);
        details.ShowCandidate(run, baseline.Candidate.Id);
        Assert.Contains("No cleanup prompt", Reader("Result prompt").Text); Assert.Equal("raw", Reader("Cleanup result").Text);
        Assert.Contains("Skipped", Reader("AI judge result").Text); Assert.Contains("Recording: Unavailable", Reader("Metadata").Text);
        details.ShowCandidate(run, failed.Candidate.Id);
        Assert.Contains("Failed", Reader("Cleanup result").Text); Assert.Contains("Failure: timeout", Reader("Metadata").Text);
        Assert.Contains("Local warnings: Generation failed.", Reader("Metadata").Text);
        details.ShowCandidate(run, "missing"); Assert.Empty(Reader("Cleanup result").Text); Assert.Empty(Reader("AI judge result").Text);
        return Task.CompletedTask;
    });
}
