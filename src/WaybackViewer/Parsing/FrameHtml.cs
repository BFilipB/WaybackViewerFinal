using System.Net;
using System.Text.RegularExpressions;

namespace WaybackViewer.Parsing;

public static partial class FrameHtml
{
    // (?=[\s>/]) makes sure "<header" is not mistaken for "<head".
    [GeneratedRegex(@"<head(?=[\s>/])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<html(?=[\s>/])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();

    /// Inserts a &lt;base&gt; tag so relative links/images resolve against the Wayback Machine.
    public static string InjectBase(string html, string baseUrl)
    {
        var tag = $"<base href=\"{WebUtility.HtmlEncode(baseUrl)}\">";

        var head = HeadOpen().Match(html);
        if (head.Success) return html.Insert(head.Index + head.Length, tag);

        var root = HtmlOpen().Match(html);
        if (root.Success) return html.Insert(root.Index + root.Length, "<head>" + tag + "</head>");

        return tag + html;
    }

    /// True when a download decoded into binary garbage (e.g. an image or a gzip body served without headers).
    public static bool LooksBinary(string content)
    {
        if (content.Length == 0) return false;
        var sample = Math.Min(content.Length, 2000);
        var bad = 0;
        for (var i = 0; i < sample; i++)
        {
            var c = content[i];
            if (c == '\0' || c == '\uFFFD' || (c < ' ' && c is not ('\t' or '\n' or '\r'))) bad++;
        }
        return bad * 50 > sample; // more than 2 %
    }
}
