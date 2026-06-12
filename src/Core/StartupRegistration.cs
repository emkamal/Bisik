using Microsoft.Win32;

namespace Bisik.Core;

public sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Bisik";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppName) is string existingValue &&
            !string.IsNullOrWhiteSpace(existingValue);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true) ??
            throw new InvalidOperationException("Failed to open the current-user Run registry key.");

        if (enabled)
        {
            key.SetValue(AppName, QuoteIfNeeded(executablePath), RegistryValueKind.String);
            return;
        }

        if (key.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static string QuoteIfNeeded(string executablePath)
    {
        var trimmedPath = executablePath.Trim();
        if (trimmedPath.StartsWith('"') && trimmedPath.EndsWith('"'))
        {
            return trimmedPath;
        }

        return $"\"{trimmedPath}\"";
    }
}
