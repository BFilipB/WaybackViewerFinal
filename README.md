# Wayback Viewer

<p align="center">
  <img src="./assets/logo.svg" alt="Wayback Viewer logo" width="860" />
</p>

<p align="center">
  <a href="./LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-2ea44f.svg" /></a>
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4.svg" />
  <img alt="Platform" src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-2b6cb0.svg" />
</p>

Wayback Viewer is a .NET 8 web app for loading website capture lists from the Internet Archive, browsing captures quickly,
and persisting metadata + archived payloads for fast local replay.

This README is intentionally comprehensive: setup, architecture, navigation, operations model, search behavior,
configuration, and troubleshooting.

---

## Open-source license

This project is open source under the **MIT License**.

- Full text: [`LICENSE`](./LICENSE)
- You can use, modify, and redistribute this project (including commercially) under MIT terms.

---

## 1) What this app does

Given a site/prefix (for example `example.com/blog/`), the app can:

1. Load capture metadata from Wayback CDX.
2. Save and reuse that list locally.
3. Background-download page content for local replay.
4. Browse captures in list + slideshow views.
5. Search by URL or by captured page content.
6. Export text across current page or full filtered result set.

It is built for large sets and resumable background workflows.

---

## 2) Quick start

### Prerequisites

- .NET 8 SDK
- Windows/macOS/Linux with network access to `web.archive.org`

Verify:

```bash
dotnet --version
```

### Run

From repository root:

```bash
dotnet run --project src/WaybackViewer
```

Open the printed URL (typically `http://localhost:5080`).

### Run tests

```bash
dotnet test
```

---

## 3) Setup and storage paths

### Default SQLite DB location

By default the app resolves DB paths to Local AppData (outside repo):

`%LOCALAPPDATA%\WaybackViewer\wayback.db`

Override in `src/WaybackViewer/appsettings.json`:

```json
"ConnectionStrings": {
  "WaybackDb": "Data Source=wayback.db"
}
```

### HTML payload storage

Archived compressed HTML payloads are file-backed and organized as:

`<HtmlRoot>/<host>/<year>/<month>/<day>/<hash>.gz`

Set root via:

```json
"Storage": {
  "HtmlRoot": "html-payloads"
}
```

If omitted, provider-specific defaults are used.

---

## 3b) Guides

### Your first search

1. Start the app and open it in the browser.
2. Enter a site or prefix, for example `example.com/` (a pasted Wayback URL also works).
3. Choose a filter: `html200` (default), `html`, or `all`, then submit.
4. The capture list loads in the background; progress bars show **Capture list** and **Page preload**.
5. The list is saved. Searching the same site again is instant until it is older than `Storage:SearchMaxAgeDays` (30 days).

### Find the most popular pages

1. Pick a **Popularity metric** (views, likes, shares, comments, score) and a **Sort** mode.
2. Use the **Top 10 / 25 / 50** presets or a minimum value; the histogram shows the distribution.
3. Pages not downloaded yet count as 0; a live notice shows how many are pending and offers a reload link when done.
4. Exports (`Home/CaptureSet`) honor the same sort/filter. Example: `/?url=example.com/&sort=liked&popularityMetric=likes&minPopularity=15`

### Themes, layouts, shortcuts, saved views

- Four layouts and ten themes; `Alt+T` / `Alt+L` cycle them (add `Shift` to go backwards). Choices persist in browser localStorage.
- **Copy link to this view** copies a URL with the exact search/sort/filters.
- **Save this view** stores a named view in your browser only.

---

## 3c) Databases and adaptability

Storage goes through one interface (`IArchiveStore`) and a named factory (`IArchiveStoreProvider`) chosen by `Storage:Provider`.

| Provider | Status | Needs |
|---|---|---|
| `Sqlite` (default) | Bundled, tested locally and in CI | Nothing |
| `Memory` | Bundled, tested | Nothing (data lost on exit) |
| `Postgres` | Bundled. The shared store scenarios run against a real PostgreSQL **in GitHub CI** | A PostgreSQL server |
| `SqlServer`, `MySql`, `MariaDb`, `Oracle` | Not bundled; add a dialect class (about 60 lines) | Driver package |

