using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Bisik.Core;

namespace Bisik.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var stateDirectory = Environment.GetEnvironmentVariable("BISIK_LAUNCHER_STATE_DIRECTORY") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik");
            using var identity = WindowsIdentity.GetCurrent();
            using var gate = new Mutex(false, "Local\\Bisik.LatestBuild." + identity.User!.Value);
            var acquired = false;
            FileStream? lease = null;
            BuildInfo? build = null;
            try
            {
                try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("The latest build is still being registered. Try again shortly.");
                var pointer = Path.Combine(stateDirectory, "latest-build.json");
                if (File.Exists(pointer))
                {
                    try { build = JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(pointer)); }
                    catch (JsonException exception) { new ErrorLogger().Log(exception); }
                }
                if (build is null || string.IsNullOrWhiteSpace(build.ExecutablePath) || !Path.IsPathFullyQualified(build.ExecutablePath) || !File.Exists(build.ExecutablePath) ||
                    string.Equals(build.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    build = new(Path.Combine(AppContext.BaseDirectory, "fallback", "Bisik.exe"), "Installed fallback", "Fallback");
                if (!File.Exists(build.ExecutablePath)) throw new FileNotFoundException("No completed build or installed fallback is available. Rebuild Bisik first.");
                if (build.Kind != "Fallback")
                    lease = new FileStream(Path.Combine(Path.GetDirectoryName(build.ExecutablePath)!, ".in-use.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            finally { if (acquired) gate.ReleaseMutex(); }

            using (lease)
            {
                if (args is ["--resolve-target", var report])
                {
                    File.WriteAllText(report, JsonSerializer.Serialize(build));
                    return 0;
                }
                var dll = Path.GetExtension(build.ExecutablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
                var start = new ProcessStartInfo(dll ? "dotnet" : build.ExecutablePath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(build.ExecutablePath)!,
                };
                if (dll) start.ArgumentList.Add(build.ExecutablePath);
                foreach (var argument in args) start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Bisik could not start.");
                // This windowless launcher holds the snapshot lease until Bisik exits. Rebuilds can
                // replace bin files freely, and cleanup cannot remove an active build's dependencies.
                child.WaitForExit();
                return child.ExitCode;
            }
        }
        catch (Exception exception)
        {
            try { new ErrorLogger().Log(exception); } catch { }
            MessageBox.Show(exception.Message, "Bisik launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private sealed record BuildInfo(string ExecutablePath, string Configuration, string Kind);
}
