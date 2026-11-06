using System.Collections.Immutable;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Core;

public enum ExperimentMatrixStatus { Ready, Unknown, Excluded }
public sealed record ExperimentCompatibility(string ApiFingerprint, string Effort,
    ExperimentVerification Verification, string? FailureCode = null)
{
    public static ExperimentCompatibility FromProbe(TextProcessingReceipt receipt)
    {
        if (receipt.RequestedEffort is null) throw new ArgumentException("Not an effort probe.");
        return new(receipt.ApiFingerprint, receipt.RequestedEffort,
            receipt.Succeeded && receipt.Verification == ExperimentVerification.Verified
                && receipt.RequestedEffort == receipt.EffectiveEffort
                ? ExperimentVerification.Verified : ExperimentVerification.Rejected, receipt.FailureCode);
    }
}
public sealed record ExperimentMatrixRow(ExperimentCandidate Candidate, ExperimentMatrixStatus Status, string? Reason);
public sealed record ExperimentMatrixPreview(ImmutableArray<ExperimentMatrixRow> Rows, int ScheduledTrials, int UnknownTrials)
{
    public bool CanRun => ScheduledTrials > 0 && UnknownTrials == 0;
}

public static class ExperimentMatrix
{
    public static string ConnectionIdentity(PostProcessorSettings source) => ReasoningCapability.Fingerprint(source with
    { Model = "", Provider = source.Provider with { Model = null, DeploymentName = "" } });

    public static ExperimentMatrixPreview Build(ExperimentDefinition definition, PostProcessorSettings connection,
        IEnumerable<ExperimentCompatibility>? evidence = null)
    {
        definition.Validate();
        if (definition.ConnectionFingerprint != ConnectionIdentity(connection))
            throw new InvalidOperationException("The experiment connection has changed. Create a new setup revision.");
        var known = (evidence ?? []).ToArray();
        var rows = ImmutableArray.CreateBuilder<ExperimentMatrixRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in definition.Models)
        foreach (var prompt in definition.Prompts)
        foreach (var effort in model.Efforts)
        {
            var settings = Settings(connection, model, prompt, effort);
            var fingerprint = ReasoningCapability.Fingerprint(settings);
            var resolvedModel = string.IsNullOrWhiteSpace(model.Model) ? model.Deployment : model.Model;
            var id = ExperimentIdentity.Hash("candidate-v1", fingerprint, resolvedModel, prompt.SystemPrompt, effort, model.StreamCompletion);
            if (!ids.Add(id)) continue;
            var state = ExperimentMatrixStatus.Ready;
            var verification = ExperimentVerification.ProviderDefault;
            string? reason = null;
            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(resolvedModel)
                || connection.Provider.Type == ProviderType.AzureOpenAi && !connection.Provider.AzureV1 && string.IsNullOrWhiteSpace(model.Deployment))
            {
                state = ExperimentMatrixStatus.Excluded; reason = "Missing model/deployment.";
            }
            else if (effort is not null)
            {
                var proof = known.LastOrDefault(e => e.ApiFingerprint == fingerprint && e.Effort == effort);
                var supported = settings.ReasoningCapability is { } capability && capability.Matches(settings)
                    && capability.SupportedEfforts.Contains(effort, StringComparer.Ordinal);
                if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                {
                    state = ExperimentMatrixStatus.Excluded; verification = ExperimentVerification.Rejected;
                    reason = "Invalid effort value.";
                }
                else if (proof?.Verification == ExperimentVerification.Rejected)
                {
                    state = ExperimentMatrixStatus.Excluded; verification = ExperimentVerification.Rejected;
                    reason = proof.FailureCode ?? "Compatibility probe failed.";
                }
                else if (supported || proof?.Verification == ExperimentVerification.Verified)
                    verification = ExperimentVerification.Verified;
                else
                {
                    state = ExperimentMatrixStatus.Unknown; verification = ExperimentVerification.Unknown;
                    reason = "Explicit compatibility probe required for this exact API/model/effort.";
                }
            }
            if (model.StreamCompletion && settings.CompletionCapability?.Supports(settings, CompletionFeature.Streaming) != true)
            { state = ExperimentMatrixStatus.Excluded; reason = "Streaming capability is not verified for this exact model/API."; }
            rows.Add(new(new(id, model.Name, model.Deployment, resolvedModel, prompt.SystemPrompt, effort,
                state == ExperimentMatrixStatus.Ready ? effort : null, verification, fingerprint, StreamCompletion: model.StreamCompletion), state, reason));
        }
        var scheduled = checked(rows.Count(r => r.Status == ExperimentMatrixStatus.Ready) * definition.Limits.Repetitions);
        var unknown = checked(rows.Count(r => r.Status == ExperimentMatrixStatus.Unknown) * definition.Limits.Repetitions);
        if (checked(scheduled + unknown) > definition.Limits.MaximumTrials)
            throw new ArgumentException($"Matrix exceeds {definition.Limits.MaximumTrials} cleanup trials.");
        return new(rows.ToImmutable(), scheduled, unknown);
    }

    public static PostProcessorSettings Settings(PostProcessorSettings connection, ExperimentModel model,
        ExperimentPrompt prompt, string? effort) => connection with
    {
        Enabled = true, StreamCompletion = model.StreamCompletion, Model = string.IsNullOrWhiteSpace(model.Model) ? model.Deployment : model.Model,
        Provider = connection.Provider with { DeploymentName = model.Deployment, Model = null },
        PresetName = PostProcessorPreset.Custom, SystemPrompt = prompt.SystemPrompt, ReasoningEffort = effort, Vocabulary = null,
    };

    public static PostProcessorSettings Settings(PostProcessorSettings connection, ExperimentCandidate candidate)
    {
        if (candidate.IsBaseline || candidate.Verification is ExperimentVerification.Unknown or ExperimentVerification.Rejected
            || candidate.RequestedEffort != candidate.EffectiveEffort)
            throw new InvalidOperationException("Candidate is not an executable cleanup configuration.");
        var settings = Settings(connection, new(candidate.Name, candidate.Deployment, candidate.Model, [candidate.RequestedEffort], candidate.StreamCompletion),
            new(candidate.Name, candidate.SystemPrompt), candidate.EffectiveEffort);
        if (ReasoningCapability.Fingerprint(settings) != candidate.ApiFingerprint)
            throw new InvalidOperationException("Candidate capability evidence is stale.");
        return settings with { ReasoningCapability = candidate.EffectiveEffort is null ? null
            : new(candidate.ApiFingerprint, [candidate.EffectiveEffort]) };
    }
}
