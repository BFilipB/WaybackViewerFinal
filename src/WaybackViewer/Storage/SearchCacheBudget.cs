namespace WaybackViewer.Storage;

/// Memory budget of the in-memory full-list caches. Lists beyond these sizes are still fully usable through the
/// SQL-paged path; they are only not kept (or pre-warmed) in RAM.
public static class SearchCacheBudget
{
    /// Total captures kept across all cached search lists (~150 bytes each: ~75 MB).
    public const long MaxCachedCaptures = 500_000;
    /// Lists larger than this are not pre-loaded by the warmup service.
    public const int MaxWarmCaptures = 250_000;
    /// Lists larger than this do not pre-classify every URL on load (the classifier cache is bounded).
    public const int MaxWarmUrls = 150_000;
}
