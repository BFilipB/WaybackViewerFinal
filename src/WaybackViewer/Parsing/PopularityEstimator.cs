using System.Globalization;
using System.Text.RegularExpressions;
using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

/// Site-independent popularity: real counts when the page shows them, otherwise a rough estimate from how much
/// there is on the page. Not accurate, but good enough to rank and filter pages from any site.
public static partial class PopularityEstimator
{
    private const int ScanChars = 200_000;

    [GeneratedRegex(@"(?<n>\d[\d,\.]*\s?[kKmM]?)\s*(?<w>views?|comments?|replies|reply|likes?|upvotes?|points?|votes?|shares?|reposts?)(?![a-zA-Z])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex CountThenWord();

    [GeneratedRegex(@"\b(?<w>views?|comments?|replies|likes?|upvotes?|points?|votes?|shares?)\s*[:(]\s*(?<n>\d[\d,\.]*\s?[kKmM]?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex WordThenCount();

    public static PageMetadata Enrich(PageMetadata? found, string html, string text)
    {
        long? views = found?.Views, likes = found?.Likes, comments = found?.Comments, shares = found?.Shares, score = found?.Score;

        if (views is null || likes is null || comments is null || shares is null || score is null)
        {
            try
            {
                var sample = text.Length > ScanChars ? text[..ScanChars] : text;
                foreach (Match m in CountThenWord().Matches(sample)) Apply(m);
                foreach (Match m in WordThenCount().Matches(sample)) Apply(m);
            }
            catch (RegexMatchTimeoutException) { }
        }

        void Apply(Match m)
        {
            var n = ParseCount(m.Groups["n"].Value);
            if (n is null or <= 0) return;
            var w = m.Groups["w"].Value.ToLowerInvariant();
            if (w.StartsWith("view")) views ??= n;
            else if (w.StartsWith("comment") || w.StartsWith("repl")) comments ??= n;
            else if (w.StartsWith("like") || w.StartsWith("upvote")) likes ??= n;
            else if (w.StartsWith("share") || w.StartsWith("repost")) shares ??= n;
            else score ??= n;   // points, votes
        }

        var hasCounts = views.HasValue || likes.HasValue || comments.HasValue || shares.HasValue || score.HasValue;
        long engagement;
        double confidence;
        if (hasCounts)
        {
            engagement = found?.EngagementScore
                ?? (likes ?? 0) * 3 + (comments ?? 0) * 4 + (shares ?? 0) * 2 + (views ?? 0) + (score ?? 0);
            confidence = found is { Confidence: > 0.6 } ? found.Confidence : 0.6;
        }
        else
        {
            engagement = Estimate(html, text);
            confidence = 0.2;
        }

        return new PageMetadata(views, likes, shares, comments, score, engagement,
            found?.Source, found?.Author, found?.PublishedAt, found?.Tags, confidence);
    }

    /// Bigger, richer, more linked and more discussed pages score higher. Roughly 0..300.
    public static long Estimate(string html, string text)
    {
        var limit = Math.Min(html.Length, ScanChars);
        var span = html.AsSpan(0, limit);
        var links = Count(span, "<a ");
        var images = Count(span, "<img");
        var discussion = Count(span, "comment") + Count(span, "reply") + Count(span, "replies");
        var textPart = Math.Min(text.Length / 400, 100);
        var score = textPart + Math.Min(links / 4, 60) + Math.Min(images * 2, 40) + Math.Min(discussion * 3, 100);
        return Math.Max(1, score);
    }

    private static int Count(ReadOnlySpan<char> haystack, string needle)
    {
        var n = 0;
        while (true)
        {
            var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return n;
            n++;
            haystack = haystack[(i + needle.Length)..];
        }
    }

    private static long? ParseCount(string value)
    {
        var text = value.Trim().Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var multiplier = 1m;
        if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000; text = text[..^1]; }
        else if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000; text = text[..^1]; }
        text = text.TrimEnd('.');
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return null;
        var result = parsed * multiplier;
        return result is < 0 or > 10_000_000_000m ? null : (long)result;
    }
}
