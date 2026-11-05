using Bisik.Core;

namespace Bisik.Tests;

public sealed class SessionMetricsTests
{
    [Fact]
    public void MissingStagesAreNullAndEventsAreMonotonic()
    {
        var metrics = new SessionMetrics("fake");
        metrics.Mark(TimingStage.StopRequested);
        metrics.Mark(TimingStage.CleanupStart);
        metrics.Mark(TimingStage.CleanupEnd);
        metrics.Mark(TimingStage.Ready);
        var report = metrics.Snapshot(SessionOutcome.Ready);
        Assert.Null(report.StopToPasteMs);
        Assert.NotNull(report.StopToReadyMs);
        Assert.Equal(report.Events.OrderBy(e => e.ElapsedMs), report.Events);
        Assert.DoesNotContain(report.Events, e => e.Stage == TimingStage.CaptureStart);
        Assert.Throws<ArgumentException>(() => new SessionMetrics("https://secret.example"));
    }

    [Fact]
    public void AmbientScopeIsRestoredAndBoundsEvents()
    {
        var outer = new SessionMetrics("outer");
        using (outer.Activate())
        {
            using (new SessionMetrics("inner").Activate()) Assert.NotSame(outer, SessionMetrics.Current);
            Assert.Same(outer, SessionMetrics.Current);
        }
        Assert.Null(SessionMetrics.Current);
        for (var i = 0; i < 3000; i++) outer.Mark(TimingStage.CommitSent);
        Assert.Equal(2048, outer.Snapshot(SessionOutcome.Ready).Events.Count);
    }

    [Fact]
    public void AggregateIgnoresFailuresAndMissingStages()
    {
        SessionTimingReport Report(double? ready, SessionOutcome outcome = SessionOutcome.Ready) =>
            new(Guid.NewGuid(), "fake", 1000, outcome, [], new Dictionary<MetricCount, long>(), ready, null);
        var summary = TimingAggregation.Summarize([Report(1), Report(5), Report(9), Report(null), Report(100, SessionOutcome.Failed)]);
        Assert.Equal(5, summary.Samples);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(5, summary.MedianMs);
        Assert.Equal(9, summary.P95Ms);
    }

    [Theory]
    [InlineData("--repeat", "6")]
    [InlineData("--timeout-seconds", "121")]
    [InlineData("--unknown", "1")]
    public void BenchmarkRejectsUnboundedOrUnknownOptions(string option, string value) =>
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(["fixture.wav", "--output", "report.json", option, value]));
}
