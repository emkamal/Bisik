namespace Bisik.Core;

public enum PendingResultKind { Completed, RawRecovery }
public sealed record PendingResult(Guid Id, TranscriptionResult Result, PendingResultKind Kind = PendingResultKind.Completed);

/// <summary>Only one insertion may claim a result. Completion cannot clear a newer publication.</summary>
public sealed class LatestResultStore
{
    private readonly object _gate = new();
    private PendingResult? _latest;
    private Guid? _claim;
    public PendingResult? Latest { get { lock (_gate) return _latest; } }
    public Guid? ClaimedId { get { lock (_gate) return _claim; } }
    public PendingResult Publish(TranscriptionResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputText)) throw new ArgumentException("Only completed nonempty results may be retained.");
        lock (_gate) return _latest = new(Guid.NewGuid(), result);
    }
    public PendingResult PublishRecovery(RawRecoverySnapshot snapshot, string failure)
    {
        if (string.IsNullOrWhiteSpace(snapshot.RawText)) throw new ArgumentException("Only authoritative nonempty raw text may be recovered.");
        lock (_gate) return _latest = new(Guid.NewGuid(), new(false, "", snapshot.RawText, failure, RawRecovery: snapshot), PendingResultKind.RawRecovery);
    }
    public PendingResult? Claim(Guid? expected = null, bool allowRecovery = false)
    {
        lock (_gate)
        {
            if (_claim is not null || _latest is null || !allowRecovery && _latest.Kind == PendingResultKind.RawRecovery
                || (expected is not null && _latest.Id != expected)) return null;
            _claim = _latest.Id;
            return _latest;
        }
    }
    public void CompleteClaim(Guid id, bool dispatched)
    {
        lock (_gate)
        {
            if (_claim != id) return;
            if (dispatched && _latest?.Id == id) _latest = null;
            _claim = null;
        }
    }
    public void Dismiss(Guid id) { lock (_gate) if (_latest?.Id == id) _latest = null; }
    public void Clear() { lock (_gate) { _latest = null; _claim = null; } }
}
