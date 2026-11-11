using System.Collections.Concurrent;
using System.Text;

namespace WaybackViewer.Parsing;

/// What a captured URL is and which "thing" it belongs to.
/// Kind     - Thread, Comment, Listing, Profile, Wiki or Page.
/// GroupKey - identical for every URL that shows the same thread/post/page (comment permalinks, ?sort=, old.reddit,
///            .json, trailing slash, http/https, www ... all collapse into one key).
/// Section = first path segment ("/r", "/news" ...), kept in the same cached object so facets do not parse the URL twice.
public sealed record UrlInfo(string Kind, string GroupKey, string Section = "/");

public static class UrlClassifier
{
    public const string Thread = "Thread";
    public const string Comment = "Comment";
    public const string Listing = "Listing";
    public const string Profile = "Profile";
    public const string Wiki = "Wiki";
    public const string Page = "Page";

    public static readonly string[] KindOrder = [Thread, Comment, Listing, Profile, Wiki, Page];

    /// Friendly names for the UI (the short value is what goes into the URL).
    public static string Label(string kind) => kind switch
    {
        Thread => "Threads / posts",
        Comment => "Comment links",
        Listing => "Listings & feeds",
        Profile => "Profiles",
        Wiki => "Wiki",
        _ => "Other pages"
    };

