using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bisik.Core;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class RealtimeTranscriptionStreamTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("en")]
    [InlineData("id")]
    public async Task Session_should_use_proven_token_handshake_and_explicit_pcm_format(string language)
    {
        using var http = new SecretHandler();
        using var client = new HttpClient(http);
        using var socket = new FakeSocket();
        Uri? destination = null;
        await using var stream = new RealtimeTranscriptionStream(client, (uri, token, ct) =>
        {
            destination = uri;
            token.Should().Be("ephemeral-test-token");
            return Task.FromResult<WebSocket>(socket);
        });
        var settings = Settings() with
        {
            Provider = Settings().Provider with
            {
                Endpoint = "https://example.azure-api.net/app/openai/v1/",
                ExtraHeaders = new() { ["bisik-realtime-calls-endpoint"] = "https://direct.openai.azure.com/openai/v1" },
            },
        };

        await stream.StartAsync(settings, language, CancellationToken.None);

        http.Uri!.AbsolutePath.Should().Be("/app/openai/v1/realtime/client_secrets");
        http.HasCallsHeader.Should().BeFalse();
        destination!.ToString().Should().Be("wss://direct.openai.azure.com/openai/v1/realtime?intent=transcription");
        var input = socket.Sent.First().GetProperty("session").GetProperty("audio").GetProperty("input");
        input.GetProperty("format").GetProperty("rate").GetInt32().Should().Be(24000);
        input.GetProperty("turn_detection").ValueKind.Should().Be(JsonValueKind.Null);
        var transcription = input.GetProperty("transcription");
        transcription.TryGetProperty("language", out var languageValue).Should().Be(language != "auto");
        if (language != "auto") languageValue.GetString().Should().Be(language);
    }

    [Fact]
    public async Task Finalization_should_wait_for_all_items_and_use_authoritative_completions_in_audio_order()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        await stream.StartAsync(Settings(), "auto", CancellationToken.None);
        await stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None);
        await stream.CommitIntermediateSegmentAsync(CancellationToken.None);
        socket.Queue("""{"type":"input_audio_buffer.committed","event_id":"ack1","item_id":"first"}""");
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.delta","event_id":"d1","item_id":"first","delta":"incorrect"}""");
        // Duplicate events must not duplicate a delta or count as a second commit.
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.delta","event_id":"d1","item_id":"first","delta":"incorrect"}""");
        await stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None);
        var result = stream.CommitAndFinalizeAsync(CancellationToken.None);
        await socket.WaitForSentAsync("input_audio_buffer.commit", 2);
        socket.Queue("""{"type":"input_audio_buffer.committed","event_id":"ack2","item_id":"second"}""");
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"second","transcript":"Baris kedua, Indonesia."}""");
        await Task.Delay(20);
        result.IsCompleted.Should().BeFalse("a later item's completion cannot finish an earlier item");
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"first","transcript":"Hello dunia!"}""");

        (await result.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("Hello dunia! Baris kedua, Indonesia.");
    }

    [Fact]
    public async Task Synchronous_completion_during_commit_should_not_be_lost()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket
        {
            OnSend = message =>
            {
                if (message.GetProperty("type").GetString() != "input_audio_buffer.commit") return [];
                return ["""{"type":"input_audio_buffer.committed","item_id":"item"}""",
                    """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item","transcript":"Last words."}"""];
            },
        };
        await using var stream = Create(client, socket);
        await stream.StartAsync(Settings(), "id", CancellationToken.None);
        await stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None);
        (await stream.CommitAndFinalizeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("Last words.");
    }

    [Fact]
    public async Task Silence_completion_should_not_fall_back_to_hallucinated_partial_text()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        await stream.StartAsync(Settings(), "auto", CancellationToken.None);
        await stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None);
        var result = stream.CommitAndFinalizeAsync(CancellationToken.None);
        await socket.WaitForSentAsync("input_audio_buffer.commit", 1);
        socket.Queue("""{"type":"input_audio_buffer.committed","item_id":"silence"}""");
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"silence","delta":"maybe words"}""");
        socket.Queue("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"silence","transcript":""}""");
        (await result.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_recording_should_not_send_an_empty_commit()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        await stream.StartAsync(Settings(), "auto", CancellationToken.None);
        await stream.CommitIntermediateSegmentAsync(CancellationToken.None);
        (await stream.CommitAndFinalizeAsync(CancellationToken.None)).Should().BeEmpty();
        socket.Sent.Should().NotContain(message => message.GetProperty("type").GetString() == "input_audio_buffer.commit");
    }

    [Fact]
    public async Task Failure_before_finalization_should_fail_promptly_and_redact_arbitrary_server_text()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.TranscriptionFailed += (_, _) => failed.TrySetResult();
        await stream.StartAsync(Settings(), "auto", CancellationToken.None);
        socket.Queue("""{"type":"error","error":{"code":"invalid_value","message":"secret echoed by gateway"}}""");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var action = () => stream.CommitAndFinalizeAsync(CancellationToken.None);
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.ToString().Should().Contain("invalid_value").And.NotContain("secret echoed");
    }

    [Fact]
    public async Task Cancellation_and_disposal_should_release_pending_receives_without_success()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        using var cancellation = new CancellationTokenSource();
        await stream.StartAsync(Settings(), "auto", cancellation.Token);
        await stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None);
        var result = stream.CommitAndFinalizeAsync(cancellation.Token);
        cancellation.Cancel();
        var action = () => result;
        await action.Should().ThrowAsync<OperationCanceledException>();
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Concurrent_audio_callbacks_should_serialize_websocket_sends()
    {
        using var client = new HttpClient(new SecretHandler());
        using var socket = new FakeSocket();
        await using var stream = Create(client, socket);
        await stream.StartAsync(Settings(), "auto", CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => stream.PushAudioChunkAsync(new byte[6400], 6400, CancellationToken.None)));
        socket.MaxConcurrentSends.Should().Be(1);
    }

    [Fact]
    public async Task Resampling_should_preserve_sample_count_and_be_independent_of_chunk_boundaries()
    {
        static async Task<byte[]> Convert(int chunkSize)
        {
            using var client = new HttpClient(new SecretHandler());
            using var socket = new FakeSocket { OnSend = message => message.GetProperty("type").GetString() == "input_audio_buffer.commit"
                ? ["""{"type":"input_audio_buffer.committed","item_id":"item"}""", """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item","transcript":""}"""] : [] };
            await using var stream = Create(client, socket);
            await stream.StartAsync(Settings(), "auto", CancellationToken.None);
            var samples = Enumerable.Range(0, 6400).Select(value => (byte)(value % 256)).ToArray();
            for (var i = 0; i < samples.Length; i += chunkSize)
            {
                var chunk = samples.Skip(i).Take(chunkSize).ToArray();
                await stream.PushAudioChunkAsync(chunk, chunk.Length, CancellationToken.None);
            }
            await stream.CommitAndFinalizeAsync(CancellationToken.None);
            return socket.Sent.Where(m => m.GetProperty("type").GetString() == "input_audio_buffer.append")
                .SelectMany(m => System.Convert.FromBase64String(m.GetProperty("audio").GetString()!)).ToArray();
        }
        var whole = await Convert(6400);
        var chunks = await Convert(98);
        whole.Length.Should().Be(9600);
        chunks.Should().Equal(whole);
    }

    private static AppSettings Settings() => new()
    {
        Provider = new() { Endpoint = "https://example.openai.azure.com", ApiKey = "test-key", DeploymentName = "test-realtime" },
    };

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Continuous_conversion_preserves_chunk_boundaries_and_final_samples(int rate)
    {
        var input = Enumerable.Range(0, rate).SelectMany(i => BitConverter.GetBytes((short)(i % 3000))).ToArray();
        var whole = new Pcm16Upsampler(rate);
        var expected = whole.Convert(input).Concat(whole.Flush()).ToArray();
        var split = new Pcm16Upsampler(rate);
        var actual = new List<byte>();
        for (var i = 0; i < input.Length; i += 98)
            actual.AddRange(split.Convert(input.AsSpan(i, Math.Min(98, input.Length - i))));
        actual.AddRange(split.Flush());
        Assert.Equal(48000, actual.Count);
        Assert.Equal(expected, actual);
        Assert.Empty(split.Flush());
    }

    private static RealtimeTranscriptionStream Create(HttpClient client, FakeSocket socket) =>
        new(client, (_, _, _) => Task.FromResult<WebSocket>(socket));

    private sealed class SecretHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public bool HasCallsHeader { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            HasCallsHeader = request.Headers.Any(header => header.Key.Equals("Bisik-Realtime-Calls-Endpoint", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":"ephemeral-test-token"}""") });
        }
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        private int _activeSends;
        private WebSocketState _state = WebSocketState.Open;
        public ConcurrentQueue<JsonElement> Sent { get; } = new();
        public Func<JsonElement, string[]>? OnSend { get; init; }
        public int MaxConcurrentSends { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public void Queue(string json) => _messages.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _activeSends);
            MaxConcurrentSends = Math.Max(MaxConcurrentSends, concurrent);
            try
            {
                await Task.Yield();
                using var document = JsonDocument.Parse(buffer.AsMemory());
                var message = document.RootElement.Clone();
                Sent.Enqueue(message);
                if (message.GetProperty("type").GetString() == "session.update") Queue("""{"type":"session.updated"}""");
                foreach (var response in OnSend?.Invoke(message) ?? []) Queue(response);
                await Task.Yield();
            }
            finally { Interlocked.Decrement(ref _activeSends); }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _current ??= await _messages.Reader.ReadAsync(cancellationToken);
            // Small fragments exercise UTF-8 and JSON message reassembly, not just whole frames.
            var count = Math.Min(7, Math.Min(buffer.Count, _current.Length - _offset));
            _current.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var done = _offset == _current.Length;
            if (done) { _current = null; _offset = 0; }
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, done);
        }

        public async Task WaitForSentAsync(string type, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Sent.Count(message => message.GetProperty("type").GetString() == type) < count)
                await Task.Delay(5, timeout.Token);
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
