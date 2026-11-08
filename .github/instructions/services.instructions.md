---
applyTo: "src/WaybackViewer/Services/**"
---
# Services rules
- All archive HTTP goes through `ArchiveClient`; all URLs through `ArchiveUrls`.
- Pass `CancellationToken` to `HttpClient`, delays and semaphore waits.
- Background work is modeled as persisted operations (queued/running/paused/interrupted/completed/failed) and must resume on startup.
- CaptureLoad and Preload are independent; one must never block the other.
- Failed captures must be retried with backoff for transient errors and surfaced (counts) to the workflow endpoint, never silently dropped.
- Shared state: prefer `ConcurrentDictionary`/channels over one global lock; bound every collection.
- Use injected `TimeProvider` for time so tests stay deterministic.
