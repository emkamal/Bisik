using System.Diagnostics;
using System.Text.Json;

namespace Bisik.Tests;

public class ExperimentPipelineTests
{
    [Fact]
    public async Task ManualTemplateGeneratesOnlyExplicitCliArgumentsWithoutRunningInference()
    {
        var repo = FindRepo();
        var root = Path.Combine(repo, "artifacts", "pipeline-validation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var sources = new[] { "setup.json", "fixture.json", "encrypted.json" }.Select(n => Path.Combine(root, n)).ToArray();
            foreach (var source in sources) await File.WriteAllTextAsync(source, "{}");
            var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(repo, "tools", "Invoke-BisikExperimentEvaluation.ps1"),
                "-DefinitionPath", sources[0], "-FixturePath", sources[1], "-EncryptedSettingsPath", sources[2],
                "-OutputDirectory", Path.Combine(root, "summary"), "-Repetitions", "3", "-ValidateOnly" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
            using var json = JsonDocument.Parse(output); var args = json.RootElement.GetProperty("Arguments").EnumerateArray().Select(v => v.GetString()).ToArray();
            Assert.Contains("--experiment", args); Assert.Contains("--repeat", args); Assert.Contains("3", args);
            Assert.DoesNotContain("--quality", args); Assert.False(Directory.Exists(Path.Combine(root, "summary")));
            var template = await File.ReadAllTextAsync(Path.Combine(repo, "tools", "azure-pipelines-experiments.yml"));
            Assert.Contains("trigger: none", template); Assert.Contains("pr: none", template);
            Assert.DoesNotContain("PublishBuildArtifacts", template);
        }
        finally { Directory.Delete(root, true); }
    }
    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bisik.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository missing.");
    }
}
