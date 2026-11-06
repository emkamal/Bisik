using System.Collections.Immutable;
using System.Text.Json;

namespace Bisik.Models;

public sealed record CleanupVocabularyEntry(string Canonical, string Context, ImmutableArray<string> Variants);
public sealed record CleanupVocabulary(string Name, ImmutableArray<CleanupVocabularyEntry> Entries)
{
    public CleanupVocabulary Snapshot()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Entries.IsDefault || Entries.Length is < 1 or > 32
            || Entries.Any(e => string.IsNullOrWhiteSpace(e.Canonical) || e.Canonical.Length > 256 || e.Context.Length > 512
                || e.Variants.IsDefault || e.Variants.Length > 20 || e.Variants.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 128)))
            throw new ArgumentException("Vocabulary requires a name and up to 32 bounded canonical/context entries.");
        return this;
    }
    public string Instructions => "\n\nProject vocabulary (use only in the stated context; preserve unrelated ordinary words, including happy):\n"
        + JsonSerializer.Serialize(Snapshot().Entries.OrderBy(e => e.Canonical, StringComparer.Ordinal));
    public static CleanupVocabulary? ParseLines(string name, string lines)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(lines)) return null;
        var entries = lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3) throw new ArgumentException("Use canonical | context | comma-separated variants on each line.");
            return new CleanupVocabularyEntry(parts[0], parts[1], parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray());
        }).ToImmutableArray();
        return new CleanupVocabulary(name, entries).Snapshot();
    }
    public string Lines => string.Join(Environment.NewLine, Entries.Select(e => $"{e.Canonical} | {e.Context} | {string.Join(",", e.Variants)}"));
    public static CleanupVocabulary AbbTemplate => ParseLines("ABB History (review before use)", """
        HAPPI | ABB History API name, never ordinary happy | happi, happy
        VtrinLib | ABB library name | vtrin lib
        cMiaServer | ABB server class | mia server
        FetchClassData | C# API method | fetch class data
        FetchGraphDataEx | C# API method | fetch graph data ex
        CommitClassData | C# API method | commit class data
        fetch_class_data | tool name; distinct from C# FetchClassData | fetch class data
        GetInstancesWithSecurityChecking | C# API method | get instances with security checking
        RTDB | database name | r t d b
        """)!;
}
