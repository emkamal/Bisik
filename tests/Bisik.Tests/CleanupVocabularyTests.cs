using System.Text.Json;
using Bisik.Core;
using Bisik.Models;
using Bisik.PostProcessor;

namespace Bisik.Tests;

public class CleanupVocabularyTests
{
    [Fact]
    public void EveryPresetComposesOptionalVocabularyAndLegacyCustomRemainsVerbatim()
    {
        var settings = new PostProcessorSettings { PresetName = PostProcessorPreset.Custom, SystemPrompt = "verbatim\nΔ" };
        Assert.Equal(settings.SystemPrompt, PostProcessorPresets.ResolveSystemPrompt(settings));
        foreach (var preset in Enum.GetValues<PostProcessorPreset>())
        {
            var original = settings with { PresetName = preset };
            var configured = original with { Vocabulary = CleanupVocabulary.AbbTemplate };
            Assert.StartsWith(PostProcessorPresets.ResolveSystemPrompt(original), PostProcessorPresets.ResolveSystemPrompt(configured));
            Assert.Contains("preserve unrelated ordinary words", PostProcessorPresets.ResolveSystemPrompt(configured));
            Assert.Equal(ReasoningCapability.Fingerprint(original), ReasoningCapability.Fingerprint(configured));
        }
    }
    [Fact]
    public void ProfileRoundTripsAndChangesSpeculativeIdentityWithoutChangingCapabilities()
    {
        var profile = CleanupVocabulary.ParseLines("Domain", "HAPPI | API only | happi, happy\nfetch_class_data | tool only | fetch class data")!;
        Assert.Equal(profile.Lines, CleanupVocabulary.ParseLines(profile.Name, profile.Lines)!.Lines);
        var app = new AppSettings { PostProcessor = new() { Vocabulary = profile } };
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(app))!.Snapshot();
        Assert.Equal(profile.Lines, copy.PostProcessor.Vocabulary!.Lines);
        Assert.NotEqual(JsonSerializer.Serialize(app.PostProcessor), JsonSerializer.Serialize(app.PostProcessor with { Vocabulary = null }));
        Assert.Throws<ArgumentException>(() => CleanupVocabulary.ParseLines("Name", "bad"));
        Assert.Null(CleanupVocabulary.ParseLines("", ""));
    }
}
