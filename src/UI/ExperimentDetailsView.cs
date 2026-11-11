using Bisik.Core;
using Bisik.Models;

namespace Bisik.UI;

// Inspection only: repeat selection reads the saved run and never starts evaluation.
internal sealed class ExperimentDetailsView : UserControl
{
    private readonly Label _candidate = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ComboBox _repeat = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Result repeat", Width = 130 };
    private readonly TableLayoutPanel _sections = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new(4) };
    private readonly RichTextBox _reference = Reader("Reference text");
    private readonly RichTextBox _raw = Reader("Shared raw");
    private readonly RichTextBox _prompt = Reader("Result prompt");
    private readonly RichTextBox _output = Reader("Cleanup result");
    private readonly RichTextBox _judge = Reader("AI judge result");
    private readonly RichTextBox _metadata = Reader("Metadata");
    private readonly Font _headingFont;
    private ExperimentRun? _run;
    private ExperimentTrial[] _trials = [];
    private bool _updating;

    public ExperimentDetailsView()
    {
        Dock = DockStyle.Fill; AccessibleName = "Experiment details";
        _headingFont = new(Font, FontStyle.Bold);
        _candidate.Font = _headingFont;
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new(8) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(_candidate, 0, 0); header.Controls.Add(_repeat, 1, 0);
        // Keep the comparison grid fixed; each section owns scrolling its contents.
        for (var column = 0; column < 3; column++) _sections.ColumnStyles.Add(new(SizeType.Percent, 100f / 3));
        for (var row = 0; row < 2; row++) _sections.RowStyles.Add(new(SizeType.Percent, 50));
        AddSection("Reference text", _reference, 0, 0);
        AddSection("Shared raw", _raw, 1, 0);
        AddSection("Cleanup result", _output, 2, 0);
        AddSection("Prompt", _prompt, 0, 1);
        AddSection("Metadata", _metadata, 1, 1);
        AddSection("AI judge result", _judge, 2, 1);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        header.Dock = DockStyle.Fill; header.Margin = Padding.Empty; _sections.Margin = Padding.Empty;
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(_sections, 0, 1); Controls.Add(layout);
        _repeat.SelectedIndexChanged += (_, _) => { if (!_updating) ShowTrial(); };
        Clear();
    }

    private IEnumerable<RichTextBox> Readers => [_reference, _raw, _prompt, _metadata, _output, _judge];
    private static RichTextBox Reader(string name) => new()
    {
        Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
        DetectUrls = false, AutoWordSelection = false,
        BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
        Dock = DockStyle.Fill, AccessibleName = name, Margin = Padding.Empty,
    };
    private void AddSection(string title, Control content, int column, int row)
    {
        var section = new GroupBox
        {
            Text = title, AccessibleName = title + " section", Font = _headingFont,
            Dock = DockStyle.Fill,
            Padding = new(12, 8, 12, 12), Margin = new(4, 4, 4, 8), BackColor = SystemColors.Window,
        };
        content.Font = Font; section.Controls.Add(content);
        _sections.Controls.Add(section, column, row);
    }
    public void Clear()
    {
        _run = null; _trials = []; _repeat.Items.Clear(); _repeat.Enabled = false;
        _candidate.Text = "Select a result to review its text and evidence.";
        foreach (var reader in Readers) reader.Clear();
        _prompt.Parent!.Text = "Prompt";
    }
    public void ShowCandidate(ExperimentRun run, string id)
    {
        var trials = run.Trials.Where(t => t.Candidate.Id == id).OrderBy(t => t.RepeatIndex).ToArray();
        // Progress elsewhere in the batch should not rebuild controls or interrupt reading this row.
        if (_run?.Definition == run.Definition && _run.Take == run.Take && _trials.SequenceEqual(trials)) { _run = run; return; }
        var repeatIndex = _run?.Definition.Id == run.Definition.Id && _trials.FirstOrDefault()?.Candidate.Id == id
            && _repeat.SelectedIndex >= 0 ? _trials[_repeat.SelectedIndex].RepeatIndex : 0;
        _run = run; _trials = trials;
        if (_trials.Length == 0) { Clear(); return; }
        var candidate = _trials[0].Candidate;
        _updating = true;
        _sections.SuspendLayout();
        try
        {
            _candidate.Text = candidate.Name;
            _repeat.Items.Clear();
            foreach (var trial in _trials) _repeat.Items.Add($"Repeat {trial.RepeatIndex + 1}");
            _repeat.Enabled = _trials.Length > 1;
            _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(_trials, t => t.RepeatIndex == repeatIndex));
            _reference.Text = run.Definition.Reference; _raw.Text = run.Take.RawText;
            _prompt.Text = candidate.IsBaseline ? "No cleanup prompt. This row uses the shared raw transcript." : candidate.SystemPrompt;
            _prompt.Parent!.Text = "Prompt · " + ExperimentExport.PromptName(run, candidate);
            ShowTrial();
        }
        finally { _updating = false; _sections.ResumeLayout(true); }
    }
    private void ShowTrial()
    {
        if (_run is null || _repeat.SelectedIndex < 0) return;
        var trial = _trials[_repeat.SelectedIndex]; var candidate = trial.Candidate;
        _metadata.SuspendLayout();
        try
        {
            _metadata.Clear();
            MetadataHeading("Transcription");
            Metadata("Stop → raw", Milliseconds(_run.Take.SharedStopToRawMs));
            Metadata("Recording", Milliseconds(_run.Take.RecordingMs));
            MetadataHeading("Cleanup");
            Metadata("Status", trial.Generation.ToString());
            Metadata("Model / deployment", candidate.IsBaseline ? "Shared raw baseline" : $"{candidate.Model} / {candidate.Deployment}");
            Metadata("Reasoning effort", $"Requested: {candidate.RequestedEffort ?? "default"} · Effective: {candidate.EffectiveEffort ?? "omitted"} · {candidate.Verification}");
            Metadata("Streaming / prediction", $"{candidate.StreamCompletion} / {candidate.UsePrediction}");
            Metadata("Cleanup time", Milliseconds(trial.CleanupMs));
            Metadata("Prediction tokens", $"Accepted: {Count(trial.Completion?.Usage?.AcceptedPredictionTokens)} · Rejected: {Count(trial.Completion?.Usage?.RejectedPredictionTokens)}");
            Metadata("Failure", string.IsNullOrEmpty(trial.FailureCode) ? "None" : trial.FailureCode);
            Metadata("Local warnings", trial.LocalWarnings.Length == 0 ? "None" : string.Join("; ", trial.LocalWarnings));
            MetadataHeading("AI judge");
            Metadata("Status", trial.EvaluationStatus.ToString());
            Metadata("Model / deployment", $"{_run.Definition.Judge.Model} / {_run.Definition.Judge.Deployment}");
            Metadata("Judge time", Milliseconds(trial.JudgeMs));
        }
        finally { _metadata.ResumeLayout(true); }
        _metadata.Select(0, 0); _metadata.ScrollToCaret();
        _output.Text = trial.Output ?? $"No cleanup result. Status: {trial.Generation}.";
        var terms = trial.TermEvidence.Length == 0 ? "No reviewed identifier evidence."
            : string.Join("\r\n", trial.TermEvidence.Select(e => $"{e.Canonical}: {e.Correct} correct / {e.Wrong} wrong"));
        var evaluation = trial.Evaluation;
        _judge.Text = (evaluation is null ? $"No AI judge result. Status: {trial.EvaluationStatus}."
            : $"Meaning: {evaluation.EffectiveMeaning:0.0} / 100 (reported: {evaluation.ReportedMeaning:0.0})\r\nReadability: {evaluation.Readability:0.0} / 100\r\n\r\n"
                + (evaluation.Warnings.Length == 0 ? "No AI judge warnings."
                    : string.Join("\r\n\r\n", evaluation.Warnings.Select(w => $"{w.Severity} · {w.Category}\r\nReference: {w.ReferenceEvidence}\r\nOutput: {w.OutputEvidence}\r\n{w.Explanation}"))))
            + "\r\n\r\nReviewed identifiers (local check)\r\n" + terms;
    }
    private static string Milliseconds(double? value) => value is { } ms ? $"{ms:0.0} ms" : "Unavailable";
    private static string Count(long? value) => value?.ToString() ?? "Unavailable";
    private void MetadataHeading(string text)
    {
        if (_metadata.TextLength > 0) _metadata.AppendText("\r\n");
        _metadata.SelectionFont = _headingFont; _metadata.AppendText(text + "\r\n");
        _metadata.SelectionFont = Font;
    }
    private void Metadata(string name, string value)
    {
        _metadata.SelectionFont = _headingFont; _metadata.AppendText(name + ": ");
        _metadata.SelectionFont = Font; _metadata.AppendText(value + "\r\n");
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _headingFont.Dispose();
    }
}
