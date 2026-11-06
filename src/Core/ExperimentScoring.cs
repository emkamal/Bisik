using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Bisik.Models;

namespace Bisik.Core;

public sealed record ExperimentLocalScores(double Wording, double? Identifiers,
    ImmutableArray<ExperimentTermEvidence> Terms, ImmutableArray<string> Warnings);
public sealed record ExperimentRanking(string CandidateId, ExperimentFactors Factors, double? Score, int? Rank,
    int Successful, int Failed, int Judged, bool Provisional, ImmutableArray<ExperimentWarning> Warnings);

public static class ExperimentScoring
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);
    public static string[] Tokens(string text)
    {
        if (text.Length > 100_000) throw new ArgumentException("Text exceeds comparison bound.");
        var tokens = Regex.Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
            @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, RegexBudget)
            .Select(m => m.Value).ToArray();
        if (tokens.Length > 4096) throw new ArgumentException("Comparison supports at most 4096 tokens.");
        return tokens;
    }

    public static ExperimentLocalScores Compare(string reference, string output, ImmutableArray<ExperimentTerm> terms)
    {
        var expected = Tokens(reference);
        if (expected.Length == 0) throw new ArgumentException("Reference must contain at least one word or number.");
        var actual = Tokens(output);
        // Two rows bound edit-distance memory even when insertions make WER greater than one.
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var next = new int[actual.Length + 1];
        for (var i = 1; i <= expected.Length; i++)
        {
            next[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                next[j] = Math.Min(Math.Min(previous[j] + 1, next[j - 1] + 1),
                    previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            (previous, next) = (next, previous);
        }
        var wording = 100 * Math.Max(0, 1 - (double)previous[actual.Length] / expected.Length);
        var correct = new int[terms.Length];
        var wrong = new int[terms.Length];
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var alternatives = terms.SelectMany(t => t.WrongAliases.Prepend(t.Canonical))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a => a.Length).ToArray();
        if (alternatives.Length > 0)
        {
            var pattern = @"(?<![\p{L}\p{N}_/\\])(?:" + string.Join("|", alternatives.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_/\\])";
            foreach (Match match in Regex.Matches(output, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget))
            {
                var exact = Enumerable.Range(0, terms.Length).Where(i => terms[i].Canonical == match.Value).ToArray();
                if (exact.Length == 1) { correct[exact[0]]++; continue; }
                var owners = Enumerable.Range(0, terms.Length).Where(i =>
                    string.Equals(terms[i].Canonical, match.Value, StringComparison.OrdinalIgnoreCase)
                    || terms[i].WrongAliases.Contains(match.Value, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (owners.Length == 1) wrong[owners[0]]++;
                else warnings.Add($"Ambiguous alias: {match.Value}");
            }
        }
        var evidence = terms.Select((term, i) => new ExperimentTermEvidence(term.Canonical, correct[i], wrong[i],
            correct[i] + wrong[i] == 0 ? 0 : 100.0 * correct[i] / (correct[i] + wrong[i]))).ToImmutableArray();
        return new(wording, evidence.Length == 0 ? null : evidence.Average(t => t.Score), evidence, warnings.ToImmutableArray());
    }

    public static ImmutableArray<string> SuggestTerms(string reference) => Regex.Matches(reference,
        @"`([^`\r\n]+)`|\b[A-Za-z][A-Za-z0-9_./\\]*", RegexOptions.CultureInvariant, RegexBudget)
        .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value)
        .Where(t => t.Length <= 256 && (t.Contains('_') || t.Contains('/') || t.Contains('\\')
            || t.Count(char.IsUpper) >= 2 || Regex.IsMatch(t, @"[a-z][A-Z]", RegexOptions.CultureInvariant, RegexBudget)))
        .Distinct(StringComparer.Ordinal).Take(100).ToImmutableArray();

    public static double Speed(double referenceSeconds, double elapsedSeconds)
    {
        if (!double.IsFinite(referenceSeconds) || referenceSeconds <= 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        return 100 * referenceSeconds / (referenceSeconds + elapsedSeconds);
    }

    public static double? WeightedScore(ExperimentFactors factors, ExperimentWeights weights)
    {
        weights.Validate();
        var pairs = new[] { (weights.Wording, factors.Wording), (weights.Identifiers, factors.Identifiers),
            (weights.Meaning, factors.Meaning), (weights.Readability, factors.Readability), (weights.Speed, factors.Speed) };
        if (pairs.Sum(p => p.Item1) == 0 || pairs.Any(p => p.Item1 > 0 && p.Item2 is null)) return null;
        if (pairs.Any(p => p.Item2 is { } v && (!double.IsFinite(v) || v < 0 || v > 100)))
            throw new ArgumentException("Invalid score.");
        return pairs.Where(p => p.Item1 > 0).Sum(p => p.Item1 * p.Item2!.Value) / pairs.Sum(p => p.Item1);
    }

    public static ImmutableArray<ExperimentRanking> Rank(ExperimentRun run, ExperimentWeights weights)
    {
        run.Take.Validate(); weights.Validate();
        var rows = run.Trials.GroupBy(t => t.Candidate.Id).Select(group =>
        {
            var generated = group.Where(t => t.Generation == ExperimentGenerationStatus.Succeeded).ToArray();
            var judged = generated.Where(t => t.EvaluationStatus is ExperimentEvaluationStatus.Succeeded or ExperimentEvaluationStatus.Cached
                && t.Evaluation is not null).ToArray();
            double? Mean(Func<ExperimentTrial, double?> select) => generated.Length == 0 || generated.Any(t => select(t) is null)
                ? null : generated.Average(t => select(t)!.Value);
            var durations = generated.Where(t => t.CleanupMs.HasValue).Select(t => t.CleanupMs!.Value).Order().ToArray();
            var median = durations.Length == 0 ? (double?)null : durations.Length % 2 == 1
                ? durations[durations.Length / 2] : (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2;
            var factors = new ExperimentFactors(Mean(t => t.Factors?.Wording), Mean(t => t.Factors?.Identifiers),
                judged.Length == 0 ? null : judged.Average(t => t.Evaluation!.EffectiveMeaning),
                judged.Length == 0 ? null : judged.Average(t => t.Evaluation!.Readability),
                durations.Length == generated.Length && median is { } ms ? Speed(weights.SpeedReferenceSeconds, (run.Take.SharedStopToRawMs + ms) / 1000) : null);
            var score = WeightedScore(factors, weights);
            return new ExperimentRanking(group.Key, factors, score is { } s ? Math.Round(s, 1, MidpointRounding.AwayFromZero) : null,
                null, generated.Length, group.Count(t => t.Generation is ExperimentGenerationStatus.Failed or ExperimentGenerationStatus.Cancelled),
                judged.Length, judged.Length < generated.Length || group.Any(t => t.Generation is ExperimentGenerationStatus.Pending or ExperimentGenerationStatus.Running),
                generated.Where(t => t.Evaluation is not null).SelectMany(t => t.Evaluation!.Warnings).ToImmutableArray());
        }).OrderByDescending(r => r.Score).ThenBy(r => r.CandidateId, StringComparer.Ordinal).ToArray();
        var rank = 0;
        double? previousScore = null;
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].Score is null) continue;
            if (previousScore != rows[i].Score) rank = i + 1;
            previousScore = rows[i].Score;
            rows[i] = rows[i] with { Rank = rank };
        }
        return rows.ToImmutableArray();
    }
}
