using System.Collections.Immutable;
using System.Diagnostics;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Core;

public sealed record ExperimentProgress(Guid RunId, ExperimentState State, string? TrialId,
    int Completed, int Total, bool RequestActive, ExperimentRun Snapshot, TextCompletionStage? CompletionStage = null);

public sealed class ExperimentRunner(TranscriptionService service, LLMExperimentJudge judge)
{
    public async Task<ExperimentRun> RunAsync(ExperimentDefinition definition, ExperimentTake take,
        PostProcessorSettings connection, CancellationToken ct, IProgress<ExperimentProgress>? progress = null,
        IEnumerable<ExperimentCompatibility>? evidence = null, int? orderSeed = null,
        ExperimentRun? previous = null, ISet<string>? selectedTrialIds = null, bool evaluationOnly = false)
    {
        definition = definition.Snapshot(); take.Validate();
        if (take.InputIdentity != definition.InputIdentity) throw new ArgumentException("Take/reference identity mismatch.");
        ExperimentScoring.Compare(definition.Reference, take.RawText, definition.Terms);
        connection = (new AppSettings { PostProcessor = connection }).Snapshot().PostProcessor;
        var proof = (evidence ?? []).ToArray();
        var matrix = ExperimentMatrix.Build(definition, connection, proof);
        if (!matrix.CanRun) throw new InvalidOperationException("Resolve unknown configurations and select at least one ready trial.");
        var judgeDefinition = definition with { Models = [definition.Judge], Prompts = [new("Judge", LLMExperimentJudge.Rubric)], Limits = definition.Limits with { Repetitions = 1 } };
        var judgeMatrix = ExperimentMatrix.Build(judgeDefinition, connection, proof);
        if (!judgeMatrix.CanRun) throw new InvalidOperationException("Verify the fixed judge configuration before generating outputs.");
        var judgeSettings = ExperimentMatrix.Settings(connection, judgeMatrix.Rows.Single(r => r.Status == ExperimentMatrixStatus.Ready).Candidate);
        var seed = previous?.OrderSeed ?? orderSeed ?? Random.Shared.Next();
        if (previous is not null && (previous.Definition.InputIdentity != definition.InputIdentity
            || previous.Definition.ConfigurationIdentity != definition.ConfigurationIdentity || previous.Take != take))
            throw new ArgumentException("Reruns require the same frozen setup and take. Create a new revision for changed inputs.");
        var trials = previous?.Trials.ToArray() ?? Schedule(matrix, definition.Limits.Repetitions, seed);
        bool Selected(ExperimentTrial trial) => selectedTrialIds is null || selectedTrialIds.Contains(trial.Id);
        if (selectedTrialIds is not null && selectedTrialIds.Any(id => !trials.Any(t => t.Id == id)))
            throw new ArgumentException("Unknown rerun trial.");
        for (var i = 0; i < trials.Length; i++)
            if (Selected(trials[i])) trials[i] = trials[i] with
            {
                Generation = evaluationOnly && previous is not null ? trials[i].Generation : ExperimentGenerationStatus.Pending,
                EvaluationStatus = ExperimentEvaluationStatus.Pending, Evaluation = null, JudgeMs = null,
            };
        var watch = Stopwatch.StartNew();
        var state = ExperimentState.Generating;
        ExperimentRun Snapshot() => new(definition, take, state, seed, trials.ToImmutableArray(), watch.Elapsed.TotalMilliseconds);
        void Report(string? id, bool active, TextCompletionStage? stage = null) => progress?.Report(new(definition.Id, state, id,
            trials.Count(t => t.Generation is ExperimentGenerationStatus.Succeeded or ExperimentGenerationStatus.Failed or ExperimentGenerationStatus.Cancelled
                && (state == ExperimentState.Generating || t.EvaluationStatus is ExperimentEvaluationStatus.Succeeded or ExperimentEvaluationStatus.Cached or ExperimentEvaluationStatus.Failed or ExperimentEvaluationStatus.Cancelled)),
            trials.Length, active, Snapshot(), stage));
        try
        {
            for (var i = 0; i < trials.Length; i++)
            {
                if (!Selected(trials[i]) || evaluationOnly && previous is not null) continue;
                ct.ThrowIfCancellationRequested();
                trials[i] = trials[i] with { Generation = ExperimentGenerationStatus.Running, Output = null, Completion = null, FailureCode = null };
                Report(trials[i].Id, true);
                try
                {
                    var candidate = trials[i].Candidate;
                    var receipt = candidate.IsBaseline ? null : await service.ProcessTextAsync(take.RawText,
                        ExperimentMatrix.Settings(connection, candidate), ct, definition.Limits.TrialSeconds,
                        definition.Limits.RetryTransientFailure, new StageProgress(stage => Report(trials[i].Id, true, stage))).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (receipt is { Succeeded: false })
                        trials[i] = trials[i] with { Generation = ExperimentGenerationStatus.Failed, Completion = receipt,
                            CleanupMs = receipt.ElapsedMs, FailureCode = receipt.FailureCode };
                    else
                    {
                        var output = receipt?.Output ?? take.RawText;
                        var scores = ExperimentScoring.Compare(definition.Reference, output, definition.Terms);
                        trials[i] = trials[i] with { Generation = ExperimentGenerationStatus.Succeeded, Output = output,
                            Completion = receipt, CleanupMs = receipt?.ElapsedMs ?? 0, TermEvidence = scores.Terms,
                            LocalWarnings = scores.Warnings, Factors = new(scores.Wording, scores.Identifiers, null, null, null) };
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { trials[i] = trials[i] with { Generation = ExperimentGenerationStatus.Failed, FailureCode = TranscriptionService.SafeFailureCode(ex) }; }
                Report(trials[i].Id, false);
            }
            state = ExperimentState.Evaluating;
            foreach (var trial in trials.Where(t => t.Evaluation is not null)) judge.Seed(trial.Evaluation!);
            for (var i = 0; i < trials.Length; i++)
            {
                if (!Selected(trials[i]) || trials[i].Generation != ExperimentGenerationStatus.Succeeded) continue;
                ct.ThrowIfCancellationRequested();
                trials[i] = trials[i] with { EvaluationStatus = ExperimentEvaluationStatus.Running };
                Report(trials[i].Id, true);
                var receipt = await judge.EvaluateAsync(definition, trials[i].Output!, judgeSettings, ct).ConfigureAwait(false);
                trials[i] = trials[i] with { EvaluationStatus = receipt.Status, Evaluation = receipt.Evaluation,
                    JudgeMs = receipt.ElapsedMs, FailureCode = receipt.FailureCode };
                Report(trials[i].Id, false);
            }
            state = ExperimentState.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state = ExperimentState.Cancelled;
            for (var i = 0; i < trials.Length; i++)
                if (Selected(trials[i])) trials[i] = trials[i] with
                {
                    Generation = trials[i].Generation is ExperimentGenerationStatus.Pending or ExperimentGenerationStatus.Running
                        ? ExperimentGenerationStatus.Cancelled : trials[i].Generation,
                    EvaluationStatus = trials[i].EvaluationStatus is ExperimentEvaluationStatus.Pending or ExperimentEvaluationStatus.Running
                        ? ExperimentEvaluationStatus.Cancelled : trials[i].EvaluationStatus,
                };
        }
        Report(null, false);
        return Snapshot();
    }

    public static ExperimentTrial[] Schedule(ExperimentMatrixPreview matrix, int repetitions, int seed)
    {
        if (!matrix.CanRun || repetitions is < 1 or > 5 || matrix.ScheduledTrials / repetitions + 1 > 101)
            throw new ArgumentException("Invalid schedule.");
        var candidates = matrix.Rows.Where(r => r.Status == ExperimentMatrixStatus.Ready).Select(r => r.Candidate).ToArray();
        if (candidates.Length * repetitions > 100) throw new ArgumentException("Too many trials.");
        var result = new List<ExperimentTrial> { new() { Id = ExperimentCandidate.Baseline.Id, Candidate = ExperimentCandidate.Baseline } };
        var random = new Random(seed);
        for (var repeat = 0; repeat < repetitions; repeat++)
        {
            var round = candidates.ToArray();
            for (var i = round.Length - 1; i > 0; i--) { var j = random.Next(i + 1); (round[i], round[j]) = (round[j], round[i]); }
            foreach (var candidate in round) result.Add(new() { Id = ExperimentIdentity.Hash("trial-v1", candidate.Id, repeat),
                Candidate = candidate, RepeatIndex = repeat, ExecutionIndex = result.Count });
        }
        return result.ToArray();
    }
    private sealed class StageProgress(Action<TextCompletionStage> report) : IProgress<TextCompletionStage>
    { public void Report(TextCompletionStage value) => report(value); }
}
