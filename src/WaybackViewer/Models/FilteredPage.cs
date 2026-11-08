namespace WaybackViewer.Models;

/// One page of a capture list with the facet filters applied in SQL, plus what the list view needs around it.
/// Only the rows of this page are ever materialized.
public sealed record FilteredPage(
    long SearchId,
    DateTimeOffset CachedAt,
    int Page,
    int Total,
    IReadOnlyList<StoredCapture> Captures,
    CaptureFacets Facets,
    IReadOnlyDictionary<string, int>? GroupSizes,
    int HtmlTotal,
    int UnsavedHtmlTotal,
    int UnsavedHtmlInView,
    IReadOnlyDictionary<string, PageMetadata>? Popularity = null,
    int PopularityExcluded = 0,
    IReadOnlyList<PopularityPreset>? Presets = null,
    IReadOnlyList<int>? Histogram = null,
    int PopularityPending = 0);

/// What is known about a capture list without loading it (freshness and origin).
public sealed record CatalogState(DateTimeOffset CachedAt, bool FromDatabase, bool Refreshing);

/// Popularity ranking requested for a SQL page: sort mode, threshold metric and minimum value.
public sealed record PopularityQuery(string Sort, string Metric, int Threshold);
