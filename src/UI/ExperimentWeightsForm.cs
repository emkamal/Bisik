using Bisik.Models;

namespace Bisik.UI;

public sealed class ExperimentWeightsForm : Form
{
    private readonly NumericUpDown[] _numbers = new NumericUpDown[5];
    private readonly TrackBar[] _sliders = new TrackBar[5];
    private readonly Label[] _percentages = new Label[5];
    private readonly NumericUpDown _speed = new() { Minimum = 0.001m, Maximum = 1_000_000, DecimalPlaces = 3, Increment = 0.5m };
    private readonly FieldHelp _help;
    private bool _updating;
    public event Action<ExperimentWeights>? WeightsChanged;
    public ExperimentWeights Weights => new((int)_numbers[0].Value, (int)_numbers[1].Value, (int)_numbers[2].Value,
        (int)_numbers[3].Value, (int)_numbers[4].Value, (double)_speed.Value);
    public ExperimentWeightsForm(ExperimentWeights initial)
    {
        _help = new(this);
        initial.Validate(); Text = "Experiment weights"; AutoScaleMode = AutoScaleMode.Dpi; Size = new(600, 410); MinimumSize = new(500, 390);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 7, Padding = new(10) };
        table.ColumnStyles.Add(new(SizeType.AutoSize)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        table.ColumnStyles.Add(new(SizeType.Absolute, 80)); table.ColumnStyles.Add(new(SizeType.Absolute, 70));
        var names = new[] { "Wording", "Identifiers", "Meaning", "Readability", "Speed" };
        var descriptions = new[]
        {
            "How closely the words match the reference. Word error rate counts substitutions, missing words and added words. Score = 100 × max(0, 1 − word error rate). Numbers and negation remain significant; wording changes can lower this score even when meaning is preserved. Repeats average successful outputs.",
            "Accuracy of the reviewed identifier spellings selected in Setup. Canonical matches use exact case; configured wrong aliases and case variants count as wrong. Required terms absent from the output score 0. With no reviewed terms, this factor is unavailable: set its weight to 0 or select terms before rerunning. A positive weight on an unavailable factor blocks the overall score.",
            "The fixed judge's assessment of how well the output preserves the reference's intent, facts, quantities and negation. Reported scores are kept, but major semantic errors cap effective meaning at 30 and extreme errors at 10. Warnings stay visible and do not disqualify a row. Repeats average available judged scores after these caps.",
            "The fixed judge's assessment of clarity, grammar, punctuation and natural phrasing. This is scored independently of meaning: a fluent sentence can still be factually wrong. Repeats average available judged scores.",
            "How quickly final text would be ready after Stop. Estimated time = the shared measured Stop→raw time + median successful cleanup time, including cleanup retries and parsing. It excludes recording, matrix queueing, judging and insertion. Score = 100 × B / (B + estimated seconds). Higher is faster.",
        };
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            _sliders[i] = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Dock = DockStyle.Fill, AccessibleName = names[i] + " slider" };
            _numbers[i] = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill, AccessibleName = names[i] + " weight" };
            _percentages[i] = new() { AutoSize = true };
            _sliders[i].ValueChanged += (_, _) => Change(index, _sliders[index].Value);
            _numbers[i].ValueChanged += (_, _) => Change(index, (int)_numbers[index].Value);
            table.Controls.Add(_help.CreateLabel(names[i], descriptions[i]), 0, i); table.Controls.Add(_sliders[i], 1, i);
            table.Controls.Add(_numbers[i], 2, i); table.Controls.Add(_percentages[i], 3, i);
        }
        table.Controls.Add(_help.CreateLabel("Speed B (seconds)", "The time scale for the Speed factor, not an extra weight. An estimated Stop→final time equal to B scores 50; zero time scores 100. Default B is 5 seconds. Changing B reranks saved measurements locally without API calls."), 0, 5); table.Controls.Add(_speed, 1, 5);
        _speed.ValueChanged += (_, _) => { if (!_updating) Publish(); };
        var reset = new Button { Text = "Reset defaults", AutoSize = true }; reset.Click += (_, _) => SetWeights(new());
        table.Controls.Add(reset, 0, 6); var explanation = new Label { Text = "Weights are relative. Zero ignores a factor. All zero has no winner.\r\nSpeed = 100 × B / (B + estimated Stop→final seconds).", AutoSize = true };
        table.Controls.Add(explanation, 1, 6); table.SetColumnSpan(explanation, 3); Controls.Add(table); SetWeights(initial);
    }
    public void SetWeights(ExperimentWeights weights)
    {
        weights.Validate(); _updating = true;
        try
        {
            var values = new[] { weights.Wording, weights.Identifiers, weights.Meaning, weights.Readability, weights.Speed };
            for (var i = 0; i < 5; i++) { _numbers[i].Value = values[i]; _sliders[i].Value = values[i]; }
            _speed.Value = (decimal)weights.SpeedReferenceSeconds;
        }
        finally { _updating = false; }
        Publish();
    }
    private void Change(int index, int value)
    {
        if (_updating) return; _updating = true;
        _numbers[index].Value = value; _sliders[index].Value = value; _updating = false; Publish();
    }
    private void Publish()
    {
        var sum = _numbers.Sum(n => n.Value);
        for (var i = 0; i < 5; i++) _percentages[i].Text = sum == 0 ? "—" : $"{100 * _numbers[i].Value / sum:0.0}%";
        WeightsChanged?.Invoke(Weights);
    }
    protected override void Dispose(bool disposing) { if (disposing) _help?.Dispose(); base.Dispose(disposing); }
}
