# Wayback Viewer

Wayback Viewer is a .NET 8 web app for loading website capture lists from the Internet Archive, browsing captures quickly,
and persisting metadata + archived payloads for fast local replay.

This README is intentionally comprehensive: setup, architecture, navigation, operations model, search behavior,
configuration, and troubleshooting.

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
- **Pagination mode**:
  - `server` (recommended)
  - `client`
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

### Export

- Current page export button
- Full filtered export button (`Export all (filtered)`)
- Formats: `xls`, `txt`, `json`, `csv`

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

---

## 7) Background operations model

Operation kinds:

- `CaptureLoad`
- `WaybackRefresh`
- `PagePreload`

Operation states:

- `Queued`, `Running`, `Paused`, `Stopping`, `Stopped`, `Interrupted`, `Completed`, `Failed`

Capture loading/refresh and preload are independent workflows and can be controlled concurrently.

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

- `Provider` (`Sqlite` or `Memory`)
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
