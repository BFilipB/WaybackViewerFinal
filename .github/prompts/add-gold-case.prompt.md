---
mode: agent
description: Turn a "this ranking is wrong" report into a gold test case.
---
Ask for: site type (forum/blog/news/shop/wiki), page that should rank higher, page that should rank lower, and why.

Then build synthetic captures and/or a trimmed HTML snippet (no network, no copied third-party content beyond a few words)
and add a test to `tests/WaybackViewer.Tests/PopularityQualityTests.cs` asserting the expected order.
Run it. If it fails, record the case as an open item in `docs/popularity/roadmap.md` with the likely failure class.
