using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bisik.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimingStage
{
    CaptureStart, StopRequested, CaptureDrained, ExportStart, ExportEnd,
    TranscriptionStart, TranscriptionEnd, SecretStart, SecretEnd, WssReady,
    FirstPartial, CommitSent, CommitAcknowledged, ItemCompleted,
    CleanupStart, CleanupEnd, HistoryStart, HistoryEnd, SoundStart, SoundEnd,
    Ready, PasteDispatched, ClipboardRestored,
}

public enum MetricCount { QueuedBytes, SentBytes, Attempts, Fallbacks, SpeculativeRequests, PrewarmSessions }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionOutcome { Ready, Empty, Failed, Cancelled, PasteDispatched, Copied, NeedsReview }
public sealed record TimingEvent(TimingStage Stage, double ElapsedMs);
public sealed record HttpTiming(int Attempt, double StartMs, double? FirstResponseMs = null,
    double? CompleteResponseMs = null, int? Status = null, double? RetryAfterMs = null);
public sealed record SessionTimingReport(Guid SessionId, string Configuration, double? AudioDurationMs,
    SessionOutcome Outcome, IReadOnlyList<TimingEvent> Events, IReadOnlyDictionary<MetricCount, long> Counts,
    double? StopToReadyMs, double? StopToPasteMs, IReadOnlyList<HttpTiming>? HttpRequests = null);

public interface IMetricsSink { void Write(SessionTimingReport report); }

/// <summary>Monotonic, bounded receipts. Only typed stages/counts can enter a report.</summary>
public sealed class SessionMetrics(string configuration, IMetricsSink? sink = null)
{
    private static readonly AsyncLocal<SessionMetrics?> Ambient = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private readonly List<TimingEvent> _events = [];
    private readonly Dictionary<MetricCount, long> _counts = [];
    private readonly List<HttpTiming> _http = [];
    private readonly string _configuration = ValidateLabel(configuration);
    public Guid Id { get; } = Guid.NewGuid();
    public SessionOutcome LastOutcome { get; private set; } = SessionOutcome.Ready;
    public double? AudioDurationMs { get; set; }
    public static SessionMetrics? Current => Ambient.Value;

    public IDisposable Activate()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(() => Ambient.Value = previous);
    }

    public void Mark(TimingStage stage, bool once = false)
    {
        lock (_gate)
        {
            if (_events.Count < 2048 && (!once || !_events.Any(e => e.Stage == stage)))
                _events.Add(new(stage, Stopwatch.GetElapsedTime(_start).TotalMilliseconds));
        }
    }

    public void Add(MetricCount count, long value = 1)
    {
        lock (_gate) _counts[count] = _counts.GetValueOrDefault(count) + value;
    }
    public double? ElapsedSince(TimingStage stage)
    {
        lock (_gate) return _events.FirstOrDefault(e => e.Stage == stage) is { } start
            ? Stopwatch.GetElapsedTime(_start).TotalMilliseconds - start.ElapsedMs : null;
    }
    public int? BeginHttpRequest()
    {
        lock (_gate)
        {
            if (_http.Count >= 128) return null;
            var id = _http.Count;
            _http.Add(new(id + 1, Stopwatch.GetElapsedTime(_start).TotalMilliseconds));
            return id;
        }
    }
    public void HttpHeaders(int id, int status, TimeSpan? retryAfter)
    { lock (_gate) _http[id] = _http[id] with { FirstResponseMs = Stopwatch.GetElapsedTime(_start).TotalMilliseconds, Status = status, RetryAfterMs = retryAfter?.TotalMilliseconds }; }
    public void HttpBodyComplete(int id)
    { lock (_gate) _http[id] = _http[id] with { CompleteResponseMs = Stopwatch.GetElapsedTime(_start).TotalMilliseconds }; }

    public SessionTimingReport Snapshot(SessionOutcome outcome)
    {
        lock (_gate)
        {
            double? At(TimingStage stage) => _events.FirstOrDefault(e => e.Stage == stage)?.ElapsedMs;
            double? SinceStop(TimingStage stage) => At(stage) - At(TimingStage.StopRequested);
            return new(Id, _configuration, AudioDurationMs, outcome, _events.ToArray(),
                new Dictionary<MetricCount, long>(_counts), SinceStop(TimingStage.Ready), SinceStop(TimingStage.PasteDispatched), _http.ToArray());
        }
    }

    public void Publish(SessionOutcome outcome)
    {
        LastOutcome = outcome;
        // Instrumentation must not turn delivery into a failure (e.g. a full trace disk).
        try { sink?.Write(Snapshot(outcome)); }
        catch (Exception exception) { Trace.TraceError("Bisik metrics sink failed: {0}", exception.GetType().Name); }
    }

    private static string ValidateLabel(string label) => label.Length is > 0 and <= 64 &&
        label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? label :
        throw new ArgumentException("Use an opaque configuration label, without connection details.");
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

/// <summary>Opt-in app trace, capped at 100 sessions/2 MiB. Contains no transcript or endpoint.</summary>
public sealed class BoundedMetricsFileSink(string path) : IMetricsSink
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _sessions = [];
    public void Write(SessionTimingReport report)
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(report) + Environment.NewLine;
            var info = new FileInfo(path);
            if ((_sessions.Count >= 100 && !_sessions.Contains(report.SessionId)) || (info.Exists ? info.Length : 0) + System.Text.Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, json);
            _sessions.Add(report.SessionId);
        }
    }
}

public sealed record TimingSummary(int Samples, int Failures, double? MedianMs, double? P95Ms);
public sealed record TimingComparison(string Configuration, double? AudioDurationMs, TimingSummary Summary, double FallbackRate);
public static class TimingAggregation
{
    public static IReadOnlyList<TimingComparison> Compare(IEnumerable<SessionTimingReport> reports) => reports
        .GroupBy(r => (r.Configuration, r.AudioDurationMs))
        .Select(group => new TimingComparison(group.Key.Configuration, group.Key.AudioDurationMs, Summarize(group),
            (double)group.Count(r => r.Counts.GetValueOrDefault(MetricCount.Fallbacks) > 0) / group.Count())).ToArray();
    public static TimingSummary Summarize(IEnumerable<SessionTimingReport> reports)
    {
        var all = reports.ToArray();
        var values = all.Where(r => r.Outcome is SessionOutcome.Ready or SessionOutcome.PasteDispatched)
            .Select(r => r.StopToReadyMs).OfType<double>().Order().ToArray();
        double? Percentile(double p) => values.Length == 0 ? null : values[(int)Math.Ceiling(p * values.Length) - 1];
        var median = values.Length == 0 ? (double?)null : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
        return new(all.Length, all.Count(r => r.Outcome is SessionOutcome.Failed or SessionOutcome.Cancelled), median, Percentile(.95));
    }
}
