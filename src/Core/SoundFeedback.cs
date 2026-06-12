using System.Media;
using System.Reflection;

namespace Bisik.Core;

public sealed class SoundFeedback
{
    private const string SuccessResourceName = "Bisik.Resources.success.wav";
    private const string ErrorResourceName = "Bisik.Resources.error.wav";

    private readonly bool _soundEnabled;
    private readonly Func<string, Stream?> _resourceLoader;
    private readonly Action<Stream> _player;

    internal SoundFeedback(
        bool soundEnabled,
        Func<string, Stream?> resourceLoader,
        Action<Stream> player)
    {
        _soundEnabled = soundEnabled;
        _resourceLoader = resourceLoader ?? throw new ArgumentNullException(nameof(resourceLoader));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public SoundFeedback(bool soundEnabled)
        : this(
            soundEnabled,
            resourceName => Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName),
            PlayWithSoundPlayer)
    {
    }

    public void PlaySuccess()
    {
        PlayResource(SuccessResourceName);
    }

    public void PlayError()
    {
        PlayResource(ErrorResourceName);
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
