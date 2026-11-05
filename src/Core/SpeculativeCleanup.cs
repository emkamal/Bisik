using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

/// <summary>One active request plus the newest snapshot. Speculative text never leaves this session.</summary>
internal sealed class SpeculativeCleanup : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PostProcessorSettings _settings;
    private readonly Func<string, PostProcessorSettings, CancellationToken, Task<string>> _process;
    private readonly CancellationToken _sessionToken;
    private readonly SessionMetrics? _metrics = SessionMetrics.Current;
    private readonly int _limit;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCancellation;
    private Task? _worker;
    private Task<string>? _active;
    private string? _activeText;
    private string? _pending;
    private int _requests;
    private bool _finalizing;

    public SpeculativeCleanup(PostProcessorSettings settings,
        Func<string, PostProcessorSettings, CancellationToken, Task<string>> process, CancellationToken ct, int limit = 2)
    {
        _settings = new AppSettings { PostProcessor = settings }.Snapshot().PostProcessor;
        _process = process;
        _sessionToken = ct;
        _limit = Math.Clamp(limit, 0, 3); // Required final cleanup is outside this speculative allowance.
    }

    public void OnSnapshot(string raw)
    {
        lock (_gate)
        {
            if (_finalizing || string.IsNullOrWhiteSpace(raw) || _requests >= _limit || raw == _activeText || _cache.ContainsKey(raw)) return;
            _pending = raw;
            if (_worker is null || _worker.IsCompleted) _worker = Task.Run(WorkAsync);
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (!_sessionToken.IsCancellationRequested)
            {
                await Task.Delay(250, _sessionToken).ConfigureAwait(false);
                string text;
                Task<string> request;
                lock (_gate)
                {
                    if (_finalizing || _pending is null || _requests >= _limit) return;
                    text = _pending;
                    _pending = null;
                    _activeText = text;
                    _activeCancellation?.Dispose();
                    _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
                    _requests++;
                    _metrics?.Add(MetricCount.SpeculativeRequests);
                    request = _active = _process(text, _settings, _activeCancellation.Token);
                }
                try
                {
                    var result = await request.ConfigureAwait(false);
                    lock (_gate) if (!_finalizing && !string.IsNullOrWhiteSpace(result)) _cache[text] = result;
                }
                catch { /* A speculative failure cannot replace the required final cleanup. */ }
                lock (_gate)
                {
                    _active = null;
                    _activeText = null;
                    if (_pending is null || _finalizing) return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<string> FinishAsync(string raw, PostProcessorSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<string>? matching = null;
        lock (_gate)
        {
            _finalizing = true;
            _pending = null;
            // Comparison includes the complete custom prompt, provider and reasoning capability.
            if (JsonSerializer.Serialize(_settings) == JsonSerializer.Serialize(settings))
            {
                if (_cache.TryGetValue(raw, out var cached)) return cached;
                if (_activeText == raw) matching = _active;
            }
            if (matching is null) _activeCancellation?.Cancel();
        }
        if (matching is not null)
        {
            try
            {
                var result = await matching.WaitAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        // Do not await an obsolete remote request before starting required whole-text cleanup.
        return await _process(raw, settings, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _finalizing = true; _pending = null; _activeCancellation?.Cancel(); }
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _activeCancellation?.Dispose();
        _cache.Clear();
    }
    public void CancelPending()
    { lock (_gate) { _finalizing = true; _pending = null; _activeCancellation?.Cancel(); } }
}
