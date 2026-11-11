using System.Text.Json;
using System.Windows.Forms;
using Bisik.Core;
using Bisik.Models;
using Bisik.UI;

namespace Bisik.Tests;

public sealed class ExperimentWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Bisik.WorkspaceTests", Guid.NewGuid().ToString("N"));
    private static AppSettings Settings => new() { PostProcessor = TranscriptionServiceTextTests.Settings() };
    private ExperimentsForm Form(AppSettings? settings = null, Action<Exception>? error = null) => new(settings ?? Settings,
        _ => throw new Exception("No network"), (_, _, _, _) => throw new Exception("No capture"),
        new ExperimentStore(_root), () => new ExperimentsFormTests.Lease(), error ?? (ex => throw new Exception("Unexpected error", ex)));
    private static T Control<T>(ExperimentsForm form, string name) where T : System.Windows.Forms.Control =>
        ExperimentsFormTests.All(form).OfType<T>().Single(c => c.AccessibleName == name);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task WindowCloseAndAppShutdownRestoreEveryEditableSetting(bool shutdown) => StaTestHost.RunAsync(async () =>
    {
        using (var form = Form())
        {
            form.Show(); await form.Initialization;
            Control<TextBox>(form, "Reference script").Text = "Read Ελληνικά, HAPPI and 123.\r\nKeep the second line.";
            var models = Control<DataGridView>(form, "Models"); models.Rows.Clear();
            models.Rows.Add(" First candidate ", "deployment one", "model-one", "default, low, high", true, false);
            models.Rows.Add("Second", "deployment-two", "model-two", "none,minimal,max", false, true);
            // Closing while a cell is still being edited must commit its newest value.
            models.CurrentCell = models.Rows[1].Cells[0]; Assert.True(models.BeginEdit(false));
            ((TextBox)models.EditingControl).Text = "Renamed candidate";
            var prompts = Control<DataGridView>(form, "Named prompts"); prompts.Rows.Clear();
            prompts.Rows.Add("Careful cleanup", "First prompt\r\n preserve spaces "); prompts.Rows.Add("Translation", "Second prompt");
            prompts.CurrentCell = prompts.Rows[1].Cells[0];
            Control<TextBox>(form, "Selected system prompt").Text = "Edited second prompt\r\nΕλληνικά";
            var terms = Control<DataGridView>(form, "Reviewed identifiers");
            terms.Rows.Add(true, "HAPPI", "happi, happy"); terms.Rows.Add(false, "ABB", " abb , AbB ");
            Control<TextBox>(form, "Judge model").Text = "fixed judge"; Control<TextBox>(form, "Judge deployment").Text = "judge deployment";
            Control<ComboBox>(form, "Judge effort").SelectedItem = "xhigh";
            Control<NumericUpDown>(form, "Repetitions").Value = 3;
            foreach (var (name, value) in new[] { (nameof(ExperimentLimits.MaximumTrials), 75), (nameof(ExperimentLimits.RecordingSeconds), 45),
                (nameof(ExperimentLimits.RecognitionSeconds), 60), (nameof(ExperimentLimits.TrialSeconds), 30), (nameof(ExperimentLimits.QuietHintSeconds), 7) })
                Control<NumericUpDown>(form, name).Value = value;
            ExperimentsFormTests.All(form).OfType<CheckBox>().Single(c => c.Text == "Retain audio").Checked = true;
            ExperimentsFormTests.All(form).OfType<CheckBox>().Single(c => c.Text == "Retry one transient failure").Checked = true;
            form.SetViewWeights(new(10, 20, 30, 40, 50, 0.125));
            if (shutdown) await form.CloseAsync();
            else { form.Close(); await ExperimentsFormTests.WaitUntilAsync(() => form.IsDisposed); }
        }
        var first = (await new ExperimentStore(_root).LoadWorkspaceAsync(CancellationToken.None))!.Value;
        using var reopened = Form(new() { PostProcessor = Settings.PostProcessor with { Model = "new default" } });
        reopened.Show(); await reopened.Initialization; reopened.RefreshIdleSettings(new());
        var definition = reopened.ReadDefinition();
        Assert.Equal(first.Workspace.Reference, definition.Reference);
        Assert.Equal("Renamed candidate", definition.Models[1].Name);
        Assert.Equal(new string?[] { null, "low", "high" }, definition.Models[0].Efforts);
        Assert.Equal(new string?[] { "none", "minimal", "max" }, definition.Models[1].Efforts);
        Assert.True(definition.Models[0].StreamCompletion); Assert.True(definition.Models[1].UsePrediction);
        Assert.Equal("Careful cleanup", definition.Prompts[0].Name); Assert.Equal("First prompt\r\n preserve spaces ", definition.Prompts[0].SystemPrompt);
        Assert.Equal("Translation", definition.Prompts[1].Name); Assert.Equal("Edited second prompt\r\nΕλληνικά", definition.Prompts[1].SystemPrompt);
        Assert.Equal(1, Control<DataGridView>(reopened, "Named prompts").CurrentRow!.Index);
        Assert.Single(definition.Terms); Assert.Equal("HAPPI", definition.Terms[0].Canonical);
        Assert.Equal("ABB", Control<DataGridView>(reopened, "Reviewed identifiers").Rows[1].Cells[1].Value);
        Assert.Equal("xhigh", definition.Judge.Efforts[0]); Assert.Equal("fixed judge", definition.Judge.Model);
        Assert.Equal("judge deployment", definition.Judge.Deployment); Assert.Equal(first.Workspace.Limits, definition.Limits);
        Assert.Equal(first.Workspace.Weights, definition.Weights); Assert.True(definition.RetainAudio);
        Assert.Equal(ExperimentMatrix.ConnectionIdentity(Settings.PostProcessor), definition.ConnectionFingerprint);
        await reopened.CloseAsync();
        var second = (await new ExperimentStore(_root).LoadWorkspaceAsync(CancellationToken.None))!.Value;
        Assert.Equal(JsonSerializer.Serialize(first.Workspace), JsonSerializer.Serialize(second.Workspace));
    });

    [Fact]
    public Task IncompleteRowsAndEmptyReferenceSurviveWithoutRunValidation() => StaTestHost.RunAsync(async () =>
    {
        using (var form = Form())
        {
            form.Show(); var models = Control<DataGridView>(form, "Models"); models.Rows.Clear(); models.Rows.Add("Draft", "", "", "", false, false);
            var prompts = Control<DataGridView>(form, "Named prompts"); prompts.Rows.Clear(); prompts.Rows.Add("", "Unfinished prompt");
            Control<DataGridView>(form, "Reviewed identifiers").Rows.Add(false, "", "unfinished, alias,");
            Assert.Throws<ArgumentException>(() => form.ReadDefinition()); await form.CloseAsync();
        }
        using var reopened = Form(); reopened.Show(); await reopened.Initialization;
        Assert.Equal("", Control<TextBox>(reopened, "Reference script").Text);
        Assert.Equal("Draft", Control<DataGridView>(reopened, "Models").Rows[0].Cells[0].Value);
        Assert.Equal("", Control<DataGridView>(reopened, "Models").Rows[0].Cells[3].Value);
        Assert.Equal("Unfinished prompt", Control<DataGridView>(reopened, "Named prompts").Rows[0].Cells[1].Value);
        Assert.Equal("unfinished, alias,", Control<DataGridView>(reopened, "Reviewed identifiers").Rows[0].Cells[2].Value);
        await reopened.CloseAsync();
    });

    [Fact]
    public Task SavedRawReopensWithLatestEditsWithoutOverwritingItsOriginalSetup() => StaTestHost.RunAsync(async () =>
    {
        var store = new ExperimentStore(_root); var definition = ExperimentRunnerTests.Definition(Settings.PostProcessor);
        var original = new ExperimentRun(definition, new(Guid.NewGuid(), "authoritative saved raw", 1234, 2000, definition.InputIdentity),
            ExperimentState.Completed, 1, []);
        await store.SaveSetupAsync(definition, Settings, CancellationToken.None); await store.SaveAsync(original, CancellationToken.None);
        using (var form = Form())
        {
            form.Show(); await form.LoadAsync(definition.Id);
            Control<TextBox>(form, "Reference script").Text = "Changed reference for the next revision.";
            var prompts = Control<DataGridView>(form, "Named prompts"); prompts.Rows.Add("Second", "second prompt");
            prompts.CurrentCell = prompts.Rows[1].Cells[1]; Assert.True(prompts.BeginEdit(false));
            ((TextBox)prompts.EditingControl).Text = "direct cell edit";
            await form.CloseAsync();
        }
        using var reopened = Form(); reopened.Show(); await reopened.Initialization;
        Assert.Equal(original.Take, reopened.CurrentRun!.Take);
        Assert.Equal(original.Definition.ConfigurationIdentity, reopened.CurrentRun.Definition.ConfigurationIdentity);
        Assert.Equal("Changed reference for the next revision.", reopened.ReadDefinition().Reference);
        Assert.Equal("direct cell edit", reopened.ReadDefinition().Prompts[1].SystemPrompt);
        Assert.Equal(definition.Reference, (await store.LoadAsync(definition.Id, CancellationToken.None)).Definition.Reference);
        await reopened.CloseAsync();
    });

    [Fact]
    public Task DraftPromptBufferSurvivesWithoutASelectedPromptRow() => StaTestHost.RunAsync(async () =>
    {
        using (var form = Form())
        {
            form.Show(); Control<DataGridView>(form, "Named prompts").Rows.Clear();
            Control<TextBox>(form, "Selected system prompt").Text = "Still drafting a prompt."; await form.CloseAsync();
        }
        using var reopened = Form(); reopened.Show(); await reopened.Initialization;
        Assert.Equal("Still drafting a prompt.", Control<TextBox>(reopened, "Selected system prompt").Text);
        await reopened.CloseAsync();
    });

    [Fact]
    public Task FailedSaveKeepsWindowOpenAndPreviousSnapshotUntilCloseIsRetried() => StaTestHost.RunAsync(async () =>
    {
        var store = new ExperimentStore(_root); await store.SaveWorkspaceAsync(new(), Settings, CancellationToken.None);
        var path = Path.Combine(_root, "workspace.json"); var original = await File.ReadAllTextAsync(path);
        var errors = new List<Exception>(); using var form = Form(error: errors.Add); form.Show(); await form.Initialization;
        Control<TextBox>(form, "Reference script").Text = "New draft";
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            form.Close(); await ExperimentsFormTests.WaitUntilAsync(() => errors.Count > 0);
            Assert.False(form.IsDisposed); Assert.True(form.Enabled); Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
        }
        form.Close(); await ExperimentsFormTests.WaitUntilAsync(() => form.IsDisposed);
        Assert.Equal("New draft", (await store.LoadWorkspaceAsync(CancellationToken.None))!.Value.Workspace.Reference);
    });

    [Fact]
    public async Task WorkspaceEncryptsConnectionAndPreservesLastSnapshotOnCancelledWrite()
    {
        var store = new ExperimentStore(_root);
        Assert.Null(await store.LoadWorkspaceAsync(CancellationToken.None));
        var settings = Settings with { PostProcessor = Settings.PostProcessor with { Provider = Settings.PostProcessor.Provider with
            { ApiKey = "workspace-private-key", Endpoint = "https://private-workspace.invalid", ExtraHeaders = new() { ["X-Token"] = "private-header" } } } };
        var workspace = new ExperimentWorkspace { Reference = "private reference", Prompts = [new("unfinished", "")],
            Compatibility = [new("fingerprint", "low", ExperimentVerification.Verified)] };
        await store.SaveWorkspaceAsync(workspace, settings, CancellationToken.None);
        var path = Path.Combine(_root, "workspace.json"); var original = await File.ReadAllTextAsync(path);
        foreach (var secret in new[] { "workspace-private-key", "private-workspace.invalid", "private-header", "private reference" }) Assert.DoesNotContain(secret, original);
        var loaded = (await new ExperimentStore(_root).LoadWorkspaceAsync(CancellationToken.None))!.Value;
        Assert.Equal("workspace-private-key", loaded.Connection.PostProcessor.Provider.ApiKey);
        Assert.Equal("https://private-workspace.invalid", loaded.Connection.PostProcessor.Provider.Endpoint);
        Assert.Equal("private-header", loaded.Connection.PostProcessor.Provider.ExtraHeaders!["X-Token"]);
        Assert.Equal(JsonSerializer.Serialize(workspace), JsonSerializer.Serialize(loaded.Workspace));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveWorkspaceAsync(workspace with { Reference = "changed" }, settings, cancelled.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path)); Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public Task UnreadableWorkspaceReportsFailureAndIsNotOverwritten() => StaTestHost.RunAsync(async () =>
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "workspace.json"); await File.WriteAllTextAsync(path, "broken workspace");
        var errors = new List<Exception>(); using var form = Form(error: errors.Add); form.Show(); await form.Initialization;
        Assert.Single(errors); await form.CloseAsync(); Assert.Equal("broken workspace", await File.ReadAllTextAsync(path));
    });

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
