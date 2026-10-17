using System.Net;
using System.Text;
using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

/// Single-pass HTML -> metadata + visible text. Written by hand (no regex backtracking) so
/// multi-megabyte pages are processed in linear time.
public static class PageParser
{
    public const int MaxTextChars = 60_000;
    public const int MaxTitleChars = 1_000;
    private const int MetaScanChars = 200_000;

    private static readonly HashSet<string> SkippedElements = new(StringComparer.Ordinal)
        { "script", "style", "noscript", "svg", "head", "template" };

    private static readonly HashSet<string> NewlineAfterClosing = new(StringComparer.Ordinal)
        { "p", "div", "li", "ul", "ol", "tr", "table", "blockquote", "section", "article", "pre",
          "h1", "h2", "h3", "h4", "h5", "h6", "header", "footer", "main", "figure" };

    public static ParsedPage Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return new ParsedPage(null, null, "");
        var (title, description) = ExtractMeta(html);
        return new ParsedPage(title, description, ExtractText(html));
    }

    private static (string? Title, string? Description) ExtractMeta(string html)
    {
        var limit = Math.Min(html.Length, MetaScanChars);
        string? ogTitle = null, ogDescription = null, metaDescription = null;

        var pos = 0;
        while (pos < limit)
        {
            var start = html.IndexOf("<meta", pos, limit - pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = html.IndexOf('>', start, limit - start);
            if (end < 0) break;
            pos = end + 1;

            var tag = html.Substring(start, end - start + 1);
            var key = (Attr(tag, "property") ?? Attr(tag, "name"))?.Trim().ToLowerInvariant();
            if (key is null) continue;
            var content = Attr(tag, "content");
            if (content is null) continue;

            switch (key)
            {
                case "og:title": ogTitle ??= content; break;
                case "og:description": ogDescription ??= content; break;
                case "description": metaDescription ??= content; break;
            }
        }

        string? titleTag = null;
        var t = html.IndexOf("<title", 0, limit, StringComparison.OrdinalIgnoreCase);
        if (t >= 0)
        {
            var open = html.IndexOf('>', t, limit - t);
            if (open >= 0)
            {
                var close = html.IndexOf("</title", open, limit - open, StringComparison.OrdinalIgnoreCase);
                if (close > open) titleTag = html.Substring(open + 1, close - open - 1);
            }
        }

        var title = Clean(ogTitle) ?? Clean(titleTag);
        if (title is { Length: > MaxTitleChars }) title = title[..MaxTitleChars];
        return (title, Clean(ogDescription) ?? Clean(metaDescription));
    }

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var v = WebUtility.HtmlDecode(value).Trim();
        return v.Length == 0 ? null : v;
    }

    private static string? Attr(string tag, string name)
    {
        var i = 0;
        while (true)
        {
            i = tag.IndexOf(name, i, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var before = i > 0 ? tag[i - 1] : ' ';
            var j = i + name.Length;
            i = j; // continue after this hit if it is not a real attribute
            if (!char.IsWhiteSpace(before) && before != '"' && before != '\'') continue;

            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (j >= tag.Length || tag[j] != '=') continue;
            j++;
            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (j >= tag.Length) return null;

            var quote = tag[j];
            if (quote is '"' or '\'')
            {
                var close = tag.IndexOf(quote, j + 1);
                return close < 0 ? null : tag.Substring(j + 1, close - j - 1);
            }

            var k = j;
            while (k < tag.Length && !char.IsWhiteSpace(tag[k]) && tag[k] != '>') k++;
            return tag.Substring(j, k - j);
        }
    }

    private static string ExtractText(string html)
    {
        var sb = new StringBuilder(Math.Min(html.Length, 64 * 1024));
        var n = html.Length;
        var i = 0;

        while (i < n && sb.Length < MaxTextChars * 3)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) { sb.Append(html, i, n - i); break; }
            if (lt > i) sb.Append(html, i, lt - i);

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var endComment = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = endComment < 0 ? n : endComment + 3;
                continue;
            }

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0) break; // dangling "<" at the end of a truncated page

            var p = lt + 1;
            if (p < gt && (html[p] == '!' || html[p] == '?')) { i = gt + 1; continue; } // doctype / processing instruction

            var closing = false;
            if (p < gt && html[p] == '/') { closing = true; p++; }
            var q = p;
            while (q < gt && char.IsAsciiLetterOrDigit(html[q])) q++;

            if (q == p) // not a tag: a literal "<" in text, e.g. "a < b"
            {
                sb.Append('<');
                i = lt + 1;
                continue;
            }

            var name = html.Substring(p, q - p).ToLowerInvariant();

            if (!closing && SkippedElements.Contains(name) && html[gt - 1] != '/')
            {
                var close = html.IndexOf("</" + name, gt + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                var closeEnd = html.IndexOf('>', close);
                i = closeEnd < 0 ? n : closeEnd + 1;
                continue;
            }

            if (closing)
            {
                if (NewlineAfterClosing.Contains(name)) sb.Append('\n');
                else if (name is "td" or "th") sb.Append(' ');
            }
            else if (name is "br" or "hr") sb.Append('\n');

            i = gt + 1;
        }

        return Collapse(WebUtility.HtmlDecode(sb.ToString()));
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxTextChars));
        var pendingSpace = false;
        var pendingNewline = false;

        foreach (var ch in s)
        {
            if (ch is '\n' or '\r') { pendingNewline = true; continue; }
            if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }

            if (sb.Length > 0)
            {
                if (pendingNewline) sb.Append('\n');
                else if (pendingSpace) sb.Append(' ');
            }
            pendingSpace = pendingNewline = false;
            sb.Append(ch);
            if (sb.Length >= MaxTextChars) break;
        }
        return sb.ToString();
    }
}
