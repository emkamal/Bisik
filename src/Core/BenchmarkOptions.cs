namespace Bisik.Core;

public sealed record BenchmarkOptions(string AudioPath, string OutputPath, int Repetitions, int TimeoutSeconds)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 1) throw new ArgumentException("Expected WAV then option/value pairs.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--output" or "--repeat" or "--timeout-seconds") || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown or duplicate benchmark option.");
        }
        int Read(string name, int fallback, int maximum) => !options.TryGetValue(name, out var value) ? fallback :
            int.TryParse(value, out var parsed) && parsed >= 1 && parsed <= maximum ? parsed :
            throw new ArgumentException("Benchmark limits exceeded.");
        if (!options.TryGetValue("--output", out var output)) throw new ArgumentException("--output is required.");
        return new(args[0], output, Read("--repeat", 1, 5), Read("--timeout-seconds", 120, 120));
    }
}
