namespace Bisik.Core;

// A lease stays held through cancellation, persistence and asynchronous drain.
public sealed class OperationGate
{
    private int _held;
    public bool IsActive => Volatile.Read(ref _held) != 0;
    public IDisposable? TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0 ? new Lease(this) : null;
    private sealed class Lease(OperationGate owner) : IDisposable
    {
        private OperationGate? _owner = owner;
        public void Dispose() { var current = Interlocked.Exchange(ref _owner, null); if (current is not null) Volatile.Write(ref current._held, 0); }
    }
}
