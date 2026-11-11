using System.Collections.Immutable;

namespace Bisik.Models;

// Editable rows are kept verbatim: an unfinished setup need not be a runnable definition.
public sealed record ExperimentWorkspaceModel(string Name, string Deployment, string Model, string Efforts,
    bool StreamCompletion = false, bool UsePrediction = false);
public sealed record ExperimentWorkspaceTerm(bool Use, string Canonical, string WrongAliases);
public sealed record ExperimentWorkspaceCompatibility(string ApiFingerprint, string Effort, ExperimentVerification Verification, string? FailureCode = null);

public sealed record ExperimentWorkspace
{
    public int Version { get; init; } = 1;
    public string Reference { get; init; } = "";
    public ImmutableArray<ExperimentWorkspaceModel> Models { get; init; } = [];
    public ImmutableArray<ExperimentPrompt> Prompts { get; init; } = [];
    public ImmutableArray<ExperimentWorkspaceTerm> Terms { get; init; } = [];
    public string JudgeModel { get; init; } = "";
    public string JudgeDeployment { get; init; } = "";
    public string JudgeEffort { get; init; } = "default";
    public ExperimentLimits Limits { get; init; } = new();
    public ExperimentWeights Weights { get; init; } = new();
    public bool RetainAudio { get; init; }
    public int SelectedPromptIndex { get; init; } = -1;
    public string? SelectedPromptText { get; init; }
    public int SelectedTabIndex { get; init; }
    public Guid? RunId { get; init; }
    public bool ConnectionAvailable { get; init; } = true;
    public ImmutableArray<ExperimentWorkspaceCompatibility> Compatibility { get; init; } = [];

    public void Validate()
    {
        if (Version != 1 || Reference is null || Reference.Length > 100_000 || Models.IsDefault || Prompts.IsDefault
            || Terms.IsDefault || Compatibility.IsDefault || Models.Length > 100 || Prompts.Length > 100 || Terms.Length > 100
            || Models.Any(m => m is null) || Prompts.Any(p => p is null || p.SystemPrompt is null || p.SystemPrompt.Length > 32_000)
            || Terms.Any(t => t is null) || SelectedPromptIndex < -1 || SelectedPromptIndex > Prompts.Length
            || SelectedPromptText?.Length > 32_000
            || SelectedTabIndex is < 0 or > 1 || RunId == Guid.Empty || JudgeModel is null || JudgeDeployment is null || JudgeEffort is null)
            throw new InvalidDataException("Invalid experiment workspace.");
        Limits.Validate(); Weights.Validate();
    }
}
