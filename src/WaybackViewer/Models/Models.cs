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

public sealed record ParsedPage(string? Title, string? Description, string Text);

/// A freshly downloaded page, ready to be saved (HTML already gzip-compressed).
public sealed record NewPage(Capture Capture, byte[] HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record StoredPage(Capture Capture, byte[]? HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record PageText(string? Title, string? Description, string Text, string? Error = null);

public sealed record PreloadStatus(int Total, int Completed, int Failed, bool Running, double PerSecond = 0);

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

public sealed record CatalogResult(SearchSnapshot Snapshot, bool FromDatabase);

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
    int TotalCaptures = 0)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCaptures / (double)PageSize));
}
