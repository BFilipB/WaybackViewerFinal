# Wayback Viewer

Pick any website address, list **every** page the Internet Archive (Wayback Machine) has captured under it, flip
through the captures as a slideshow, and export the page text (Excel, txt, json, csv).

Everything downloaded once is saved in a local **SQLite** file (nothing to install), so repeat visits are instant
and nothing is downloaded twice.

## Run it

1. Install the **.NET 8 SDK** (`dotnet --version` should print 8.x).
2. In the folder with `WaybackViewer.slnx` run:
   ```
   dotnet run --project src/WaybackViewer
   ```
3. Open http://localhost:5080, type an address such as `example.com/blog/` (or paste a `web.archive.org/web/*/...`
   link) and press **Load captures**.

The database file is created automatically. The console prints where it is:
`Database file: C:\Users\<you>\AppData\Local\WaybackViewer\wayback.db`.
It lives outside the project (so it never ends up in git) and is found again whichever folder you start from.
To use another place, set `ConnectionStrings:WaybackDb` in `src/WaybackViewer/appsettings.json`, e.g.
`Data Source=D:\\data\\wayback.db`. To start fresh, stop the app and delete that file.

## Move your old file cache into the database (one time)

The previous version kept pages in `App_Data/wayback-cache/*.json`:

```
dotnet run --project src/WaybackViewer -- import-legacy "C:\path\to\old\App_Data\wayback-cache"
```

(721 MB of old cache becomes about a 120 MB database; takes around 40 seconds.)

## Why things can still take time, and what is done about it

| Step | Notes |
|------|-------|
| Capture list | One request to archive.org (a big site can take a minute). Saved, so next time it is instant. |
| Downloading pages | **This is the slow part and it is limited by archive.org**, not by the database. The page shows live speed and time left (`Downloading and saving: 412 / 1879 - 3.4 pages/s, about 7 min left`). Requests start 8 in parallel and automatically slow down after HTTP 429 and speed up again. Tune `Archive` / `Preload` in `appsettings.json`. |
| Slideshow | **Fast view (default)** shows title + extracted text from the local database: instant. **Full page** (press **F**) loads the real archived page, which pulls every image/script from archive.org and is slow by nature. Slides near your position are downloaded first. |

Everything already downloaded is read from SQLite in well under a millisecond per page.

## Tests

```
dotnet test
```

* Unit tests: URL normalisation, HTML parsing, CDX parsing, compression, archive client (retry, back-off,
  rate-limit, de-duplication), preload pipeline (batching, priority, failures, throughput), catalog and page
  services, legacy importer.
* Store tests: the same 14 scenarios against the in-memory store and a real SQLite file, plus restart,
  speed and file-creation tests.
* Integration tests: the real application is started on a local port with a fake archive and checked over HTTP,
  including a full restart on the same SQLite file that needs no archive access.

## Layout

```
src/WaybackViewer/         web app (Controllers, Views, Services, Storage, Parsing)
tests/WaybackViewer.Tests/
```
