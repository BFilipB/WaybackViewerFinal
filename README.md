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
  - diacritic-insensitive (for example `café` ~= `cafe`)
  - basic Cyrillic/Latin equivalence (`zdravo` ~= `здраво`, `njegos` ~= `његош`)

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
- Header shows operation summary (`X selected · Y active`).
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
