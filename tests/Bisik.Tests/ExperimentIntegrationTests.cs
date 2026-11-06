using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public class ExperimentIntegrationTests
{
    [Fact]
    public async Task FrozenTakeRunsAndReopensWithSameRankingAndNoSideEffects()
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.IntegrationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var connection = TranscriptionServiceTextTests.Settings(); var definition = ExperimentRunnerTests.Definition(connection);
            var raw = "HAPPI does not return 12 objects.";
            var calls = new List<string>();
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((input, settings, _) =>
            {
                if (settings.Model == "judge") return Task.FromResult("{\"meaning\":80,\"readability\":95,\"warnings\":[{\"category\":\"Negation\",\"severity\":\"Major\",\"reference\":\"not\",\"output\":\"does\",\"explanation\":\"Negation lost\"}]}");
                calls.Add(input); return Task.FromResult("HAPPI returns 12 objects.");
            }));
            var take = new ExperimentTake(Guid.NewGuid(), raw, 1000, 2000, definition.InputIdentity);
            var run = await new ExperimentRunner(service, new(service)).RunAsync(definition, take, connection, CancellationToken.None);
            Assert.All(calls, input => Assert.Equal(raw, input)); Assert.Equal(4, calls.Count);
            var weights = new ExperimentWeights(0, 0, 0, 0, 100);
            Assert.All(ExperimentScoring.Rank(run, weights), row => Assert.NotNull(row.Score));
            var store = new ExperimentStore(root); await store.SaveAsync(run, CancellationToken.None);
            var reopened = await store.LoadAsync(definition.Id, CancellationToken.None);
            Assert.Equal(ExperimentScoring.Rank(run, weights).Select(r => r.Score), ExperimentScoring.Rank(reopened, weights).Select(r => r.Score));
            Assert.False(File.Exists(Path.Combine(root, "history.jsonl")));
            Assert.Equal(connection, TranscriptionServiceTextTests.Settings());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
