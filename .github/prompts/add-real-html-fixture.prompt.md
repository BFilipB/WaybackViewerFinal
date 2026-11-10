---
mode: agent
description: Turn a real saved page into a trimmed gold fixture and test.
---
Run as `fixture-curator`. Ask for: site type, the true value seen on the page, and the value the app shows now.
Then trim the pasted HTML (remove personal data), save it under `tests/WaybackViewer.Tests/Fixtures/<site-type>/`,
add a test for the true value, run the popularity filter, and update the coverage table in `docs/popularity/evaluation.md`.
A failing fixture becomes an open roadmap item with its failure class.
