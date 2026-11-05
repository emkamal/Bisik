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

public enum MetricCount { QueuedBytes, SentBytes, Attempts, Fallbacks, SpeculativeRequests }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionOutcome { Ready, Empty, Failed, Cancelled, PasteDispatched, Copied, NeedsReview }
public sealed record TimingEvent(TimingStage Stage, double ElapsedMs);
public sealed record SessionTimingReport(Guid SessionId, string Configuration, double? AudioDurationMs,
    SessionOutcome Outcome, IReadOnlyList<TimingEvent> Events, IReadOnlyDictionary<MetricCount, long> Counts,
    double? StopToReadyMs, double? StopToPasteMs);

public interface IMetricsSink { void Write(SessionTimingReport report); }

/// <summary>Monotonic, bounded receipts. Only typed stages/counts can enter a report.</summary>
public sealed class SessionMetrics(string configuration, IMetricsSink? sink = null)
{
    private static readonly AsyncLocal<SessionMetrics?> Ambient = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private readonly List<TimingEvent> _events = [];
    private readonly Dictionary<MetricCount, long> _counts = [];
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

    public SessionTimingReport Snapshot(SessionOutcome outcome)
    {
        lock (_gate)
        {
            double? At(TimingStage stage) => _events.FirstOrDefault(e => e.Stage == stage)?.ElapsedMs;
            double? SinceStop(TimingStage stage) => At(stage) - At(TimingStage.StopRequested);
            return new(Id, _configuration, AudioDurationMs, outcome, _events.ToArray(),
                new Dictionary<MetricCount, long>(_counts), SinceStop(TimingStage.Ready), SinceStop(TimingStage.PasteDispatched));
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
public static class TimingAggregation
{
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
