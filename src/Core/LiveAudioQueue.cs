using System.Threading.Channels;

namespace Bisik.Core;

/// <summary>Capture callbacks only copy/enqueue. One sender owns ordering and network awaits.</summary>
internal sealed class LiveAudioQueue
{
    private readonly Channel<AudioChunkEventArgs> _queue = Channel.CreateUnbounded<AudioChunkEventArgs>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly object _gate = new();
    private readonly int _maximumBytes;
    private int _queuedBytes;
    private bool _closed;
    private Exception? _failure;
    private readonly SessionMetrics? _metrics = SessionMetrics.Current;
    public Task Sender { get; }

    public LiveAudioQueue(IRealtimeTranscriber stream, Task setup, int sampleRate, CancellationToken ct)
    {
        // Bound queued PCM to 10 seconds and at most 2 MiB, including delayed setup.
        _maximumBytes = Math.Min(2 * 1024 * 1024, Math.Clamp(sampleRate, 8000, 96000) * 2 * 10);
        Sender = SendAsync(stream, setup, ct);
    }

    public void Enqueue(AudioChunkEventArgs chunk)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (chunk.BytesRecorded == 0) return;
            if (chunk.BytesRecorded < 0 || chunk.BytesRecorded > chunk.Buffer.Length || chunk.BytesRecorded % 2 != 0)
            { Fail(new InvalidDataException("Capture supplied an incomplete PCM sample.")); return; }
            if (chunk.BytesRecorded > _maximumBytes - _queuedBytes)
            { Fail(new IOException("Live audio queue exceeded 10 seconds/2 MiB. Complete recording fallback is required.")); return; }
            _queuedBytes += chunk.BytesRecorded;
            _metrics?.Add(MetricCount.QueuedBytes, chunk.BytesRecorded);
            _queue.Writer.TryWrite(new AudioChunkEventArgs(chunk.Buffer.AsSpan(0, chunk.BytesRecorded).ToArray(), chunk.BytesRecorded, chunk.SampleRate));
        }
    }

    public void Close() { lock (_gate) { _closed = true; _queue.Writer.TryComplete(_failure); } }
    private void Fail(Exception failure) { _failure = failure; _closed = true; _queue.Writer.TryComplete(failure); }

    private async Task SendAsync(IRealtimeTranscriber stream, Task setup, CancellationToken ct)
    {
        await setup.WaitAsync(ct).ConfigureAwait(false);
        await foreach (var chunk in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // Count in-flight bytes until the awaited send completes, bounding slow transports too.
            await stream.PushAudioChunkAsync(chunk.Buffer, chunk.BytesRecorded, chunk.SampleRate, ct).ConfigureAwait(false);
            lock (_gate) _queuedBytes -= chunk.BytesRecorded;
        }
    }
}