**How it adapts:** `SqlArchiveStore` is one store for every relational database (ADO.NET). A small `SqlDialect` class supplies what differs: connection, table definitions and upsert / insert-ignore SQL. Shipped dialects: `SqliteDialect`, `PostgresDialect`. NoSQL databases are out of scope.

Switch database with two settings (environment variables use `__` instead of `:`; the same keys work in `appsettings.json`):

```powershell
$env:Storage__Provider = "Postgres"
$env:ConnectionStrings__WaybackDb = "Host=localhost;Database=wayback;Username=wayback;Password=secret"
```

The schema is created automatically at startup. To add another database: derive from `SqlDialect`, add a provider, register it in `AppBuilder`, and add a test class that runs `StoreScenarios`. Details: **[docs/DATABASES.md](./docs/DATABASES.md)**.

PostgreSQL has not been run on the author's machine; it is verified by the CI workflow. Treat it as unverified until your CI run is green.

## 3d) Docker

```bash
docker compose up --build                                                         # SQLite, http://localhost:8080
docker compose -f docker-compose.yml -f docker/compose.postgres.yml up --build   # PostgreSQL + app
docker compose down                                                               # stop, keep data
docker compose down -v                                                            # stop and delete data
```

Files: `Dockerfile` (multi-stage, non-root), `docker-compose.yml` (SQLite volume), `docker/compose.postgres.yml` (adds a PostgreSQL container; change its password for real use). Settings are environment variables (`Storage__Provider`, `ConnectionStrings__WaybackDb`, `Storage__HtmlRoot`). Data lives in `/data`.

## 3e) Testing, CI and CD

### What the tests cover

| Kind | Where | What |
|---|---|---|
| Unit | `tests/WaybackViewer.Tests/*` | URL input, CDX parsing, facets, text matching, codecs, parsers, scheduler, preload coordinator |
| Store contract | `StoreTests.cs`, `GenericSqlStoreTests.cs` | Identical behavior scenarios for SQLite, generic SQL on SQLite, in-memory, and PostgreSQL (CI) |
| Integration | `IntegrationTests.cs`, `FilterIntegrationTests.cs`, `FacetTests.cs` | The real app with a fake archive: every sort, popularity metric, filter, paging mode, export and their combinations |
| Browser | `BrowserTests.cs` | Playwright/Chromium. **Skipped locally** unless Chromium is installed; run in CI |
| Load / stress | `scripts/Test-LoadAndSmoke.ps1` | Concurrent mixed traffic, invalid-input stress, p95 latency budget |
| Repository health | `scripts/Test-Integrity.ps1` | Quick structural checks |

```powershell
dotnet test                                                        # everything runnable locally
dotnet test --filter "FullyQualifiedName~FilterIntegrationTests"   # one area
./scripts/Test-LoadAndSmoke.ps1                                    # load/stress (Windows PowerShell)
```

No test touches the internet.

### CI/CD workflow guide (GitHub Actions, nothing to install locally)

`.github/workflows/ci.yml` runs on every push and pull request. Open the **Actions** tab of your repository to follow it.

| Job | Runs on | Does |
|---|---|---|
| `test` | Ubuntu | Builds, installs Chromium, starts a PostgreSQL service container, runs **all** tests (browser and PostgreSQL included) |
| `docker` | Ubuntu | Builds the image, starts compose with SQLite, waits for `/healthz`, checks the home page |
| `docker-postgres` | Ubuntu | Same, with the PostgreSQL compose override |
| `load` | Windows | Runs `Test-LoadAndSmoke.ps1` |

**Using it, step by step**
1. Push to GitHub (`git push`). The workflow starts automatically; there is nothing to enable.
2. Open **Actions**, choose the latest run, click a job to see its log. Green check = passed.
3. A red job: open it, expand the failed step, read the first error, fix, push again.
4. To re-run without a new commit: open the run and choose **Re-run all jobs**.
5. To protect `main`: repository **Settings > Branches > Add rule**, and require the checks above before merging.

