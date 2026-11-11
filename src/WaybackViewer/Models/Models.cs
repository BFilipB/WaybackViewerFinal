using System.Globalization;
using System.Text.Json.Serialization;

namespace WaybackViewer.Models;

public sealed record Capture(string Timestamp, string Original, string MimeType, string StatusCode)
{
    [JsonIgnore] public string Key => $"{Timestamp}|{Original}";
    [JsonIgnore] public string ArchiveUrl => ArchiveUrls.Viewer(Timestamp, Original);
    [JsonIgnore] public string FrameUrl => ArchiveUrls.Frame(Timestamp, Original);
    [JsonIgnore] public bool IsHtml => MimeType.Contains("html", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public DateTime Date =>
        DateTime.TryParseExact(Timestamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
}

/// A capture as stored in the database. Ready = its HTML is already saved locally.
public sealed record StoredCapture(long Id, Capture Capture, bool Ready);

public sealed record SearchSnapshot(long SearchId, DateTimeOffset CachedAt, IReadOnlyList<StoredCapture> Captures);

public sealed record PagedSearchResult(
    long SearchId,
    DateTimeOffset CachedAt,
    IReadOnlyList<StoredCapture> Captures,
    int TotalCount);

public sealed record PageMetadata(
    long? Views = null,
    long? Likes = null,
    long? Shares = null,
    long? Comments = null,
    long? Score = null,
    long? EngagementScore = null,
    string? Source = null,
    string? Author = null,
    DateTimeOffset? PublishedAt = null,
    string? Tags = null,
    double Confidence = 0)
{
    /// Ranking tier: 2 = measured counts (an explicit 0 included), 1 = content estimate, 0 = archive signal only. Mirrors FilteredQueries.
    public int Tier => Confidence >= 0.5 ? 2 : Confidence >= 0.15 ? 1 : 0;

    /// Log-scaled, weighted engagement (x10, integer): comments 4, likes/score 3, shares 2, views 1. Each signal is
    /// compressed with ln(1+n) so one huge count (views) cannot drown out discussion. Unknown signals add nothing.
    public static long Engagement(long? views, long? likes, long? comments, long? shares, long? score)
    {
        static double L(long? n) => n is > 0 ? Math.Log(1 + n.Value) : 0;
        return (long)Math.Round(10 * (L(views) + 3 * L(likes) + 4 * L(comments) + 2 * L(shares) + 3 * L(score)));
    }

    public long EffectiveEngagementScore =>
        EngagementScore ?? Engagement(Views, Likes, Comments, Shares, Score);
}

public sealed record ParsedPage(string? Title, string? Description, string Text, PageMetadata? Metadata = null);

/// A freshly downloaded page, ready to be saved (HTML already gzip-compressed).
public sealed record NewPage(Capture Capture, byte[] HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record StoredPage(Capture Capture, byte[]? HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record PageText(string? Title, string? Description, string Text, string? Error = null);

public sealed record JobProgress(string Prefix, string Filter, OperationState State, int Queued, int InFlight, int Completed, int Total,
    int Failed = 0, int Skipped = 0, double PerSecond = 0, double? EtaSeconds = null);

public sealed record PreloadStatus(int Total, int Completed, int Failed, bool Running, double PerSecond = 0,
    int Queued = 0, int InFlight = 0, int ActiveWorkers = 0, double? EtaSeconds = null,
    IReadOnlyList<JobProgress>? Jobs = null, int Skipped = 0);

/// Terminal preload result of a capture that was not saved. Saved captures are recognised by their stored page.
public enum PreloadOutcome
{
    Skipped = 1,
    Failed = 2
}

/// Why a capture is not (or could not be) preloaded. Reason: non-html, not-found, redirect, http-NNN, network, timeout, error.
public sealed record CaptureOutcome(Capture Capture, PreloadOutcome Outcome, string Reason, DateTimeOffset At);

/// Preload counters of a whole search, aggregated in SQL: HTML captures, unsaved ones, and the unsaved ones that
/// already have a Skipped / Failed outcome.
public sealed record PreloadCounts(int Html, int Unsaved, int Skipped, int Failed);

public enum OperationKind
{
    CaptureLoad,
    WaybackRefresh,
    PagePreload,
    /// Removes the preloaded pages of a searched URL (the capture list is kept).
    DeletePages,
    /// Removes everything stored for a searched URL.
    DeleteAll
}

public enum OperationState
{
    Queued,
    Running,
    Paused,
    Stopping,
    Stopped,
    Interrupted,
    Completed,
    Failed,
    /// Finished: every capture reached a final result, but some of them failed.
    CompletedWithFailures,
    /// Being removed (Phase 4). Terminal: no further transitions.
    Deleting
}

public sealed record BackgroundOperation(
    long Id,
    string Prefix,
    string Filter,
    OperationKind Kind,
    OperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Total,
    int Completed,
    int Failed,
    string? Error = null)
{
    public string Key => $"{Kind}|{Filter}|{Prefix}";
    public bool IsActive => OperationStates.IsActive(State);
}

public sealed record OperationStatus(
    IReadOnlyList<BackgroundOperation> Operations,
    int ActiveCount);

public sealed record WorkflowBar(
    string Label,
    string State,
    int Completed,
    int Total,
    double Percent,
    bool Active,
    string? Error = null);

public sealed record WorkflowEvent(
    string Kind,
    string State,
    DateTimeOffset UpdatedAt,
    int Completed,
    int Total,
    string? Error = null);

public sealed record WorkflowStatusView(
    string Message,
    WorkflowBar CaptureLoad,
    WorkflowBar Preload,
    IReadOnlyList<WorkflowEvent> Timeline,
    /// Other searched URLs that currently have unfinished operations (shown as a link to the Operations page).
    int OtherActiveUrls = 0);

/// One searched URL (prefix + capture filter).
public readonly record struct SearchKey(string Prefix, string Filter);

/// Operations page query: which searched URLs to list. Null Kinds/States = no filter.
public sealed record OperationListQuery(IReadOnlyList<OperationKind>? Kinds, IReadOnlyList<OperationState>? States,
    int Offset, int Limit, string? Search = null, OperationSort Sort = OperationSort.Updated, bool Descending = true);

public enum OperationSort { Updated, Url, State }

public sealed record OperationSearchPage(IReadOnlyList<SearchKey> Searches, int Total);

public sealed record PopularityPreset(int Percent, int Value);

public sealed record CatalogResult(SearchSnapshot Snapshot, bool FromDatabase, bool Refreshing = false);

public sealed record CaptureListViewModel(
    string Url,
    string Filter,
    IReadOnlyList<StoredCapture> Captures,
    string? Error,
    bool FromDatabase = false,
    DateTimeOffset? CachedAt = null,
    string PaginationMode = "server",
    int Page = 1,
    int PageSize = 50,
    int TotalCaptures = 0,
    CaptureFacets? Facets = null,
    CaptureFilter? Active = null,
    string SearchMode = "url",
    IReadOnlyDictionary<string, int>? GroupSizes = null,
    int HiddenNotSaved = 0,
    bool Refreshing = false,
    string SortMode = "newest",
    string PopularityMetric = "none",
    int? MinPopularity = null,
    IReadOnlyDictionary<string, PageMetadata>? Popularity = null,
    int PopularityExcluded = 0,
    int PopularityPending = 0,
    IReadOnlyList<PopularityPreset>? Presets = null,
    IReadOnlyList<int>? Histogram = null,
    bool DownloadAll = false,
    bool IncludeEstimates = false,
    int CoverageMeasured = 0,
    int CoverageTotal = 0,
    int MaxPopularity = 0,
    IReadOnlyDictionary<string, int>? MetricCoverage = null)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCaptures / (double)PageSize));
    public bool HasPopularityFilter => !string.Equals(PopularityMetric, "none", StringComparison.OrdinalIgnoreCase) && (MinPopularity ?? 0) > 0;
}
