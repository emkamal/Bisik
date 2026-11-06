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
    private readonly Panel _setup = new() { Dock = DockStyle.Fill };
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
    public bool IsBusy { get; private set; }
    public ExperimentRun? CurrentRun { get; private set; }
    public Task DrainAsync() { _cancellation?.Cancel(); return _work; }

    public ExperimentsForm(AppSettings settings, Func<AppSettings, TranscriptionService> serviceFactory,
        Func<AppSettings, ExperimentDefinition, CancellationToken, IProgress<ExperimentCaptureProgress>, ExperimentSession> captureFactory,
        ExperimentStore store, Func<IDisposable?> acquireOperation)
    {
        _settings = settings.Snapshot(); _serviceFactory = serviceFactory; _captureFactory = captureFactory;
        _store = store; _acquireOperation = acquireOperation;
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
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1 };
        foreach (var height in new[] { 23, 25, 20, 17, 15 }) fields.RowStyles.Add(new(SizeType.Percent, height));
        fields.RowStyles.Add(new(SizeType.AutoSize)); fields.RowStyles.Add(new(SizeType.AutoSize));
        AddColumns(_models, "Name", "Deployment", "Model", "Efforts (default,low,...)");
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
        _setup.Controls.Add(fields); _tabs.TabPages.AddRange([setupPage, _resultsPage]); root.Controls.Add(_tabs, 0, 0);
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
        _prompts.SelectionChanged += (_, _) => SelectPrompt();
        _reference.TextChanged += (_, _) => Preview(); _models.CellEndEdit += (_, _) => Preview(); _repeats.ValueChanged += (_, _) => Preview();
        FormClosing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; if (_closing) return; _closing = true;
            try { await DrainAsync(); } catch (Exception ex) { Log(ex.GetType().Name); }
            _allowClose = true; Close();
        };
        FormClosed += (_, _) => { _help.Dispose(); _cancellation?.Dispose(); };
        SelectPrompt(); Preview();
    }

    private static TextBox Editor(string name) => new() { Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true,
        AcceptsTab = true, Dock = DockStyle.Fill, AccessibleName = name, WordWrap = true };
    private static DataGridView Grid(string name) => new() { Dock = DockStyle.Fill, AutoGenerateColumns = false,
        AccessibleName = name, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private static void AddColumns(DataGridView grid, params string[] names) { foreach (var name in names) grid.Columns.Add(name, name); }
    private static GroupBox Group(string label, Control control) { var group = new GroupBox { Text = label, Dock = DockStyle.Fill }; group.Controls.Add(control); return group; }
    private Button Command(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => { try { if (!IsBusy) await action(); } catch (Exception ex) { Log(ex.GetType().Name + ": " + ex.Message); } };
        return button;
    }
    private void SelectPrompt()
    {
        if (_promptIndex >= 0 && _promptIndex < _prompts.Rows.Count && !_prompts.Rows[_promptIndex].IsNewRow)
            _prompts.Rows[_promptIndex].Cells[1].Value = _prompt.Text;
        _promptIndex = _prompts.CurrentRow?.Index ?? -1;
        _prompt.Text = _promptIndex < 0 ? "" : Convert.ToString(_prompts.Rows[_promptIndex].Cells[1].Value) ?? "";
    }
    private static IEnumerable<DataGridViewRow> Rows(DataGridView grid) => grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow);
    private static string Cell(DataGridViewRow row, int column) => Convert.ToString(row.Cells[column].Value)?.Trim() ?? "";
    private static ImmutableArray<string?> Efforts(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(v => v == "default" ? null : v).ToImmutableArray();
    internal ExperimentDefinition ReadDefinition()
    {
        foreach (var grid in new[] { _models, _prompts, _terms }) { grid.EndEdit(); }
        if (_promptIndex >= 0 && !_prompts.Rows[_promptIndex].IsNewRow) _prompts.Rows[_promptIndex].Cells[1].Value = _prompt.Text;
        return new ExperimentDefinition
        {
            Reference = _reference.Text, ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(_settings.PostProcessor),
            Models = Rows(_models).Select(r => new ExperimentModel(Cell(r, 0), Cell(r, 1), Cell(r, 2), Efforts(Cell(r, 3)))).ToImmutableArray(),
            Prompts = Rows(_prompts).Select(r => new ExperimentPrompt(Cell(r, 0), Convert.ToString(r.Cells[1].Value) ?? "")).ToImmutableArray(),
            Terms = Rows(_terms).Where(r => r.Cells[0].Value is true).Select(r => new ExperimentTerm(Cell(r, 1),
                Cell(r, 2).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))).ToImmutableArray(),
            Judge = new("Judge", _judgeDeployment.Text.Trim(), _judgeModel.Text.Trim(), Efforts(Convert.ToString(_judgeEffort.SelectedItem) ?? "default")),
            Limits = (_loadedDefinition?.Limits ?? new()) with { Repetitions = (int)_repeats.Value }, RetainAudio = _retain.Checked,
            Weights = CurrentRun?.Definition.Weights ?? _loadedDefinition?.Weights ?? new(),
        }.Snapshot();
    }
    private void Preview()
    {
        if (IsBusy) return;
        try
        {
            var preview = ExperimentMatrix.Build(ReadDefinition(), _settings.PostProcessor, _evidence);
            _preview.Text = $"{preview.ScheduledTrials} ready / {preview.UnknownTrials} unknown calls + raw baseline\r\n" + string.Join("\r\n",
                preview.Rows.Select(r => $"{r.Candidate.Name} / {r.Candidate.RequestedEffort ?? "default"}: {r.Status} {r.Reason}"));
        }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }

    internal Task StartWorkAsync(bool probe, bool reuseRaw)
    {
        if (IsBusy || _closing) return _work;
        var lease = _acquireOperation();
        if (lease is null) { Log("Another operation is active."); return Task.CompletedTask; }
        IsBusy = true; _setup.Enabled = false; _reference.ReadOnly = true; _start.Enabled = false; _cancel.Enabled = true;
        _cancellation = new(); _runId = Guid.NewGuid(); _acceptProgress = true; _checkpoint = Task.CompletedTask;
        return _work = ExecuteAsync(probe, reuseRaw, lease);
    }
    private async Task ExecuteAsync(bool probe, bool reuseRaw, IDisposable lease)
    {
        try
        {
            var definition = reuseRaw ? CurrentRun?.Definition ?? throw new InvalidOperationException("Open a saved raw take first.") : ReadDefinition();
            _runId = definition.Id;
            var service = _serviceFactory(_settings);
            if (probe)
            {
                var models = definition.Models.Add(definition.Judge);
                var matrix = ExperimentMatrix.Build(definition with { Models = models, Limits = definition.Limits with { Repetitions = 1 } }, _settings.PostProcessor, _evidence);
                foreach (var row in matrix.Rows.Where(r => r.Status == ExperimentMatrixStatus.Unknown).DistinctBy(r => (r.Candidate.ApiFingerprint, r.Candidate.RequestedEffort)))
                {
                    var model = new ExperimentModel(row.Candidate.Name, row.Candidate.Deployment, row.Candidate.Model, [row.Candidate.RequestedEffort]);
                    var settings = ExperimentMatrix.Settings(_settings.PostProcessor, model, new("Probe", "Return only the input."), row.Candidate.RequestedEffort);
                    var result = await service.ProbeTextCompatibilityAsync("This is a compatibility test.", settings, _cancellation!.Token);
                    _evidence.Add(ExperimentCompatibility.FromProbe(result)); Log($"Probe {row.Candidate.Name}: {result.Verification} {result.FailureCode}");
                }
                return;
            }
            // Validate matrix and judge before asking the human to read or scheduling any calls.
            if (!ExperimentMatrix.Build(definition, _settings.PostProcessor, _evidence).CanRun) throw new InvalidOperationException("Resolve the matrix preview before Start.");
            var judgePreview = ExperimentMatrix.Build(definition with { Models = [definition.Judge], Prompts = [new("Judge", LLMExperimentJudge.Rubric)],
                Limits = definition.Limits with { Repetitions = 1 } }, _settings.PostProcessor, _evidence);
            if (!judgePreview.CanRun) throw new InvalidOperationException("Verify the fixed judge effort before Start.");
            await _store.SaveSetupAsync(definition, _settings, _cancellation!.Token);
            ExperimentTake take;
            if (reuseRaw) take = CurrentRun!.Take;
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
            CurrentRun = await runner.RunAsync(definition, take, _settings.PostProcessor, _cancellation.Token,
                new Progress<ExperimentProgress>(p =>
                {
                    if (_closing || !_acceptProgress || p.RunId != _runId) return;
                    CurrentRun = p.Snapshot; _status.Text = $"{p.State} {p.Completed}/{p.Total}: {p.TrialId}";
                    _progress.Maximum = Math.Max(1, p.Total); _progress.Value = Math.Min(p.Completed, _progress.Maximum); _request.Visible = p.RequestActive;
                    Log($"{p.State}: {p.Completed}/{p.Total}"); _checkpoint = SaveAfterAsync(_checkpoint, p.Snapshot); RefreshResults();
                }), _evidence);
            _acceptProgress = false;
            await _checkpoint; await _store.SaveAsync(CurrentRun, CancellationToken.None, _capture?.AudioPath);
            RefreshResults(); _tabs.SelectedTab = _resultsPage; Log(CurrentRun.State.ToString());
        }
        catch (OperationCanceledException) { Log("Cancelled; completed rows retained."); }
        catch (Exception ex) { Log(ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            _acceptProgress = false;
            if (_capture is not null) { await _capture.DisposeAsync(); _capture = null; }
            lease.Dispose(); IsBusy = false; _setup.Enabled = true; _reference.ReadOnly = false;
            _start.Enabled = true; _done.Enabled = false; _cancel.Enabled = false; _request.Visible = false;
            _cancellation?.Dispose(); _cancellation = null; Preview();
        }
    }
    private async Task SaveAfterAsync(Task previous, ExperimentRun snapshot) { await previous; await _store.SaveAsync(snapshot, CancellationToken.None); }
    private void Log(string message) { if (IsDisposed) return; _log.Items.Add(message); while (_log.Items.Count > 100) _log.Items.RemoveAt(0); _log.TopIndex = _log.Items.Count - 1; }
    private async Task SaveSetupAsync() { var definition = ReadDefinition(); await _store.SaveSetupAsync(definition, _settings, CancellationToken.None); _loadedDefinition = definition; Log("Setup saved: " + definition.Id); }
    private async Task OpenAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Saved experiment run|run.json", InitialDirectory = Path.GetDirectoryName(_store.DirectoryFor(Guid.NewGuid())) };
        if (dialog.ShowDialog(this) != DialogResult.OK || !Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(dialog.FileName)), out var id)) return;
        await LoadAsync(id);
    }
    public async Task LoadAsync(Guid id)
    {
        CurrentRun = await _store.LoadAsync(id, CancellationToken.None); _loadedDefinition = CurrentRun.Definition;
        var settings = await _store.LoadConnectionAsync(id, CancellationToken.None);
        if (settings is not null) _settings = settings; else Log("Connection unavailable; inspection/export remains available.");
        var d = CurrentRun.Definition; _reference.Text = d.Reference; _models.Rows.Clear();
        foreach (var m in d.Models) _models.Rows.Add(m.Name, m.Deployment, m.Model, string.Join(",", m.Efforts.Select(e => e ?? "default")));
        _promptIndex = -1; _prompts.Rows.Clear(); foreach (var p in d.Prompts) _prompts.Rows.Add(p.Name, p.SystemPrompt);
        _terms.Rows.Clear(); foreach (var t in d.Terms) _terms.Rows.Add(true, t.Canonical, string.Join(",", t.WrongAliases));
        _judgeModel.Text = d.Judge.Model; _judgeDeployment.Text = d.Judge.Deployment; _judgeEffort.SelectedItem = d.Judge.Efforts[0] ?? "default";
        _repeats.Value = d.Limits.Repetitions; _retain.Checked = d.RetainAudio; SelectPrompt(); RefreshResults();
        _tabs.SelectedTab = _resultsPage; Log(CurrentRun.Interrupted ? "Interrupted run recovered." : "Saved run opened.");
    }
    private void RefreshResults() { /* Task 10 adds the local weighted view. */ }
}
