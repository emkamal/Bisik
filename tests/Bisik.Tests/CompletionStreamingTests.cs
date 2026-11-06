using System.Net;
using System.Text;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Tests;

public class CompletionStreamingTests
{
    private static string Frame(string text) => "data: " + text + "\n\n";
    private static string Content(string value) => Frame("{\"choices\":[{\"delta\":{\"content\":\"" + value + "\"},\"finish_reason\":null}]}");
    private static string Stop => Frame("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}");
    private static PostProcessorSettings Settings()
    {
        var settings = TranscriptionServiceTextTests.Settings() with { StreamCompletion = true };
        return settings with { CompletionCapability = new(ReasoningCapability.Fingerprint(settings), [CompletionFeature.Streaming]) };
    }
    [Fact]
    public async Task SplitUtf8FramesAcceptOnlyCompleteTextAndUsage()
    {
        var sse = Content("Δ") + Content(" café") + Stop + Frame("{\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}") + Frame("[DONE]");
        using var client = Client(sse);
        var stages = new List<TextCompletionStage>();
        var metrics = new SessionMetrics("stream-test"); using var scope = metrics.Activate();
        var result = await new LLMPostProcessor(client).CompleteAsync("raw", Settings(), false, CancellationToken.None,
            new ExperimentSessionTests.InlineProgress<TextCompletionStage>(stages.Add));
        Assert.Equal("Δ café", result.Text); Assert.Equal(12, result.Usage!.PromptTokens); Assert.Equal(2, result.Usage.ReasoningTokens);
        Assert.Null(result.Usage.CachedTokens); Assert.True(result.FirstContentMs >= result.HeadersMs); Assert.True(result.FullResponseMs >= result.FirstContentMs);
        Assert.Equal(new[] { TextCompletionStage.Headers, TextCompletionStage.FirstContent, TextCompletionStage.Complete }, stages);
        Assert.Equal(2, metrics.Snapshot(SessionOutcome.Ready).Counts[MetricCount.ReasoningTokens]);
    }
    [Theory]
    [InlineData("eof")] [InlineData("length")] [InlineData("refusal")] [InlineData("oversized")]
    public async Task IncompleteRefusedAndOversizedStreamCannotReturnPartials(string kind)
    {
        var sse = kind switch
        {
            "eof" => Content("partial"),
            "length" => Content("partial") + Frame("{\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}") + Frame("[DONE]"),
            "refusal" => Frame("{\"choices\":[{\"delta\":{\"refusal\":\"no\"}}]}") + Stop + Frame("[DONE]"),
            _ => Content(new string('x', 64_001)) + Stop + Frame("[DONE]"),
        };
        using var client = Client(sse);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new LLMPostProcessor(client).CompleteAsync("raw", Settings(), false, CancellationToken.None));
    }
    [Fact]
    public async Task UnknownStreamCapabilityIsBlockedAndMissingUsageRemainsNull()
    {
        using var client = Client(Content("complete") + Stop + Frame("[DONE]"));
        var processor = new LLMPostProcessor(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.CompleteAsync("raw", Settings() with { CompletionCapability = null }, false, CancellationToken.None));
        Assert.Null((await processor.CompleteAsync("raw", Settings(), false, CancellationToken.None)).Usage);
    }
    [Fact]
    public async Task CompleteResponseParsesNullableUsageOnceAndDoesNotInventFirstContentTime()
    {
        using var client = new HttpClient(new TranscriptionServiceTextTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"complete\"}}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4,\"prompt_tokens_details\":{\"cached_tokens\":0}}}") })));
        var result = await new LLMPostProcessor(client).CompleteAsync("raw", TranscriptionServiceTextTests.Settings(), false, CancellationToken.None);
        Assert.Equal(0, result.Usage!.CachedTokens); Assert.Null(result.Usage.ReasoningTokens); Assert.Null(result.FirstContentMs);
        Assert.True(result.FullResponseMs >= result.HeadersMs);
    }
    private static HttpClient Client(string sse) => new(new TranscriptionServiceTextTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new SplitStream(Encoding.UTF8.GetBytes(sse))) })));
    private sealed class SplitStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => base.ReadAsync(buffer, offset, Math.Min(3, count), ct);
    }
}