    /// Case-insensitive match against the known kinds; null for anything else.
    public static string? NormalizeKind(string? value) =>
        KindOrder.FirstOrDefault(k => string.Equals(k, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static readonly ConcurrentDictionary<string, UrlInfo> Cache = new(StringComparer.Ordinal);

    private static int cacheSize;
    /// Upper bound of cached classifications; the cache is cleared when it is reached.
    internal const int MaxCacheEntries = 200_000;

    public static UrlInfo Classify(string original)
    {
        original ??= "";
        if (Cache.TryGetValue(original, out var hit)) return hit;
        var info = Compute(original);
        if (Interlocked.Increment(ref cacheSize) > MaxCacheEntries)
        {
            Cache.Clear();
            Interlocked.Exchange(ref cacheSize, 0);
        }
        Cache[original] = info;
        return info;
    }

    /// Classifies many URLs at once on all cores, so the first filter click after a restart is not slow.
    public static void Warm(IReadOnlyList<string> originals)
    {
        if (originals.Count < 2_000) { foreach (var o in originals) Classify(o); return; }
        Parallel.For(0, originals.Count, i => Classify(originals[i]));
    }

    private static UrlInfo Compute(string original)
    {
        var text = original.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return new UrlInfo(Page, "raw:" + text.ToLowerInvariant(), "/");
        var section = SectionOf(uri);
        var info = ComputeKind(text, uri);
        return info.Section == section ? info : info with { Section = section };
    }

    private static string SectionOf(Uri uri)
    {
        var path = uri.AbsolutePath;
        var start = 0;
        while (start < path.Length && path[start] == '/') start++;
        if (start >= path.Length) return "/";
        var end = path.IndexOf('/', start);
        return "/" + (end < 0 ? path[start..] : path[start..end]);
    }

    private static UrlInfo ComputeKind(string text, Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new UrlInfo(Page, "raw:" + text.ToLowerInvariant());

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var reddit = IsHost(host, "reddit.com") || host == "redd.it";
        var segments = Segments(uri.AbsolutePath, reddit);

        if (IsHost(host, "reddit.com")) return Reddit(segments);
        if (host == "redd.it") return segments.Length >= 1 && IsId(segments[0])
            ? new UrlInfo(Thread, "reddit:t:" + segments[0]) : Generic(host, uri);
        if (LooksLikeMicroblogHost(host)) return SocialHost(host, uri, segments);
        if (host == "news.ycombinator.com") return HackerNews(host, uri, segments);
        if (IsHost(host, "youtube.com") || host == "youtu.be") return YouTube(host, uri, segments);

        return Generic(host, uri);
    }

    // ---- Reddit ------------------------------------------------------------------------------------------

    private static UrlInfo Reddit(string[] seg)
    {
        // .../comments/{id}[/{slug}[/{commentId}]]   (also .json / .compact / .mobile / .embed suffixes)
        var c = Array.IndexOf(seg, "comments");
        if (c >= 0 && c <= 3 && c + 1 < seg.Length && IsId(seg[c + 1]))
        {
            var id = seg[c + 1];
            var isComment = c + 3 < seg.Length && IsId(seg[c + 3]);
            return new UrlInfo(isComment ? Comment : Thread, "reddit:t:" + id);
        }

        if (seg.Length >= 2 && seg[0] is "r" or "subreddit")
        {
            var sub = seg[1];
            if (seg.Length == 2) return new UrlInfo(Listing, "reddit:l:" + sub);
            if (seg[2] == "wiki") return new UrlInfo(Wiki, "reddit:w:" + string.Join('/', seg.Skip(1)));
            return new UrlInfo(Listing, "reddit:l:" + string.Join('/', seg.Skip(1).Take(2)));
        }

        if (seg.Length >= 2 && seg[0] is "user" or "u")
            return new UrlInfo(Profile, "reddit:u:" + seg[1]);

        return new UrlInfo(Listing, "reddit:p:/" + string.Join('/', seg));
    }

    // ---- X / Twitter -------------------------------------------------------------------------------------

    private static readonly HashSet<string> TwitterReserved = new(StringComparer.Ordinal)
        { "i", "home", "search", "explore", "hashtag", "intent", "share", "login", "signup", "messages", "settings",
          "notifications", "compose", "tos", "privacy", "about" };

    private static bool LooksLikeMicroblogHost(string host) =>
        host == "x.com" || host == "mobile.x.com" || host.EndsWith(".x.com", StringComparison.Ordinal);

    private static UrlInfo SocialHost(string host, Uri uri, string[] seg)
    {
        for (var i = 0; i + 1 < seg.Length; i++)
            if ((seg[i] is "status" or "statuses") && seg[i + 1].Length > 5 && seg[i + 1].All(char.IsAsciiDigit))
                return new UrlInfo(Thread, "x:" + seg[i + 1]);

        if (seg.Length >= 1 && !TwitterReserved.Contains(seg[0]) && seg[0].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            return new UrlInfo(Profile, "x:u:" + seg[0]);

        return Generic(host, uri);
    }

    // ---- Hacker News / YouTube ---------------------------------------------------------------------------

    private static UrlInfo HackerNews(string host, Uri uri, string[] seg)
    {
        var id = QueryValue(uri.Query, "id");
        if (seg.Length == 1 && seg[0] == "item" && id is not null) return new UrlInfo(Thread, "hn:" + id);
        if (seg.Length == 1 && seg[0] == "user" && id is not null) return new UrlInfo(Profile, "hn:u:" + id.ToLowerInvariant());
        return Generic(host, uri);
    }

    private static UrlInfo YouTube(string host, Uri uri, string[] seg)
    {
        // video ids are case-sensitive, so read them from a segment list that keeps the original case
        var raw = Segments(uri.AbsolutePath, false, lower: false);
        string? id = null;
        if (host == "youtu.be") id = raw.Length >= 1 ? raw[0] : null;
        else if (seg.Length == 1 && seg[0] == "watch") id = QueryValue(uri.Query, "v");
        else if (seg.Length == 2 && seg[0] is "shorts" or "embed" or "live") id = raw[1];
        return id is { Length: >= 6 and <= 16 } ? new UrlInfo(Thread, "yt:" + id) : Generic(host, uri);
    }

    // ---- everything else ---------------------------------------------------------------------------------

    private static readonly string[] IndexNames = ["index.html", "index.htm", "index.php", "index.asp", "index.aspx", "default.aspx"];

    private static UrlInfo Generic(string host, Uri uri)
    {
        var path = string.Join('/', Segments(uri.AbsolutePath, false));
        foreach (var index in IndexNames)
            if (path == index) { path = ""; break; }
            else if (path.EndsWith("/" + index, StringComparison.Ordinal)) { path = path[..^(index.Length + 1)]; break; }

        var query = CleanQuery(uri.Query);
        var key = host + "/" + path + (query.Length > 0 ? "?" + query : "");
        return new UrlInfo(Page, key);
    }

    private static string CleanQuery(string query)
    {
        if (query.Length <= 1) return "";
        var kept = new List<string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Split('=', 2)[0].ToLowerInvariant();
            if (name.StartsWith("utm_", StringComparison.Ordinal) ||
                name is "fbclid" or "gclid" or "mc_cid" or "mc_eid" or "igshid" or "ref_src" or "ref_url" or "ref")
                continue;
            kept.Add(part.ToLowerInvariant());
        }
        kept.Sort(StringComparer.Ordinal);
        return string.Join('&', kept);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static bool IsHost(string host, string root) => host == root || host.EndsWith("." + root, StringComparison.Ordinal);

    private static bool IsId(string s)
    {
        if (s.Length is < 2 or > 12) return false;
        foreach (var ch in s) if (!char.IsAsciiLetterOrDigit(ch)) return false;
        return true;
    }

    private static readonly string[] StrippedSuffixes = [".json", ".compact", ".mobile", ".embed", ".rss", ".xml"];

    private static string[] Segments(string path, bool stripSuffixes, bool lower = true)
    {
        var list = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Contains('%') ? Decode(raw) : raw;
            if (lower) s = s.ToLowerInvariant();   // returns the same instance when nothing changes
            if (stripSuffixes)
                foreach (var ext in StrippedSuffixes)
                    if (s.EndsWith(ext, StringComparison.Ordinal)) { s = s[..^ext.Length]; break; }
            if (s.Length > 0) list.Add(s);
        }
        return list.ToArray();
    }

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], name, StringComparison.OrdinalIgnoreCase) && kv[1].Length > 0)
                return Decode(kv[1]);
        }
        return null;
    }
}
