using System.Collections.Immutable;

namespace Bisik.Models;

public enum ExperimentGenerationStatus { Pending, Running, Succeeded, Failed, Cancelled, Skipped }
public enum ExperimentEvaluationStatus { Pending, Running, Succeeded, Failed, Cancelled, Cached }
public enum ExperimentSeverity { Minor, Substantial, Major, Extreme }
public enum ExperimentWarningCategory { Meaning, Omission, Negation, Quantity, Identifier, Readability }

public sealed record ExperimentCandidate(string Id, string Name, string Deployment, string Model,
    string SystemPrompt, string? RequestedEffort, string? EffectiveEffort,
    ExperimentVerification Verification, string ApiFingerprint, bool IsBaseline = false)
{
    public static ExperimentCandidate Baseline { get; } = new("raw-baseline-v1", "Raw transcript", "", "",
        "", null, null, ExperimentVerification.ProviderDefault, "", true);
}

public sealed record ExperimentWarning(ExperimentWarningCategory Category, ExperimentSeverity Severity,
    string ReferenceEvidence, string OutputEvidence, string Explanation);

public sealed record ExperimentEvaluation(double ReportedMeaning, double EffectiveMeaning, double Readability,
    ImmutableArray<ExperimentWarning> Warnings, string CacheIdentity);
public sealed record ExperimentFactors(double? Wording, double? Identifiers, double? Meaning,
    double? Readability, double? Speed);
public sealed record ExperimentTermEvidence(string Canonical, int Correct, int Wrong, double Score);

public sealed record ExperimentTake(Guid Id, string RawText, double SharedStopToRawMs,
    double? RecordingMs, string InputIdentity)
{
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(RawText) || RawText.Length > 100_000
            || !double.IsFinite(SharedStopToRawMs) || SharedStopToRawMs < 0
            || RecordingMs is { } ms && (!double.IsFinite(ms) || ms < 0))
            throw new ArgumentException("Invalid experiment take.");
    }
}

public sealed record ExperimentTrial
{
    public string Id { get; init; } = string.Empty;
    public ExperimentCandidate Candidate { get; init; } = ExperimentCandidate.Baseline;
    public int RepeatIndex { get; init; }
    public int ExecutionIndex { get; init; }
    public ExperimentGenerationStatus Generation { get; init; }
    public ExperimentEvaluationStatus EvaluationStatus { get; init; }
    public string? Output { get; init; }
    public double? CleanupMs { get; init; }
    public double? JudgeMs { get; init; }
    public string? FailureCode { get; init; }
    public ExperimentEvaluation? Evaluation { get; init; }
    public ExperimentFactors? Factors { get; init; }
    public ImmutableArray<ExperimentTermEvidence> TermEvidence { get; init; } = [];
    public ImmutableArray<string> LocalWarnings { get; init; } = [];
    public TextProcessingReceipt? Completion { get; init; }
}

public sealed record ExperimentRun(ExperimentDefinition Definition, ExperimentTake Take,
    ExperimentState State, int OrderSeed, ImmutableArray<ExperimentTrial> Trials, double BatchElapsedMs = 0);

public sealed record TextProcessingReceipt(bool Succeeded, string? Output, double ElapsedMs, int Attempts,
    string? RequestedEffort, string? EffectiveEffort, string Model, string SystemPrompt, string ApiFingerprint,
    ExperimentVerification Verification, string? FailureCode = null);
