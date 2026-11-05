using System.Net;

namespace Bisik.Core;

public sealed class HttpMetricsHandler(HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var metrics = SessionMetrics.Current;
        var receipt = metrics?.BeginHttpRequest();
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (receipt is { } id)
        {
            metrics!.HttpHeaders(id, (int)response.StatusCode, RetryPolicy.FromResponse(response));
            response.Content = new TrackedContent(response.Content, () => metrics.HttpBodyComplete(id));
        }
        return response;
    }
    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _completed;
        public TrackedContent(HttpContent inner, Action completed)
        { _inner = inner; _completed = completed; foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { await _inner.CopyToAsync(stream).ConfigureAwait(false); _completed(); }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        { await _inner.CopyToAsync(stream, context, ct).ConfigureAwait(false); _completed(); }
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            return new CompletionStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _completed);
        }
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
        {
            return new CompletionStream(await _inner.ReadAsStreamAsync(ct).ConfigureAwait(false), _completed);
        }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class CompletionStream(Stream inner, Action completed) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); if (read == 0) completed(); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false); if (read == 0) completed(); return read; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
