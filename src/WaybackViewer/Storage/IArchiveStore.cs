using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Persistent store for capture lists and downloaded pages.
public interface IArchiveStore
{
    /// Creates the database / tables when they do not exist. Safe to call repeatedly.
    Task InitializeAsync(CancellationToken ct = default);

    /// The saved capture list for a search, newest capture first, or null when never saved.
    Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default);

    /// Saves (or replaces) the capture list for a search and returns it. Captures and pages already stored are kept.
    Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> captures,
        DateTimeOffset cachedAt, CancellationToken ct = default);

    /// One saved page, or null. includeHtml=false skips the (large) HTML column.
    Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default);

    /// Inserts or replaces pages in a single round trip. Unknown captures are created on the fly.
    Task SavePagesAsync(IReadOnlyList<NewPage> pages, CancellationToken ct = default);
}
