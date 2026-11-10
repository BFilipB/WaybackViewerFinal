using System.Globalization;
using System.Text.RegularExpressions;
using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

/// Site-independent popularity: real counts when the page shows them, otherwise a rough estimate from how much
/// there is on the page. Not accurate, but good enough to rank and filter pages from any site.
/// See docs/popularity/scoring.md.
public static partial class PopularityEstimator
{
    private const int ScanChars = 200_000;
    private const int MaxMatchesPerKind = 64;

    [GeneratedRegex(@"(?<n>\d[\d,\.]*\s?[kKmM]?)\s*(?<w>views?|visninger|aufrufe|visitas|vues|pregleda|pregledi|comments?|replies|reply|svar|kommentarer|kommentare|comentarios|commentaires|odgovora|odgovori|odgovor|antworten|likes?|upvotes?|points?|votes?|shares?|reposts?)(?![\\p{L}])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex CountThenWord();

    [GeneratedRegex(@"\b(?<w>views?|comments?|replies|likes?|upvotes?|points?|votes?|shares?)\s*[:(]\s*(?<n>\d[\d,\.]*\s?[kKmM]?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex WordThenCount();

    // Structured count on the post element itself (e.g. <shreddit-post comment-count="13">, data-comment-count="13").
    [GeneratedRegex(@"\b(?:data-)?comments?-?count\s*=\s*[""']?(?<n>\d{1,9})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex CommentCountAttribute();

    // Structured vote score on the post element (e.g. <shreddit-post score="42">, data-score="42").
    [GeneratedRegex(@"\b(?:data-)?score\s*=\s*[""']?(?<n>\d{1,9})""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex ScoreAttribute();

    // phpBB listing rows with unlabeled count cells: replies then views (postdetails spans).
    [GeneratedRegex("class=\"topictitle\"[^<]*>[\\s\\S]{0,400}?class=\"name\"[\\s\\S]{0,300}?class=\"postdetails\">\\s*(?<r>\\d{1,9})\\s*</span>[\\s\\S]{0,200}?class=\"postdetails\">\\s*(?<v>\\d{1,9})\\s*</span>",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PhpBbListingRow();

    public static PageMetadata Enrich(PageMetadata? found, string html, string text)
    {
        long? views = found?.Views, likes = found?.Likes, comments = found?.Comments, shares = found?.Shares, score = found?.Score;

        comments ??= StructuredMedian(html, CommentCountAttribute());
        score ??= StructuredMedian(html, ScoreAttribute());
        comments ??= StructuredMedian(html, PhpBbListingRow(), "r");
        views ??= StructuredMedian(html, PhpBbListingRow(), "v");

        if (views is null || likes is null || comments is null || shares is null || score is null)
        {
            var seen = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var sample = text.Length > ScanChars ? text[..ScanChars] : text;
            try
            {
                foreach (Match m in CountThenWord().Matches(sample)) Collect(seen, m);
                foreach (Match m in WordThenCount().Matches(sample)) Collect(seen, m);
            }
            catch (RegexMatchTimeoutException)
            {
                // Bounded scan: keep whatever matched before the timeout.
            }
            // Median, not first match: stray sidebar/footer counts no longer decide the page's value.
            views ??= Median(seen, "view");
            comments ??= Median(seen, "comment");
            likes ??= Median(seen, "like");
            shares ??= Median(seen, "share");
            score ??= Median(seen, "score");
        }

        var hasCounts = views.HasValue || likes.HasValue || comments.HasValue || shares.HasValue || score.HasValue;
        long engagement;
        double confidence;
        if (hasCounts)
        {
            engagement = found?.EngagementScore
                ?? PageMetadata.Engagement(views, likes, comments, shares, score);
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

    private static void Collect(Dictionary<string, List<long>> seen, Match m)
    {
        var n = ParseCount(m.Groups["n"].Value);
        if (n is null or <= 0) return;
        var w = m.Groups["w"].Value.ToLowerInvariant();
        var kind = w.StartsWith("view") || w.StartsWith("visning") || w.StartsWith("aufruf") || w.StartsWith("visita") || w.StartsWith("vue") || w.StartsWith("pregled") ? "view"
            : w.StartsWith("comment") || w.StartsWith("repl") || w == "svar" || w.StartsWith("kommentar") || w.StartsWith("comentario") || w.StartsWith("odgovor") || w.StartsWith("antwort") ? "comment"
            : w.StartsWith("like") || w.StartsWith("upvote") ? "like"
            : w.StartsWith("share") || w.StartsWith("repost") ? "share"
            : "score";   // points, votes
        if (!seen.TryGetValue(kind, out var list)) seen[kind] = list = new List<long>();
        if (list.Count < MaxMatchesPerKind) list.Add(n.Value);
    }

    /// Median of structured comment-count attributes (bounded scan). Zero is a real value and is kept.
    private static long? StructuredMedian(string html, Regex attribute, string group = "n")
    {
        var sample = html.Length > ScanChars ? html[..ScanChars] : html;
        var values = new List<long>();
        try
        {
            foreach (Match m in attribute.Matches(sample))
            {
                if (values.Count >= MaxMatchesPerKind) break;
                if (ParseCount(m.Groups[group].Value) is { } n) values.Add(n);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Bounded scan: keep whatever matched before the timeout.
        }
        if (values.Count == 0) return null;
        values.Sort();
        return values[(values.Count - 1) / 2];
    }

    private static long? Median(Dictionary<string, List<long>> seen, string kind)
    {
        if (!seen.TryGetValue(kind, out var list) || list.Count == 0) return null;
        list.Sort();
        return list[(list.Count - 1) / 2];
    }

    /// Bigger, richer, more linked and more discussed pages score higher. Roughly 0..300.
    public static long Estimate(string html, string text)
    {
        var span = html.AsSpan(0, Math.Min(html.Length, ScanChars));
        var links = Count(span, "<a ");
        var images = Count(span, "<img");
        // Visible text only: "comment" in CSS classes / scripts (e.g. comment-form boilerplate) is not discussion.
        var visible = text.AsSpan(0, Math.Min(text.Length, ScanChars));
        var discussion = Count(visible, "comment") + Count(visible, "repl");
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
