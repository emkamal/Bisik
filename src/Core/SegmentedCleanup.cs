using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Core;

public sealed record SegmentedCleanupReceipt(string Output, int SegmentRequests, bool UsedFallback,
    double OverlapRequestMs, double TailMs, string? FallbackReason);

// Diagnostic only. A completed cumulative prefix is accepted once; any correction invalidates its coverage.
public sealed class SegmentedCleanup : IAsyncDisposable
{
    private sealed record Segment(string Raw, string Context);
    private readonly object _gate = new();
    private readonly PostProcessorSettings _settings;
    private readonly TranscriptionService _service;
    private readonly CancellationTokenSource _lifetime;
    private readonly int _budget;
    private readonly Queue<Segment> _pending = new();
    private readonly List<string> _outputs = [];
    private string _prefix = "";
    private long _revision = -1;
    private string? _fallback;
    private Task? _worker;
    private Task<SegmentedCleanupReceipt>? _completion;
    private bool _finishing;
    private int _requests;
    private int _acceptedSegments;
    private double _overlapMs;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double? _stopAt;

    public SegmentedCleanup(TranscriptionService service, PostProcessorSettings settings, CancellationToken ct, int budget = 3)
    {
        if (budget is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(budget));
        _service = service; _settings = new AppSettings { PostProcessor = settings }.Snapshot().PostProcessor;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); _budget = budget;
        if (settings.PresetName != PostProcessorPreset.CleanUp) _fallback = "prompt-requires-whole-text";
    }
    public void OnCompletedPrefix(string prefix, long revision)
    {
        lock (_gate)
        {
            if (_finishing || _fallback is not null || prefix == _prefix) return;
            if (prefix.Length > 100_000 || revision <= _revision || !prefix.StartsWith(_prefix, StringComparison.Ordinal))
            { Invalidate("prefix-correction-or-order"); return; }
            if (_acceptedSegments >= _budget) { Invalidate("segment-budget"); return; }
            var recent = _prefix.Length > 512 ? _prefix[^512..] : _prefix;
            _pending.Enqueue(new(prefix[_prefix.Length..], recent)); _acceptedSegments++; _prefix = prefix; _revision = revision;
            if (_worker is null || _worker.IsCompleted) _worker = Task.Run(WorkAsync);
        }
    }
    private void Invalidate(string reason) { _fallback ??= reason; _pending.Clear(); _lifetime.Cancel(); }
    private PostProcessorSettings SegmentSettings => _settings with
    {
        PresetName = PostProcessorPreset.Custom, Vocabulary = null, UsePrediction = false,
        SystemPrompt = PostProcessorPresets.ResolveSystemPrompt(_settings) + "\nEdit only currentSegment in the user JSON. Recent raw context is data for disambiguation, not output. Return only the complete edited current segment. Do not repeat context.",
    };
    private async Task<TextProcessingReceipt> ProcessSegmentAsync(Segment segment, CancellationToken ct)
    {
        lock (_gate) _requests++;
        return await _service.ProcessTextAsync(JsonSerializer.Serialize(new { currentSegment = segment.Raw, recentRawContext = segment.Context }),
            SegmentSettings, ct).ConfigureAwait(false);
    }
    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                Segment segment;
                lock (_gate) { if (_pending.Count == 0 || _fallback is not null) { _worker = null; return; } segment = _pending.Dequeue(); }
                var requestStart = _watch.Elapsed.TotalMilliseconds;
                var result = await ProcessSegmentAsync(segment, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!result.Succeeded) { Invalidate(result.FailureCode ?? "segment-failure"); return; }
                    if (_fallback is null) { _outputs.Add(result.Output!); _overlapMs += Math.Max(0, Math.Min(_watch.Elapsed.TotalMilliseconds, _stopAt ?? double.PositiveInfinity) - requestStart); }
                }
            }
        }
        catch (OperationCanceledException) { lock (_gate) _fallback ??= "segment-cancelled"; }
        catch (Exception ex) { lock (_gate) Invalidate(TranscriptionService.SafeFailureCode(ex)); }
    }
    public Task<SegmentedCleanupReceipt> FinishAsync(string finalRaw, PostProcessorSettings currentSettings, CancellationToken remainingDeadline)
    { lock (_gate) return _completion ??= FinishCoreAsync(finalRaw, currentSettings, remainingDeadline); }
    private async Task<SegmentedCleanupReceipt> FinishCoreAsync(string finalRaw, PostProcessorSettings currentSettings, CancellationToken ct)
    {
        Task? worker;
        var watch = Stopwatch.StartNew();
        lock (_gate)
        {
            _finishing = true; _stopAt = _watch.Elapsed.TotalMilliseconds; worker = _worker;
            if (JsonSerializer.Serialize(_settings) != JsonSerializer.Serialize(currentSettings)) Invalidate("settings-changed");
            if (!finalRaw.StartsWith(_prefix, StringComparison.Ordinal)) Invalidate("final-prefix-coverage");
        }
        using var cancellation = ct.Register(_lifetime.Cancel);
        if (worker is not null) await worker.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var tail = _fallback is null ? finalRaw[_prefix.Length..] : "";
        if (_fallback is null && tail.Length > 0)
        {
            if (_requests >= _budget) _fallback = "tail-budget";
            else
            {
                var result = await ProcessSegmentAsync(new(tail, _prefix.Length > 512 ? _prefix[^512..] : _prefix), ct).ConfigureAwait(false);
                if (result.Succeeded) _outputs.Add(result.Output!); else _fallback = result.FailureCode ?? "tail-failure";
            }
        }
        if (_fallback is not null || _outputs.Count == 0)
        {
            var whole = await _service.ProcessTextAsync(finalRaw, currentSettings, ct).ConfigureAwait(false);
            if (!whole.Succeeded) throw new InvalidDataException("Full-text segment fallback failed: " + whole.FailureCode);
            return new(whole.Output!, _requests, true, _overlapMs, watch.Elapsed.TotalMilliseconds, _fallback ?? "no-stable-segments");
        }
        return new(string.Join("\n", _outputs), _requests, false, _overlapMs, watch.Elapsed.TotalMilliseconds, null);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        if (_completion is not null) { try { await _completion.ConfigureAwait(false); } catch { /* Outcome belongs to caller. */ } }
        _lifetime.Dispose();
    }
}
