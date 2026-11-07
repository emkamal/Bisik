using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.UI;

namespace Bisik.Tests;

public class ExperimentRerunFormTests
{
    [Fact]
    public Task EditedModelsAndPromptsRerunAllCombinationsWithoutRecognitionAndPreserveOldRun() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.EditedRerunTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { PostProcessor = TranscriptionServiceTextTests.Settings() };
            var definition = ExperimentModelTests.Definition() with { ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(settings.PostProcessor),
                Models = [new("One", "one", "one", [null])], Prompts = [new("First", "first prompt")], Judge = new("Judge", "judge", "judge", [null]), RetainAudio = true };
            var take = new ExperimentTake(Guid.NewGuid(), "saved raw exactly", 1234.5, 4321, definition.InputIdentity);
            var old = new ExperimentRun(definition, take, ExperimentState.Completed, 1,
                [new() { Id = "raw", Candidate = ExperimentCandidate.Baseline, Generation = ExperimentGenerationStatus.Succeeded, Output = take.RawText, CleanupMs = 0 }]);
            var store = new ExperimentStore(root); await store.SaveSetupAsync(definition, settings, CancellationToken.None);
            var wave = Path.Combine(root, "source.wav"); await File.WriteAllBytesAsync(wave, [1, 2, 3]); await store.SaveAsync(old, CancellationToken.None, wave);
            var oldJson = await File.ReadAllTextAsync(Path.Combine(store.DirectoryFor(definition.Id), "run.json"));
            var calls = new List<(string Input, string Model, string Prompt)>(); var captures = 0;
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((input, config, _) =>
            {
                lock (calls) calls.Add((input, config.Model, config.SystemPrompt));
                return Task.FromResult(config.Model == "judge" ? "{\"meaning\":100,\"readability\":100,\"warnings\":[]}"
                    : $"edited {config.Model}: {config.SystemPrompt}");
            }));
            using var form = new ExperimentsForm(settings, _ => service, (_, _, _, _) => { captures++; throw new Exception("No recording allowed"); }, store, () => new ExperimentsFormTests.Lease());
            form.Show(); await form.LoadAsync(definition.Id);
            var models = ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Models");
            models.Rows.Add("Two", "two", "two", "default", false, false);
            var prompts = ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Named prompts");
            prompts.Rows.Add("Second", "second prompt");
            ExperimentsFormTests.All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Reference script").Text = "New reference for saved raw.";
            var terms = ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Reviewed identifiers");
            terms.Rows.Add(true, "saved", "");
            var editedSetup = form.ReadDefinition(); await store.SaveSetupAsync(editedSetup, settings, CancellationToken.None);
            await form.LoadSetupAsync(editedSetup.Id); Assert.Equal(take.Id, form.CurrentRun!.Take.Id);
            await form.StartWorkAsync(false, true);
            var rerun = form.CurrentRun!; Assert.Equal(ExperimentState.Completed, rerun.State); Assert.Equal(5, rerun.Trials.Length);
            Assert.NotEqual(definition.Id, rerun.Definition.Id); Assert.Equal(definition.Id, rerun.Definition.PreviousExperimentId);
            Assert.Equal(take.Id, rerun.Take.Id); Assert.Equal(take.RawText, rerun.Take.RawText); Assert.Equal(take.RawIdentity, rerun.Take.RawIdentity);
            Assert.Equal(take.SharedStopToRawMs, rerun.Take.SharedStopToRawMs); Assert.Equal(rerun.Definition.InputIdentity, rerun.Take.InputIdentity);
            Assert.Equal(0, captures); var cleanups = calls.Where(c => c.Model != "judge").ToArray(); Assert.Equal(4, cleanups.Length);
            Assert.All(cleanups, c => Assert.Equal(take.RawText, c.Input));
            Assert.Equal(4, cleanups.Select(c => (c.Model, c.Prompt)).Distinct().Count());
            Assert.Equal(oldJson, await File.ReadAllTextAsync(Path.Combine(store.DirectoryFor(definition.Id), "run.json")));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(store.AudioPathFor(rerun.Definition.Id)!));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(store.AudioPathFor(definition.Id)!));
            var reopened = await store.LoadAsync(rerun.Definition.Id, CancellationToken.None); Assert.Equal(5, reopened.Trials.Length);
            await form.DrainAsync(); form.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
