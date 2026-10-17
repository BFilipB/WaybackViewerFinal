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

public sealed record ParsedPage(string? Title, string? Description, string Text);

/// A freshly downloaded page, ready to be saved (HTML already gzip-compressed).
public sealed record NewPage(Capture Capture, byte[] HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record StoredPage(Capture Capture, byte[]? HtmlGz, ParsedPage Parsed, DateTimeOffset FetchedAt);

public sealed record PageText(string? Title, string? Description, string Text, string? Error = null);

public sealed record PreloadStatus(int Total, int Completed, int Failed, bool Running, double PerSecond = 0);

public sealed record CatalogResult(SearchSnapshot Snapshot, bool FromDatabase);

public sealed record CaptureListViewModel(
    string Url,
    string Filter,
    IReadOnlyList<StoredCapture> Captures,
    string? Error,
    bool FromDatabase = false,
    DateTimeOffset? CachedAt = null);
