---
mode: agent
description: Make popularity sorting good on one site, measured by log coverage.
---
Site: ${input:host:www.example.com}

1. Run `./tools/Summarize-Logs.ps1 -LogDir E:\WaybackViewer\logs` and report the site's Measured%, Comments%, Views%, Estimated%. If Measured% >= 60, stop and report.
2. As `popularity-analyst`: which counts does the page show that we do not read? Follow the pyramid (structured data -> embedded JSON -> attributes/ARIA -> visible text median). Evidence with file:line.
3. Ask the user for a trimmed real-HTML fixture if none exists in `tests/WaybackViewer.Tests/Fixtures/<host>/`.
4. As `signal-engineer`: fixture test first, minimal extraction change, bump `ExtractorVersion`, run popularity tests and build.
5. Append before/after coverage to `docs/popularity/experiments.md`.