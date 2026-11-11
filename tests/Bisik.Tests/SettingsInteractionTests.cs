using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Bisik.Core;
using Bisik.UI;

namespace Bisik.Tests;

public class SettingsInteractionTests
{
    [Fact]
    public Task EnterEditsMultilineFieldsWithoutApplyingOrClosing() => WithSettings(form =>
    {
        var saves = 0; form.SettingsApplied += _ => saves++;
        var inputs = All(form).OfType<TextBox>().Where(t => t.Multiline && !t.ReadOnly).ToArray();
        Assert.NotEmpty(inputs); Assert.All(inputs, t => Assert.True(t.AcceptsReturn));
        foreach (var input in inputs.Where(t => t.Parent?.Parent is TabPage))
        {
            form.Tabs.SelectedTab = (TabPage)input.Parent!.Parent!;
            input.Text = "first"; input.Focus(); input.SelectionStart = input.TextLength;
            var message = Message.Create(input.Handle, 0x100, (IntPtr)Keys.Enter, IntPtr.Zero);
            Assert.False(input.PreProcessMessage(ref message));
            SendMessage(input.Handle, 0x102, (IntPtr)'\r', IntPtr.Zero);
            Assert.Equal("first\r\n", input.Text);
            Assert.True(form.Visible); Assert.Equal(0, saves);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task HelpTogglesAndDismissesWithItsWindow() => WithSettings(form =>
    {
        var help = (FieldHelp)typeof(SettingsForm).GetField("_fieldHelp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var button = All(form).OfType<Button>().First(b => b.AccessibleName?.EndsWith(" help") == true);
        button.PerformClick(); Assert.True(help.IsVisible);
        button.PerformClick(); Assert.False(help.IsVisible);
        button.PerformClick(); Assert.True(help.IsVisible);
        typeof(Form).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [EventArgs.Empty]);
        Assert.False(help.IsVisible);
        typeof(Form).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [EventArgs.Empty]);
        button.PerformClick(); Assert.True(help.IsVisible);
        form.Hide(); Assert.False(help.IsVisible);
        form.Show(); button.PerformClick(); Assert.True(help.IsVisible);
        form.Close(); Assert.False(help.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RegisteredShortcutCanBeCapturedWithoutStartingRecording() => WithSettings(form =>
    {
        var input = (TextBox)typeof(SettingsForm).GetField("_hotkeyTextBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        input.Focus(); var gesture = HotkeyManager.ParseHotkey("Ctrl+Shift+Space");
        Assert.True(form.TryCaptureHotkey(gesture)); Assert.Equal("Ctrl+Shift+Space", input.Text);
        form.Tabs.Focus(); Assert.False(form.TryCaptureHotkey(gesture));
        return Task.CompletedTask;
    });

    [Fact]
    public Task BackgroundSettingsDoesNotBlockRecording() => WithSettings(async form =>
    {
        form.Activate(); form.Tabs.Focus(); Assert.True(form.BlocksNewRecording);
        using var other = new Form(); other.Show(); other.Activate();
        await Task.Yield(); Assert.False(form.BlocksNewRecording); Assert.True(form.Visible);
        form.WindowState = FormWindowState.Minimized; Assert.False(form.BlocksNewRecording);
    });

    [Fact]
    public Task BusyDictationBlocksSettingsApplyAndConnectionCapture() => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.SettingsInteraction", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new SettingsManager(root); manager.Save(new()); var saves = 0; var captures = 0;
            var service = ExperimentRunnerTests.Service(new TranscriptionServiceTextTests.Processor((_, _, _) => throw new Exception("No request expected")));
            using var form = new SettingsForm(null, service, manager, testRecorderFactory: _ => { captures++; throw new Exception("Capture must not start"); }, canRunOperation: () => false);
            form.Show(); form.SettingsApplied += _ => saves++;
            await form.BeginConnectionTestAsync();
            All(form).OfType<Button>().Single(b => b.Text == "Apply").PerformClick();
            Assert.Equal(0, saves); Assert.Equal(0, captures); Assert.True(form.Visible);
            foreach (Form popup in form.OwnedForms) popup.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private static Task WithSettings(Func<SettingsForm, Task> action) => StaTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Bisik.SettingsInteraction", Guid.NewGuid().ToString("N"));
        try
        {
            using var form = new SettingsForm(null, null, new SettingsManager(root)); form.Show();
            await action(form);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
