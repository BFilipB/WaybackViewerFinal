# WaybackViewer – Copilot Instructions

## Project overview
- ASP.NET Core (.NET 8) app: `src/WaybackViewer` (MVC controller + Razor views), tests in `tests/WaybackViewer.Tests` (xUnit, Playwright).
- Browses Internet Archive (Wayback Machine) captures for a URL prefix: loads capture lists from the CDX API, preloads archived HTML in the background, and renders filtered, paginated capture tables.
- Composition root: `src/WaybackViewer/AppBuilder.cs`. Options: `src/WaybackViewer/Options.cs` + `appsettings.json`.

## Key components
- `Services/ArchiveClient.cs` – the ONLY component that talks to web.archive.org (concurrency limit, adaptive pacing, retries, Retry-After).
- `Services/ArchiveUrls.cs` – builds all archive URLs. Never hand-build Wayback URLs elsewhere.
- `Services/CaptureCatalog.cs` – capture-list (CaptureLoad) loading with per-key de-duplication and stale fallback.
- `Services/PreloadCoordinator.cs` – hosted service; background page preload workers, save channel, operation tracking and resume on startup.
- `Services/PageService.cs`, `PageLoader.cs` – on-demand page loading.
- `Storage/*` – `IArchiveStore` with SQLite (default) and SQL dialect abstraction; `HtmlFileStore` for HTML payloads.
- `Controllers/OperationsController.cs` + `Views/Operations/Index.cshtml` + `Storage/OperationQueries.cs` - Operations page; search (case-insensitive, escaped `LIKE`), sort and filters run in SQL. Bulk actions act on selected keys or on a `matching` scope (search + filters re-applied server-side, capped at 5,000); the UI never acts on URLs outside the current search/filters.
- `Controllers/HomeController.cs` + `Views/Home/Index.cshtml` – UI, unified `WorkflowStatus` endpoint, operation controls.

## Product rules (must follow)
- Capture-list loading and Wayback refreshing are **independent asynchronous operations**. Background work must be resumable after interruption/restart and must persist progress/state as operations.
- The UI uses a **unified workflow endpoint** with separate **CaptureLoad** and **Preload** progress bars and a **workflow timeline** (not a plain operation list).
- Pagination must be **server-side**: the rendered HTML capture table contains only the current page's rows. Never render all rows and hide them client-side.

## Engineering rules
- Propagate `CancellationToken` through every async call, including `HttpClient` calls and delays.
- Do not swallow exceptions (`catch {}` / `catch { return null; }`). Distinguish not-found, transient and permanent failures; log at an appropriate level.
- No sync-over-async (`.Result`, `.GetAwaiter().GetResult()`) in request paths or stores.
- Avoid unbounded in-memory collections; prefer SQL paging/EXISTS queries and bounded caches (`IMemoryCache` with size limits).
- Controllers are transient: never use instance fields as caches.
- Never return `ex.Message` to clients.
- State-changing endpoints are `[HttpPost]` and must validate antiforgery tokens; JS `fetch` calls must send the token.
- Validate all user input with `Parsing/UrlInput.cs` helpers; encode values placed in URLs/attributes.
- Use parameterized SQL only (`@name`), via the dialect classes.
- Keep changes minimal and match existing style; no new packages unless necessary.

## Testing
- Add/update xUnit tests in `tests/WaybackViewer.Tests` for every behavior change (use `TestSupport.cs` fakes; no real network calls).
- Build the solution and run related tests before finishing.
