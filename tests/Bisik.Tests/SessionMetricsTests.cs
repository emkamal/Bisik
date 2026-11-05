using Bisik.Core;

namespace Bisik.Tests;

public sealed class SessionMetricsTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("-1", null)]
    [InlineData("NaN", null)]
    [InlineData("0.5", null)]
    public void Retry_after_parsing_is_bounded_and_validated(string value, int? expected)
    {
        var delay = RetryPolicy.ParseRetryAfter(value, DateTimeOffset.UnixEpoch);
        Assert.Equal(expected is null ? null : TimeSpan.FromSeconds(expected.Value), delay);
    }

    [Fact]
    public async Task Retry_after_dates_and_deadline_cancellation_are_respected()
    {
        var now = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.ParseRetryAfter(now.AddSeconds(5).ToString("r"), now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryPolicy.WaitAsync(TimeSpan.FromHours(1), cancellation.Token));
    }

    [Fact]
    public async Task Http_receipts_include_first_full_response_and_status_without_connection_data()
    {
        using var handler = new HttpMetricsHandler(new MetricsResponseHandler());
        using var client = new HttpClient(handler);
        var metrics = new SessionMetrics("http-fake");
        using var scope = metrics.Activate();
        using var response = await client.GetAsync("https://private.example/secret", HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();
        var report = metrics.Snapshot(SessionOutcome.Ready);
        var receipt = Assert.Single(report.HttpRequests!);
        Assert.Equal(429, receipt.Status);
        Assert.Equal(1000, receipt.RetryAfterMs);
        Assert.True(receipt.StartMs <= receipt.FirstResponseMs && receipt.FirstResponseMs <= receipt.CompleteResponseMs);
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("secret", json);
    }

    private sealed class MetricsResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { Content = new StringContent("synthetic body") };
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return Task.FromResult(response);
        }
    }
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
