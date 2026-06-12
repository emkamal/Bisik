using System.Runtime.InteropServices;
using System.Windows.Forms;
using Bisik.Models;

namespace Bisik.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, Keys Key);

public enum HotkeyAction
{
    None,
    StartRecording,
    StopRecordingAndTranscribe,
    StartVoiceActivityDetection,
    StopVoiceActivityDetection,
}

public interface IHotkeyRegistrar
{
    bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    bool UnregisterHotKey(IntPtr windowHandle, int id);
}

public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    // Keep the P/Invoke isolated so the manager can be tested with a fake registrar.
    public bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey)
    {
        return NativeMethods.RegisterHotKey(windowHandle, id, modifiers, virtualKey);
    }

    public bool UnregisterHotKey(IntPtr windowHandle, int id)
    {
        return NativeMethods.UnregisterHotKey(windowHandle, id);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

public sealed class HotkeyManager(IHotkeyRegistrar registrar, IntPtr windowHandle)
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Ctrl,
        ["Control"] = HotkeyModifiers.Ctrl,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
    };

    private static readonly HotkeyModifiers[] ModifierOrder =
    [
        HotkeyModifiers.Ctrl,
        HotkeyModifiers.Shift,
        HotkeyModifiers.Alt,
        HotkeyModifiers.Win,
    ];

    private readonly IHotkeyRegistrar _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    private readonly IntPtr _windowHandle = windowHandle;
    private readonly HashSet<int> _registeredIds = [];
    private bool _pushToTalkActive;
    private bool _toggleRecordingActive;
    private bool _vadListeningActive;

    public bool Register(int id, HotkeyGesture hotkey)
    {
        if (!IsSupportedHotkey(hotkey))
        {
            throw new InvalidOperationException("Hotkey must include at least one modifier and a non-empty key.");
        }

        var registered = _registrar.RegisterHotKey(
            _windowHandle,
            id,
            (uint)hotkey.Modifiers,
            (uint)hotkey.Key);

        if (registered)
        {
            _registeredIds.Add(id);
        }

        return registered;
    }

    public bool Unregister(int id)
    {
        var unregistered = _registrar.UnregisterHotKey(_windowHandle, id);
        if (unregistered)
        {
            _registeredIds.Remove(id);
        }

        return unregistered;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registeredIds.ToArray())
        {
            _registrar.UnregisterHotKey(_windowHandle, id);
        }

        _registeredIds.Clear();
    }

    public HotkeyAction HandleKeyDown(TriggerMode triggerMode)
    {
        if (triggerMode == TriggerMode.Vad)
        {
            _vadListeningActive = !_vadListeningActive;
            return _vadListeningActive
                ? HotkeyAction.StartVoiceActivityDetection
                : HotkeyAction.StopVoiceActivityDetection;
        }

        if (triggerMode == TriggerMode.Toggle)
        {
            _toggleRecordingActive = !_toggleRecordingActive;
            return _toggleRecordingActive
                ? HotkeyAction.StartRecording
                : HotkeyAction.StopRecordingAndTranscribe;
        }

        if (triggerMode != TriggerMode.PushToTalk)
        {
            return HotkeyAction.None;
        }

        if (_pushToTalkActive)
        {
            return HotkeyAction.None;
        }

        _pushToTalkActive = true;
        return HotkeyAction.StartRecording;
    }

    public HotkeyAction HandleKeyUp(TriggerMode triggerMode)
    {
        if (triggerMode != TriggerMode.PushToTalk || !_pushToTalkActive)
        {
            return HotkeyAction.None;
        }

        _pushToTalkActive = false;
        return HotkeyAction.StopRecordingAndTranscribe;
    }

    public HotkeyAction HandleVoiceActivityAction(VoiceActivityAction action)
    {
        if (!_vadListeningActive)
        {
            return HotkeyAction.None;
        }

        return action switch
        {
            VoiceActivityAction.StartRecording => HotkeyAction.StartRecording,
            VoiceActivityAction.StopRecording => HotkeyAction.StopRecordingAndTranscribe,
            _ => HotkeyAction.None,
        };
    }

    public static HotkeyGesture ParseHotkey(string hotkey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hotkey);

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;

        var tokens = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            var rawToken = tokens[index];
            var isLastToken = index == tokens.Length - 1;

            if (IsWindowsKeyToken(rawToken) && key == Keys.None && modifiers != HotkeyModifiers.None && isLastToken)
            {
                key = Keys.LWin;
                continue;
            }

            if (ModifierTokens.TryGetValue(rawToken, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key != Keys.None)
            {
                throw new FormatException("A hotkey can only contain one primary key.");
            }

            key = ParseKeyToken(rawToken);
        }

        if (key == Keys.None)
        {
            throw new FormatException("Hotkey must contain a primary key.");
        }

        return new HotkeyGesture(modifiers, key);
    }

    public static string FormatHotkey(HotkeyGesture hotkey)
    {
        var parts = new List<string>();

        foreach (var modifier in ModifierOrder)
        {
            if (!hotkey.Modifiers.HasFlag(modifier))
            {
                continue;
            }

            parts.Add(modifier switch
            {
                HotkeyModifiers.Ctrl => "Ctrl",
                HotkeyModifiers.Shift => "Shift",
                HotkeyModifiers.Alt => "Alt",
                HotkeyModifiers.Win => "Win",
                _ => modifier.ToString(),
            });
        }

        parts.Add(FormatKeyToken(hotkey.Key));
        return string.Join("+", parts);
    }

    public static bool IsSupportedHotkey(HotkeyGesture hotkey)
    {
        return hotkey.Modifiers != HotkeyModifiers.None && hotkey.Key != Keys.None;
    }

    private static Keys ParseKeyToken(string token)
    {
        if (string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase))
        {
            return Keys.Space;
        }

        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            return Enum.Parse<Keys>(token.ToUpperInvariant(), ignoreCase: false);
        }

        if (Enum.TryParse<Keys>(token, ignoreCase: true, out var parsedKey) && parsedKey != Keys.None)
        {
            return parsedKey;
        }

        throw new FormatException($"Unsupported hotkey token '{token}'.");
    }

    private static string FormatKeyToken(Keys key)
    {
        return key switch
        {
            Keys.Space => "Space",
            Keys.LWin or Keys.RWin => "Win",
            _ => key.ToString(),
        };
    }

    private static bool IsWindowsKeyToken(string token)
    {
        return string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Windows", StringComparison.OrdinalIgnoreCase);
    }
}
