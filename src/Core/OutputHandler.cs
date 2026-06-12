using Bisik.Models;

namespace Bisik.Core;

public sealed class OutputHandler(
    Action<string> autoTypeSink,
    Action<string> clipboardSink,
    Action<string> popupSink)
{
    private readonly Action<string> _autoTypeSink = autoTypeSink ??
        throw new ArgumentNullException(nameof(autoTypeSink));
    private readonly Action<string> _clipboardSink = clipboardSink ??
        throw new ArgumentNullException(nameof(clipboardSink));
    private readonly Action<string> _popupSink = popupSink ??
        throw new ArgumentNullException(nameof(popupSink));

    public Task DispatchAsync(
        TranscriptionResult result,
        OutputMode outputMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputText))
        {
            return Task.CompletedTask;
        }

        switch (outputMode)
        {
            case OutputMode.AutoType:
                _autoTypeSink(result.OutputText);
                break;

            case OutputMode.Clipboard:
                _clipboardSink(result.OutputText);
                break;

            case OutputMode.Popup:
                _popupSink(result.OutputText);
                break;
        }

        return Task.CompletedTask;
    }
}
