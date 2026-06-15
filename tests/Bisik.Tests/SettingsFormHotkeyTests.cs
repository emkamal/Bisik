using System.Reflection;
using System.Windows.Forms;
using Bisik.Core;
using Bisik.UI;
using FluentAssertions;
using Xunit.Sdk;

namespace Bisik.Tests;

public sealed class SettingsFormHotkeyTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), "Bisik.SettingsFormHotkeyTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Hotkey_text_box_should_capture_pressed_hotkey_combo()
    {
        RunOnSta(() =>
        {
            using var form = new SettingsForm(audioRecorder: null, transcriptionService: null, settingsManager: new SettingsManager(_settingsDirectory));
            var hotkeyTextBox = GetHotkeyTextBox(form);
            hotkeyTextBox.Text = string.Empty;

            RaiseKeyEvents(hotkeyTextBox, Keys.Control | Keys.Shift | Keys.Space);

            hotkeyTextBox.Text.Should().Be("Ctrl+Shift+Space");
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private static TextBox GetHotkeyTextBox(SettingsForm form)
    {
        var field = typeof(SettingsForm).GetField("_hotkeyTextBox", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(form) is not TextBox textBox)
        {
            throw new XunitException("Expected SettingsForm to expose a private _hotkeyTextBox field.");
        }

        return textBox;
    }

    private static void RaiseKeyEvents(TextBox textBox, Keys keyData)
    {
        var onPreviewKeyDown = typeof(Control).GetMethod("OnPreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
        var onKeyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
        if (onPreviewKeyDown is null || onKeyDown is null)
        {
            throw new XunitException("Expected Control.OnPreviewKeyDown and Control.OnKeyDown to exist.");
        }

        onPreviewKeyDown.Invoke(textBox, [new PreviewKeyDownEventArgs(keyData)]);
        onKeyDown.Invoke(textBox, [new KeyEventArgs(keyData)]);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new XunitException($"STA test failed: {failure}");
        }
    }
}
