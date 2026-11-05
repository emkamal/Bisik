using Bisik.Models;

namespace Bisik.Core;

public sealed class OutputHandler(WindowsTextOutput textOutput, Action<string> popupSink)
{
    public async Task<OutputDispatchResult> DispatchAsync(TranscriptionResult result, OutputMode outputMode,
        bool alwaysCopyToClipboard, DictationTarget? target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputText))
        {
            return new(OutputDisposition.Skipped);
        }

        OutputDispatchResult outcome;
        switch (outputMode)
        {
            case OutputMode.AutoType:
            case OutputMode.RealtimeAutoType:
                outcome = await textOutput.InsertAsync(result.OutputText, target, alwaysCopyToClipboard, cancellationToken);
                break;
            case OutputMode.Clipboard:
                outcome = textOutput.Copy(result.OutputText);
                break;
            case OutputMode.Popup:
                outcome = alwaysCopyToClipboard ? textOutput.Copy(result.OutputText) : new(OutputDisposition.NeedsReview);
                popupSink(result.OutputText);
                return outcome;
            default:
                throw new ArgumentOutOfRangeException(nameof(outputMode));
        }

        if (outcome.Disposition == OutputDisposition.NeedsReview)
            popupSink(result.OutputText);
        return outcome;
    }
}
