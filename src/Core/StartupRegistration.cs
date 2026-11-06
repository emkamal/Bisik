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
            // A workspace build runs from an immutable copy. Startup must follow the same
            // stable launcher as Start, rather than pinning that one copy forever.
            var registration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "launcher-path.txt");
            executablePath = ResolveExecutablePath(executablePath, registration);
            key.SetValue(AppName, QuoteIfNeeded(executablePath), RegistryValueKind.String);
            return;
        }

        if (key.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    internal static string ResolveExecutablePath(string executablePath, string registration)
    {
        if (!File.Exists(registration)) return executablePath;
        var launcher = File.ReadAllText(registration).Trim();
        return Path.IsPathFullyQualified(launcher) && File.Exists(launcher) ? launcher : executablePath;
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
