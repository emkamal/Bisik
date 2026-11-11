using System.Collections.Immutable;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.UI;

public sealed class ExperimentsForm : Form
{
    private AppSettings _settings;
    private readonly Func<AppSettings, TranscriptionService> _serviceFactory;
    private readonly Func<AppSettings, ExperimentDefinition, CancellationToken, IProgress<ExperimentCaptureProgress>, ExperimentSession> _captureFactory;
    private readonly ExperimentStore _store;
    private readonly Func<IDisposable?> _acquireOperation;
    private readonly Action<Exception> _reportError;
    private readonly ToolTip _help = new() { ShowAlways = true, AutoPopDelay = 30000 };
    private readonly TextBox _reference = Editor("Reference script");
    private readonly TextBox _prompt = Editor("Selected system prompt");
    private readonly DataGridView _models = Grid("Models");
    private readonly DataGridView _prompts = Grid("Named prompts");
    private readonly DataGridView _terms = Grid("Reviewed identifiers");
    private readonly TextBox _judgeModel = new() { Dock = DockStyle.Fill, AccessibleName = "Judge model" };
    private readonly TextBox _judgeDeployment = new() { Dock = DockStyle.Fill, AccessibleName = "Judge deployment" };
    private readonly ComboBox _judgeEffort = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Judge effort" };
    private readonly NumericUpDown _repeats = new() { Minimum = 1, Maximum = 5, Value = 1, AccessibleName = "Repetitions" };
    private readonly CheckBox _retain = new() { Text = "Retain audio", AutoSize = true };
    private readonly Dictionary<string, NumericUpDown> _limits = [];
    private readonly CheckBox _retry = new() { Text = "Retry one transient failure", AutoSize = true };
    private readonly Panel _setup = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Button _start = new() { Text = "Start", AutoSize = true };
    private readonly Button _done = new() { Text = "Done", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, AccessibleName = "Bounded run log" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _request = new() { Dock = DockStyle.Fill, Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _resultsPage = new("Results");
    private readonly Label _winner = new() { Dock = DockStyle.Fill, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly DataGridView _results = Grid("Ranked results");
    private readonly ExperimentDetailsView _detail = new();
    private ExperimentWeights _weights = new();
    private ExperimentWeightsForm? _weightsForm;
    private bool _refreshing;
    private bool _previewing;
    private bool _connectionAvailable = true;
    private CancellationTokenSource? _cancellation;
    private ExperimentSession? _capture;
    private Task _work = Task.CompletedTask;
    private Task _checkpoint = Task.CompletedTask;
    private readonly List<ExperimentCompatibility> _evidence = [];
    private Guid _runId;
    private bool _closing;
    private bool _allowClose;
    private bool _acceptProgress;
    private int _promptIndex = -1;
    private ExperimentDefinition? _loadedDefinition;
    private bool _populatingSetup;
    private bool _restoring;
    private bool _hasWorkspace;
    private bool _workspaceLoadFailed;
    private string? _savedWorkspaceIdentity;
    public Task Initialization { get; }
    public bool IsBusy { get; private set; }
    public ExperimentRun? CurrentRun { get; private set; }
    public async Task DrainAsync()
    {
        _cancellation?.Cancel();
        await Initialization;
        try { await _work; }
        finally
        {
            try { await _checkpoint; }
            finally
            {
                if (!_workspaceLoadFailed)
                {
                    var workspace = ReadWorkspace();
                    var identity = ExperimentIdentity.Hash("workspace-v1", workspace, _settings);
                    if (identity != _savedWorkspaceIdentity)
                    {
                        await _store.SaveWorkspaceAsync(workspace, _settings, CancellationToken.None);
                        _savedWorkspaceIdentity = identity;
                    }
                }
            }
        }
    }
    public async Task CloseAsync()
    {
        _closing = true; Enabled = false;
        await DrainAsync();
        _allowClose = true; Close();
    }

    public ExperimentsForm(AppSettings settings, Func<AppSettings, TranscriptionService> serviceFactory,
        Func<AppSettings, ExperimentDefinition, CancellationToken, IProgress<ExperimentCaptureProgress>, ExperimentSession> captureFactory,
        ExperimentStore store, Func<IDisposable?> acquireOperation, Action<Exception>? reportError = null)
    {
        _settings = settings.Snapshot(); _serviceFactory = serviceFactory; _captureFactory = captureFactory;
        _store = store; _acquireOperation = acquireOperation;
        _reportError = reportError ?? new ErrorLogger().Log;
        Text = "Bisik Experiments"; AutoScaleMode = AutoScaleMode.Dpi; Size = new(1180, 800); MinimumSize = new(760, 540);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new(8) };
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Absolute, 18)); root.RowStyles.Add(new(SizeType.Absolute, 12)); root.RowStyles.Add(new(SizeType.Absolute, 70));
        var setupPage = new TabPage("Setup");
        var split = new SplitContainer { Size = new(1100, 650), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 480, Panel1MinSize = 200, Panel2MinSize = 200 };
        var script = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        script.RowStyles.Add(new(SizeType.AutoSize)); script.RowStyles.Add(new(SizeType.Percent, 100));
        script.Controls.Add(new Label { Text = "Read this reference once. Scroll/copy remains available while running.", AutoSize = true }, 0, 0);
        script.Controls.Add(_reference, 0, 1); split.Panel1.Controls.Add(script); split.Panel2.Controls.Add(_setup); setupPage.Controls.Add(split);
        // Keep editable rows visible beneath the taller headers; small windows scroll this setup pane.
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, RowCount = 7, ColumnCount = 1 };
        foreach (var height in new[] { 130, 130, 80, 130, 60 }) fields.RowStyles.Add(new(SizeType.Absolute, height));
        fields.RowStyles.Add(new(SizeType.AutoSize)); fields.RowStyles.Add(new(SizeType.AutoSize));
        AddColumns(_models, "Name", "Deployment", "Model", "Efforts (default,low,...)");
        _models.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Stream (verified)" });
        _models.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Prediction (verified)" });
        AddColumns(_prompts, "Name", "Prompt"); AddColumns(_terms, "Canonical", "Wrong aliases (comma separated)");
        _terms.Columns.Insert(0, new DataGridViewCheckBoxColumn { HeaderText = "Use", Name = "Use", Width = 45 });
        _models.Rows.Add("Candidate", settings.PostProcessor.Provider.DeploymentName, settings.PostProcessor.Model, "default");
        _prompts.Rows.Add("Current cleanup", PostProcessorPresets.ResolveSystemPrompt(settings.PostProcessor));
        _judgeModel.Text = settings.PostProcessor.Model; _judgeDeployment.Text = settings.PostProcessor.Provider.DeploymentName;
        _judgeEffort.Items.AddRange(new object[] { "default", "none", "minimal", "low", "medium", "high", "xhigh", "max" }); _judgeEffort.SelectedIndex = 0;
        fields.Controls.Add(Group("Candidate models × effort × prompts", _models), 0, 0);
        fields.Controls.Add(Group("Named prompts (edit selected prompt below)", _prompts), 0, 1);
        fields.Controls.Add(_prompt, 0, 2); fields.Controls.Add(Group("Reviewed identifiers; suggestions require a check", _terms), 0, 3);
        fields.Controls.Add(_preview, 0, 4);
        var judgeFields = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        judgeFields.Controls.AddRange([new Label { Text = "Fixed judge model / deployment / effort", AutoSize = true }, _judgeModel, _judgeDeployment, _judgeEffort]);
        _judgeModel.Width = 150; _judgeDeployment.Width = 150; fields.Controls.Add(judgeFields, 0, 5);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var suggest = new Button { Text = "Suggest terms", AutoSize = true };
        suggest.Click += (_, _) => { foreach (var term in ExperimentScoring.SuggestTerms(_reference.Text)) _terms.Rows.Add(false, term, ""); };
        options.Controls.AddRange([new Label { Text = "Repeats", AutoSize = true }, _repeats, _retain, suggest]); fields.Controls.Add(options, 0, 6);
        foreach (var (name, value, maximum) in new[] { (nameof(ExperimentLimits.MaximumTrials), 100, 100),
            (nameof(ExperimentLimits.RecordingSeconds), 300, 300), (nameof(ExperimentLimits.RecognitionSeconds), 120, 120),
            (nameof(ExperimentLimits.TrialSeconds), 120, 120), (nameof(ExperimentLimits.QuietHintSeconds), 5, 60) })
        {
            var input = new NumericUpDown { Minimum = 1, Maximum = maximum, Value = value, Width = 60, AccessibleName = name };
            var label = name switch { nameof(ExperimentLimits.MaximumTrials) => "Max trials", nameof(ExperimentLimits.RecordingSeconds) => "Recording limit (s)",
                nameof(ExperimentLimits.RecognitionSeconds) => "Recognition limit (s)", nameof(ExperimentLimits.TrialSeconds) => "Trial limit (s)", _ => "Quiet hint (s)" };
            _limits[name] = input; options.Controls.Add(new Label { Text = label, AutoSize = true }); options.Controls.Add(input);
            _help.SetToolTip(input, $"{label}: 1–{maximum}; default {value}.");
            input.ValueChanged += (_, _) => Preview();
        }
        options.Controls.Add(_retry);
        _setup.Controls.Add(fields); BuildResults(); _tabs.TabPages.AddRange([setupPage, _resultsPage]); root.Controls.Add(_tabs, 0, 0);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var probe = Command("Verify unknown efforts", () => StartWorkAsync(true, false));
        var open = Command("Open saved…", OpenAsync); var save = Command("Save setup", SaveSetupAsync);
        var rerun = Command("Rerun saved raw", () => StartWorkAsync(false, true));
        _start.Click += async (_, _) => await StartWorkAsync(false, false); _done.Click += (_, _) => _capture?.Done();
        _cancel.Click += (_, _) => _cancellation?.Cancel(); commands.Controls.AddRange([_start, _done, _cancel, probe, open, save, rerun, _status]);
        root.Controls.Add(commands, 0, 1); root.Controls.Add(_progress, 0, 2); root.Controls.Add(_request, 0, 3); root.Controls.Add(_log, 0, 4); Controls.Add(root);
        _help.SetToolTip(probe, "Explicit calls verify unknown effort parameters for this exact connection and model. These are separate from measured trials.");
        _help.SetToolTip(_retain, "Off by default. Reference, raw text and results are saved; audio is retained only when selected.");
        _help.SetToolTip(_repeats, "1–5 randomized rounds, at most 100 cleanup requests plus a separate fixed judge phase.");
        // SelectionChanged can run before CurrentRow moves, so follow the committed current cell.
        _prompts.CurrentCellChanged += (_, _) => SelectPrompt();
        _prompt.TextChanged += (_, _) =>
        {
            if (!_populatingSetup && _promptIndex >= 0 && _promptIndex < _prompts.Rows.Count && !_prompts.Rows[_promptIndex].IsNewRow)
                _prompts.Rows[_promptIndex].Cells[1].Value = _prompt.Text;
        };
        _prompts.CellValueChanged += (_, e) =>
        {
            if (!_populatingSetup && e.RowIndex == _promptIndex && e.ColumnIndex == 1) SelectPrompt();
        };
        _reference.TextChanged += (_, _) => Preview(); _models.CellEndEdit += (_, _) => Preview(); _repeats.ValueChanged += (_, _) => Preview();
        FormClosing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; if (_closing) return;
            try { await CloseAsync(); }
            catch (Exception ex)
            {
                _closing = false; Enabled = true;
                _status.Text = "Could not save the experiment workspace. Close again to retry.";
                Log(_status.Text); _reportError(ex);
            }
        };
        FormClosed += (_, _) => { _weightsForm?.Dispose(); _help.Dispose(); _cancellation?.Dispose(); };
        SelectPrompt(); Preview();
        Initialization = RestoreWorkspaceAsync();
    }

    private static TextBox Editor(string name) => new() { Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true,
        AcceptsTab = true, Dock = DockStyle.Fill, AccessibleName = name, WordWrap = true };
    private static DataGridView Grid(string name)
    {
        var headerFont = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false,
            AccessibleName = name, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 56, ColumnHeadersDefaultCellStyle = new() { Font = headerFont, WrapMode = DataGridViewTriState.True,
                Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new(4) } };
        grid.Disposed += (_, _) => headerFont.Dispose();
        return grid;
    }
    private static void AddColumns(DataGridView grid, params string[] names)
    {
        foreach (var name in names) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, MinimumWidth = 90 });
    }
    private static GroupBox Group(string label, Control control) { var group = new GroupBox { Text = label, Dock = DockStyle.Fill }; group.Controls.Add(control); return group; }
    private Button Command(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => { try { if (!IsBusy) await action(); } catch (Exception ex) { Log(ex.GetType().Name + ": " + ex.Message); } };
        return button;
    }
    private void SelectPrompt()
    {
        if (_populatingSetup) return;
        _promptIndex = _prompts.CurrentRow?.Index ?? -1;
        _prompt.Text = _promptIndex < 0 ? "" : Convert.ToString(_prompts.Rows[_promptIndex].Cells[1].Value) ?? "";
    }
    private static IEnumerable<DataGridViewRow> Rows(DataGridView grid) => grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow);
    private static string CellText(DataGridViewRow row, int column) => Convert.ToString(row.Cells[column].Value) ?? "";
    private static string Cell(DataGridViewRow row, int column) => CellText(row, column).Trim();
    private static ImmutableArray<string?> Efforts(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(v => v == "default" ? null : v).ToImmutableArray();
    internal ExperimentDefinition ReadDefinition()
    {
        CommitEditors();
        return new ExperimentDefinition
        {
            Reference = _reference.Text, ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(_settings.PostProcessor),
            Models = Rows(_models).Select(r => new ExperimentModel(Cell(r, 0), Cell(r, 1), Cell(r, 2), Efforts(Cell(r, 3)), r.Cells[4].Value is true, r.Cells[5].Value is true)).ToImmutableArray(),
            Prompts = Rows(_prompts).Select(r => new ExperimentPrompt(Cell(r, 0), Convert.ToString(r.Cells[1].Value) ?? "")).ToImmutableArray(),
            Terms = Rows(_terms).Where(r => r.Cells[0].Value is true).Select(r => new ExperimentTerm(Cell(r, 1),
                Cell(r, 2).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))).ToImmutableArray(),
            Judge = new("Judge", _judgeDeployment.Text.Trim(), _judgeModel.Text.Trim(), Efforts(Convert.ToString(_judgeEffort.SelectedItem) ?? "default")),
            Limits = ReadLimits(), RetainAudio = _retain.Checked,
            Weights = _weights,
        }.Snapshot();
    }
    private void CommitEditors()
    {
        foreach (var grid in new[] { _models, _prompts, _terms }) { grid.CommitEdit(DataGridViewDataErrorContexts.Commit); grid.EndEdit(); }
        ValidateChildren(); // Commit NumericUpDown text too, including when Exit is chosen from the tray.
    }
    private ExperimentLimits ReadLimits() => new()
    {
        Repetitions = (int)_repeats.Value, MaximumTrials = (int)_limits[nameof(ExperimentLimits.MaximumTrials)].Value,
        RecordingSeconds = (int)_limits[nameof(ExperimentLimits.RecordingSeconds)].Value, RecognitionSeconds = (int)_limits[nameof(ExperimentLimits.RecognitionSeconds)].Value,
        TrialSeconds = (int)_limits[nameof(ExperimentLimits.TrialSeconds)].Value, QuietHintSeconds = (int)_limits[nameof(ExperimentLimits.QuietHintSeconds)].Value,
        RetryTransientFailure = _retry.Checked,
    };
    private ExperimentWorkspace ReadWorkspace()
    {
        CommitEditors();
        return new()
        {
            Reference = _reference.Text,
            Models = Rows(_models).Select(r => new ExperimentWorkspaceModel(CellText(r, 0), CellText(r, 1), CellText(r, 2), CellText(r, 3),
                r.Cells[4].Value is true, r.Cells[5].Value is true)).ToImmutableArray(),
            Prompts = Rows(_prompts).Select(r => new ExperimentPrompt(CellText(r, 0), CellText(r, 1))).ToImmutableArray(),
            Terms = Rows(_terms).Select(r => new ExperimentWorkspaceTerm(r.Cells[0].Value is true, CellText(r, 1), CellText(r, 2))).ToImmutableArray(),
            JudgeModel = _judgeModel.Text, JudgeDeployment = _judgeDeployment.Text, JudgeEffort = Convert.ToString(_judgeEffort.SelectedItem) ?? "default",
            Limits = ReadLimits(), Weights = _weights, RetainAudio = _retain.Checked, SelectedPromptIndex = _promptIndex,
            SelectedPromptText = _prompt.Text,
            SelectedTabIndex = _tabs.SelectedIndex, RunId = CurrentRun?.Definition.Id, ConnectionAvailable = _connectionAvailable,
            Compatibility = _evidence.Select(e => new ExperimentWorkspaceCompatibility(e.ApiFingerprint, e.Effort, e.Verification, e.FailureCode)).ToImmutableArray(),
        };
    }
    private async Task RestoreWorkspaceAsync()
    {
        _restoring = true; Enabled = false;
        try
        {
            var saved = await _store.LoadWorkspaceAsync(CancellationToken.None);
            if (saved is not { } snapshot) return;
            _settings = snapshot.Connection; _connectionAvailable = snapshot.Workspace.ConnectionAvailable; _hasWorkspace = true;
            if (snapshot.Workspace.RunId is { } id)
            {
                try { CurrentRun = await _store.LoadAsync(id, CancellationToken.None); }
                catch (Exception ex) { Log("Saved results unavailable; the experiment setup is restored."); _reportError(ex); }
            }
            _evidence.AddRange(snapshot.Workspace.Compatibility.Select(e => new ExperimentCompatibility(e.ApiFingerprint, e.Effort, e.Verification, e.FailureCode)));
            PopulateWorkspace(snapshot.Workspace); RefreshResults(); Log("Experiment workspace restored.");
        }
        catch (Exception ex)
        {
            // Preserve an unreadable snapshot rather than silently replacing the user's configuration.
            _workspaceLoadFailed = true; _status.Text = "Could not restore the experiment workspace; saved configuration has been preserved.";
            Log(_status.Text); _reportError(ex);
        }
        finally { _restoring = false; Enabled = true; }
    }
    private void Preview()
    {
        if (IsBusy || _previewing || _populatingSetup || _limits.Count == 0) return;
        _previewing = true;
        try
        {
            var preview = ExperimentMatrix.Build(ReadDefinition(), _settings.PostProcessor, _evidence);
            _preview.Text = $"{preview.ScheduledTrials} ready / {preview.UnknownTrials} unknown calls + raw baseline\r\n" + string.Join("\r\n",
                preview.Rows.Select(r => $"{r.Candidate.Name} / {r.Candidate.RequestedEffort ?? "default"}: {r.Status} {r.Reason}"));
        }
        catch (Exception ex) { _preview.Text = ex.Message; }
        finally { _previewing = false; }
    }

    internal Task StartWorkAsync(bool probe, bool reuseRaw)
    {
        if (IsBusy || _closing || _restoring) return _work;
        var lease = _acquireOperation();
        if (lease is null) { Log("Another operation is active."); return Task.CompletedTask; }
        IsBusy = true; _setup.Enabled = false; _reference.ReadOnly = true; _start.Enabled = false; _cancel.Enabled = true;
        _cancellation = new(); _runId = Guid.NewGuid(); _acceptProgress = true;
        _progress.Value = 0; _status.Text = probe ? "Checking compatibility…" : "Preparing experiment…";
        return _work = ExecuteAsync(probe, reuseRaw, lease);
    }
    private async Task ExecuteAsync(bool probe, bool reuseRaw, IDisposable lease)
    {
        try
        {
            await _checkpoint; _checkpoint = Task.CompletedTask;
            if (!_connectionAvailable) throw new InvalidOperationException("Stored connection unavailable. Inspection/export is available; reopen Experiments for a new setup.");
            var previous = reuseRaw ? CurrentRun ?? throw new InvalidOperationException("Open a saved raw take first.") : null;
            // Every rerun freezes today's Setup in a new revision; the original run stays inspectable.
            var definition = ReadDefinition() with { PreviousExperimentId = previous?.Definition.Id };
            _runId = definition.Id;
            var service = _serviceFactory(_settings);
            await VerifyEffortsAsync(definition, service, retryRejected: probe);
            RequireReady(ExperimentMatrix.Build(definition, _settings.PostProcessor, _evidence), "Candidates");
            RequireReady(JudgePreview(definition), "Judge");
            if (probe) { _status.Text = "Compatibility checks complete."; return; }
            await _store.SaveSetupAsync(definition, _settings, _cancellation!.Token);
            ExperimentTake take;
            string? retainedAudio = null;
            if (previous is not null)
            {
                take = previous.Take with { InputIdentity = definition.InputIdentity };
                retainedAudio = definition.RetainAudio ? _store.AudioPathFor(previous.Definition.Id) : null;
                Log("Reusing the saved raw take with the edited models, prompts and evaluation settings.");
                if (definition.RetainAudio && retainedAudio is null) Log("Saved audio is unavailable; this rerun still uses the saved raw text.");
            }
            else
            {
                _capture = _captureFactory(_settings, definition, _cancellation.Token, new Progress<ExperimentCaptureProgress>(p =>
                {
                    if (_closing || _capture?.Id != p.SessionId) return;
                    _done.Enabled = p.State == ExperimentState.Recording;
                    _status.Text = $"{p.State} {p.ElapsedSeconds:0.0}s · level {p.Level:0.00}" + (p.QuietHint ? (p.HasBuffers ? " · Quiet microphone" : " · No microphone buffers") : "")
                        + (p.LimitReached ? " · Recording limit reached" : "");
                }));
                _done.Enabled = true; take = await _capture.RunAsync(); _done.Enabled = false;
            }
            var runner = new ExperimentRunner(service, new(service));
            var progress = new Progress<ExperimentProgress>(p =>
                {
                    if (_closing || !_acceptProgress || p.RunId != _runId) return;
                    CurrentRun = p.Snapshot; _status.Text = $"{p.State} {p.Completed}/{p.Total}: {p.TrialId} {p.CompletionStage}";
                    _progress.Maximum = Math.Max(1, p.Total); _progress.Value = Math.Min(p.Completed, _progress.Maximum); _request.Visible = p.RequestActive;
                    Log($"{p.State}: {p.Completed}/{p.Total}"); _checkpoint = SaveAfterAsync(_checkpoint, p.Snapshot); RefreshResults();
                });
            CurrentRun = await Task.Run(() => runner.RunAsync(definition, take, _settings.PostProcessor, _cancellation.Token, progress, _evidence));
            _acceptProgress = false; CurrentRun = CurrentRun with { Definition = CurrentRun.Definition with { Weights = _weights } };
            await _checkpoint; await _store.SaveAsync(CurrentRun, CancellationToken.None, _capture?.AudioPath ?? retainedAudio);
            _loadedDefinition = CurrentRun.Definition;
            RefreshResults(); _tabs.SelectedTab = _resultsPage; Log(CurrentRun.State.ToString());
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled; completed rows retained."; Log(_status.Text); }
        catch (Exception ex) { _status.Text = ex.Message; Log(ex.Message); }
        finally
        {
            _acceptProgress = false;
            try { if (_capture is not null) await _capture.DisposeAsync(); }
            catch (Exception ex) { Log("Audio cleanup failed: " + ex.GetType().Name); }
            finally { _capture = null; }
            lease.Dispose(); IsBusy = false; _setup.Enabled = true; _reference.ReadOnly = false;
            _start.Enabled = true; _done.Enabled = false; _cancel.Enabled = false; _request.Visible = false;
            _cancellation?.Dispose(); _cancellation = null; Preview();
        }
    }
    private ExperimentMatrixPreview JudgePreview(ExperimentDefinition definition) => ExperimentMatrix.Build(definition with
    {
        Models = [definition.Judge], Prompts = [new("Judge", LLMExperimentJudge.Rubric)],
        Limits = definition.Limits with { Repetitions = 1 },
    }, _settings.PostProcessor, _evidence);

    private async Task VerifyEffortsAsync(ExperimentDefinition definition, TranscriptionService service, bool retryRejected)
    {
        var rows = ExperimentMatrix.Build(definition, _settings.PostProcessor, _evidence).Rows.Concat(JudgePreview(definition).Rows);
        // Probes establish local evidence; they never send the reference/raw take or count as measured cleanup.
        foreach (var row in rows.Where(r => r.Status == ExperimentMatrixStatus.Unknown || retryRejected && r.Candidate.Verification == ExperimentVerification.Rejected && _evidence.Any(e =>
            e.ApiFingerprint == r.Candidate.ApiFingerprint && e.Effort == r.Candidate.RequestedEffort && e.Verification == ExperimentVerification.Rejected))
            .DistinctBy(r => (r.Candidate.ApiFingerprint, r.Candidate.RequestedEffort)))
        {
            _cancellation!.Token.ThrowIfCancellationRequested();
            var candidate = row.Candidate;
            _status.Text = $"Checking {candidate.Name} / {candidate.RequestedEffort}…"; _request.Visible = true;
            var model = new ExperimentModel(candidate.Name, candidate.Deployment, candidate.Model, [candidate.RequestedEffort]);
            var settings = ExperimentMatrix.Settings(_settings.PostProcessor, model, new("Probe", "Return only the input."), candidate.RequestedEffort);
            var result = await service.ProbeTextCompatibilityAsync("This is a compatibility test.", settings, _cancellation.Token, definition.Limits.TrialSeconds);
            _evidence.Add(ExperimentCompatibility.FromProbe(result));
            Log($"Compatibility {candidate.Name} / {candidate.RequestedEffort}: {result.Verification} {result.FailureCode}");
        }
        _request.Visible = false;
    }

    private static void RequireReady(ExperimentMatrixPreview preview, string label)
    {
        var blocked = preview.Rows.Where(r => r.Status != ExperimentMatrixStatus.Ready).Select(r =>
            $"{r.Candidate.Name} / {r.Candidate.RequestedEffort ?? "default"}: {r.Reason}").ToArray();
        if (blocked.Length > 0) throw new InvalidOperationException($"{label} not ready: {string.Join("; ", blocked)} Choose a supported configuration or use Verify unknown efforts to retry a failed check.");
        if (!preview.CanRun) throw new InvalidOperationException($"{label}: select at least one model and prompt.");
    }
    private async Task SaveAfterAsync(Task previous, ExperimentRun snapshot) { await previous; await _store.SaveAsync(snapshot, CancellationToken.None); }
    private void Log(string message) { if (IsDisposed) return; _log.Items.Add(message); while (_log.Items.Count > 100) _log.Items.RemoveAt(0); _log.TopIndex = _log.Items.Count - 1; }
    private async Task SaveSetupAsync() { var definition = ReadDefinition(); await _store.SaveSetupAsync(definition, _settings, CancellationToken.None); _loadedDefinition = definition; Log("Setup saved: " + definition.Id); }
    private async Task OpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Saved experiment run or setup|run.json;setup.json", InitialDirectory = Path.GetDirectoryName(_store.DirectoryFor(Guid.NewGuid())) };
        if (dialog.ShowDialog(this) != DialogResult.OK || !Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(dialog.FileName)), out var id)) return;
        if (Path.GetFileName(dialog.FileName) == "setup.json") await LoadSetupAsync(id); else await LoadAsync(id);
    }
    public async Task LoadAsync(Guid id)
    {
        await Initialization;
        CurrentRun = await _store.LoadAsync(id, CancellationToken.None); _loadedDefinition = CurrentRun.Definition;
        var settings = await _store.LoadConnectionAsync(id, CancellationToken.None);
        _connectionAvailable = settings is not null;
        if (settings is not null) _settings = settings; else Log("Connection unavailable; inspection/export remains available.");
        foreach (var candidate in CurrentRun.Trials.Select(t => t.Candidate).Where(c => c.Verification == ExperimentVerification.Verified
            && c.RequestedEffort is not null && c.RequestedEffort == c.EffectiveEffort))
            _evidence.Add(new(candidate.ApiFingerprint, candidate.RequestedEffort!, ExperimentVerification.Verified));
        PopulateSetup(CurrentRun.Definition);
        RefreshResults(); _tabs.SelectedTab = _resultsPage; Log(CurrentRun.Interrupted ? "Interrupted run recovered." : "Saved run opened.");
    }
    public async Task LoadSetupAsync(Guid id)
    {
        await Initialization;
        var rawSource = CurrentRun;
        var definition = await _store.LoadSetupAsync(id, CancellationToken.None); definition.Validate();
        var settings = await _store.LoadConnectionAsync(id, CancellationToken.None); _connectionAvailable = settings is not null;
        if (settings is not null) _settings = settings;
        CurrentRun = null; _results.Rows.Clear(); _detail.Clear(); _winner.Text = "No results yet.";
        PopulateSetup(definition);
        // Opening a setup changes the configuration, not the already selected authoritative raw take.
        CurrentRun = rawSource; RefreshResults(); _tabs.SelectedIndex = 0;
        Log(rawSource is null ? "Saved setup opened." : "Saved setup opened; the selected raw take remains available for Rerun saved raw.");
    }
    public void RefreshIdleSettings(AppSettings settings)
    { if (!IsBusy && !_restoring && !_hasWorkspace && CurrentRun is null && _loadedDefinition is null) _settings = settings.Snapshot(); }
    private void PopulateSetup(ExperimentDefinition d)
    {
        _loadedDefinition = d;
        PopulateWorkspace(new()
        {
            Reference = d.Reference, Models = d.Models.Select(m => new ExperimentWorkspaceModel(m.Name, m.Deployment, m.Model,
                string.Join(",", m.Efforts.Select(e => e ?? "default")), m.StreamCompletion, m.UsePrediction)).ToImmutableArray(),
            Prompts = d.Prompts, Terms = d.Terms.Select(t => new ExperimentWorkspaceTerm(true, t.Canonical, string.Join(",", t.WrongAliases))).ToImmutableArray(),
            JudgeModel = d.Judge.Model, JudgeDeployment = d.Judge.Deployment, JudgeEffort = d.Judge.Efforts[0] ?? "default",
            Limits = d.Limits, Weights = d.Weights, RetainAudio = d.RetainAudio, SelectedPromptIndex = d.Prompts.Length > 0 ? 0 : -1,
        });
    }
    private void PopulateWorkspace(ExperimentWorkspace workspace)
    {
        _populatingSetup = true;
        try
        {
            _weights = workspace.Weights; _weightsForm?.SetWeights(_weights); _reference.Text = workspace.Reference;
            _models.Rows.Clear(); foreach (var m in workspace.Models) _models.Rows.Add(m.Name, m.Deployment, m.Model, m.Efforts, m.StreamCompletion, m.UsePrediction);
            _promptIndex = -1; _prompts.Rows.Clear(); foreach (var p in workspace.Prompts) _prompts.Rows.Add(p.Name, p.SystemPrompt);
            _terms.Rows.Clear(); foreach (var t in workspace.Terms) _terms.Rows.Add(t.Use, t.Canonical, t.WrongAliases);
            _judgeModel.Text = workspace.JudgeModel; _judgeDeployment.Text = workspace.JudgeDeployment; _judgeEffort.SelectedItem = workspace.JudgeEffort;
            _repeats.Value = workspace.Limits.Repetitions; _retain.Checked = workspace.RetainAudio; _retry.Checked = workspace.Limits.RetryTransientFailure;
            _limits[nameof(ExperimentLimits.MaximumTrials)].Value = workspace.Limits.MaximumTrials;
            _limits[nameof(ExperimentLimits.RecordingSeconds)].Value = workspace.Limits.RecordingSeconds;
            _limits[nameof(ExperimentLimits.RecognitionSeconds)].Value = workspace.Limits.RecognitionSeconds;
            _limits[nameof(ExperimentLimits.TrialSeconds)].Value = workspace.Limits.TrialSeconds;
            _limits[nameof(ExperimentLimits.QuietHintSeconds)].Value = workspace.Limits.QuietHintSeconds;
            _prompts.CurrentCell = workspace.SelectedPromptIndex >= 0 && workspace.SelectedPromptIndex < _prompts.Rows.Count
                ? _prompts.Rows[workspace.SelectedPromptIndex].Cells[0] : null;
            _tabs.SelectedIndex = workspace.SelectedTabIndex;
        }
        finally { _populatingSetup = false; }
        SelectPrompt();
        if (workspace.SelectedPromptText is { } text) _prompt.Text = text;
        Preview();
    }
    private void BuildResults()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        _results.ReadOnly = true; _results.AllowUserToAddRows = false; _results.AllowUserToDeleteRows = false;
        AddColumns(_results, "Rank", "Candidate", "Prompt", "Score", "Wording", "Identifiers", "Meaning", "Readability", "Speed", "Samples / failed / judged", "Cleanup ms (median)", "Estimated Stop→final ms", "Warnings");
        _results.Columns["Prompt"].MinimumWidth = 140;
        _results.Columns["Prompt"].ToolTipText = "Saved cleanup prompt name. Hover a cell or select a row to read the full prompt.";
        var split = new SplitContainer { Size = new(1100, 650), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 170, Panel1MinSize = 90, Panel2MinSize = 180 };
        split.Panel1.Controls.Add(_results); split.Panel2.Controls.Add(_detail);
        _results.CurrentCellChanged += (_, _) => { if (!_refreshing) ShowDetail(); };
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var weights = new Button { Text = "Weights…", AutoSize = true }; weights.Click += (_, _) => ShowWeights();
        commands.Controls.Add(weights);
        foreach (var format in Enum.GetValues<HistoryCopyFormat>())
        {
            var copy = new Button { Text = "Copy " + format, AutoSize = true };
            copy.Click += (_, _) => { try { Copy(format); } catch (Exception ex) { Log(ex.GetType().Name); } }; commands.Controls.Add(copy);
        }
        commands.Controls.Add(Command("Export JSON…", async () =>
        {
            if (CurrentRun is null) return;
            using var dialog = new SaveFileDialog { Filter = "Experiment JSON|*.json", FileName = "experiment.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllTextAsync(dialog.FileName, ExperimentExport.Format(CurrentRun, _weights, HistoryCopyFormat.Json));
        }));
        commands.Controls.Add(Command("Import JSON…", async () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Experiment JSON|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (new FileInfo(dialog.FileName).Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized import.");
            var run = System.Text.Json.JsonSerializer.Deserialize<ExperimentRun>(await File.ReadAllTextAsync(dialog.FileName)) ?? throw new InvalidDataException("Empty import.");
            ExperimentStore.Validate(run); await _store.SaveAsync(run, CancellationToken.None); await LoadAsync(run.Definition.Id);
        }));
        layout.Controls.Add(_winner, 0, 0); layout.Controls.Add(split, 0, 1); layout.Controls.Add(commands, 0, 2);
        _resultsPage.Controls.Add(layout);
    }
    internal void SetViewWeights(ExperimentWeights weights)
    {
        weights.Validate(); _weights = weights;
        if (CurrentRun is not null)
        {
            CurrentRun = CurrentRun with { Definition = CurrentRun.Definition with { Weights = weights } };
            if (!IsBusy) _checkpoint = SaveAfterAsync(_checkpoint, CurrentRun);
        }
        RefreshResults();
    }
    private void ShowWeights()
    {
        if (_weightsForm is null || _weightsForm.IsDisposed)
        {
            _weightsForm = new(_weights); _weightsForm.WeightsChanged += SetViewWeights;
            _weightsForm.Show(this);
        }
        else { _weightsForm.Show(); _weightsForm.BringToFront(); }
    }
    private void Copy(HistoryCopyFormat format)
    {
        if (CurrentRun is null) return;
        var data = new DataObject(); data.SetData(DataFormats.UnicodeText, ExperimentExport.Format(CurrentRun, _weights, format));
        if (format == HistoryCopyFormat.Table) data.SetData(DataFormats.Html, ExperimentExport.HtmlClipboard(CurrentRun, _weights));
        Clipboard.SetDataObject(data, true, 5, 50);
    }
    private void RefreshResults()
    {
        if (CurrentRun is null || IsDisposed) return;
        var selected = _results.CurrentRow?.Tag as string;
        _refreshing = true;
        try
        {
            _results.Rows.Clear(); var ranked = ExperimentScoring.Rank(CurrentRun, _weights);
            var winners = ranked.Where(r => r.Rank == 1).ToArray();
            _winner.Text = winners.Length == 0 ? "No winner: all weights are zero or a positively weighted factor is unavailable."
                : $"Top for these weights: {string.Join(" / ", winners.Select(r => CurrentRun.Trials.First(t => t.Candidate.Id == r.CandidateId).Candidate.Name))} · {winners[0].Score:0.0}" + (winners.Length > 1 ? " · Tie" : "")
                    + (winners.Any(r => r.Provisional) ? " · Provisional" : "");
            static string Number(double? value) => value is { } n ? n.ToString("0.0") : "—";
            foreach (var r in ranked)
            {
                var trials = CurrentRun.Trials.Where(t => t.Candidate.Id == r.CandidateId).ToArray();
                var timings = trials.Where(t => t.Generation == ExperimentGenerationStatus.Succeeded && t.CleanupMs.HasValue).Select(t => t.CleanupMs!.Value).Order().ToArray();
                double? cleanup = timings.Length == 0 ? null : timings.Length % 2 == 1 ? timings[timings.Length / 2]
                    : (timings[timings.Length / 2 - 1] + timings[timings.Length / 2]) / 2;
                var warning = string.Join(", ", r.Warnings.Select(w => w.Severity.ToString()).Distinct().Concat(trials.SelectMany(t => t.LocalWarnings)).Distinct());
                var candidate = trials[0].Candidate;
                var index = _results.Rows.Add(r.Rank?.ToString() ?? "—", candidate.Name, ExperimentExport.PromptName(CurrentRun, candidate), Number(r.Score), Number(r.Factors.Wording), Number(r.Factors.Identifiers),
                    Number(r.Factors.Meaning), Number(r.Factors.Readability), Number(r.Factors.Speed), $"{r.Successful}/{r.Failed}/{r.Judged}" + (r.Provisional ? " provisional" : ""),
                    Number(cleanup), Number(cleanup + CurrentRun.Take.SharedStopToRawMs), warning);
                _results.Rows[index].Tag = r.CandidateId;
                _results.Rows[index].Cells["Prompt"].ToolTipText = candidate.IsBaseline ? "No cleanup prompt; this is the shared raw transcript." : candidate.SystemPrompt;
                if (r.CandidateId == selected) _results.CurrentCell = _results.Rows[index].Cells[0];
            }
        }
        finally { _refreshing = false; }
        ShowDetail();
    }
    private void ShowDetail()
    {
        if (CurrentRun is null || _results.CurrentRow?.Tag is not string id) { _detail.Clear(); return; }
        _detail.ShowCandidate(CurrentRun, id);
    }
}
