using Bisik.Models;

internal sealed record InstructedAudioContract(string ApiFingerprint, string Model, int MaximumSeconds,
    long MaximumBytes, bool CallableContractVerified)
{
    internal static string Fingerprint(ProviderSettings provider, string model) => ExperimentIdentity.Hash("audio-chat-v1",
        provider.Type, provider.Endpoint.TrimEnd('/'), provider.AzureV1, provider.ApiVersion, provider.DeploymentName, model,
        provider.ExtraHeaders?.OrderBy(h => h.Key, StringComparer.Ordinal).ToArray());
    internal void Validate(ProviderSettings provider, string model, double seconds, long bytes)
    {
        if (!CallableContractVerified || Model != model || ApiFingerprint != Fingerprint(provider, model)
            || MaximumSeconds is < 1 or > 3600 || MaximumBytes is < 1 or > 64 * 1024 * 1024)
            throw new NotSupportedException("Audio contract is unavailable or stale.");
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaximumSeconds || bytes < 1 || bytes > MaximumBytes)
            throw new NotSupportedException("Fixture exceeds the verified audio contract; no truncation performed.");
    }
}
