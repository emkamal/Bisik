using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public sealed class ExperimentsFormTests
{
    internal static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
    private static AppSettings Settings => new() { PostProcessor = TranscriptionServiceTextTests.Settings(), Provider = new() { Type = ProviderType.OpenAi } };

    [Fact]
    public Task ValidationAndNativeEditorsPreserveLongText() => StaTestHost.RunAsync(async () =>
    {
        using var form = new ExperimentsForm(Settings, _ => throw new Exception(), (_, _, _, _) => throw new Exception(),
            new ExperimentStore(), () => new Lease());
        form.Show();
        Assert.Throws<ArgumentException>(() => form.ReadDefinition());
        var script = All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Reference script");
        script.Text = string.Join("\r\n", Enumerable.Repeat("Do not change 12 HAPPI instances.", 100));
        Assert.Equal(script.Text, form.ReadDefinition().Reference);
        var prompt = All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Selected system prompt");
        prompt.Text = "Keep identifiers.\r\n" + new string('x', 4000);
        Assert.Equal(prompt.Text, form.ReadDefinition().Prompts[0].SystemPrompt);
        await form.DrainAsync(); form.Close();
    });

    [Fact]
    public Task RunRejectsRepeatedStartAndCloseCancelsCapture() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.FormTests", Guid.NewGuid().ToString("N"));
        var wave = new ExperimentSessionTests.TestCapture(); ExperimentSession? session = null; var captures = 0; var leases = 0;
        try
        {
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((_, _, _) => Task.FromResult("output")));
            using var form = new ExperimentsForm(Settings, _ => service, (settings, definition, ct, progress) =>
            {
                captures++;
                var recognition = new TranscriptionService(new Dictionary<ProviderType, Bisik.Providers.ITranscriptionProvider>
                    { [ProviderType.OpenAi] = new Provider() }, new TranscriptionServiceTextTests.Processor((_, _, _) => throw new Exception()),
                    new HistoryLogger(root), new ErrorLogger(root), () => { }, () => { });
                return session = new(new AudioRecorder(outputDirectory: root, captureFactory: (_, _) => wave), recognition, settings, definition, ct, progress);
            }, new(root), () => { leases++; return new Lease(); });
            form.Show(); All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Reference script").Text = "Read once.";
            var run = form.StartWorkAsync(false, false);
            await WaitUntilAsync(() => session is not null);
            Assert.True(form.IsBusy); Assert.Same(run, form.StartWorkAsync(false, false));
            Assert.Equal(1, captures); Assert.Equal(1, leases);
            var script = All(form).OfType<TextBox>().Single(t => t.AccessibleName == "Reference script");
            Assert.True(script.ReadOnly); Assert.True(script.Enabled);
            form.Close(); await run;
            await WaitUntilAsync(() => form.IsDisposed);
            Assert.True(wave.Disposed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    [Fact]
    public Task SavedDraftRestoresBoundsAndOptionalVariants() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.DraftTests", Guid.NewGuid().ToString("N"));
        try
        {
            var definition = ExperimentRunnerTests.Definition(Settings.PostProcessor) with { Limits = new() { MaximumTrials = 15, RecordingSeconds = 30, TrialSeconds = 10 },
                Models = [new("advanced", "dep", "one", [null], true, true)] };
            var store = new ExperimentStore(root); await store.SaveSetupAsync(definition, Settings, CancellationToken.None);
            using var form = new ExperimentsForm(Settings, _ => throw new Exception(), (_, _, _, _) => throw new Exception(), store, () => new Lease());
            form.Show(); await form.LoadSetupAsync(definition.Id); var loaded = form.ReadDefinition();
            Assert.Equal(definition.Limits, loaded.Limits); Assert.True(loaded.Models[0].StreamCompletion); Assert.True(loaded.Models[0].UsePrediction);
            Assert.Null(form.CurrentRun); await form.DrainAsync(); form.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    internal sealed class Provider : Bisik.Providers.ITranscriptionProvider
    {
        public string Name => "test"; public ProviderType Type => ProviderType.OpenAi;
        public Task<string> TranscribeAsync(string path, string language, CancellationToken ct) => Task.FromResult("raw");
    }
    internal sealed class Lease : IDisposable { public void Dispose() { } }
}
