using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bisik.Models;

[assembly: InternalsVisibleTo("Bisik.Tests")]

namespace Bisik.Core;

public sealed class HistoryLogger
{
    private const string HistoryFileName = "history.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _historyPath;
    private readonly bool _historyEnabled;
    private readonly SemaphoreSlim _pathLock;
    private readonly HistoryLoggerTestHooks? _testHooks;

    public HistoryLogger(string? baseDirectory = null, bool historyEnabled = true)
        : this(baseDirectory, historyEnabled, testHooks: null)
    {
    }

    internal HistoryLogger(string? baseDirectory, bool historyEnabled, HistoryLoggerTestHooks? testHooks)
    {
        var rootDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik")
            : baseDirectory;

        _historyPath = NormalizeHistoryPath(rootDirectory);
        _historyEnabled = historyEnabled;
        _pathLock = PathLocks.GetOrAdd(_historyPath, static _ => new SemaphoreSlim(1, 1));
        _testHooks = testHooks;
    }

    public async Task AppendAsync(TranscriptionEntry entry, CancellationToken ct = default)
    {
        if (!_historyEnabled)
        {
            return;
        }

        await _pathLock.WaitAsync(ct);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);

            var json = JsonSerializer.Serialize(entry, SerializerOptions);

            await using var stream = new FileStream(_historyPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteLineAsync(json.AsMemory(), ct);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task<IReadOnlyList<TranscriptionEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        await _pathLock.WaitAsync(ct);

        try
        {
            return await ReadAllCoreAsync(ct);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task<IReadOnlyList<TranscriptionEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        var entries = await ReadAllAsync(ct);
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries;
        }

        return entries
            .Where(entry =>
                Contains(entry.Text, query) ||
                (!string.IsNullOrEmpty(entry.RawText) && Contains(entry.RawText, query)))
            .ToArray();
    }

    public async Task<bool> DeleteAsync(DateTimeOffset timestamp, string text, CancellationToken ct = default)
    {
        await _pathLock.WaitAsync(ct);

        try
        {
            var entries = (await ReadAllCoreAsync(ct)).ToList();
            var index = entries.FindIndex(entry => entry.Timestamp == timestamp && string.Equals(entry.Text, text, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            entries.RemoveAt(index);
            if (_testHooks?.BeforeRewriteAsync is not null)
            {
                await _testHooks.BeforeRewriteAsync(ct);
            }

            await RewriteHistoryAsync(entries, ct);
            return true;
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _pathLock.WaitAsync(ct);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(_historyPath))
            {
                File.Delete(_historyPath);
            }
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task ExportToTextAsync(string exportPath, CancellationToken ct = default)
    {
        var entries = await ReadAllAsync(ct);
        var orderedEntries = entries
            .Select((entry, index) => new { entry, index })
            .OrderBy(item => item.entry.Timestamp)
            .ThenBy(item => item.index)
            .Select(item => item.entry);

        Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);

        await using var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var entry in orderedEntries)
        {
            ct.ThrowIfCancellationRequested();

            await writer.WriteLineAsync($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz} | {entry.Text}".AsMemory(), ct);
        }
    }

    private async Task RewriteHistoryAsync(IReadOnlyList<TranscriptionEntry> entries, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_historyPath)!;
        Directory.CreateDirectory(directory);

        if (entries.Count == 0)
        {
            if (File.Exists(_historyPath))
            {
                File.Delete(_historyPath);
            }

            return;
        }

        var tempPath = Path.Combine(directory, $"{HistoryFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var json = JsonSerializer.Serialize(entry, SerializerOptions);
                    await writer.WriteLineAsync(json.AsMemory(), ct);
                }
            }

            File.Move(tempPath, _historyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<IReadOnlyList<TranscriptionEntry>> ReadAllCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        var entries = new List<TranscriptionEntry>();
        using var stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (_testHooks?.AfterReadOpenAsync is not null)
        {
            await _testHooks.AfterReadOpenAsync(ct);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<TranscriptionEntry>(line, SerializerOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Best-effort history loading should skip malformed/truncated lines.
            }
        }

        return entries;
    }

    private static string NormalizeHistoryPath(string rootDirectory)
    {
        return Path.GetFullPath(Path.Combine(rootDirectory, HistoryFileName));
    }

    private static bool Contains(string value, string query)
    {
        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HistoryLoggerTestHooks
{
    public Func<CancellationToken, Task>? AfterReadOpenAsync { get; init; }

    public Func<CancellationToken, Task>? BeforeRewriteAsync { get; init; }
}
