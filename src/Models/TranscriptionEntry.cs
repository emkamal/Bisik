namespace Bisik.Models;

public sealed record TranscriptionEntry(
    DateTimeOffset Timestamp,
    string Text,
    string? RawText,
    string Provider,
    string Language,
    long DurationMs,
    string? AudioFilePath);
