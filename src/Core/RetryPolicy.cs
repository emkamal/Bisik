using System.Globalization;
using System.Net.Http;

namespace Bisik.Core;

public static class RetryPolicy
{
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= int.MaxValue)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date > now ? date - now : TimeSpan.Zero;
        return null;
    }
    internal static TimeSpan? FromResponse(HttpResponseMessage response) => response.Headers.TryGetValues("Retry-After", out var values)
        ? ParseRetryAfter(values.FirstOrDefault(), DateTimeOffset.UtcNow) : null;
    public static async Task WaitAsync(TimeSpan? serverDelay, CancellationToken ct)
    {
        var delay = serverDelay ?? TimeSpan.FromMilliseconds(300);
        if (delay > TimeSpan.FromMinutes(2))
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            throw new TimeoutException("Server retry delay exceeds the post-Stop budget.");
        }
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
