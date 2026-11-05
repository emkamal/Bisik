namespace Bisik.Core;

public enum OutputDisposition
{
    Skipped,
    Inserted,
    Copied,
    NeedsReview,
}

public sealed record OutputDispatchResult(OutputDisposition Disposition, string? Message = null);

public sealed class DictationTarget : IDisposable
{
    private readonly Func<bool> _isCurrent;
    private readonly Action _dispose;
    private bool _disposed;

    internal DictationTarget(uint clipboardSequence, Func<bool> isCurrent, Action? dispose = null, string? unsafeReason = null)
    {
        ClipboardSequence = clipboardSequence;
        _isCurrent = isCurrent;
        _dispose = dispose ?? (() => { });
        UnsafeReason = unsafeReason;
    }

    internal uint ClipboardSequence { get; }
    public string? UnsafeReason { get; }
    public bool IsCurrent => !_disposed && UnsafeReason is null && _isCurrent();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispose();
    }
}

/// <summary>Captures focus and pastes only into the unchanged, verified editable target.</summary>
public sealed class WindowsTextOutput : IDisposable
{
    private readonly ITextOutputPlatform _platform;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Exception> _logError;

    public WindowsTextOutput(Action<Exception>? logError = null)
        : this(new WindowsTextOutputPlatform(logError), Task.Delay, logError)
    {
    }

    internal WindowsTextOutput(ITextOutputPlatform platform,
        Func<TimeSpan, CancellationToken, Task> delay, Action<Exception>? logError = null)
    {
        _platform = platform;
        _delay = delay;
        _logError = logError ?? (_ => { });
    }

    public DictationTarget CaptureTarget() => _platform.CaptureTarget();

    public async Task<OutputDispatchResult> InsertAsync(string text, DictationTarget? target,
        bool alwaysCopyToClipboard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return new(OutputDisposition.Skipped);

        if (target is null || !target.IsCurrent)
            return CopyFallback(text, target, target?.UnsafeReason ?? "The dictation target or cursor changed.");

        // Never synthesize modifier releases: the user may still be holding the dictation hotkey.
        await _delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        for (var attempt = 0; _platform.AreModifiersPressed && attempt < 60; attempt++)
            await _delay(TimeSpan.FromMilliseconds(25), cancellationToken);

        if (_platform.AreModifiersPressed || !target.IsCurrent)
            return CopyFallback(text, target, "The target changed or a modifier key is still held.");

        var snapshot = alwaysCopyToClipboard ? null : _platform.CaptureClipboard();
        if (!alwaysCopyToClipboard && snapshot is null)
            return new(OutputDisposition.NeedsReview, "The clipboard could not be preserved. Copy the text from the review window.");

        var expectedSequence = snapshot?.Sequence ?? _platform.ClipboardSequence;
        var ownedSequence = _platform.TrySetClipboardText(text, expectedSequence);
        if (ownedSequence is null)
            return new(OutputDisposition.NeedsReview, "The clipboard changed or is busy. Copy the text from the review window.");

        if (_platform.ClipboardSequence != ownedSequence)
            return new(OutputDisposition.NeedsReview, "The clipboard changed before insertion. Copy the text from the review window.");

        // Check again after clipboard access. There is no activation or refocusing of the target.
        if (!target.IsCurrent || _platform.AreModifiersPressed)
            return new(OutputDisposition.Copied, "The target changed. Transcription copied; select your target and paste manually.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _platform.Paste();
        }
        catch (OperationCanceledException)
        {
            if (snapshot is not null) _platform.TryRestoreClipboard(snapshot, ownedSequence.Value);
            throw;
        }
        catch (Exception exception)
        {
            _logError(exception);
            return new(OutputDisposition.Copied, "Automatic paste was unavailable. Transcription copied; paste manually.");
        }

        if (snapshot is not null)
        {
            // Windows has no general paste acknowledgement. Allow the target to consume the data,
            // then restore only while Bisik still owns this exact clipboard revision.
            await _delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);
            if (!_platform.TryRestoreClipboard(snapshot, ownedSequence.Value) && _platform.ClipboardSequence == ownedSequence)
                return new(OutputDisposition.Inserted, "Paste sent. The clipboard could not be restored and still contains the transcription.");
        }

        return new(OutputDisposition.Inserted);
    }

    public OutputDispatchResult Copy(string text)
    {
        var sequence = _platform.ClipboardSequence;
        return _platform.TrySetClipboardText(text, sequence) is not null
            ? new(OutputDisposition.Copied, "Transcription copied. Select your target and paste manually.")
            : new(OutputDisposition.NeedsReview, "The clipboard changed or is busy. Copy the text from the review window.");
    }

    private OutputDispatchResult CopyFallback(string text, DictationTarget? target, string reason)
    {
        // A focus change is not permission to replace something the user copied while dictating.
        if (target is null || _platform.TrySetClipboardText(text, target.ClipboardSequence) is null)
            return new(OutputDisposition.NeedsReview, reason + " Copy the text from the review window.");

        return new(OutputDisposition.Copied, reason + " Transcription copied; select your target and paste manually.");
    }

    public void Dispose() => _platform.Dispose();
}

internal interface ITextOutputPlatform : IDisposable
{
    DictationTarget CaptureTarget();
    bool AreModifiersPressed { get; }
    uint ClipboardSequence { get; }
    ClipboardSnapshot? CaptureClipboard();
    uint? TrySetClipboardText(string text, uint expectedSequence);
    bool TryRestoreClipboard(ClipboardSnapshot snapshot, uint expectedSequence);
    void Paste();
}

internal sealed record ClipboardFormat(uint Id, byte[] Data);
internal sealed record ClipboardSnapshot(uint Sequence, IReadOnlyList<ClipboardFormat> Formats);
