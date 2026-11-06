using System.Globalization;

namespace Bisik.Core;

public sealed record ExperimentOptions(string DefinitionPath, string FixturePath, string SettingsPath,
    string OutputPath, string? QualityPath, string? AudioPath, string Label, int? Repetitions = null)
{
    public static ExperimentOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Expected option/value pairs.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--definition" or "--fixture" or "--settings" or "--output" or "--quality" or "--wav" or "--label" or "--repeat")
                || !values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException("Invalid experiment option.");
        string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException(key + " is required.");
        var label = values.GetValueOrDefault("--label", "fixture");
        if (label.Length is < 1 or > 24 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new ArgumentException("Use an opaque label.");
        var result = new ExperimentOptions(Required("--definition"), Required("--fixture"), Required("--settings"), Required("--output"),
            values.GetValueOrDefault("--quality"), values.GetValueOrDefault("--wav"), label,
            values.TryGetValue("--repeat", out var repeat) ? int.TryParse(repeat, out var n) && n is >= 1 and <= 5 ? n : throw new ArgumentException("Repetitions must be 1–5.") : null);
        var inputs = new[] { result.DefinitionPath, result.FixturePath, result.SettingsPath, result.AudioPath }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray();
        var outputs = new[] { result.OutputPath, result.QualityPath }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray();
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length || outputs.Any(p => inputs.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Output paths must be separate from sources.");
        return result;
    }
}

public enum ExperimentFixtureKind { Human, SyntheticText, SyntheticAudio }
public sealed record ExperimentFixture(string RawText, double? MeasuredStopToRawMs, ExperimentFixtureKind Kind)
{
    public void Validate(bool recognizing)
    {
        if (!Enum.IsDefined(Kind) || !recognizing && (string.IsNullOrWhiteSpace(RawText) || MeasuredStopToRawMs is null)
            || MeasuredStopToRawMs is { } ms && (!double.IsFinite(ms) || ms < 0)) throw new ArgumentException("Invalid fixture or missing measured timing.");
    }
}