**Deploying (CD).** The workflow tests and builds the image but does not publish it, because that needs your registry and secrets. To publish to GitHub Container Registry, add a job after `docker` that logs in with `docker/login-action` using `GITHUB_TOKEN` and pushes with `docker/build-push-action`; then run the image anywhere with the environment variables from section 3d.

## 3f) Filters, sorting and how they combine

Everything in the URL composes: search mode and text, capture-type filter, facets (year, category, status, section, type, threads), sort, popularity metric and minimum, page and page size. Pager, facet, sort, export, copy-link and saved-view links all carry the full set, including content-search mode and the "download everything" choice. Searching a **different** URL starts fresh; re-applying the same list keeps its facets. Export (`/Home/CaptureSet`) applies the same filters and sort as the page being viewed. Invalid input keeps your sort and popularity selection.

## 3g) Troubleshooting

- **`Storage:Provider 'X' is not bundled`**: use `Sqlite`, `Memory` or `Postgres`, or add a dialect (docs/DATABASES.md).
- **Popularity shows 0**: pages are not downloaded yet; wait for preload or set `Preload:Scope` to `all`.
Saved pages are re-scored from stored HTML without downloading again. Captures with no page data yet fall back to archive-only signals (how often the URL was captured, and how many captured URLs sit beneath it), so every capture has a score immediately and a minimum never hides everything. These fallback scores are ranked as a percentile (1-100) within each site and shown in the list as "est.".
- **Where is my data?** `%LOCALAPPDATA%\WaybackViewer\` on Windows, `/data` in Docker.
- **Slow / HTTP 429 from the archive**: the client backs off automatically; lower `Archive:MaxConcurrency`.
- **Port in use**: `dotnet run --project src/WaybackViewer --urls http://localhost:5090`
- **Themes do not stick**: they live in localStorage; private windows or cleared data reset them.
- **Browser tests skipped**: expected locally; they run in CI.

---

## 4) Project layout

```text
src/WaybackViewer/
  Controllers/     HTTP endpoints and page workflow orchestration
  Services/        Capture loading, preload pipeline, caching
  Storage/         SQLite + in-memory providers, file payload store
  Models/          Contracts and view models
  Views/           Razor UI (main page + layout)
  Parsing/         CDX + page text parsing helpers

tests/WaybackViewer.Tests/
  Integration, pipeline, storage, facets, frame/text, operations tests
```

---

## 5) Main user navigation (UI guide)

### Search bar

- **URL**: site/prefix (or pasted Wayback URL)
- **Filter**:
  - `html200`
  - `html`
  - `all`
- **Pagination**: always server-side; only one page of rows is rendered. Old `pagination=client` links still work and are served as server pages.
- **Page size**: 25/50/100/200
- **Search mode**:
  - `Search URLs`
  - `Search content`
- **Search query**: keyword input

### Background activity area

- Unified bars:
  - Capture list / refresh
  - Page preload
- Workflow timeline of operations
- Per-operation and bulk controls:
  - Pause / Resume / Stop

### Capture list and inspector

- Click a row to inspect URL/history.
- Timeline/facets allow refining by year/category/status/section.

### Slideshow

- Launch from row or from "Slideshow all (filtered)".
- Two modes:
  - **IFrame archived page** (preferred visual replay)
  - **Fast text view** (fallback/fast read)
- If iframe is slow/blocked, app auto-falls back to fast text view.
- Keyboard controls:
  - `ArrowRight` / `PageDown`: next
  - `ArrowLeft` / `PageUp`: previous
  - `Home` / `End`: first / last
  - `F`: toggle full/text mode
  - `T`: toggle text panel (in full mode)
  - `O`: open current capture in new tab
  - `Esc`: close slideshow
- Navigation is throttled for key-repeat smoothness (avoids jumpy rapid-fire transitions).
- When a capture was already loaded in this slideshow session, loading overlay is minimized and transition is near-instant.

### Export

