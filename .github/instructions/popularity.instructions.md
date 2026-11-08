---
applyTo: "src/WaybackViewer/Parsing/**,src/WaybackViewer/Storage/*Popularity*,tests/**/*Popularity*,tests/**/ArchiveSignals*"
---
# Popularity & sorting rules

Read `docs/popularity/scoring.md` and `docs/popularity/signals.md` before changing anything here.

- Never compare raw values from different sources directly; compare percentiles within a site (later: within a page template).
- Every signal must document: kind, source, cost, known false positives, confidence. Add it to `docs/popularity/signals.md`.
- Prefer structured data (JSON-LD `interactionStatistic`, microdata, meta) over regex. Regex counts use the median of matches, never the first match.
- Count only visible text for word-based signals; never count CSS classes, attributes or scripts.
- Signals must be monotonic and bounded (log-scaled, capped). No linear caps that collapse many values into ties.
- Ranking must be deterministic: explicit tie-breakers (score, capture count, timestamp, id).
- Bounded work: scan limits, regex `matchTimeoutMilliseconds`, `CancellationToken` everywhere, no unbounded collections.
- Every behavior change adds a case to `tests/WaybackViewer.Tests/PopularityQualityTests.cs` (one test per failure mode)
  and a line to `docs/popularity/experiments.md`.
