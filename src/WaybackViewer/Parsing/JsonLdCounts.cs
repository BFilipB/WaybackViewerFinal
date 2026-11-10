using System.Globalization;
using System.Text.Json;

namespace WaybackViewer.Parsing;

/// Reads the page's own counts from schema.org JSON-LD (DiscussionForumPosting, Article, VideoObject, ...).
/// Only the item itself (or the items of an @graph) is read: nested "comment" arrays and "isPartOf" are never
/// walked, because their counts belong to replies and to the parent forum, not to this page.
/// See docs/popularity/signals.md.
internal static class JsonLdCounts
{
    private const int ScanChars = 500_000;
    private const int MaxBlockChars = 200_000;
    private const int MaxBlocks = 8;
    private const int MaxItems = 32;
    private const long MaxCount = 10_000_000_000;

    internal sealed class Counts
    {
        public long? Views, Likes, Shares, Comments;
        public DateTimeOffset? Published;
        public bool Any => Views.HasValue || Likes.HasValue || Shares.HasValue || Comments.HasValue;
    }

    public static Counts Read(string html)
    {
        var result = new Counts();
        var limit = Math.Min(html.Length, ScanChars);
        var pos = 0;
        var blocks = 0;
        while (pos < limit && blocks < MaxBlocks)
        {
            var start = html.IndexOf("<script", pos, limit - pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var tagEnd = html.IndexOf('>', start, limit - start);
            if (tagEnd < 0) break;
            pos = tagEnd + 1;
            if (html.AsSpan(start, tagEnd - start + 1).IndexOf("ld+json", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var close = html.IndexOf("</script", pos, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;
            var length = close - pos;
            var bodyStart = pos;
            pos = close;
            if (length <= 0 || length > MaxBlockChars) continue;
            blocks++;

            try
            {
                using var doc = JsonDocument.Parse(html.AsMemory(bodyStart, length),
                    new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true });
                var seen = 0;
                Visit(doc.RootElement, result, ref seen);
            }
            catch (JsonException)
            {
                // Malformed JSON-LD is common on archived pages; skip this block and keep what other blocks gave.
            }
        }
        return result;
    }

    private static void Visit(JsonElement element, Counts result, ref int seen)
    {
        if (seen >= MaxItems) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (seen >= MaxItems) return;
                Visit(item, result, ref seen);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        seen++;
        if (element.TryGetProperty("@graph", out var graph)) Visit(graph, result, ref seen);

        result.Comments ??= Number(element, "commentCount");
        result.Published ??= Date(element, "datePublished") ?? Date(element, "uploadDate") ?? Date(element, "dateCreated");
        result.Likes ??= Number(element, "upvoteCount") ?? Number(element, "likeCount");
        result.Views ??= Number(element, "viewCount") ?? Number(element, "interactionCount");
        result.Shares ??= Number(element, "shareCount");

        if (element.TryGetProperty("interactionStatistic", out var stats)) ReadStatistics(stats, result);
    }

    private static void ReadStatistics(JsonElement stats, Counts result)
    {
        if (stats.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var item in stats.EnumerateArray())
            {
                if (++n > MaxItems) return;
                ReadStatistics(item, result);
            }
            return;
        }
        if (stats.ValueKind != JsonValueKind.Object) return;
        if (!stats.TryGetProperty("interactionType", out var type)) return;

        var name = type.ValueKind == JsonValueKind.Object && type.TryGetProperty("@type", out var t) ? t.ToString()
                 : type.ValueKind == JsonValueKind.String ? type.GetString() ?? "" : "";
        var count = Number(stats, "userInteractionCount");
        if (count is null || name.Length == 0) return;

        if (name.Contains("Dislike", StringComparison.OrdinalIgnoreCase)) return;
        if (name.Contains("Like", StringComparison.OrdinalIgnoreCase)) result.Likes ??= count;
        else if (name.Contains("Comment", StringComparison.OrdinalIgnoreCase) || name.Contains("Reply", StringComparison.OrdinalIgnoreCase)) result.Comments ??= count;
        else if (name.Contains("Watch", StringComparison.OrdinalIgnoreCase) || name.Contains("View", StringComparison.OrdinalIgnoreCase)) result.Views ??= count;
        else if (name.Contains("Share", StringComparison.OrdinalIgnoreCase)) result.Shares ??= count;
    }

    private static DateTimeOffset? Date(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    /// Zero is a real value and is kept.
    private static long? Number(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var v)) return null;
        long n;
        if (v.ValueKind == JsonValueKind.Number) { if (!v.TryGetInt64(out n)) return null; }
        else if (v.ValueKind == JsonValueKind.String) { if (!long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null; }
        else return null;
        return n is < 0 or > MaxCount ? null : n;
    }
}
