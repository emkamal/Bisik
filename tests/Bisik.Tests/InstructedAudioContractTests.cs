using System.Text.Json;
using Bisik.Models;

namespace Bisik.Tests;

public class InstructedAudioContractTests
{
    [Fact]
    public void ExplicitCallableModelAndBoundsAreScopedAndNeverTruncate()
    {
        var provider = new ProviderSettings { Endpoint = "https://example.invalid", AzureV1 = true };
        var contract = new InstructedAudioContract(InstructedAudioContract.Fingerprint(provider, "selected"), "selected", 60, 8 * 1024 * 1024, true);
        using var payload = JsonDocument.Parse(InstructedAudioDiagnostics.BuildPayload("selected", "edit raw audio", [1, 2], contract, provider));
        Assert.Equal("selected", payload.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain("reference", payload.RootElement.GetRawText());
        Assert.Throws<NotSupportedException>(() => contract.Validate(provider, "selected", 61, 2));
        Assert.Throws<NotSupportedException>(() => contract.Validate(provider, "selected", 10, 9 * 1024 * 1024));
        Assert.Throws<NotSupportedException>(() => contract.Validate(provider, "other", 10, 2));
        Assert.Throws<NotSupportedException>(() => contract.Validate(provider with { Endpoint = "https://changed.invalid" }, "selected", 10, 2));
    }
    [Fact]
    public void ParserPreservesOldCommandButUnspecifiedContentIsNeverSynthetic()
    {
        var old = InstructedAudioDiagnostics.Options.Parse(["audio.wav", "--output", "report.json"]);
        Assert.Equal("unspecified", old.FixtureKind); Assert.Null(old.ContractPath);
        var selected = InstructedAudioDiagnostics.Options.Parse(["audio.wav", "--output", "report.json", "--audio-model", "selected", "--fixture-kind", "human"]);
        Assert.Equal("human", selected.FixtureKind); Assert.Equal("selected", selected.Model);
        Assert.Throws<ArgumentException>(() => InstructedAudioDiagnostics.Options.Parse(["audio.wav", "--output", "audio.wav"]));
    }
    [Fact]
    public async Task MissingAccessIsASafeFailureAndNeverAQualityScore()
    {
        using var failed = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("private endpoint and credentials") };
        var exception = await Assert.ThrowsAsync<Bisik.Providers.ProviderRequestException>(() => InstructedAudioDiagnostics.ReadAudioCompletionAsync(failed, CancellationToken.None));
        Assert.DoesNotContain("private", exception.Message);
        Assert.Equal("http-404", Bisik.Core.TranscriptionService.SafeFailureCode(exception));
    }
}
