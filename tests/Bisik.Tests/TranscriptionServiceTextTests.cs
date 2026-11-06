using System.Net;
using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;
using Bisik.Providers;

namespace Bisik.Tests;

public sealed class TranscriptionServiceTextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bisik.TextTests", Guid.NewGuid().ToString("N"));
    private int _sideEffects;
    private TranscriptionService Service(IPostProcessor processor) => new(
        new Dictionary<ProviderType, ITranscriptionProvider>(), processor, new HistoryLogger(_directory),
        new ErrorLogger(_directory), () => _sideEffects++, () => _sideEffects++);
    internal static PostProcessorSettings Settings(string? effort = null) => new()
    {
        Enabled = true, Model = "model", PresetName = PostProcessorPreset.Custom, SystemPrompt = "Exact prompt\nΔ",
        ReasoningEffort = effort, Provider = new() { Type = ProviderType.CustomEndpoint, Endpoint = "https://example.invalid/chat" },
    };

    [Fact]
    public async Task ProbeSendsEffortWithoutFabricatingCapabilityAndNormalTrialRequiresEvidence()
    {
        var requests = new List<JsonDocument>();
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()));
            return Complete("edited");
        }));
        var service = Service(new LLMPostProcessor(client));
        service.PhaseChanged += _ => _sideEffects++;
        var unknown = Settings("low");
        Assert.False((await service.ProcessTextAsync("raw", unknown, CancellationToken.None)).Succeeded);
        Assert.Empty(requests);
        var probe = await service.ProbeTextCompatibilityAsync("raw", unknown, CancellationToken.None);
        Assert.True(probe.Succeeded);
        Assert.Equal(ExperimentVerification.Verified, probe.Verification);
        Assert.Null(unknown.ReasoningCapability);
        var verified = unknown with { ReasoningCapability = new(probe.ApiFingerprint, ["low"]) };
        var trial = await service.ProcessTextAsync("same raw", verified, CancellationToken.None);
        Assert.Equal("low", trial.EffectiveEffort);
        Assert.Equal("model", trial.Model);
        Assert.Equal(verified.SystemPrompt, trial.SystemPrompt);
        Assert.Equal("same raw", requests[1].RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("low", requests[1].RootElement.GetProperty("reasoning_effort").GetString());
        await service.ProcessTextAsync("raw", Settings(), CancellationToken.None);
        Assert.False(requests[2].RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(0, _sideEffects);
        Assert.False(File.Exists(Path.Combine(_directory, "history.jsonl")));
        requests.ForEach(r => r.Dispose());
    }

    [Theory]
    [InlineData(429, true, 2)] [InlineData(500, true, 2)] [InlineData(408, true, 2)]
    [InlineData(401, true, 1)] [InlineData(400, true, 1)] [InlineData(429, false, 1)]
    public async Task RetryIsExplicitAndIncludesDelay(int status, bool retry, int expected)
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage((HttpStatusCode)status) : Complete("edited"))));
        var result = await Service(new LLMPostProcessor(client)).ProcessTextAsync("raw", Settings(), CancellationToken.None, retryTransient: retry);
        Assert.Equal(expected, result.Attempts);
        Assert.Equal(expected == 2, result.Succeeded);
        if (expected == 2) Assert.True(result.ElapsedMs >= 250);
        Assert.Equal(0, _sideEffects);
    }

    [Fact]
    public async Task ServerDelayCannotExceedTrialDeadlineOrRetryEarly()
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "10");
            return Task.FromResult(response);
        }));
        var result = await Service(new LLMPostProcessor(client)).ProcessTextAsync("raw", Settings(), CancellationToken.None, 1, true);
        Assert.Equal("deadline", result.FailureCode);
        Assert.Equal(1, attempts);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("length", null)] [InlineData("stop", "refused")]
    public async Task ProbeCannotVerifyIncompleteOrRefusedOutput(string finish, string? refusal)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = "edited", refusal } } } })) })));
        var result = await Service(new LLMPostProcessor(client)).ProbeTextCompatibilityAsync("raw", Settings("none"), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(ExperimentVerification.Rejected, result.Verification);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task LateCompletionAfterCancellationIsNotAccepted()
    {
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new Processor((_, _, _) => pending.Task));
        var run = service.ProcessTextAsync("raw", Settings(), cts.Token);
        cts.Cancel();
        pending.SetResult("late edited");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    internal static HttpResponseMessage Complete(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } })) };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    internal sealed class Processor(Func<string, PostProcessorSettings, CancellationToken, Task<string>> process) : IPostProcessor
    {
        public Task<string> ProcessAsync(string text, PostProcessorSettings settings, CancellationToken ct) => process(text, settings, ct);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
