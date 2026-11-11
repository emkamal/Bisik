using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public class ExperimentWeightsFormTests
{
    [Fact]
    public Task InfoButtonsExplainFactorsAndSupportFocusAndClickWithoutReweighting() => StaTestHost.RunAsync(() =>
    {
        using var form = new ExperimentWeightsForm(new()); form.Show(); var changes = 0; form.WeightsChanged += _ => changes++;
        var buttons = ExperimentsFormTests.All(form).OfType<Button>().Where(b => b.AccessibleName?.EndsWith(" help") == true).ToArray();
        Assert.Equal(6, buttons.Length);
        var wording = buttons.Single(b => b.AccessibleName == "Wording help"); Assert.Contains("Word error rate", wording.AccessibleDescription);
        var identifiers = buttons.Single(b => b.AccessibleName == "Identifiers help"); Assert.Contains("unavailable", identifiers.AccessibleDescription);
        var meaning = buttons.Single(b => b.AccessibleName == "Meaning help"); Assert.Contains("30", meaning.AccessibleDescription); Assert.Contains("10", meaning.AccessibleDescription);
        Assert.Contains("independently", buttons.Single(b => b.AccessibleName == "Readability help").AccessibleDescription);
        Assert.Contains("matrix queueing", buttons.Single(b => b.AccessibleName == "Speed help").AccessibleDescription);
        Assert.Contains("50", buttons.Single(b => b.AccessibleName == "Speed B (seconds) help").AccessibleDescription);
        foreach (var button in buttons)
        {
            Assert.Equal("ⓘ", button.Text); Assert.True(button.TabStop); Assert.Equal(Cursors.Help, button.Cursor);
            button.Focus(); button.PerformClick();
        }
        Assert.Equal(new ExperimentWeights(), form.Weights); Assert.Equal(0, changes);
        form.Close(); return Task.CompletedTask;
    });
    [Fact]
    public Task SlidersAndNumbersSynchronizeOnceAndReset() => StaTestHost.RunAsync(() =>
    {
        using var form = new ExperimentWeightsForm(new()); var changes = 0; form.WeightsChanged += _ => changes++;
        var number = ExperimentsFormTests.All(form).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Meaning weight");
        var slider = ExperimentsFormTests.All(form).OfType<TrackBar>().Single(n => n.AccessibleName == "Meaning slider");
        number.Value = 0; Assert.Equal(0, slider.Value); Assert.Equal(1, changes);
        slider.Value = 100; Assert.Equal(100, number.Value); Assert.Equal(2, changes);
        form.SetWeights(new()); Assert.Equal(new ExperimentWeights(), form.Weights); Assert.Equal(3, changes);
        return Task.CompletedTask;
    });

    [Fact]
    public Task MajorWarningCanWinAndSelectionSurvivesLocalResortWithoutNetwork() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.ResultsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { PostProcessor = TranscriptionServiceTextTests.Settings() };
            var definition = ExperimentRunnerTests.Definition(settings.PostProcessor);
            var bad = ExperimentCandidate.Baseline with { Id = "bad", Name = "Fast with major warning" };
            var good = bad with { Id = "good", Name = "Preserved" };
            var run = new ExperimentRun(definition, new(Guid.NewGuid(), "raw", 0, null, definition.InputIdentity), ExperimentState.Completed, 1,
                [new() { Id = "bad", Candidate = bad, Generation = ExperimentGenerationStatus.Succeeded, Output = "wrong", CleanupMs = 1,
                    Factors = new(0, 0, null, null, null), EvaluationStatus = ExperimentEvaluationStatus.Succeeded,
                    Evaluation = new(80, 30, 80, [new(ExperimentWarningCategory.Meaning, ExperimentSeverity.Major, "not", "is", "Lost negation")], "bad") },
                 new() { Id = "good", Candidate = good, Generation = ExperimentGenerationStatus.Succeeded, Output = "correct", CleanupMs = 5000,
                     Factors = new(100, 100, null, null, null), EvaluationStatus = ExperimentEvaluationStatus.Succeeded, Evaluation = new(100, 100, 100, [], "good") }]);
            var store = new ExperimentStore(root); await store.SaveAsync(run, CancellationToken.None);
            using var form = new ExperimentsForm(settings, _ => throw new Exception("No network"), (_, _, _, _) => throw new Exception("No capture"), store, () => new ExperimentsFormTests.Lease());
            form.Show(); await form.LoadAsync(definition.Id); form.SetViewWeights(new(0, 0, 0, 0, 100));
            var grid = ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Ranked results");
            Assert.Equal("bad", grid.Rows[0].Tag); Assert.Equal("Major", grid.Rows[0].Cells["Warnings"].Value);
            grid.CurrentCell = grid.Rows[0].Cells[0]; form.SetViewWeights(new(0, 0, 100, 0, 0));
            Assert.Equal("bad", grid.CurrentRow!.Tag); Assert.Equal("good", grid.Rows[0].Tag);
            form.SetViewWeights(new(0, 0, 0, 0, 0)); Assert.All(grid.Rows.Cast<DataGridViewRow>(), r => Assert.Equal("—", r.Cells[0].Value));
            await form.DrainAsync(); form.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
