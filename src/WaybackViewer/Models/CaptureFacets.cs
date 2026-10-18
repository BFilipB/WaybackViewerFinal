namespace WaybackViewer.Models;

public sealed record FacetCount(string Value, int Count, int Saved = 0);

public sealed record CaptureFilter(string? Year = null, string? Category = null, string? Status = null, string? Section = null, string? Text = null)
{
    public bool IsActive => Year is not null || Category is not null || Status is not null || Section is not null || Text is not null;
}

public sealed record CaptureFacets(
    IReadOnlyList<FacetCount> Years,
    IReadOnlyList<FacetCount> Categories,
    IReadOnlyList<FacetCount> Statuses,
    IReadOnlyList<FacetCount> Sections)
{
    private static readonly string[] CategoryOrder = ["Pages", "Images", "Code & styles", "Documents", "Other"];

    public static string YearOf(Capture c) => c.Timestamp.Length >= 4 ? c.Timestamp[..4] : "?";

    public static string CategoryOf(Capture c)
    {
        var m = c.MimeType.ToLowerInvariant();
        if (m.Contains("html")) return "Pages";
        if (m.StartsWith("image/")) return "Images";
        if (m.Contains("css") || m.Contains("javascript") || m.Contains("ecmascript")) return "Code & styles";
        if (m.Contains("pdf") || m.Contains("word") || m.Contains("office") || m.StartsWith("text/")) return "Documents";
        return "Other";
    }

    public static string SectionOf(Capture c)
    {
        if (!Uri.TryCreate(c.Original, UriKind.Absolute, out var uri)) return "/";
        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null ? "/" : "/" + first;
    }

    public static CaptureFacets Build(IReadOnlyList<StoredCapture> captures)
    {
        var years = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var savedByYear = new Dictionary<string, int>(StringComparer.Ordinal);
        var categories = new Dictionary<string, int>();
        var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sections = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var s in captures)
        {
            var c = s.Capture;
            Bump(years, YearOf(c));
            if (s.Ready) Bump(savedByYear, YearOf(c));
            Bump(categories, CategoryOf(c));
            Bump(statuses, string.IsNullOrWhiteSpace(c.StatusCode) ? "-" : c.StatusCode);
            Bump(sections, SectionOf(c));
        }

        return new CaptureFacets(
            years.Select(p => new FacetCount(p.Key, p.Value, savedByYear.GetValueOrDefault(p.Key))).ToList(),
            CategoryOrder.Where(categories.ContainsKey).Select(k => new FacetCount(k, categories[k])).ToList(),
            statuses.Select(p => new FacetCount(p.Key, p.Value)).ToList(),
            sections.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12)
                .Select(p => new FacetCount(p.Key, p.Value)).ToList());
    }

    public static List<StoredCapture> Apply(IReadOnlyList<StoredCapture> captures, CaptureFilter f) =>
        captures
            .Where(s => f.Year is null || YearOf(s.Capture) == f.Year)
            .Where(s => f.Category is null || CategoryOf(s.Capture) == f.Category)
            .Where(s => f.Status is null || s.Capture.StatusCode == f.Status)
            .Where(s => f.Section is null || SectionOf(s.Capture) == f.Section)
            .Where(s => f.Text is null || s.Capture.Original.Contains(f.Text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(s => s.Id)
            .ToList();

    private static void Bump<TDict>(TDict d, string key) where TDict : IDictionary<string, int> =>
        d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
}
