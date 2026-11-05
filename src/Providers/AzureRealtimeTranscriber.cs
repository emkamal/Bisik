using System.Net.WebSockets;
using Bisik.Core;
using Bisik.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Bisik.Providers;

public interface IAzureRealtimeTranscriber
{
    Task<string> TranscribeAsync(string audioFilePath, string language, ProviderSettings settings, CancellationToken cancellationToken);
}

public sealed class AzureRealtimeTranscriber(HttpClient httpClient, Func<ClientWebSocket>? webSocketFactory = null)
    : IAzureRealtimeTranscriber
{
    public async Task<string> TranscribeAsync(string audioFilePath, string language, ProviderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        using var source = Path.GetExtension(audioFilePath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? (WaveStream)new WaveFileReader(audioFilePath) : new MediaFoundationReader(audioFilePath);
        var format = new WaveFormat(24000, 16, 1);
        var needsConversion = source.WaveFormat.SampleRate != format.SampleRate || source.WaveFormat.Channels != 1 ||
            source.WaveFormat.BitsPerSample != 16 || source.WaveFormat.Encoding != WaveFormatEncoding.Pcm;
        if (needsConversion) MediaFoundationApi.Startup();
        using var resampler = needsConversion ? new MediaFoundationResampler(source, format) { ResamplerQuality = 60 } : null;
        IWaveProvider audio = resampler is null ? source : resampler;
        await using var stream = new RealtimeTranscriptionStream(httpClient, webSocketFactory);
        await stream.StartAsync(new AppSettings { Provider = settings }, language, cancellationToken).ConfigureAwait(false);

        // One continuous conversion preserves boundaries and the tail of the captured recording.
        var buffer = new byte[24 * 1024];
        int count;
        while ((count = audio.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.PushPcm24KhzAsync(buffer, count, cancellationToken).ConfigureAwait(false);
        }
        return await stream.CommitAndFinalizeAsync(cancellationToken).ConfigureAwait(false);
    }
}
