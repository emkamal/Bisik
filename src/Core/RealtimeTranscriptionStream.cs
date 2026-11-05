using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bisik.Models;
using Bisik.Providers;

namespace Bisik.Core;

/// <summary>A single transcription session. Sends are serialized; final results include every committed item.</summary>
public sealed class RealtimeTranscriptionStream : IRealtimeTranscriber
{
    private const int MinimumCommitBytes = 4800; // 100 ms of PCM16 mono at 24 kHz.
    private readonly HttpClient _httpClient;
    private readonly Func<Uri, string, CancellationToken, Task<WebSocket>> _connect;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TranscriptItem> _items = new(StringComparer.Ordinal);
    private readonly List<string> _committedItems = [];
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private Pcm16Upsampler? _upsampler;
    private int? _inputSampleRate;
    private CancellationTokenSource? _lifetime;
    private WebSocket? _socket;
    private Task? _receiveTask;
    private int _pendingCommits;
    private int _bufferedBytes;
    private bool _started;
    private bool _finalizing;
    private bool _disposed;
    private int _completionRaised;
    private Exception? _failure;
    private string _lastSnapshot = string.Empty;
    private long _snapshotRevision;

    public RealtimeTranscriptionStream(HttpClient httpClient, Func<ClientWebSocket>? webSocketFactory = null)
        : this(httpClient, async (uri, token, ct) =>
        {
            var socket = webSocketFactory?.Invoke() ?? new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                return socket;
            }
            catch (WebSocketException)
            {
                socket.Dispose();
                throw new WebSocketException("Azure realtime WebSocket connection failed. Check the network and realtime endpoint.");
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }) { }

    // A transport seam lets protocol tests exercise the real receive/send state machine.
    public RealtimeTranscriptionStream(HttpClient httpClient,
        Func<Uri, string, CancellationToken, Task<WebSocket>> connect)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    public event EventHandler<PartialTranscriptEventArgs>? PartialTranscriptAvailable;
    public event EventHandler<RealtimeCompletedEventArgs>? TranscriptionCompleted;
    public event EventHandler<RealtimeFailedEventArgs>? TranscriptionFailed;
    public event EventHandler<CompletedTranscriptEventArgs>? CompletedTranscriptAvailable;
    public bool IsConnected => !_disposed && !_finalizing && _failure is null && _socket?.State == WebSocketState.Open;

