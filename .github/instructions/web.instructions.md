---
applyTo: "src/WaybackViewer/{Controllers,Views}/**"
---
# Controller and view rules
- Controllers are thin: delegate to services; no instance-field caches.
- State-changing actions: `[HttpPost]` + `[ValidateAntiForgeryToken]`; JS `fetch` sends the token header.
- Validate inputs with `UrlInput`; return generic error messages (log details server-side).
- Capture table pagination is server-side: render only the current page's rows.
- List pages keep search, filters, sort and page in the GET query string; pager/filter links must preserve them. Search/sort run in SQL (escaped, parameterized `LIKE`), never client-side.
- Bulk selection: the header checkbox is page-local; "Select all N matching" sends the current search/filter scope (`OperationScope`) and the server re-applies it (unknown filters -> 400, capped). Selection persists per scope in `sessionStorage` across pages. Search inputs set `autocomplete="off"`.
- Progress UI: unified `WorkflowStatus` endpoint, separate CaptureLoad and Preload bars, workflow timeline.
- Avoid `@Html.Raw` for user/archive-derived data; escape in JS before `innerHTML`.
- Archived HTML is served only via the sandboxed `Frame` endpoint; never relax the sandbox to `allow-same-origin`.
