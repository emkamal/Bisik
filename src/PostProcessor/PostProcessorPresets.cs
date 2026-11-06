using Bisik.Models;

namespace Bisik.PostProcessor;

public static class PostProcessorPresets
{
    public static string ResolveSystemPrompt(PostProcessorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var prompt = settings.PresetName switch
        {
            PostProcessorPreset.CleanUp =>
                "You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else.",
            PostProcessorPreset.HomogenizeToEnglish =>
                "You are a multilingual transcription editor. Rewrite the transcription as fluent natural English, preserving meaning and intent. Return only the final English text.",
            PostProcessorPreset.HomogenizeToIndonesian =>
                "You are a multilingual transcription editor. Rewrite the transcription as fluent natural Indonesian, preserving meaning and intent. Return only the final Indonesian text.",
            PostProcessorPreset.TranslateToLanguage =>
                CreateTranslatePrompt(settings.TargetLanguage),
            PostProcessorPreset.Custom =>
                settings.SystemPrompt,
            _ =>
                settings.SystemPrompt,
        };
        return prompt + (settings.Vocabulary?.Instructions ?? "");
    }

    private static string CreateTranslatePrompt(string? targetLanguage)
    {
        var resolvedTarget = string.IsNullOrWhiteSpace(targetLanguage)
            ? "the requested target language"
            : targetLanguage.Trim();

        return $"You are a translation-focused transcription editor. Translate the transcription into {resolvedTarget} while preserving the original meaning, tone, and named entities. Return only the translated text.";
    }
}
