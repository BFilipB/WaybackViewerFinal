namespace WaybackViewer;

public sealed class ArchiveOptions
{
    /// Maximum simultaneous requests to web.archive.org.
    public int MaxConcurrency { get; set; } = 8;
    /// Starting (and minimum) gap between request starts. Adapts upwards after HTTP 429.
    public int MinIntervalMs { get; set; } = 100;
    public int MaxIntervalMs { get; set; } = 10_000;
    public int MaxAttempts { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 180;
}

public sealed class PreloadOptions
{
    public int Workers { get; set; } = 8;
    public int WriteBatchSize { get; set; } = 50;
    public int WriteMaxDelayMs { get; set; } = 300;
    public int MaxHtmlChars { get; set; } = 5_000_000;
}

public sealed class StorageOptions
{
    /// "Sqlite" (default, one local file) or "Memory" (volatile, for demos and tests).
    public string Provider { get; set; } = "Sqlite";
    /// A saved capture list older than this is re-fetched. 0 = never expires.
    public int SearchMaxAgeDays { get; set; } = 30;
    /// Optional root folder for archived compressed HTML payloads. When empty, a provider-specific default is used.
    public string? HtmlRoot { get; set; }
}
