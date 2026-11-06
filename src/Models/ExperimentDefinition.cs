using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bisik.Models;

public enum ExperimentState { Draft, Recording, Stopping, Transcribing, Generating, Evaluating, Completed, Cancelled, Failed }
public enum ExperimentVerification { ProviderDefault, Unknown, Verified, Rejected }

public sealed record ExperimentLimits
{
    public int Repetitions { get; init; } = 1;
    public int MaximumTrials { get; init; } = 100;
    public int RecordingSeconds { get; init; } = 300;
    public int RecognitionSeconds { get; init; } = 120;
    public int TrialSeconds { get; init; } = 120;
    public int QuietHintSeconds { get; init; } = 5;
    public bool RetryTransientFailure { get; init; }

    public void Validate()
    {
        InRange(Repetitions, 1, 5); InRange(MaximumTrials, 1, 100);
        InRange(RecordingSeconds, 1, 300); InRange(RecognitionSeconds, 1, 120);
        InRange(TrialSeconds, 1, 120); InRange(QuietHintSeconds, 1, 60);
    }

    private static void InRange(int value, int min, int max)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public sealed record ExperimentWeights(int Wording = 5, int Identifiers = 25, int Meaning = 40,
    int Readability = 15, int Speed = 15, double SpeedReferenceSeconds = 5)
{
    public void Validate()
    {
        if (new[] { Wording, Identifiers, Meaning, Readability, Speed }.Any(w => w is < 0 or > 100)
            || !double.IsFinite(SpeedReferenceSeconds) || SpeedReferenceSeconds is < 0.001 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(ExperimentWeights));
    }
}

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record ExperimentTerm(string Canonical, ImmutableArray<string> WrongAliases)
{
    public ExperimentTerm(string canonical, params string[] wrongAliases)
        : this(canonical, wrongAliases.ToImmutableArray()) { }
}

public sealed record ExperimentPrompt(string Name, string SystemPrompt);
public sealed record ExperimentModel(string Name, string Deployment, string Model,
    ImmutableArray<string?> Efforts, bool StreamCompletion = false, bool UsePrediction = false);

// Public setup/results deliberately contain no ProviderSettings. Connections live in a separate DPAPI file.
public sealed record ExperimentDefinition
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Reference { get; init; } = string.Empty;
    public string ConnectionFingerprint { get; init; } = string.Empty;
    public ImmutableArray<ExperimentTerm> Terms { get; init; } = [];
    public ImmutableArray<ExperimentPrompt> Prompts { get; init; } = [];
    public ImmutableArray<ExperimentModel> Models { get; init; } = [];
    public ExperimentModel Judge { get; init; } = new("", "", "", [null]);
    public ExperimentLimits Limits { get; init; } = new();
    public ExperimentWeights Weights { get; init; } = new();
    public bool RetainAudio { get; init; }
    public string RubricVersion { get; init; } = "meaning-readability-v1";
    public string ScoringVersion { get; init; } = "unicode-wer-identifiers-v1";

    public ExperimentDefinition Snapshot()
    {
        Validate();
        // Immutable collections and string-only child records make this a deep, shareable snapshot.
        return this with { Limits = Limits with { }, Weights = Weights with { } };
    }

    public void Validate()
    {
        if (Version != CurrentVersion || Id == Guid.Empty || string.IsNullOrWhiteSpace(Reference)
            || Reference.Length > 100_000 || Terms.IsDefault || Prompts.IsDefault || Models.IsDefault
            || Terms.Length > 100 || Prompts.Length > 100 || Models.Length > 100)
            throw new ArgumentException("Invalid experiment definition.");
        Limits.Validate(); Weights.Validate();
        foreach (var term in Terms)
            if (string.IsNullOrWhiteSpace(term.Canonical) || term.Canonical.Length > 256
                || term.WrongAliases.IsDefault || term.WrongAliases.Length > 20
                || term.WrongAliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 256))
                throw new ArgumentException("Invalid reviewed term.");
        foreach (var prompt in Prompts)
            if (string.IsNullOrWhiteSpace(prompt.Name) || string.IsNullOrWhiteSpace(prompt.SystemPrompt)
                || prompt.SystemPrompt.Length > 32_000) throw new ArgumentException("Invalid prompt.");
        foreach (var model in Models.Append(Judge))
            if (model.Efforts.IsDefault || model.Efforts.Length is < 1 or > 10
                || model.Efforts.Any(e => e is not null && string.IsNullOrWhiteSpace(e)))
                throw new ArgumentException("Invalid effort selection.");
    }

    // Weights and speed scale are view preferences, never request/evaluation identity.
    public string InputIdentity => ExperimentIdentity.Hash("input-v1", Reference, Terms);
    public string ConfigurationIdentity => ExperimentIdentity.Hash("config-v1", ConnectionFingerprint,
        Models, Prompts, Judge, Limits, RubricVersion, ScoringVersion);
}

public static class ExperimentIdentity
{
    public static string Hash(string version, params object?[] values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[] { version, values }))));
}