    public async Task StartAsync(AppSettings settings, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lifetime is not null)
            throw new InvalidOperationException("Create a new realtime stream for each recording.");

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            SessionMetrics.Current?.Mark(TimingStage.SecretStart);
            var token = await AzureRealtimeProtocol.CreateClientSecretAsync(_httpClient, settings.Provider, timeout.Token).ConfigureAwait(false);
            SessionMetrics.Current?.Mark(TimingStage.SecretEnd);
            _socket = await _connect(AzureRealtimeProtocol.BuildRealtimeUri(settings.Provider), token, timeout.Token).ConfigureAwait(false);
            _receiveTask = ReceiveLoopAsync(_lifetime.Token);
            // Dedicated transcription capability is explicit for aliases, or identified by the documented model.
            var supportsDelay = settings.Provider.AzureRealtime || settings.Provider.DeploymentName.Contains("realtime-whisper", StringComparison.OrdinalIgnoreCase) ||
                (settings.Provider.Model?.Contains("realtime-whisper", StringComparison.OrdinalIgnoreCase) ?? false);
            await SendAsync(AzureRealtimeProtocol.SessionUpdate(settings.Provider.DeploymentName, language,
                supportsDelay ? settings.Provider.TranscriptionDelay : null), timeout.Token).ConfigureAwait(false);
            await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            _started = true;
            SessionMetrics.Current?.Mark(TimingStage.WssReady);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lifetime.Cancel();
            throw new TimeoutException("Azure realtime connection or session setup timed out after 30 seconds.");
        }
        catch
        {
            _lifetime.Cancel();
            throw;
        }
    }

    public Task PushAudioChunkAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct) =>
        PushAudioChunkAsync(pcmData, bytesRecorded, 16000, ct);

    public Task PushAudioChunkAsync(byte[] pcmData, int bytesRecorded, int sampleRate, CancellationToken ct) =>
        PushAudioAsync(pcmData, bytesRecorded, sampleRate, ct);

    internal Task PushPcm24KhzAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct) =>
        PushAudioAsync(pcmData, bytesRecorded, null, ct);

    private async Task PushAudioAsync(byte[] pcmData, int bytesRecorded, int? sampleRate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcmData);
        if (bytesRecorded < 0 || bytesRecorded > pcmData.Length || bytesRecorded % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRecorded), "PCM16 chunks must contain complete samples.");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            if (_finalizing)
                throw new InvalidOperationException("Audio cannot be appended after finalization starts.");
            if (sampleRate is not null)
            {
                if (sampleRate is < 8000 or > 96000 || (_inputSampleRate is not null && _inputSampleRate != sampleRate))
                    throw new InvalidDataException("Capture rate is unsupported or changed within a recording.");
                _inputSampleRate = sampleRate;
                _upsampler ??= new Pcm16Upsampler(sampleRate.Value);
            }
            var bytes = sampleRate is not null ? _upsampler!.Convert(pcmData.AsSpan(0, bytesRecorded)) : pcmData[..bytesRecorded];
            await AppendAsync(bytes, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async Task CommitIntermediateSegmentAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            if (!_finalizing && _bufferedBytes >= MinimumCommitBytes)
                await CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async Task<string> CommitAndFinalizeAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            if (!_finalizing)
            {
                // Flush the last resampling fraction instead of dropping the last input sample.
                if (_upsampler is not null) await AppendAsync(_upsampler.Flush(), ct).ConfigureAwait(false);
                if (_bufferedBytes > 0)
                {
                    if (_bufferedBytes < MinimumCommitBytes)
                        await AppendAsync(new byte[MinimumCommitBytes - _bufferedBytes], ct).ConfigureAwait(false);
                    await CommitAsync(ct).ConfigureAwait(false);
                }
                lock (_stateLock)
                {
                    _finalizing = true;
                    TryFinish();
                }
            }
        }
        finally { _sendLock.Release(); }

        try
        {
            var text = await _final.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            _lifetime!.Cancel();
            if (_receiveTask is not null)
                await _receiveTask.ConfigureAwait(false);
            if (Interlocked.Exchange(ref _completionRaised, 1) == 0)
                TranscriptionCompleted?.Invoke(this, new RealtimeCompletedEventArgs(text));
            return text;
        }
        catch (TimeoutException)
        {
            _lifetime!.Cancel();
            throw new TimeoutException("Azure realtime transcription did not finalize every audio segment within 30 seconds. No partial text was inserted.");
        }
        catch (OperationCanceledException)
        {
            _lifetime!.Cancel();
            throw;
        }
    }

    private async Task AppendAsync(byte[] bytes, CancellationToken ct)
    {
        for (var offset = 0; offset < bytes.Length; offset += 32 * 1024)
        {
            var count = Math.Min(32 * 1024, bytes.Length - offset);
            await SendAsync(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(bytes, offset, count) }, ct).ConfigureAwait(false);
            _bufferedBytes += count;
            SessionMetrics.Current?.Add(MetricCount.SentBytes, count);
        }
    }

    private async Task CommitAsync(CancellationToken ct)
    {
        lock (_stateLock) _pendingCommits++;
        SessionMetrics.Current?.Mark(TimingStage.CommitSent);
        await SendAsync(new { type = "input_audio_buffer.commit" }, ct).ConfigureAwait(false);
        _bufferedBytes = 0;
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await _socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
            throw new TimeoutException("Azure realtime audio upload timed out.");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket!.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Azure realtime connection closed before transcription finished.");
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1024 * 1024)
                        throw new InvalidDataException("Azure realtime returned an invalid or oversized message.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                HandleEvent(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ready.TrySetCanceled(ct);
            _final.TrySetCanceled(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Aborting a Windows WebSocket during disposal may surface as a socket error instead of cancellation.
            _ready.TrySetCanceled(ct);
            _final.TrySetCanceled(ct);
        }
        catch (Exception exception)
        {
            lock (_stateLock) _failure = exception;
            _ready.TrySetException(exception);
            _final.TrySetException(exception);
            TranscriptionFailed?.Invoke(this, new RealtimeFailedEventArgs(exception.Message));
        }
    }

    private void HandleEvent(JsonElement root)
    {
        lock (_stateLock)
        {
            var eventId = Text(root, "event_id");
            if (eventId.Length > 0 && !_eventIds.Add(eventId)) return;
            var type = Text(root, "type");
            if (type == "session.updated") { _ready.TrySetResult(); return; }
            if (type is "error" or "conversation.item.input_audio_transcription.failed")
                throw new InvalidOperationException(AzureRealtimeProtocol.DescribeError(root));
            if (type is not ("input_audio_buffer.committed" or "conversation.item.input_audio_transcription.delta" or
                "conversation.item.input_audio_transcription.completed")) return;

            var id = Text(root, "item_id");
            if (id.Length == 0) throw new InvalidDataException("Azure realtime transcript event omitted its item identifier.");
            if (!_items.TryGetValue(id, out var item)) _items[id] = item = new TranscriptItem();
            switch (type)
            {
                case "input_audio_buffer.committed":
                    if (!_committedItems.Contains(id))
                    {
                        _committedItems.Add(id);
                        if (_pendingCommits > 0) _pendingCommits--;
                        SessionMetrics.Current?.Mark(TimingStage.CommitAcknowledged);
                    }
                    break;
                case "conversation.item.input_audio_transcription.delta":
                    if (!item.Completed)
                    {
                        var delta = Text(root, "delta");
                        SessionMetrics.Current?.Mark(TimingStage.FirstPartial, once: true);
                        item.Partial.Append(delta);
                        PartialTranscriptAvailable?.Invoke(this, new PartialTranscriptEventArgs(delta, JoinTranscript()));
                    }
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    if (item.Completed) break;
                    // Completed text is authoritative, including an empty transcript for silence.
                    item.Text = Text(root, "transcript");
                    item.Completed = true;
                    SessionMetrics.Current?.Mark(TimingStage.ItemCompleted);
                    break;
            }
            var snapshot = string.Join(" ", _committedItems.TakeWhile(id => _items[id].Completed)
                .Select(id => _items[id].Text.Trim()).Where(text => text.Length > 0));
            if (snapshot != _lastSnapshot)
            {
                _lastSnapshot = snapshot;
                CompletedTranscriptAvailable?.Invoke(this, new(snapshot, ++_snapshotRevision));
            }
            TryFinish();
        }
    }

    private void TryFinish()
    {
        if (_finalizing && _pendingCommits == 0 && _committedItems.All(id => _items[id].Completed))
            _final.TrySetResult(JoinTranscript());
    }

    private string JoinTranscript() => string.Join(" ", _committedItems
        .Select(id => _items[id].Completed ? _items[id].Text : _items[id].Partial.ToString())
        .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim()));

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) throw new InvalidOperationException("Realtime transcription has not started.");
        lock (_stateLock)
            if (_failure is not null) throw new InvalidOperationException("Azure realtime session failed.", _failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime?.Cancel();
        _socket?.Abort();
        if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
        _socket?.Dispose();
        _lifetime?.Dispose();
        _final.TrySetCanceled();
    }

    private sealed class TranscriptItem
    {
        public StringBuilder Partial { get; } = new();
        public string Text { get; set; } = string.Empty;
        public bool Completed { get; set; }
    }
}

// Keep fractional position between chunks: restarting a resampler at every mic callback adds seams.
internal sealed class Pcm16Upsampler(int inputRate = 16000)
{
    private long _samples;
    private long _nextPosition;
    private short _previous;

    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        for (var offset = 0; offset < input.Length; offset += 2)
        {
            var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(input[offset..]);
            var position = _samples * 24000;
            while (_nextPosition <= position)
            {
                var value = _samples == 0 ? sample : _previous + (sample - _previous) * (_nextPosition - position + 24000) / 24000;
                writer.Write((short)value);
                _nextPosition += inputRate;
            }
            _previous = sample;
            _samples++;
        }
        return output.ToArray();
    }

    public byte[] Flush()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        while (_nextPosition < _samples * 24000)
        {
            writer.Write(_previous);
            _nextPosition += inputRate;
        }
        return output.ToArray();
    }
}
