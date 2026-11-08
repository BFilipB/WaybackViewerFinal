---
mode: ask
description: Security review of selected code or the whole web layer
---
Review ${input:target:file, folder or "web layer"} for:
- Missing antiforgery on POST endpoints, missing auth where needed.
- Input validation (UrlInput usage), encoding in Razor/JS, `@Html.Raw` use.
- Error message leakage, log injection.
- Unencoded values in archive URLs (`ArchiveUrls`), SSRF/path injection.
- Frame sandbox and CSP/security headers.
- SQL parameterization.
Report findings as a table: severity, file:line, issue, fix. Do not edit files.
