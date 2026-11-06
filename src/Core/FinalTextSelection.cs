namespace Bisik.Core;

/// <summary>Raw/edited completion races select exactly one final text; cleanup owns a separate token.</summary>
internal sealed class FinalTextSelection : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string> _selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cleanupCancellation;
    private Task? _cleanupTask;
    private string? _raw;
    private bool _skipCleanup;
    private bool _cleanupSelected;
    public bool CleanupSelected { get { lock (_gate) return _cleanupSelected; } }
    public bool HasTranscript { get { lock (_gate) return _raw is not null; } }
    public void SkipCleanup() { lock (_gate) _skipCleanup = true; }
    public bool UseTranscriptNow()
    {
        lock (_gate)
        {
            if (_raw is null || !_selected.TrySetResult(_raw)) return false;
            _cleanupCancellation?.Cancel();
            return true;
        }
    }
    public Task<string> SelectAsync(string raw, Func<CancellationToken, Task<string>> cleanup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _raw = raw;
            if (_skipCleanup) _selected.TrySetResult(raw);
            if (!_selected.Task.IsCompleted)
            {
                _cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cleanupTask = ObserveCleanupAsync(cleanup, _cleanupCancellation.Token);
            }
        }
        return _selected.Task.WaitAsync(ct);
    }
    private async Task ObserveCleanupAsync(Func<CancellationToken, Task<string>> cleanup, CancellationToken ct)
    {
        try
        {
            var edited = await cleanup(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_selected.TrySetResult(edited)) _cleanupSelected = true;
            }
        }
        catch (OperationCanceledException) { _selected.TrySetCanceled(ct); }
        catch (Exception exception) { _selected.TrySetException(exception); }
    }
    public async ValueTask DisposeAsync()
    {
        _cleanupCancellation?.Cancel();
        if (_cleanupTask is not null) await _cleanupTask.ConfigureAwait(false);
        _cleanupCancellation?.Dispose();
    }
}
