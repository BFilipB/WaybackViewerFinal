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
    /// "view" (default): download only the captures in the list you are looking at (filters, one-per-thread ...).
    /// "all": download every HTML capture of the search in the background.
    public string Scope { get; set; } = "view";
    public int Workers { get; set; } = 8;
    public int WriteBatchSize { get; set; } = 50;
    public int WriteMaxDelayMs { get; set; } = 300;
    public int MaxHtmlChars { get; set; } = 5_000_000;
    /// Downloads failing with a temporary error (429, 5xx, timeouts, network) are retried up to this many times.
    public int MaxAttempts { get; set; } = 8;
    /// First retry delay; doubles per attempt up to RetryMaxDelayMs. Retry-After from the archive wins.
    public int RetryBaseDelayMs { get; set; } = 2_000;
    public int RetryMaxDelayMs { get; set; } = 60_000;
    /// A database write of preload results is tried this many times before the job is marked Failed.
    /// The pages stay without an outcome, so they are downloaded again on resume/restart. 0 = retry forever.
    public int SaveMaxAttempts { get; set; } = 20;
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
