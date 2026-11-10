using System.Globalization;

namespace WaybackViewer.Parsing;

/// Reads the page's own view/like counts from script JSON that player pages embed (for example the
/// "ytInitialPlayerResponse" blob). Only the FIRST quoted "viewCount"/"likeCount" is used: in those blobs the
/// video's own details come first and recommendations later, so visible-text counts ("71,157 views") are not
/// trusted here. Bounded, linear-time scan; no JSON parsing of multi-megabyte scripts.
internal static class EmbeddedJsonCounts
{
    private const int ScanChars = 2_000_000;
    private const long MaxCount = 10_000_000_000;

    internal readonly record struct Counts(long? Views, long? Likes);

    public static Counts Read(string html)
    {
        if (string.IsNullOrEmpty(html)) return default;
        var limit = Math.Min(html.Length, ScanChars);
        var scope = html.AsSpan(0, limit);
        if (scope.IndexOf("ytInitialPlayerResponse".AsSpan(), StringComparison.Ordinal) < 0) return default;
        return new Counts(First(scope, "\"viewCount\":\""), First(scope, "\"likeCount\":\""));
    }

    private static long? First(ReadOnlySpan<char> scope, string key)
    {
        var at = scope.IndexOf(key.AsSpan(), StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = scope[(at + key.Length)..];
        var n = 0;
        while (n < digits.Length && n < 12 && char.IsAsciiDigit(digits[n])) n++;
        if (n == 0) return null;
        if (!long.TryParse(digits[..n], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
        return value > MaxCount ? null : value;
    }
}
