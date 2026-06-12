using System.Text;
using System.Text.Json;
using Bisik.Models;

namespace Bisik.Core;

public sealed class HistoryLogger
{
    private const string HistoryFileName = "history.jsonl";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _historyPath;
    private readonly bool _historyEnabled;

    public HistoryLogger(string? baseDirectory = null, bool historyEnabled = true)
    {
        var rootDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bisik")
            : baseDirectory;

        _historyPath = Path.Combine(rootDirectory, HistoryFileName);
        _historyEnabled = historyEnabled;
    }

    public async Task AppendAsync(TranscriptionEntry entry, CancellationToken ct = default)
    {
        if (!_historyEnabled)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);

        var json = JsonSerializer.Serialize(entry, SerializerOptions);

        await using var stream = new FileStream(_historyPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteLineAsync(json.AsMemory(), ct);
    }

    public async Task<IReadOnlyList<TranscriptionEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        var entries = new List<TranscriptionEntry>();
        using var stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<TranscriptionEntry>(line, SerializerOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
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
        var entries = (await ReadAllAsync(ct)).ToList();
        var index = entries.FindIndex(entry => entry.Timestamp == timestamp && string.Equals(entry.Text, text, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        entries.RemoveAt(index);
        await RewriteHistoryAsync(entries, ct);
        return true;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(_historyPath))
        {
            File.Delete(_historyPath);
        }

        return Task.CompletedTask;
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

    private static bool Contains(string value, string query)
    {
        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