- Current page export button
- Full filtered export button (`Export all (filtered)`)
- Formats: `xls`, `txt`, `json`, `csv`
- Export actions are snapshot-isolated by default (new tab):
  - You can continue changing filters/navigation in the original tab.
  - Export continues in the opened tab with the captured query state.
  - If popup opening is blocked, export falls back to current-tab execution.

---

## 6) Search behavior (important)

### URL mode

Matches capture URL/path text.

### Content mode

Matches parsed page **title/description/text** (with URL fallback).

Notes:

- Content search can be slower on first use for large sets because pages may need to be loaded/parsed.
- Results are server-paginated when server mode is enabled.
- Legacy `text:keyword` style still works for compatibility.
- Search matching is tolerant (not overly literal):
  - case-insensitive
  - punctuation/extra-space tolerant token matching
  - diacritic-insensitive (for example `cafÃ©` ~= `cafe`)
  - basic Cyrillic/Latin equivalence (`zdravo` ~= `ÃÂ·ÃÂ´Ã‘â‚¬ÃÂ°ÃÂ²ÃÂ¾`, `njegos` ~= `Ã‘Å¡ÃÂµÃÂ³ÃÂ¾Ã‘Ë†`)

---

## 7) Background operations model

Operation kinds:

- `CaptureLoad`
- `WaybackRefresh`
- `PagePreload`

Operation states:

- `Queued`, `Running`, `Paused`, `Stopping`, `Stopped`, `Interrupted`, `Completed`, `Failed`

Capture loading/refresh and preload are independent workflows and can be controlled concurrently.

UI interaction details:

- Timeline selection is stable across polling refreshes.
- Header shows operation summary (`X selected Â· Y active`).
- Bulk controls (`Pause`/`Resume`/`Stop`) and row controls use busy states to prevent accidental duplicate clicks.
- Background operation commands are grouped and refreshed coherently to reduce flicker.

---

## 8) Configuration reference

`src/WaybackViewer/appsettings.json`

### `Archive`

- `MaxConcurrency`
- `MinIntervalMs`
- `MaxIntervalMs`
- `MaxAttempts`
- `RequestTimeoutSeconds`

### `Preload`

- `Workers`
- `WriteBatchSize`
- `WriteMaxDelayMs`
- `MaxHtmlChars`

### `Storage`

- `Provider` (`Sqlite`, `Memory`, or a provider you register; see docs/DATABASES.md)
- `SearchMaxAgeDays`
- `HtmlRoot`

### `RateLimit`

- `PermitLimit`
- `WindowSeconds`

---

## 9) Endpoints

### Primary page/actions

- `GET /` (main UI)
- `POST /Home/StartSearch`
- `POST /Home/Pause`
- `POST /Home/Resume`
- `POST /Home/Stop`

### Status/integration

- `GET /Home/WorkflowStatus`
- `GET /Home/OperationStatus`
- `GET /Home/PreloadStatus`
- `GET /healthz`

### Capture APIs

- `GET /Home/CaptureSet` (full filtered set)
- `GET /Home/History`
- `GET /Home/Text`
- `GET /Home/Frame`
- `POST /Home/Prioritize`

---

## 10) Reliability and pitfalls (known + handled)

### File payload write contention

When multiple workflows target same capture file path concurrently, transient IO lock races can happen.
Current behavior includes cooperative file sharing + short retry on save.

### Slow iframe loads

Archived iframe content can be slow/unframeable depending on target page behavior.
UI auto-falls back to text mode to avoid dead-end waits.

### Content search cost

Content mode may trigger text loading/parsing for captures not yet locally parsed.
Use URL mode for fast structural filtering, then content mode for deep lookup.

---

## 11) Legacy cache import

```bash
dotnet run --project src/WaybackViewer -- import-legacy "C:\path\to\old\App_Data\wayback-cache"
```

---

## 12) Developer notes

- Build:

```bash
dotnet build
```

- Full tests:

```bash
dotnet test
```

- Prefer `server` pagination for large datasets.
- If running under debugger, UI file changes may require Hot Reload/restart.
