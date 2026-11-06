using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Tests;

public sealed class ExperimentRunnerTests
{
    internal static ExperimentDefinition Definition(PostProcessorSettings connection) => ExperimentModelTests.Definition() with
    {
        ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(connection),
        Models = [new("One", "dep", "one", [null]), new("Two", "dep", "two", [null])],
        Judge = new("Judge", "dep", "judge", [null]), Limits = new() { Repetitions = 2 },
    };
    internal static TranscriptionService Service(IPostProcessor processor) => new(new Dictionary<ProviderType, Bisik.Providers.ITranscriptionProvider>(),
        processor, new HistoryLogger(Path.GetTempPath()), new ErrorLogger(Path.GetTempPath()), () => throw new Exception(), () => throw new Exception());

    [Fact]
    public async Task OneRawSequentialGenerationPrecedesDistinctOutputJudgingAndReweightingMakesNoCalls()
    {
        var connection = TranscriptionServiceTextTests.Settings(); var definition = Definition(connection);
        var events = new List<string>();
        var service = Service(new TranscriptionServiceTextTests.Processor((text, settings, _) =>
        {
            events.Add(settings.Model);
            if (settings.Model == "judge") return Task.FromResult("{\"meaning\":90,\"readability\":80,\"warnings\":[]}");
            Assert.Equal("identical raw", text);
            return Task.FromResult(settings.Model == "one" ? "same output" : "other output");
        }));
        var runner = new ExperimentRunner(service, new(service));
        var take = new ExperimentTake(Guid.NewGuid(), "identical raw", 1200, null, definition.InputIdentity);
        var run = await runner.RunAsync(definition, take, connection, CancellationToken.None, orderSeed: 123);
        Assert.Equal(ExperimentState.Completed, run.State); Assert.Equal(5, run.Trials.Length);
        Assert.All(events.Take(4), model => Assert.NotEqual("judge", model));
        Assert.Equal(3, events.Count(model => model == "judge"));
        Assert.Equal(2, run.Trials.Count(t => t.EvaluationStatus == ExperimentEvaluationStatus.Cached));
        var before = events.Count; ExperimentScoring.Rank(run, new(0, 0, 0, 0, 100)); Assert.Equal(before, events.Count);
        var matrix = ExperimentMatrix.Build(definition, connection);
        Assert.Equal(ExperimentRunner.Schedule(matrix, 2, 123).Select(t => t.Id), run.Trials.Select(t => t.Id));
        var selected = new HashSet<string> { run.Trials[1].Id };
        await runner.RunAsync(definition, take, connection, CancellationToken.None, previous: run, selectedTrialIds: selected, evaluationOnly: true);
        Assert.Equal(before, events.Count); // Saved evaluation cache; no new recognition or cleanup.
    }

    [Fact]
    public async Task FailureContinuesAndCancelRetainsCompletedSamples()
    {
        var connection = TranscriptionServiceTextTests.Settings(); var definition = Definition(connection);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var service = Service(new TranscriptionServiceTextTests.Processor((_, _, _) =>
        {
            if (++calls == 1) throw new HttpRequestException("private endpoint", null, System.Net.HttpStatusCode.NotFound);
            if (calls == 3) cts.Cancel();
            return Task.FromResult("complete output");
        }));
        var run = await new ExperimentRunner(service, new(service)).RunAsync(definition,
            new(Guid.NewGuid(), "raw", 100, null, definition.InputIdentity), connection, cts.Token);
        Assert.Equal(ExperimentState.Cancelled, run.State);
        Assert.Contains(run.Trials, t => t.Generation == ExperimentGenerationStatus.Failed && t.FailureCode == "http-404");
        Assert.Contains(run.Trials, t => t.Generation == ExperimentGenerationStatus.Succeeded && !t.Candidate.IsBaseline);
        Assert.Contains(run.Trials, t => t.Generation == ExperimentGenerationStatus.Cancelled);
        Assert.All(run.Trials, t => Assert.Null(t.Evaluation));
    }
}
