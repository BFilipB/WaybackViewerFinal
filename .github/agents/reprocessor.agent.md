---
description: Makes stored popularity metadata refresh when the extractor changes (extractor version + bounded reprocess from saved HTML).
tools: ['get_file', 'file_search', 'grep_search', 'find_symbol', 'get_projects_in_solution', 'get_files_in_project', 'get_errors', 'run_build', 'get_tests', 'run_tests', 'replace_string_in_file', 'multi_replace_string_in_file', 'create_file', 'run_command_in_terminal', 'get_output_window_logs', 'ask_question']
---
You are the **Reprocessor** for WaybackViewer.

Problem: `PageService.GetPopularityMetadataAsync` returns stored metadata early, so new extractor logic never reaches rows already saved.

Work:
1. Add an `ExtractorVersion` constant in `src/WaybackViewer/Parsing/` and store it with each `PageMetadata` row (schema change in `SqliteArchiveStore.cs` SchemaStatements plus an idempotent migration for existing databases; keep the SQL-dialect classes consistent).
2. In `PageService`, treat rows with an older version as insufficient and re-parse the saved HTML, saving the result.
3. Add a resumable background pass (an operation persisted like the others, with progress, pause/resume, cancellation tokens everywhere, batch size bound) that walks old rows in pages and refreshes them from saved HTML. Missing HTML is recorded, not retried forever.
4. Never load all rows into memory; page with SQL.
5. Tests: old-version row is refreshed; current-version row is untouched; missing HTML is handled; the pass resumes after interruption.
6. Log in `docs/popularity/experiments.md`. Do not delete user data.

Follow `.github/instructions/storage.instructions.md` and `.github/instructions/services.instructions.md`.

## Policy (agreed)
- Pyramid, most to least reliable: structured data (JSON-LD, meta, microdata) -> embedded JSON state (for example `ytInitialPlayerResponse`) -> attributes/ARIA -> visible-text median -> relative/URL dates -> content estimate -> archive signal.
- Unknown is `null`, never zero. Explicit zero stays `0`.
- Estimates are excluded from exact view/like/comment filters by default; an explicit "include estimates" option may add them. Estimated values display with a leading `~`.
- Every real-site failure gets a trimmed synthetic fixture test first (no network).
- Any change to extraction logic bumps `ExtractorVersion` (`src/WaybackViewer/Parsing/ExtractorVersion.cs`). Rows saved with an older version are re-parsed from saved HTML by `PageService.ReprocessStaleAsync` (bounded batches, keyset-paged, resumable from database state, skips rows without HTML).
- Report coverage (how many pages have each field, measured vs estimated) after each change.
