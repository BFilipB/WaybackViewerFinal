---
mode: agent
description: WaybackViewer master implementation plan (run one phase per session)
---
You are implementing the agreed WaybackViewer roadmap. Follow `.github/copilot-instructions.md`
and all `.github/instructions/*.instructions.md` strictly.

## How to work
- Implement ONLY the phase I name: ${input:phase:Phase number, e.g. "Phase 1"}.
- Before coding: read the relevant files, then give me a short plan (files to change, approach,
  risks) and WAIT for my confirmation.
- Small, atomic commits-worth of change. Match existing style. No new NuGet packages unless
  you justify it and I approve.
- Every behavior change gets xUnit tests (use TestSupport.cs fakes, no real network).
- Build the solution and run all tests at the end of the phase. Fix failures.
- Finish with: summary of changes, files touched, test results, known limitations, and
  anything that should change in later phases.
- If you find something that contradicts this plan, STOP and ask instead of guessing.

## Agreed decisions (do not re-litigate)
- Background operations: only Pause/Resume and Delete. No Stop (Stop = Pause). No Freeze.
- Pause persists across restarts. Interrupted operations resume on startup.
- CaptureLoad and Preload are independent operations; one never blocks or cancels the other.
- Home page shows/controls ONLY the current URL's operations.
- A separate Operations page manages all URLs, with bulk actions.
- "Run all at once" = all operations active with fair round-robin scheduling under the existing
  ArchiveClient global concurrency limit (NOT unlimited parallelism).
- Autocomplete ranks by number of captures, matches the typed prefix, returns ~20 results.
- "Searched URLs" page lists only URLs the user has searched; each opens a detail page
  listing all captured URLs for it.
- Server-side pagination everywhere: render only the current page's rows.

## Phase 0 – Diagnose preload stopping at ~1000 (investigation, minimal code)
Preload almost always stops at roughly 1000 captures even when more exist.
Investigate and report the root cause with file:line evidence. Check at least:
- Any cap near 1000 (`1000`, `1_000`, `1024`, `Take(`) in code, `Options.cs`, `appsettings.json` `Preload` section.
- Save channel capacity (PreloadCoordinator, ~500) + items marked failed when TryWrite fails.
- Download failures logged at Debug and marked failed without retry.
- Preload Scope `view` vs `all` and how the total is computed vs what gets enqueued.
- Boost `Take(50_000)`, shutdown timeouts dropping in-flight items.
Write a failing test that reproduces it (e.g. 5000 captures never reach completion).
Do not fix yet; report findings and wait.

## Phase 1 – Fix preloading to always complete
- Remove per-job caps; process large URLs in successive batches until done.
- Save channel uses backpressure (await WriteAsync), never marks items failed because the queue is full. Batch DB writes.
- Every capture reaches a persisted terminal state: Saved, Skipped(reason: binary/non-HTML/404/redirect/too large), Failed(reason).
- Transient errors (429, 5xx, timeouts) retried with exponential backoff + Retry-After, up to a configurable max; then Failed.
- Progress denominator = preloadable captures; skipped shown separately.
- Operation completes when all captures are terminal: "Completed" or "Completed with failures".
- Add "Retry failed" (re-enqueue only failed captures).
- No silently dropped items on shutdown; pending work persisted and resumed on startup.
- Fix ArchiveClient to pass CancellationToken to HttpClient and delays.
- Stop swallowing errors in PageService (distinguish not-found / transient / permanent).
Tests: 5000 captures complete; full queue causes no failures; 429 burst recovers; restart mid-run resumes; skipped/failed counts correct.

## Phase 2 – Operation states, Pause/Resume, scope Home page
- Explicit operation state machine: Queued, Running, Paused, Completed, CompletedWithFailures, Failed, Deleting. Validate transitions; reject/log invalid ones.
- Remove Stop from API and UI; Pause/Resume only. Pause persisted and survives restart.
- At most one active operation per (type, URL key); duplicates are de-duplicated.
- CaptureLoad resumes on startup like Preload. After a refresh discovers new captures, enqueue a delta preload.
- WorkflowStatus takes the current URL/filter and queries ONLY that URL's operations in SQL (no loading all operations).
- Home page bars + timeline show only the current URL; show one line "N other URLs processing → Operations" linking to the Operations page.
- All state-changing endpoints: [HttpPost] + antiforgery; JS fetch sends the token.
Tests: transitions, pause survives restart, home endpoint never returns other URLs' operations, de-duplication.

