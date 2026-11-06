using System.Collections.Immutable;

namespace Bisik.Models;

public enum RecognitionField { Prompt, Keywords, Language, Delay }
public sealed record RecognitionContext(string? Prompt, ImmutableArray<string> Keywords, string? ExpectedLanguage)
{
    public RecognitionContext Snapshot()
    {
        if (Prompt?.Length > 1024 || Keywords.IsDefault || Keywords.Length > 32 || Keywords.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 128)
            || ExpectedLanguage?.Length > 20) throw new ArgumentException("Recognition hints exceed bounds.");
        return this;
    }
}
public sealed record RecognitionCapability(string ApiFingerprint, ImmutableArray<RecognitionField> Fields)
{
    public static string Fingerprint(ProviderSettings provider, bool realtime) => ExperimentIdentity.Hash("recognition-fields-v1",
        provider.Type, provider.Endpoint.TrimEnd('/'), provider.ApiVersion, provider.AzureV1, provider.DeploymentName, provider.Model,
        provider.ExtraHeaders?.OrderBy(h => h.Key, StringComparer.Ordinal).ToArray(),
        realtime ? "realtime-v1" : "multipart");
    public bool Supports(ProviderSettings provider, bool realtime, RecognitionField field) => !Fields.IsDefault && Fields.Contains(field)
        && ApiFingerprint == Fingerprint(provider, realtime);
}
