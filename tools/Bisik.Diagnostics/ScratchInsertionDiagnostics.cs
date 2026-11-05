using System.Text.Json;
using Bisik.Core;

internal static class ScratchInsertionDiagnostics
{
    private const string Probe = "Bisik scratch — Selamat pagi! café 😀.\r\nSecond paragraph.";
    public static int Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output") return 2;
        Application.EnableVisualStyles();
        using var form = new Form { Text = "Bisik disposable insertion verification", Width = 620, Height = 300 };
        var first = new TextBox { Multiline = true, Dock = DockStyle.Top, Height = 100 };
        var second = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(second);
        form.Controls.Add(first);
        var enterCount = 0;
        first.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) enterCount++; };
        second.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) enterCount++; };
        var exit = 1;
        form.Shown += async (_, _) =>
        {
            using var platform = new ScratchPlatform();
            var original = platform.CaptureClipboard();
            for (var i = 0; original is null && i < 20; i++) { await Task.Delay(50); original = platform.CaptureClipboard(); }
            if (original is null)
            {
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { Verified = false, Blocker = platform.LastCaptureFailure, ExistingClipboardPreserved = true }));
                form.Close();
                return;
            }
            using var output = new WindowsTextOutput(platform, Task.Delay);
            try
            {
                form.Activate();
                first.Focus();
                await Task.Delay(150);
                using var target = output.CaptureTarget();
                var initial = await output.InsertAsync(Probe, target, false, CancellationToken.None);
                await Task.Delay(150);
                var accepted = first.Text == Probe;
                await output.DrainAsync();
                first.Select(first.TextLength, 0);
                using var stale = output.CaptureTarget();
                var store = new LatestResultStore();
                var pending = store.Publish(new(true, Probe, Probe));
                // A competing synthetic clipboard copy forces review without replacing any newer copy.
                platform.WriteSyntheticCopy("Synthetic newer clipboard");
                second.Focus();
                await Task.Delay(150);
                var fallback = await output.InsertAsync(Probe, stale, false, CancellationToken.None);
                var unchanged = second.Text.Length == 0 && first.Text == Probe;
                var claim = store.Claim(pending.Id)!;
                using var current = output.CaptureTarget();
                var recovery = await output.InsertAsync(claim.Result.OutputText, current, false, CancellationToken.None);
                store.CompleteClaim(claim.Id, recovery.Disposition == OutputDisposition.Inserted);
                await Task.Delay(150);
                var recovered = second.Text == Probe;
                await output.DrainAsync();
                var newerPreserved = Clipboard.ContainsText() && Clipboard.GetText() == "Synthetic newer clipboard";
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
                {
                    DisposableFieldsOnly = true, InitialDispatch = initial.Disposition.ToString(), Accepted = accepted,
                    ChangedTargetDisposition = fallback.Disposition.ToString(), ChangedFieldUntouched = unchanged,
                    RecoveryDispatch = recovery.Disposition.ToString(), RecoveryAccepted = recovered,
                    PendingCleared = store.Latest is null, NewerClipboardPreserved = newerPreserved, EnterKeyEvents = enterCount,
                }, new JsonSerializerOptions { WriteIndented = true }));
                exit = accepted && recovered && unchanged && newerPreserved && enterCount == 0 ? 0 : 1;
            }
            catch (Exception exception) { await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { ErrorType = exception.GetType().Name })); }
            finally
            {
                await output.DrainAsync();
                platform.RestoreOriginal(original);
                form.Close();
            }
        };
        Application.Run(form);
        return exit;
    }

    private sealed class ScratchPlatform : ITextOutputPlatform
    {
        private readonly WindowsTextOutputPlatform _focus = new();
        private readonly WindowsClipboard _clipboard = new();
        private uint? _owned;
        private bool _disposed;
        public DictationTarget CaptureTarget() => _focus.CaptureTarget();
        public bool AreModifiersPressed => _focus.AreModifiersPressed;
        public uint ClipboardSequence => WindowsClipboard.Sequence;
        public ClipboardSnapshot? CaptureClipboard() => _clipboard.Capture();
        public string LastCaptureFailure => _clipboard.LastCaptureFailure;
        public uint? TrySetClipboardText(string text, uint sequence) => _owned = _clipboard.TrySetText(text, sequence);
        public bool TryRestoreClipboard(ClipboardSnapshot snapshot, uint sequence) => (_owned = _clipboard.TryRestoreWithReceipt(snapshot, sequence)) is not null;
        public void WriteSyntheticCopy(string text) => TrySetClipboardText(text, ClipboardSequence);
        public void RestoreOriginal(ClipboardSnapshot original) { if (_owned is { } owned) _clipboard.TryRestore(original, owned); }
        public void Paste() => _focus.Paste();
        public void Dispose() { if (_disposed) return; _disposed = true; _focus.Dispose(); _clipboard.Dispose(); }
    }
}
