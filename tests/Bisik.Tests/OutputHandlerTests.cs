using Bisik.Core;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public sealed class OutputHandlerTests
{
    private const string Dictation = "Selamat pagi, Indonesia! — café 😀\r\nSecond paragraph.\r\n";

    [Theory]
    [InlineData(OutputMode.AutoType)]
    [InlineData(OutputMode.RealtimeAutoType)]
    public async Task Automatic_output_should_paste_unicode_and_paragraphs_once_then_restore_clipboard(OutputMode mode)
    {
        var platform = new FakeTextOutputPlatform();
        var original = platform.ClipboardText;
        using var output = CreateOutput(platform);
        var popups = new List<string>();
        var handler = new OutputHandler(output, popups.Add);
        using var target = output.CaptureTarget();

        var outcome = await handler.DispatchAsync(Success(), mode, false, target, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Inserted);
        platform.Pasted.Should().ContainSingle().Which.Should().Be(Dictation);
        platform.ClipboardText.Should().Be(original);
        platform.RestoreCount.Should().Be(1);
        popups.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_always_copy_should_keep_the_transcription_on_the_clipboard()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();

        await output.InsertAsync(Dictation, target, true, CancellationToken.None);

        platform.ClipboardText.Should().Be(Dictation);
        platform.RestoreCount.Should().Be(0);
    }

    [Fact]
    public async Task Focus_change_should_copy_for_manual_paste_without_typing()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();
        platform.TargetIsCurrent = false;

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        outcome.Message.Should().Contain("target").And.Contain("manually");
        platform.ClipboardText.Should().Be(Dictation);
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Focus_fallback_must_not_overwrite_a_newer_clipboard_change()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();
        platform.TargetIsCurrent = false;
        platform.CopyExternally("new user copy");
        var popups = new List<string>();
        var handler = new OutputHandler(output, popups.Add);

        var outcome = await handler.DispatchAsync(Success(), OutputMode.AutoType, false, target, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.NeedsReview);
        platform.ClipboardText.Should().Be("new user copy");
        platform.Pasted.Should().BeEmpty();
        popups.Should().ContainSingle().Which.Should().Be(Dictation);
    }

    [Fact]
    public async Task Focus_change_while_modifiers_are_releasing_should_prevent_paste()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform, () => platform.TargetIsCurrent = false);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Focus_change_during_clipboard_write_should_prevent_paste()
    {
        var platform = new FakeTextOutputPlatform();
        platform.AfterWrite = () => platform.TargetIsCurrent = false;
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Clipboard_change_between_snapshot_and_write_should_prevent_paste()
    {
        var platform = new FakeTextOutputPlatform();
        platform.AfterSnapshot = () => platform.CopyExternally("new copy");
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.NeedsReview);
        platform.ClipboardText.Should().Be("new copy");
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Clipboard_change_after_write_should_prevent_pasting_unrelated_content()
    {
        var platform = new FakeTextOutputPlatform();
        platform.AfterWrite = () => platform.CopyExternally("unrelated clipboard text");
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.NeedsReview);
        platform.ClipboardText.Should().Be("unrelated clipboard text");
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Clipboard_restoration_must_not_erase_a_copy_made_after_paste()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform, () =>
        {
            if (platform.Pasted.Count > 0) platform.CopyExternally("copied meanwhile");
        });
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Inserted);
        platform.ClipboardText.Should().Be("copied meanwhile");
        platform.RestoreCount.Should().Be(0);
    }

    [Fact]
    public async Task Held_modifier_should_time_out_without_synthesizing_releases_or_paste()
    {
        var platform = new FakeTextOutputPlatform { AreModifiersPressed = true };
        var waits = 0;
        using var output = CreateOutput(platform, () => waits++);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        platform.Pasted.Should().BeEmpty();
        waits.Should().Be(61);
    }

    [Fact]
    public async Task Modifier_release_should_allow_paste_after_async_wait()
    {
        var platform = new FakeTextOutputPlatform { AreModifiersPressed = true };
        var waits = 0;
        using var output = CreateOutput(platform, () =>
        {
            if (++waits == 3) platform.AreModifiersPressed = false;
        });
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Inserted);
        platform.Pasted.Should().ContainSingle();
    }

    [Fact]
    public async Task Inaccessible_or_unsupported_clipboard_should_offer_review_without_destroying_it()
    {
        var platform = new FakeTextOutputPlatform { SnapshotAvailable = false };
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();
        var original = platform.ClipboardText;

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.NeedsReview);
        platform.ClipboardText.Should().Be(original);
        platform.WriteCount.Should().Be(0);
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_native_paste_should_keep_text_for_manual_paste_and_log_failure()
    {
        var platform = new FakeTextOutputPlatform { PasteException = new InvalidOperationException("SendInput failed") };
        var errors = new List<Exception>();
        using var output = new WindowsTextOutput(platform, (_, _) => Task.CompletedTask, errors.Add);
        using var target = output.CaptureTarget();

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        platform.ClipboardText.Should().Be(Dictation);
        platform.RestoreCount.Should().Be(0);
        errors.Should().ContainSingle().Which.Should().BeSameAs(platform.PasteException);
    }

    [Fact]
    public async Task Cancellation_after_clipboard_write_should_restore_without_pasting()
    {
        var platform = new FakeTextOutputPlatform();
        using var cancellation = new CancellationTokenSource();
        platform.AfterWrite = cancellation.Cancel;
        using var output = CreateOutput(platform);
        using var target = output.CaptureTarget();
        var original = platform.ClipboardText;

        var action = () => output.InsertAsync(Dictation, target, false, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        platform.Pasted.Should().BeEmpty();
        platform.ClipboardText.Should().Be(original);
    }

    [Theory]
    [InlineData(false, "provider failed")]
    [InlineData(true, " ")]
    public async Task Failed_or_empty_transcription_should_not_touch_output(bool succeeded, string text)
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        var popups = new List<string>();
        var handler = new OutputHandler(output, popups.Add);

        var outcome = await handler.DispatchAsync(new(succeeded, text, text), OutputMode.AutoType,
            true, null, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Skipped);
        platform.WriteCount.Should().Be(0);
        platform.Pasted.Should().BeEmpty();
        popups.Should().BeEmpty();
    }

    [Fact]
    public async Task Clipboard_mode_should_copy_without_pasting()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        var handler = new OutputHandler(output, _ => throw new InvalidOperationException());

        var outcome = await handler.DispatchAsync(Success(), OutputMode.Clipboard, false, null, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        platform.ClipboardText.Should().Be(Dictation);
        platform.Pasted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Popup_mode_should_display_once_and_honor_always_copy(bool alwaysCopy)
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        var popups = new List<string>();
        var handler = new OutputHandler(output, popups.Add);
        var original = platform.ClipboardText;

        await handler.DispatchAsync(Success(), OutputMode.Popup, alwaysCopy, null, CancellationToken.None);

        popups.Should().ContainSingle().Which.Should().Be(Dictation);
        platform.ClipboardText.Should().Be(alwaysCopy ? Dictation : original);
        platform.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task Protected_terminal_or_unverified_target_should_never_receive_input()
    {
        var platform = new FakeTextOutputPlatform();
        using var output = CreateOutput(platform);
        using var target = new DictationTarget(platform.ClipboardSequence, () => true,
            unsafeReason: "Automatic insertion into command terminals is disabled.");

        var outcome = await output.InsertAsync(Dictation, target, false, CancellationToken.None);

        outcome.Disposition.Should().Be(OutputDisposition.Copied);
        outcome.Message.Should().Contain("terminals");
        platform.Pasted.Should().BeEmpty();
    }

    private static TranscriptionResult Success() => new(true, Dictation, Dictation);
    private static WindowsTextOutput CreateOutput(FakeTextOutputPlatform platform, Action? onDelay = null) =>
        new(platform, (_, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            onDelay?.Invoke();
            return Task.CompletedTask;
        });
}

