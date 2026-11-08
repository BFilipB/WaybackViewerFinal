namespace WaybackViewer.Models;

/// One searched URL with its maintained summary (capture count, first/last capture, preload progress, last refresh).
public sealed record UrlSummary(long Id, string Prefix, string Filter, int CaptureCount, string? FirstTimestamp,
    string? LastTimestamp, int Preloaded, DateTimeOffset LastRefresh)
{
    public int PreloadPercent => CaptureCount <= 0 ? 0 : (int)Math.Min(100, Preloaded * 100L / CaptureCount);
}

/// Keyset page request: AfterKey/AfterId are the sort key and id of the last row of the previous page (null = first page).
public sealed record UrlListQuery(string? Search, string Sort, string? AfterKey, long AfterId, int Limit);

public sealed record UrlListPage(IReadOnlyList<UrlSummary> Items, bool HasMore);

public sealed record UrlSuggestion(string Prefix, int Captures);

/// Optional filters for the captures of one searched URL. Timestamps are 14-digit Wayback timestamps (inclusive range).
public sealed record UrlCaptureFilter(string? Mime, string? Status, string? FromTimestamp, string? ToTimestamp,
    string Sort = CaptureSorts.Date, bool Descending = true);

/// Whitelisted sort columns for the captures of one searched URL (never interpolate user input into SQL).
public static class CaptureSorts
{
    public const string Date = "date";
    public const string Url = "url";
    public const string Type = "type";
    public const string Status = "status";

    public static string Normalize(string? sort) => sort is Url or Type or Status ? sort : Date;

    public static string Column(string sort) => sort switch
    {
        Url => "c.Original",
        Type => "c.MimeType",
        Status => "c.StatusCode",
        _ => "c.CaptureTs"
    };
}

/// Distinct MIME types and status codes present in one searched URL's captures (bounded; feeds the filter dropdowns).
public sealed record UrlCaptureOptions(IReadOnlyList<string> Mimes, IReadOnlyList<string> Statuses);

public sealed record CaptureRowsPage(IReadOnlyList<StoredCapture> Items, int Total);

public static class UrlSorts
{
    public const string Captures = "captures";
    public const string Recent = "recent";
    public const string Alpha = "az";

    public static string Normalize(string? sort) =>
        sort is Recent or Alpha ? sort : Captures;

    /// Value of the sort column that the next page continues after.
    public static string CursorKey(string sort, UrlSummary item) => sort switch
    {
        Recent => item.LastRefresh.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        Alpha => item.Prefix,
        _ => item.CaptureCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}

public sealed record UrlListViewModel(IReadOnlyList<UrlSummary> Items, string? Search, string Sort, string? NextKey,
    long NextId, bool HasMore, bool IsFirstPage);

public sealed record UrlDetailViewModel(UrlSummary Summary, IReadOnlyList<StoredCapture> Captures, int Total, int Page,
    int PageSize, string? Mime, string? Status, string? From, string? To, IReadOnlyList<BackgroundOperation> Operations,
    string Sort = CaptureSorts.Date, bool Desc = true, UrlCaptureOptions? Options = null)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}
