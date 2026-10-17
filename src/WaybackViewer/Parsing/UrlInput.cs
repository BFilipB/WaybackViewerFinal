using System.Text.RegularExpressions;

namespace WaybackViewer.Parsing;

public static partial class UrlInput
{
    [GeneratedRegex(@"^https?://web\.archive\.org/web/[^/]*/(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WaybackLink();

    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    /// Turns whatever the user pasted into the CDX prefix we search for.
    /// Accepts plain URLs, URLs with a trailing "*", and full web.archive.org listing links.
    /// Returns null when the input is empty or unusable.
    public static string? NormalizePrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        var wayback = WaybackLink().Match(s);
        if (wayback.Success) s = wayback.Groups["target"].Value;

        s = Scheme().Replace(s, "").TrimEnd('*').Trim();

        if (s.Length == 0 || s.Length > 400) return null;
        foreach (var ch in s)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return null;

        return s.ToLowerInvariant(); // CDX keys are case-insensitive; one canonical form avoids duplicate searches
    }

    public static string NormalizeFilter(string? filter) => filter is "html" or "all" ? filter : "html200";

    public static bool IsValidTimestamp(string? ts) =>
        ts is { Length: 14 } && ts.All(char.IsAsciiDigit);

    public static bool IsValidCaptureUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= 4096 &&
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
