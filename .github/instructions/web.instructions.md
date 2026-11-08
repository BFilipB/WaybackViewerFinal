---
applyTo: "src/WaybackViewer/{Controllers,Views}/**"
---
# Controller and view rules
- Controllers are thin: delegate to services; no instance-field caches.
- State-changing actions: `[HttpPost]` + `[ValidateAntiForgeryToken]`; JS `fetch` sends the token header.
- Validate inputs with `UrlInput`; return generic error messages (log details server-side).
- Capture table pagination is server-side: render only the current page's rows.
- Progress UI: unified `WorkflowStatus` endpoint, separate CaptureLoad and Preload bars, workflow timeline.
- Avoid `@Html.Raw` for user/archive-derived data; escape in JS before `innerHTML`.
- Archived HTML is served only via the sandboxed `Frame` endpoint; never relax the sandbox to `allow-same-origin`.
