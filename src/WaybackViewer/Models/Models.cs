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
    public long EffectiveEngagementScore =>
        EngagementScore ?? (Score ?? 0) + (Views ?? 0) + (Likes ?? 0) * 3 + (Comments ?? 0) * 4 + (Shares ?? 0) * 2;
}

public sealed record ParsedPage(string? Title, string? Description, string Text, PageMetadata? Metadata = null);

/// A freshly downloaded page, ready to be saved (HTML already gzip-compressed).
public sealed record NewPage(Capture Capture, byte[] HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record StoredPage(Capture Capture, byte[]? HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record PageText(string? Title, string? Description, string Text, string? Error = null);

public sealed record JobProgress(string Prefix, string Filter, OperationState State, int Queued, int InFlight, int Completed, int Total);

public sealed record PreloadStatus(int Total, int Completed, int Failed, bool Running, double PerSecond = 0,
    int Queued = 0, int InFlight = 0, int ActiveWorkers = 0, double? EtaSeconds = null,
    IReadOnlyList<JobProgress>? Jobs = null);

public enum OperationKind
{
    CaptureLoad,
    WaybackRefresh,
    PagePreload
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
    Failed
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
    public bool IsActive => State is OperationState.Queued or OperationState.Running or OperationState.Paused or OperationState.Stopping;
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
    IReadOnlyList<WorkflowEvent> Timeline);

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
    bool DownloadAll = false)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCaptures / (double)PageSize));
    public bool HasPopularityFilter => !string.Equals(PopularityMetric, "none", StringComparison.OrdinalIgnoreCase) && (MinPopularity ?? 0) > 0;
}
