namespace WaybackViewer.Models;

/// How much of a searched URL to delete.
public enum DeleteLevel
{
    /// The preloaded pages (rows, outcomes and HTML files). The capture list stays.
    Pages,
    /// Everything: capture list, pages, HTML files, summary and operations.
    All
}

/// What a delete would remove. SharedCaptures stay because another searched URL still lists them.
public sealed record DeletePreview(int Captures, int Pages, int SharedCaptures, long Bytes);

public enum DeleteStartResult
{
    Started,
    /// A delete of this searched URL is already running.
    AlreadyDeleting,
    /// A capture-list load is still running; pause it and wait for it to stop first.
    Busy
}

/// Body of the Operations page delete endpoints: one searched URL and, for the delete itself, the level.
public sealed record DeleteRequest(OperationKeyInput? Key, string? Level);
