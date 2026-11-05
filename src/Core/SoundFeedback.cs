using System.Media;
using System.Reflection;

namespace Bisik.Core;

public sealed class SoundFeedback : IAsyncDisposable
{
    private const string SuccessResourceName = "Bisik.Resources.success.wav";
    private const string ErrorResourceName = "Bisik.Resources.error.wav";

    private volatile bool _soundEnabled;
    private readonly Func<string, Stream?> _resourceLoader;
    private readonly Action<Stream> _player;
    private readonly Action<Exception> _logError;
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;

    internal SoundFeedback(
        bool soundEnabled,
        Func<string, Stream?> resourceLoader,
        Action<Stream> player)
        : this(soundEnabled, resourceLoader, player, _ => { }) { }

    internal SoundFeedback(bool soundEnabled, Func<string, Stream?> resourceLoader,
        Action<Stream> player, Action<Exception> logError)
    {
        _soundEnabled = soundEnabled;
        _resourceLoader = resourceLoader ?? throw new ArgumentNullException(nameof(resourceLoader));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _logError = logError;
    }

    public SoundFeedback(bool soundEnabled, Action<Exception>? logError = null)
        : this(
            soundEnabled,
            resourceName => Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName),
            PlayWithSoundPlayer, logError ?? new ErrorLogger().Log)
    {
    }

    public void PlaySuccess()
    {
        Enqueue(SuccessResourceName);
    }

    public void PlayError()
    {
        Enqueue(ErrorResourceName);
    }

    public void SetEnabled(bool enabled) => _soundEnabled = enabled;
    public Task DrainAsync() { lock (_gate) return _pending; }
    public async ValueTask DisposeAsync() => await DrainAsync().ConfigureAwait(false);

    private void Enqueue(string resourceName)
    {
        if (!_soundEnabled) return;
        var metrics = SessionMetrics.Current;
        lock (_gate)
        {
            var previous = _pending;
            _pending = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    metrics?.Mark(TimingStage.SoundStart);
                    PlayResource(resourceName);
                    metrics?.Mark(TimingStage.SoundEnd);
                }
                catch (Exception exception)
                {
                    try { _logError(exception); }
                    catch { System.Diagnostics.Trace.TraceError("Bisik sound error logging failed."); }
                }
                finally { metrics?.Publish(metrics.LastOutcome); }
            });
        }
    }

    private void PlayResource(string resourceName)
    {
        if (!_soundEnabled)
        {
            return;
        }

        using var stream = _resourceLoader(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded sound resource '{resourceName}' was not found.");
        }

        _player(stream);
    }

    private static void PlayWithSoundPlayer(Stream stream)
    {
        stream.Position = 0;
        using var player = new SoundPlayer(stream);
        player.PlaySync();
    }
}