internal sealed class FakeTextOutputPlatform : ITextOutputPlatform
{
    public bool TargetIsCurrent { get; set; } = true;
    public bool AreModifiersPressed { get; set; }
    public uint ClipboardSequence { get; private set; } = 1;
    public string ClipboardText { get; private set; } = "original clipboard";
    public bool SnapshotAvailable { get; set; } = true;
    public int RestoreCount { get; private set; }
    public int WriteCount { get; private set; }
    public List<string> Pasted { get; } = [];
    public Exception? PasteException { get; set; }
    public Action? AfterSnapshot { get; set; }
    public Action? AfterWrite { get; set; }
    public DictationTarget CaptureTarget() => new(ClipboardSequence, () => TargetIsCurrent);

    public ClipboardSnapshot? CaptureClipboard()
    {
        if (!SnapshotAvailable) return null;
        var snapshot = new ClipboardSnapshot(ClipboardSequence,
            [new ClipboardFormat(13, System.Text.Encoding.Unicode.GetBytes(ClipboardText))]);
        AfterSnapshot?.Invoke();
        return snapshot;
    }

    public uint? TrySetClipboardText(string text, uint expectedSequence)
    {
        if (expectedSequence != ClipboardSequence) return null;
        WriteCount++;
        CopyExternally(text);
        var ownedSequence = ClipboardSequence;
        AfterWrite?.Invoke();
        return ownedSequence;
    }

    public bool TryRestoreClipboard(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        if (expectedSequence != ClipboardSequence) return false;
        RestoreCount++;
        CopyExternally(System.Text.Encoding.Unicode.GetString(snapshot.Formats[0].Data));
        return true;
    }

    public void CopyExternally(string text)
    {
        ClipboardText = text;
        ClipboardSequence++;
    }

    public void Paste()
    {
        if (PasteException is not null) throw PasteException;
        Pasted.Add(ClipboardText);
    }

    public void Dispose() { }
}
