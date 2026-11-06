using Bisik.Core;
using Bisik.Models;
using Bisik.Providers;

namespace Bisik.Tests;

public class RawRecoveryTests
{
    [Fact]
    public async Task CleanupFailureRetainsAuthoritativeRawWhileOriginalResultStaysFailed()
    {
        var service = Service(new TranscriptionServiceTextTests.Processor((_, _, _) => throw new InvalidDataException("incomplete")));
        await using var session = service.CreateSession(Settings(), CancellationToken.None);
        var result = await session.CompleteAsync("unused.wav");
        Assert.False(result.Succeeded); Assert.Empty(result.OutputText); Assert.Equal("final raw", result.RawRecovery!.RawText);
        Assert.Equal(result.RawRecovery, session.RawRecovery);
    }
    [Fact]
    public async Task CleanupDeadlineSnapshotSurvivesCancellationButExplicitCancelIsIdentified()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new TranscriptionServiceTextTests.Processor(async (_, _, ct) =>
            { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "never"; }));
        using var deadline = new CancellationTokenSource();
        await using var session = service.CreateSession(Settings(), deadline.Token);
        var result = session.CompleteAsync("unused.wav"); await started.Task;
        Assert.Equal("final raw", session.RawRecovery!.RawText);
        deadline.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.False(session.ExplicitlyCancelled); Assert.NotNull(session.RawRecovery);
        session.Cancel(); Assert.True(session.ExplicitlyCancelled);
    }
    [Fact]
    public void RecoveryCannotBeClaimedAutomaticallyAndNewerPublicationSurvivesDismissal()
    {
        var store = new LatestResultStore(); var old = store.PublishRecovery(new("raw"), "failed");
        Assert.Equal(PendingResultKind.RawRecovery, old.Kind); Assert.False(old.Result.Succeeded);
        Assert.Null(store.Claim()); var claim = store.Claim(allowRecovery: true)!;
        Assert.Null(store.Claim(allowRecovery: true));
        var newer = store.Publish(new(true, "newer", "newer")); store.Dismiss(claim.Id); store.CompleteClaim(claim.Id, true);
        Assert.Equal(newer.Id, store.Latest!.Id);
    }
    private static AppSettings Settings() => new() { Provider = new() { Type = ProviderType.OpenAi }, HistoryEnabled = false, PostProcessor = new() { Enabled = true } };
    private static TranscriptionService Service(Bisik.PostProcessor.IPostProcessor processor) => new(new Dictionary<ProviderType, ITranscriptionProvider>
        { [ProviderType.OpenAi] = new Provider() }, processor, new HistoryLogger(Path.GetTempPath()), new ErrorLogger(Path.GetTempPath()), () => { }, () => { });
    private sealed class Provider : ITranscriptionProvider
    {
        public string Name => "test"; public ProviderType Type => ProviderType.OpenAi;
        public Task<string> TranscribeAsync(string path, string language, CancellationToken ct) => Task.FromResult("final raw");
    }
}
