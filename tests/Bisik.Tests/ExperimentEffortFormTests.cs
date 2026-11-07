using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.UI;

namespace Bisik.Tests;

public class ExperimentEffortFormTests
{
    private static AppSettings Settings => new() { Provider = new() { Type = ProviderType.OpenAi }, PostProcessor = TranscriptionServiceTextTests.Settings() };
    private static DataGridView Models(ExperimentsForm form) => ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Models");
    private static void SelectLow(ExperimentsForm form)
    {
        Models(form).Rows[0].SetValues("AstraLow", "astra", "astra", "low", false, false);
        ExperimentsFormTests.All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Reference script").Text = "Read once.";
    }
    private static TranscriptionService Service(HttpClient client, string root) => new(new Dictionary<ProviderType, Bisik.Providers.ITranscriptionProvider>(),
        new LLMPostProcessor(client), new HistoryLogger(root), new ErrorLogger(root), () => throw new Exception(), () => throw new Exception());

    [Fact]
    public Task StartVerifiesUnknownEffortOnceAndSavedEvidenceAvoidsAnotherProbe() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.EffortTests", Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<(string User, string? Effort)>();
            using var client = new HttpClient(new TranscriptionServiceTextTests.Handler(async (request, _) =>
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var user = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                var effort = payload.RootElement.TryGetProperty("reasoning_effort", out var e) ? e.GetString() : null;
                lock (calls) calls.Add((user, effort));
                return TranscriptionServiceTextTests.Complete(payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == LLMExperimentJudge.Rubric
                    ? "{\"meaning\":100,\"readability\":100,\"warnings\":[]}" : "edited");
            }));
            var service = Service(client, root); var store = new ExperimentStore(root);
            var wave = new ExperimentSessionTests.TestCapture(); ExperimentSession? capture = null;
            using var form = new ExperimentsForm(Settings, _ => service, (settings, definition, ct, progress) =>
            {
                var recognize = new TranscriptionService(new Dictionary<ProviderType, Bisik.Providers.ITranscriptionProvider>
                    { [ProviderType.OpenAi] = new ExperimentsFormTests.Provider() },
                    new TranscriptionServiceTextTests.Processor((_, _, _) => throw new Exception()), new HistoryLogger(root), new ErrorLogger(root), () => { }, () => { });
                return capture = new(new AudioRecorder(outputDirectory: root, captureFactory: (_, _) => wave), recognize, settings, definition, ct, progress);
            }, store, () => new ExperimentsFormTests.Lease());
            form.Show(); SelectLow(form);
            var prompts = ExperimentsFormTests.All(form).OfType<DataGridView>().Single(g => g.AccessibleName == "Named prompts");
            prompts.Rows.Add("Duplicate prompt", form.ReadDefinition().Prompts[0].SystemPrompt);
            var work = form.StartWorkAsync(false, false); await ExperimentsFormTests.WaitUntilAsync(() => capture is not null);
            wave.Emit(new byte[3200]); capture!.Done(); await work;
            Assert.Equal(ExperimentState.Completed, form.CurrentRun!.State);
            Assert.Equal(1, calls.Count(c => c.User == "This is a compatibility test."));
            var trial = Assert.Single(form.CurrentRun.Trials.Where(t => !t.Candidate.IsBaseline));
            Assert.Equal("low", trial.Candidate.EffectiveEffort); Assert.Equal(ExperimentVerification.Verified, trial.Candidate.Verification);
            Assert.Contains(calls, c => c.User == "raw" && c.Effort == "low");
            var id = form.CurrentRun.Definition.Id; await form.DrainAsync(); form.Close();
            using var reopened = new ExperimentsForm(Settings, _ => service, (_, _, _, _) => throw new Exception("No new capture"), store, () => new ExperimentsFormTests.Lease());
            reopened.Show(); await reopened.LoadAsync(id); await reopened.StartWorkAsync(false, true);
            Assert.Equal(1, calls.Count(c => c.User == "This is a compatibility test."));
            Assert.Equal(ExperimentState.Completed, reopened.CurrentRun!.State); await reopened.DrainAsync(); reopened.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public Task RejectedEffortNamesThePairAndBlocksCaptureUntilExplicitRetry() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.EffortRejectTests", Guid.NewGuid().ToString("N"));
        var requests = 0; var reject = true;
        using var client = new HttpClient(new TranscriptionServiceTextTests.Handler((_, _) =>
            { requests++; return Task.FromResult(reject ? new HttpResponseMessage(HttpStatusCode.BadRequest) : TranscriptionServiceTextTests.Complete("compatible")); }));
        using var form = new ExperimentsForm(Settings, _ => Service(client, root), (_, _, _, _) => throw new Exception("Must not capture"),
            new(root), () => new ExperimentsFormTests.Lease());
        form.Show(); SelectLow(form); await form.StartWorkAsync(false, false);
        var log = ExperimentsFormTests.All(form).OfType<ListBox>().Single();
        Assert.Contains(log.Items.Cast<string>(), s => s.Contains("AstraLow / low") && s.Contains("http-400"));
        Assert.Null(form.CurrentRun); Assert.False(form.IsBusy); Assert.Equal(1, requests);
        await form.StartWorkAsync(false, false); Assert.Equal(1, requests);
        reject = false; await form.StartWorkAsync(true, false); Assert.Equal(2, requests);
        await form.StartWorkAsync(true, false); Assert.Equal(2, requests);
        Assert.Contains(log.Items.Cast<string>(), s => s.Contains("Verified")); await form.DrainAsync(); form.Close();
    });

    [Fact]
    public Task CancelDuringProbeNeverStartsCapture() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.EffortCancelTests", Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new TranscriptionServiceTextTests.Handler((_, _) => { started.TrySetResult(); return pending.Task; }));
        using var form = new ExperimentsForm(Settings, _ => Service(client, root), (_, _, _, _) => throw new Exception("Must not capture"),
            new(root), () => new ExperimentsFormTests.Lease());
        form.Show(); SelectLow(form); var work = form.StartWorkAsync(false, false); await started.Task;
        form.Close(); pending.SetResult(TranscriptionServiceTextTests.Complete("late")); await work; await form.DrainAsync();
        Assert.Null(form.CurrentRun); Assert.False(form.IsBusy);
    });
}
