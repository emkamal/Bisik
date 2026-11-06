using System.Text.Json;
using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public class SegmentedCleanupTests
{
    private static PostProcessorSettings Settings => TranscriptionServiceTextTests.Settings() with { PresetName = PostProcessorPreset.CleanUp };
    [Fact]
    public async Task CumulativeSnapshotsHaveNoRepeatedOrOmittedCoverageAndFinalTailStaysOrdered()
    {
        var seen = new List<string>();
        var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((raw, _, _) =>
        {
            using var json = JsonDocument.Parse(raw); var segment = json.RootElement.GetProperty("currentSegment").GetString()!;
            seen.Add(segment); return Task.FromResult(segment.Trim());
        }));
        await using var coordinator = new SegmentedCleanup(service, Settings, CancellationToken.None);
        coordinator.OnCompletedPrefix("First.", 1); coordinator.OnCompletedPrefix("First.", 1); coordinator.OnCompletedPrefix("First. Second.", 2);
        var result = await coordinator.FinishAsync("First. Second. Tail.", Settings, CancellationToken.None);
        Assert.Equal(new[] { "First.", " Second.", " Tail." }, seen); Assert.Equal("First.\nSecond.\nTail.", result.Output);
        Assert.False(result.UsedFallback); Assert.Equal(3, result.SegmentRequests);
    }
    [Theory]
    [InlineData("correction")] [InlineData("settings")] [InlineData("budget")] [InlineData("failure")]
    public async Task InvalidCoverageSettingsBudgetOrFailureFallsBackExactlyOnce(string reason)
    {
        var wholeCalls = 0;
        var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((raw, _, _) =>
        {
            if (!raw.StartsWith('{')) { wholeCalls++; return Task.FromResult("whole edited"); }
            if (reason == "failure") throw new InvalidDataException(); return Task.FromResult("segment");
        }));
        await using var coordinator = new SegmentedCleanup(service, Settings, CancellationToken.None, budget: 1);
        coordinator.OnCompletedPrefix("First.", 1);
        if (reason == "correction") coordinator.OnCompletedPrefix("Changed.", 2);
        var current = reason == "settings" ? Settings with { Vocabulary = CleanupVocabulary.AbbTemplate } : Settings;
        var result = await coordinator.FinishAsync(reason == "correction" ? "Changed. Tail." : "First. Tail.", current, CancellationToken.None);
        Assert.True(result.UsedFallback); Assert.Equal(1, wholeCalls);
        Assert.Same(coordinator.FinishAsync("ignored", current, CancellationToken.None), coordinator.FinishAsync("ignored", current, CancellationToken.None));
    }
    [Fact]
    public async Task CancellationRejectsLateSegmentsAndNeverFallsBackOutsideDeadline()
    {
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((_, _, _) => { started.TrySetResult(); return pending.Task; }));
        await using var coordinator = new SegmentedCleanup(service, Settings, cts.Token);
        coordinator.OnCompletedPrefix("First.", 1); await started.Task;
        var finish = coordinator.FinishAsync("First.", Settings, cts.Token); cts.Cancel(); pending.SetResult("late");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
    }
    [Fact]
    public async Task ShortenedFinalPrefixAndUnsupportedPromptUseWholeFallback()
    {
        var calls = 0;
        var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((raw, _, _) =>
            { calls++; return Task.FromResult("whole"); }));
        var settings = Settings with { PresetName = PostProcessorPreset.TranslateToLanguage };
        await using var coordinator = new SegmentedCleanup(service, settings, CancellationToken.None);
        coordinator.OnCompletedPrefix("Long prefix.", 1);
        var result = await coordinator.FinishAsync("Short.", settings, CancellationToken.None);
        Assert.True(result.UsedFallback); Assert.Equal(1, calls);
    }
}
