using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using WindowsInput;
using WindowsInput.Native;

namespace Bisik.Core;

internal sealed class WindowsTextOutputPlatform(Action<Exception>? logError = null) : ITextOutputPlatform
{
    private readonly WindowsClipboard _clipboard = new();
    private readonly InputSimulator _input = new();
    private readonly Action<Exception> _logError = logError ?? (_ => { });

    public uint ClipboardSequence => WindowsClipboard.Sequence;
    public bool AreModifiersPressed => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }
        .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public DictationTarget CaptureTarget()
    {
        var sequence = ClipboardSequence;
        FocusGuard? guard = null;
        try
        {
            guard = new FocusGuard(_logError);
            var reason = guard.Initialize();
            if (reason is not null)
            {
                guard.Dispose();
                return new(sequence, () => false, unsafeReason: reason);
            }

            return new(sequence, guard.IsCurrent, guard.Dispose);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
            or COMException or ArgumentException or Win32Exception)
        {
            guard?.Dispose();
            _logError(exception);
            return new(sequence, () => false, unsafeReason: "Windows could not verify an editable target.");
        }
    }

    public ClipboardSnapshot? CaptureClipboard() => _clipboard.Capture();
    public uint? TrySetClipboardText(string text, uint expectedSequence) => _clipboard.TrySetText(text, expectedSequence);
    public bool TryRestoreClipboard(ClipboardSnapshot snapshot, uint expectedSequence) => _clipboard.TryRestore(snapshot, expectedSequence);

    // Paste preserves Unicode and paragraphs without sending Enter or interpreting transcription as keys.
    public void Paste() => _input.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
    public void Dispose() => _clipboard.Dispose();

    private sealed class FocusGuard : IDisposable
    {
        private readonly WinEventCallback _callback;
        private readonly List<IntPtr> _hooks = [];
        private IntPtr _foreground;
        private IntPtr _focus;
        private uint _processId;
        private int[]? _runtimeId;
        private TextPatternRange[]? _selection;
        private bool _changed;
        private readonly Action<Exception> _logError;

        public FocusGuard(Action<Exception> logError)
        {
            _callback = OnWinEvent;
            _logError = logError;
        }

        public string? Initialize()
        {
            _foreground = GetForegroundWindow();
            if (_foreground == IntPtr.Zero) return "No foreground text target was found.";
            var thread = GetWindowThreadProcessId(_foreground, out var processId);
            _processId = processId;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero)
                return "Windows could not identify the focused control.";
            _focus = info.Focus;

            foreach (var eventId in new uint[] { 3, 0x8005, 0x800B })
            {
                var hook = SetWinEventHook(eventId, eventId, IntPtr.Zero, _callback, 0, 0, 0);
                if (hook == IntPtr.Zero) return "Windows could not monitor the dictation target.";
                _hooks.Add(hook);
            }

            using var process = Process.GetProcessById((int)processId);
            if (new[] { "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "OpenConsole" }
                .Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                return "Automatic insertion into command terminals is disabled.";

            var element = AutomationElement.FocusedElement;
            if (element is null || !element.Current.HasKeyboardFocus || !IsOwnedFocus(element))
                return "Windows could not verify the focused text field.";
            if (element.Current.IsPassword)
                return "Automatic insertion into protected fields is disabled.";
            if (!element.Current.IsEnabled || !element.Current.IsKeyboardFocusable)
                return "The target does not accept text.";

            var writableValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && !((ValuePattern)value).Current.IsReadOnly;
            if (!writableValue && element.Current.ControlType != ControlType.Edit)
                return "The focused control could not be verified as an editable text field.";
            if (value is ValuePattern valuePattern && valuePattern.Current.IsReadOnly)
                return "The focused text field is read-only.";

            // Electron terminals can share the editor's process and top-level HWND. Inspect their
            // accessibility ancestry instead of granting every field in an editor permission to paste.
            foreach (var parent in Ancestors(element))
            {
                var current = parent.Current;
                if (ContainsTerminal(current.ClassName) || ContainsTerminal(current.AutomationId)
                    || current.Name.Equals("Terminal", StringComparison.OrdinalIgnoreCase)
                    || current.Name.StartsWith("Terminal ", StringComparison.OrdinalIgnoreCase))
                    return "Automatic insertion into command terminals is disabled.";
                if (current.NativeWindowHandle == _foreground.ToInt64()) break;
            }

            _runtimeId = element.GetRuntimeId();
            if (_runtimeId.Length == 0) return "Windows could not identify the text field reliably.";
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                _selection = ((TextPattern)text).GetSelection().Select(range => range.Clone()).ToArray();
            return IsCurrent() ? null : "The dictation target changed while it was being captured.";
        }

        public bool IsCurrent()
        {
            if (_changed || _runtimeId is null || GetForegroundWindow() != _foreground) return false;
            try
            {
                var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                if (!GetGUIThreadInfo(GetWindowThreadProcessId(_foreground, out _), ref info) || info.Focus != _focus)
                    return false;
                var element = AutomationElement.FocusedElement;
                if (element is null || !element.Current.HasKeyboardFocus || element.Current.IsPassword
                    || !element.Current.IsEnabled || !element.Current.IsKeyboardFocusable
                    || !_runtimeId.SequenceEqual(element.GetRuntimeId()) || !IsOwnedFocus(element)) return false;
                var hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value);
                if ((hasValue && ((ValuePattern)value).Current.IsReadOnly)
                    || (!hasValue && element.Current.ControlType != ControlType.Edit)) return false;
                if (_selection is null) return true;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return false;
                var current = ((TextPattern)text).GetSelection();
                return current.Length == _selection.Length && current.Zip(_selection)
                    .All(pair => pair.First.CompareEndpoints(TextPatternRangeEndpoint.Start, pair.Second, TextPatternRangeEndpoint.Start) == 0
                        && pair.First.CompareEndpoints(TextPatternRangeEndpoint.End, pair.Second, TextPatternRangeEndpoint.End) == 0);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                _changed = true;
                _logError(exception);
                return false;
            }
        }

        private static bool ContainsTerminal(string value) => value.Contains("terminal", StringComparison.OrdinalIgnoreCase)
            || value.Contains("consolewindow", StringComparison.OrdinalIgnoreCase);

        private bool IsOwnedFocus(AutomationElement element) => WindowsFocusOwnership.AllowsTarget(
            element.Current.ProcessId == _processId, _foreground, _focus,
            Ancestors(element).Select(ancestor => new IntPtr(ancestor.Current.NativeWindowHandle)),
            window => GetAncestor(window, 2));

        private static IEnumerable<AutomationElement> Ancestors(AutomationElement element)
        {
            for (var depth = 0; element is not null && depth < 40; depth++,
                element = TreeWalker.ControlViewWalker.GetParent(element))
                yield return element;
        }

        private void OnWinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            // Sticky invalidation also rejects switching away and back before transcription finishes.
            if ((eventId == 3 && window != _foreground)
                || (eventId == 0x8005 && (GetAncestor(window, 2) == _foreground || GetForegroundWindow() != _foreground))
                || (eventId == 0x800B && objectId == -8 && window == _focus))
                _changed = true;
        }

        public void Dispose()
        {
            _changed = true;
            foreach (var hook in _hooks) UnhookWinEvent(hook);
            _hooks.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}

internal static class WindowsFocusOwnership
{
    internal static bool AllowsTarget(bool sameProcess, IntPtr foreground, IntPtr nativeFocus,
        IEnumerable<IntPtr> ancestorWindows, Func<IntPtr, IntPtr> rootOf)
    {
        if (sameProcess) return true;
        // Embedded WebView controls can belong to a renderer process. Both the native focus and
        // an accessibility ancestor must be rooted in our captured foreground window.
        return foreground != IntPtr.Zero && nativeFocus != IntPtr.Zero && rootOf(nativeFocus) == foreground
            && ancestorWindows.Take(40).Any(window => window != IntPtr.Zero && rootOf(window) == foreground);
    }
}
