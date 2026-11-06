using System.Collections.Immutable;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public sealed class ExperimentStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private sealed record Envelope(int Version, string Hash, JsonElement Data);
    public ExperimentStore(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik", "experiments"));
    public string DirectoryFor(Guid id) => id != Guid.Empty ? Path.Combine(_root, id.ToString("N")) : throw new ArgumentException("Empty ID.");
    public IEnumerable<Guid> List() => Directory.Exists(_root) ? Directory.EnumerateDirectories(_root)
        .Select(Path.GetFileName).Where(n => Guid.TryParseExact(n, "N", out _)).Select(n => Guid.Parse(n!)) : [];

    public async Task SaveSetupAsync(ExperimentDefinition definition, AppSettings connection, CancellationToken ct)
    {
        definition = definition.Snapshot(); connection = connection.Snapshot();
        if (definition.ConnectionFingerprint != ExperimentMatrix.ConnectionIdentity(connection.PostProcessor))
            throw new ArgumentException("Connection does not match setup.");
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => new SettingsManager(Path.Combine(DirectoryFor(definition.Id), "connection")).Save(connection), ct).ConfigureAwait(false);
            await WriteAsync(definition.Id, "setup.json", definition, ct).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public async Task SaveAsync(ExperimentRun run, CancellationToken ct, string? ownedAudioPath = null)
    {
        Validate(run);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = DirectoryFor(run.Definition.Id);
            Directory.CreateDirectory(directory);
            if (ownedAudioPath is not null && run.Definition.RetainAudio)
            {
                var temp = Path.Combine(directory, $"audio-{Guid.NewGuid():N}.tmp");
                try
                {
                    await using (var source = File.OpenRead(ownedAudioPath))
                    await using (var target = File.Create(temp)) await source.CopyToAsync(target, ct).ConfigureAwait(false);
                    File.Move(temp, Path.Combine(directory, "recording" + Path.GetExtension(ownedAudioPath)), true);
                }
                finally { File.Delete(temp); }
            }
            await WriteAsync(run.Definition.Id, "run.json", run, ct).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public Task<ExperimentDefinition> LoadSetupAsync(Guid id, CancellationToken ct) => ReadAsync<ExperimentDefinition>(id, "setup.json", ct);
    public async Task<ExperimentRun> LoadAsync(Guid id, CancellationToken ct)
    {
        var run = await ReadAsync<ExperimentRun>(id, "run.json", ct).ConfigureAwait(false);
        Validate(run);
        if (run.Definition.Id != id) throw new InvalidDataException("Stored ID mismatch.");
        return run.State is ExperimentState.Recording or ExperimentState.Stopping or ExperimentState.Transcribing or ExperimentState.Generating or ExperimentState.Evaluating
            ? run with { State = ExperimentState.Failed, Interrupted = true, Trials = run.Trials.Select(t => t with
                { Generation = t.Generation == ExperimentGenerationStatus.Running ? ExperimentGenerationStatus.Failed
                    : t.Generation == ExperimentGenerationStatus.Pending ? ExperimentGenerationStatus.Skipped : t.Generation,
                    EvaluationStatus = t.EvaluationStatus == ExperimentEvaluationStatus.Running ? ExperimentEvaluationStatus.Failed : t.EvaluationStatus,
                    FailureCode = t.Generation == ExperimentGenerationStatus.Running || t.EvaluationStatus == ExperimentEvaluationStatus.Running ? "interrupted" : t.FailureCode }).ToImmutableArray() }
            : run;
    }

    public async Task<AppSettings?> LoadConnectionAsync(Guid id, CancellationToken ct)
    {
        var source = Path.Combine(DirectoryFor(id), "connection", "settings.json");
        if (!File.Exists(source)) return null;
        var scratch = Path.Combine(DirectoryFor(id), "scratch-" + Guid.NewGuid().ToString("N"));
        try { return await Task.Run(() => SettingsManager.LoadEncryptedCopy(source, scratch), ct).ConfigureAwait(false); }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private async Task WriteAsync<T>(Guid id, string name, T value, CancellationToken ct)
    {
        var directory = DirectoryFor(id); Directory.CreateDirectory(directory);
        var data = JsonSerializer.SerializeToElement(value);
        var envelope = new Envelope(1, ExperimentIdentity.Hash("stored-v1", data.GetRawText()), data);
        var temp = Path.Combine(directory, $"{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(envelope), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, Path.Combine(directory, name), true);
        }
        finally { File.Delete(temp); }
    }
    private async Task<T> ReadAsync<T>(Guid id, string name, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryFor(id), name);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized experiment file.");
        var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        if (envelope is null || envelope.Version != 1 || envelope.Hash != ExperimentIdentity.Hash("stored-v1", envelope.Data.GetRawText()))
            throw new InvalidDataException("Corrupt or unsupported experiment file.");
        return envelope.Data.Deserialize<T>() ?? throw new InvalidDataException("Empty experiment file.");
    }
    public static void Validate(ExperimentRun run)
    {
        run.Definition.Validate(); run.Take.Validate();
        if (run.Take.InputIdentity != run.Definition.InputIdentity || run.Trials.IsDefault || run.Trials.Length > 101
            || run.Trials.Select(t => t.Id).Distinct().Count() != run.Trials.Length
            || !double.IsFinite(run.BatchElapsedMs) || run.BatchElapsedMs < 0)
            throw new InvalidDataException("Invalid run identity or bounds.");
        foreach (var trial in run.Trials)
        {
            if (trial.Output?.Length > 100_000 || trial.Generation == ExperimentGenerationStatus.Succeeded && string.IsNullOrWhiteSpace(trial.Output)
                || new[] { trial.CleanupMs, trial.JudgeMs }.Any(v => v is { } n && (!double.IsFinite(n) || n < 0)))
                throw new InvalidDataException("Invalid trial.");
            if (trial.Evaluation is { } evaluation) PostProcessor.LLMExperimentJudge.ValidateEvaluation(evaluation);
        }
    }
}
