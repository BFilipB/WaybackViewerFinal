using System.Runtime.InteropServices;
using WaybackViewer.Parsing;

namespace WaybackViewer.Models;

public sealed record FacetCount(string Value, int Count, int Saved = 0);

/// Kind: Thread / Comment / Listing / Profile / Wiki / Page (see UrlClassifier).
/// Unique: keep only one capture per thread/post/page (comment permalinks, ?sort= variants, old.reddit ... collapse).
public sealed record CaptureFilter(string? Year = null, string? Category = null, string? Status = null, string? Section = null,
    string? Text = null, string? Kind = null, bool Unique = false)
{
    public bool IsActive => Year is not null || Category is not null || Status is not null || Section is not null
                            || Text is not null || Kind is not null || Unique;
}

public sealed record CaptureFacets(
    IReadOnlyList<FacetCount> Years,
    IReadOnlyList<FacetCount> Categories,
    IReadOnlyList<FacetCount> Statuses,
    IReadOnlyList<FacetCount> Sections,
    IReadOnlyList<FacetCount>? Kinds = null,
    int UniqueCount = 0)
{
    private static readonly string[] CategoryOrder = ["Pages", "Images", "Code & styles", "Documents", "Other"];

    // 4-digit years are shared strings, so counting 100k captures allocates nothing per capture.
    private static readonly string[] YearStrings = Enumerable.Range(0, 10_000).Select(i => i.ToString("D4")).ToArray();

    public static string YearOf(Capture c)
    {
        var ts = c.Timestamp;
        if (ts.Length < 4) return "?";
        var a = ts[0] - '0'; var b = ts[1] - '0'; var d = ts[2] - '0'; var e = ts[3] - '0';
        if ((uint)a > 9 || (uint)b > 9 || (uint)d > 9 || (uint)e > 9) return ts[..4];
        return YearStrings[a * 1000 + b * 100 + d * 10 + e];
    }

    public static string CategoryOf(Capture c)
    {
        var m = c.MimeType;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (m.Contains("html", ic)) return "Pages";
        if (m.StartsWith("image/", ic)) return "Images";
        if (m.Contains("css", ic) || m.Contains("javascript", ic) || m.Contains("ecmascript", ic)) return "Code & styles";
        if (m.Contains("pdf", ic) || m.Contains("word", ic) || m.Contains("office", ic) || m.StartsWith("text/", ic)) return "Documents";
        return "Other";
    }

    public static string SectionOf(Capture c)
    {
        return UrlClassifier.Classify(c.Original).Section; // parsed once and cached with the kind / group key
    }

    public static CaptureFacets Build(IReadOnlyList<StoredCapture> captures)
    {
        var years = new Dictionary<string, int>(StringComparer.Ordinal);
        var savedByYear = new Dictionary<string, int>(StringComparer.Ordinal);
        var categories = new Dictionary<string, int>();
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        var sections = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in captures)
        {
            var c = s.Capture;
            var year = YearOf(c);
            Bump(years, year);
            if (s.Ready) Bump(savedByYear, year);
            Bump(categories, CategoryOf(c));
            Bump(statuses, string.IsNullOrWhiteSpace(c.StatusCode) ? "-" : c.StatusCode);
            var info = UrlClassifier.Classify(c.Original);
            Bump(sections, info.Section);
            Bump(kinds, info.Kind);
            groups.Add(info.GroupKey);
        }

        return new CaptureFacets(
            years.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new FacetCount(p.Key, p.Value, savedByYear.GetValueOrDefault(p.Key))).ToList(),
            CategoryOrder.Where(categories.ContainsKey).Select(k => new FacetCount(k, categories[k])).ToList(),
            statuses.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new FacetCount(p.Key, p.Value)).ToList(),
            sections.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12)
                .Select(p => new FacetCount(p.Key, p.Value)).ToList(),
            UrlClassifier.KindOrder.Where(kinds.ContainsKey).Select(k => new FacetCount(k, kinds[k])).ToList(),
            groups.Count);
    }

    /// Applies the facet filters, then (when Unique) keeps one capture per thread/post/page, newest first.
    public static List<StoredCapture> Apply(IReadOnlyList<StoredCapture> captures, CaptureFilter f)
    {
        IEnumerable<StoredCapture> result = Filtered(captures, f);
        if (f.Unique) result = OnePerGroup(result);
        return result
            .OrderByDescending(s => s.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(s => s.Id)
            .ToList();
    }

    private static IEnumerable<StoredCapture> Filtered(IReadOnlyList<StoredCapture> captures, CaptureFilter f) =>
        captures
            .Where(s => f.Year is null || YearOf(s.Capture) == f.Year)
            .Where(s => f.Category is null || CategoryOf(s.Capture) == f.Category)
            .Where(s => f.Status is null || s.Capture.StatusCode == f.Status)
            .Where(s => f.Section is null || SectionOf(s.Capture) == f.Section)
            .Where(s => f.Text is null || SearchTextMatcher.Matches(f.Text, s.Capture.Original))
            .Where(s => f.Kind is null || UrlClassifier.Classify(s.Capture.Original).Kind == f.Kind);

    /// How many captures each thread/post/page has under the same filters, ignoring the type filter and the
    /// one-per-thread reduction (so a thread shown on its own still reports its folded comment links).
    public static Dictionary<string, int> GroupSizes(IReadOnlyList<StoredCapture> captures, CaptureFilter f)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in Filtered(captures, f with { Unique = false, Kind = null }))
            Bump(sizes, UrlClassifier.Classify(s.Capture.Original).GroupKey);
        return sizes;
    }

    /// One capture per group. Preference: the main page (thread/post/page) over comment permalinks, a 200 over
    /// redirects/errors, HTML over other types, one that is already saved locally, then the newest capture.
    public static IEnumerable<StoredCapture> OnePerGroup(IEnumerable<StoredCapture> items)
    {
        var best = new Dictionary<string, (StoredCapture Item, (int, int, int, int) Rank)>(StringComparer.Ordinal);
        foreach (var s in items)
        {
            var info = UrlClassifier.Classify(s.Capture.Original);
            var rank = RankOf(s, info);
            if (!best.TryGetValue(info.GroupKey, out var cur) || IsBetter(s, rank, cur.Item, cur.Rank))
                best[info.GroupKey] = (s, rank);
        }
        return best.Values.Select(v => v.Item);
    }

    private static (int, int, int, int) RankOf(StoredCapture s, UrlInfo info)
    {
        var kind = info.Kind switch
        {
            UrlClassifier.Thread or UrlClassifier.Page => 0,
            UrlClassifier.Comment => 2,
            _ => 1
        };
        var status = s.Capture.StatusCode == "200" ? 0 : s.Capture.StatusCode.StartsWith('3') ? 1 : 2;
        return (kind, status, s.Capture.IsHtml ? 0 : 1, s.Ready ? 0 : 1);
    }

    private static bool IsBetter(StoredCapture a, (int, int, int, int) ra, StoredCapture b, (int, int, int, int) rb)
    {
        var cmp = ra.CompareTo(rb);
        if (cmp != 0) return cmp < 0;
        cmp = string.CompareOrdinal(a.Capture.Timestamp, b.Capture.Timestamp);
        return cmp != 0 ? cmp > 0 : a.Id > b.Id;
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        CollectionsMarshal.GetValueRefOrAddDefault(d, key, out _)++; // one hash lookup instead of two
}
