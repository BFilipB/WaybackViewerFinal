---
mode: agent
description: Make table, filters, sort, and slideshow agree.
---
Run as `ui-harmonizer`. List every code path that builds a capture list, compare their sort/filter/tie-breaks,
report mismatches with `file:line`, then fix them with an integration test that the table and the filtered-set endpoint
return the same order for the same query. Keep pagination server-side and POST endpoints antiforgery-protected.
