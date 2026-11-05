using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

/// <summary>One fresh unused session. Idle cancellation is disarmed on atomic ownership transfer.</summary>
internal sealed class RealtimePrewarmer(Func<AppSettings, IRealtimeTranscriber?> create,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Slot? _slot;
    private bool _disposed;
    private Task _retirement = Task.CompletedTask;
    public Task PrepareAsync(AppSettings settings, CancellationToken shutdownToken)
    {
        lock (_gate)
        {
            if (_slot is { } invalid && (invalid.Setup.IsFaulted || invalid.Setup.IsCompletedSuccessfully && !invalid.Stream.IsConnected))
            { _slot = null; Retire(invalid); }
            if (_disposed || _slot is not null) return _slot?.Setup ?? Task.CompletedTask;
            var stream = create(settings);
            if (stream is null) return Task.CompletedTask;
            SessionMetrics.Current?.Add(MetricCount.PrewarmSessions);
            var slot = _slot = new(stream, settings.Snapshot(), shutdownToken);
            slot.Setup = Task.Run(async () =>
            {
                await stream.StartAsync(slot.Settings, slot.Settings.DefaultLanguage, slot.Lifetime.Token).ConfigureAwait(false);
                slot.Expiry = ExpireAsync(slot, Math.Clamp(settings.PrewarmIdleSeconds, 5, 60));
            });
            return slot.Setup;
        }
    }

    public IRealtimeTranscriber? Claim(AppSettings settings, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_slot is not { } slot) return null;
            if (Key(slot.Settings) != Key(settings) || slot.Setup.IsFaulted || slot.Setup.IsCompletedSuccessfully && !slot.Stream.IsConnected)
            { _slot = null; Retire(slot); return null; }
            if (!slot.Setup.IsCompletedSuccessfully) return null;
            _slot = null;
            slot.Idle.Cancel();
            return new ClaimedStream(slot, ct);
        }
    }

    private async Task ExpireAsync(Slot slot, int seconds)
    {
        try { await (delay?.Invoke(TimeSpan.FromSeconds(seconds), slot.Idle.Token) ?? Task.Delay(TimeSpan.FromSeconds(seconds), slot.Idle.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Task retirement;
        lock (_gate) { if (!ReferenceEquals(_slot, slot)) return; _slot = null; retirement = Retire(slot); }
        await retirement.ConfigureAwait(false);
    }

    private static string Key(AppSettings settings) => JsonSerializer.Serialize(new { settings.Provider, settings.DefaultLanguage });
    private Task Retire(Slot slot)
    {
        var previous = _retirement;
        return _retirement = RetireAsync();
        async Task RetireAsync() { await previous.ConfigureAwait(false); await DisposeSlotAsync(slot).ConfigureAwait(false); }
    }
    private static async Task DisposeSlotAsync(Slot slot)
    {
        slot.Lifetime.Cancel();
        slot.Idle.Cancel();
        try { await slot.Setup.ConfigureAwait(false); } catch { /* Observed setup failure. */ }
        await slot.Stream.DisposeAsync().ConfigureAwait(false);
        slot.Lifetime.Dispose();
        slot.Idle.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Slot? slot;
        lock (_gate) { _disposed = true; slot = _slot; _slot = null; }
        Task retirement;
        lock (_gate) retirement = slot is null ? _retirement : Retire(slot);
        await retirement.ConfigureAwait(false);
    }

    private sealed class Slot(IRealtimeTranscriber stream, AppSettings settings, CancellationToken ct)
    {
        public IRealtimeTranscriber Stream { get; } = stream;
        public AppSettings Settings { get; } = settings;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public CancellationTokenSource Idle { get; } = new();
        public Task Setup { get; set; } = Task.CompletedTask;
        public Task Expiry { get; set; } = Task.CompletedTask;
    }

    private sealed class ClaimedStream : IRealtimeTranscriber
    {
        private readonly Slot _slot;
        private readonly CancellationTokenRegistration _activeCancellation;
        public ClaimedStream(Slot slot, CancellationToken ct) { _slot = slot; _activeCancellation = ct.Register(slot.Lifetime.Cancel); }
        public bool IsConnected => _slot.Stream.IsConnected;
        public Task StartAsync(AppSettings settings, string language, CancellationToken ct) => _slot.Setup.WaitAsync(ct);
        public Task PushAudioChunkAsync(byte[] data, int count, CancellationToken ct) => _slot.Stream.PushAudioChunkAsync(data, count, ct);
        public Task PushAudioChunkAsync(byte[] data, int count, int rate, CancellationToken ct) => _slot.Stream.PushAudioChunkAsync(data, count, rate, ct);
        public Task CommitIntermediateSegmentAsync(CancellationToken ct) => _slot.Stream.CommitIntermediateSegmentAsync(ct);
        public Task<string> CommitAndFinalizeAsync(CancellationToken ct) => _slot.Stream.CommitAndFinalizeAsync(ct);
        public event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable { add => _slot.Stream.PartialTranscriptAvailable += value; remove => _slot.Stream.PartialTranscriptAvailable -= value; }
        public event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted { add => _slot.Stream.TranscriptionCompleted += value; remove => _slot.Stream.TranscriptionCompleted -= value; }
        public event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed { add => _slot.Stream.TranscriptionFailed += value; remove => _slot.Stream.TranscriptionFailed -= value; }
        public event EventHandler<CompletedTranscriptEventArgs>? CompletedTranscriptAvailable { add => _slot.Stream.CompletedTranscriptAvailable += value; remove => _slot.Stream.CompletedTranscriptAvailable -= value; }
        public async ValueTask DisposeAsync()
        {
            _activeCancellation.Dispose();
            await _slot.Expiry.ConfigureAwait(false);
            await DisposeSlotAsync(_slot).ConfigureAwait(false);
        }
    }
}
