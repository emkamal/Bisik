using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class FocusDiagnostics
{
    // Deliberately omit names, values, handles, process IDs and document contents.
    public static object Capture()
    {
        try
        {
            var foreground = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(foreground, out var process);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            var hasThreadInfo = GetGUIThreadInfo(thread, ref info);
            var element = AutomationElement.FocusedElement;
            if (element is null) return new { ElementPresent = false };
            var current = element.Current;
            var hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value);
            return new
            {
                ElementPresent = true,
                current.HasKeyboardFocus,
                SameProcessAsForeground = current.ProcessId == process,
                NativeFocusPresent = hasThreadInfo && info.Focus != IntPtr.Zero,
                NativeFocusRootMatchesForeground = hasThreadInfo && GetAncestor(info.Focus, 2) == foreground,
                ControlTypeId = current.ControlType.Id,
                HasValuePattern = hasValue,
                ValueIsReadOnly = hasValue ? (bool?)((ValuePattern)value).Current.IsReadOnly : null,
                ControlAncestorNativeMatchDepth = FindNativeAncestor(element, foreground, TreeWalker.ControlViewWalker),
                RawAncestorNativeMatchDepth = FindNativeAncestor(element, foreground, TreeWalker.RawViewWalker),
            };
        }
        catch (Exception exception) { return new { Error = exception.GetType().Name }; }
    }

    private static int? FindNativeAncestor(AutomationElement element, IntPtr foreground, TreeWalker walker)
    {
        AutomationElement? ancestor = element;
        for (var depth = 0; ancestor is not null && depth < 40; depth++, ancestor = walker.GetParent(ancestor))
        {
            var handle = new IntPtr(ancestor.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero && GetAncestor(handle, 2) == foreground) return depth;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