## Phase 3 – Operations page
- New page `/operations`: one row per searched URL with CaptureLoad and Preload progress bars, state badge, counts (saved/skipped/failed/pending), speed, ETA, last error.
- Row actions: Pause/Resume, Retry failed, Refresh capture list, Delete (Phase 4).
- Bulk: Pause all, Resume all, Retry all failed, and actions on selected rows.
- Filters by state/type; server-side pagination.
- Fair round-robin scheduler across active operations; optional priority boost for the URL currently viewed on Home.
- Efficient status endpoint for the visible rows only.
Tests: bulk pause/resume, fairness (a large URL does not starve small ones), pagination.

## Phase 4 – Delete
- Two levels: "Delete preloaded pages" (keep capture list) and "Delete everything for this URL" (capture list, pages, HTML files, summary, operations).
- Confirmation dialog shows counts and size.
- Pauses operations first, then deletes in batches as a background operation.
- Never delete captures/pages still referenced by another searched URL.
- Deletes HTML payload files from HtmlFileStore as well as DB rows.
Tests: shared captures preserved, files removed, large delete batched, cannot run while active.

## Phase 5 – URL summary table, autocomplete, Searched URLs pages, nav, About
- URL summary table (url, capture count, first/last capture, preload %, last refresh), maintained on save, backfilled once for existing data, indexed.
- Autocomplete endpoint: prefix match, ORDER BY capture count DESC, LIMIT ~20, indexed; client debounce ~200ms, min 2–3 chars, abort previous request; short bounded server cache.
- `/urls`: searched URLs only; search, sort (most captures / recent / A–Z), keyset pagination, state in query string.
- `/urls/{id}`: all captured URLs for that searched URL, server-side paginated, filters (MIME, status, date range), operation status, open in viewer.
- Shared nav bar: Home · Searched URLs · Operations · About.
- `/about`: purpose, how to use, Internet Archive attribution/links, app version.
Tests: ranking, pagination, keyset correctness, backfill.

## Phase 6 – Theme system (look + layout + motion)
- Refactor CSS to design tokens (CSS custom properties) for color, typography, spacing, radius, shadow, density.
- Independent axes, combinable:
  - Look: start with Midnight Glass and Clean Light; later Aurora, Archive Paper, Neon Terminal, High-contrast.
  - Layout: start with Table and Cards; later Timeline and Split view.
  - Motion: Rich / Subtle / None; respect prefers-reduced-motion.
- Theme picker in nav; persisted in localStorage; no flash on load (inline head script); default follows prefers-color-scheme.
- All layouts still render only the current page's rows.
- Never apply themes inside the archived-page Frame; never relax its sandbox.
- Check contrast for text, progress bars and badges.
Tests: Playwright smoke test per look/layout; pagination unaffected.

## Phase 7 – Performance
- Add Server-Timing (db/store/render) and slow-request logging.
- SQLite: WAL, synchronous=NORMAL, busy_timeout; indexes for new queries.
- Response compression (Brotli/gzip); long-lived caching for fingerprinted static assets.
- Serve stored compressed HTML without recompressing where possible.
- Fix unbounded memory: savedIds HashSet → indexed EXISTS; prune lastViews; bounded IMemoryCache instead of controller instance-field caches and FacetCache.Clear().
- Paginate in SQL instead of loading full snapshots into memory.
- Reduce global lock contention in PreloadCoordinator.
- Targets with 100k+ captures: server time <200ms for pages, <50ms for autocomplete.
Report before/after timings.

## Phase 8 – Live updates and sounds
- Operation state changes published as events; Server-Sent Events endpoint with event ids and catch-up after reconnect; polling kept as fallback.
- Timeline, progress bars and Operations page update from events.
- Sounds: opt-in toggle (persisted), only on operation completed / completed with failures / failed, throttled, ignore per-capture events, only in the visible tab.
