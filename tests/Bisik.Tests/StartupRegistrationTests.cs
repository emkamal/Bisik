using Bisik.Core;

namespace Bisik.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void Startup_follows_registered_launcher_and_keeps_portable_fallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Bisik.StartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var registration = Path.Combine(directory, "launcher-path.txt");
            var currentBuild = Path.Combine(directory, "snapshot", "Bisik.exe");
            Assert.Equal(currentBuild, StartupRegistration.ResolveExecutablePath(currentBuild, registration));
            var launcher = Path.Combine(directory, "Bisik.exe");
            File.WriteAllBytes(launcher, []);
            File.WriteAllText(registration, launcher);
            Assert.Equal(launcher, StartupRegistration.ResolveExecutablePath(currentBuild, registration));
            File.WriteAllText(registration, "relative.exe");
            Assert.Equal(currentBuild, StartupRegistration.ResolveExecutablePath(currentBuild, registration));
            File.WriteAllText(registration, Path.Combine(directory, "missing.exe"));
            Assert.Equal(currentBuild, StartupRegistration.ResolveExecutablePath(currentBuild, registration));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
