using System.Globalization;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public static class ExperimentExport
{
    private static readonly string[] Headers = ["candidate", "prompt_name", "system_prompt", "repeat", "requested_effort", "effective_effort", "verification",
        "generation", "evaluation", "wording", "identifiers", "meaning", "readability", "speed", "weighted_score",
        "shared_stop_to_raw_ms", "cleanup_ms", "estimated_stop_to_final_ms", "judge_ms", "reference", "raw", "output", "warnings", "input_identity", "config_identity"];
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    public static string PromptName(ExperimentRun run, ExperimentCandidate candidate) => candidate.IsBaseline
        ? "None (raw transcript)"
        // Resolve against the immutable run, never the editable workspace, including for older saved results.
        : run.Definition.Prompts.FirstOrDefault(p => p.SystemPrompt == candidate.SystemPrompt)?.Name ?? "Unnamed prompt";
    private static IEnumerable<string[]> Rows(ExperimentRun run, ExperimentWeights weights)
    {
        var ranks = ExperimentScoring.Rank(run, weights).ToDictionary(r => r.CandidateId);
        return run.Trials.Select(t =>
        {
            var r = ranks[t.Candidate.Id];
            return new[] { t.Candidate.Name, PromptName(run, t.Candidate), t.Candidate.SystemPrompt,
                t.RepeatIndex.ToString(CultureInfo.InvariantCulture), t.Candidate.RequestedEffort ?? "provider default",
                t.Candidate.EffectiveEffort ?? "omitted", t.Candidate.Verification.ToString(), t.Generation.ToString(), t.EvaluationStatus.ToString(),
                Number(r.Factors.Wording), Number(r.Factors.Identifiers), Number(r.Factors.Meaning), Number(r.Factors.Readability), Number(r.Factors.Speed), Number(r.Score),
                Number(run.Take.SharedStopToRawMs), Number(t.CleanupMs), Number(t.Generation == ExperimentGenerationStatus.Succeeded && t.CleanupMs is { } ms ? run.Take.SharedStopToRawMs + ms : null),
                Number(t.JudgeMs), run.Definition.Reference, run.Take.RawText, t.Output ?? "", JsonSerializer.Serialize(t.Evaluation?.Warnings), run.Definition.InputIdentity, run.Definition.ConfigurationIdentity };
        });
    }
    public static string Format(ExperimentRun run, ExperimentWeights weights, HistoryCopyFormat format) => format switch
    {
        HistoryCopyFormat.Json => JsonSerializer.Serialize(run with { Definition = run.Definition with { Weights = weights } }, new JsonSerializerOptions { WriteIndented = true }),
        HistoryCopyFormat.Csv => TabularExport.Delimited(Headers, Rows(run, weights), ','),
        HistoryCopyFormat.Table => TabularExport.Delimited(Headers, Rows(run, weights), '\t'),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
    public static string HtmlClipboard(ExperimentRun run, ExperimentWeights weights) => TabularExport.HtmlClipboard(Headers, Rows(run, weights));
}
