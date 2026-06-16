namespace Bisik.Core;

/// <summary>
/// Fired by <see cref="AudioRecorder"/> when a new PCM audio chunk is available
/// during an active recording session.
/// </summary>
public sealed class AudioChunkEventArgs(byte[] buffer, int bytesRecorded, int sampleRate) : EventArgs
{
    /// <summary>The raw PCM 16-bit mono audio buffer.</summary>
    public byte[] Buffer { get; } = buffer;

    /// <summary>Number of valid bytes in <see cref="Buffer"/>.</summary>
    public int BytesRecorded { get; } = bytesRecorded;

    /// <summary>Sample rate of the captured audio (e.g. 16000 Hz).</summary>
    public int SampleRate { get; } = sampleRate;
}