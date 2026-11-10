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
- Unknown is NULL, never zero. Sorts place NULL last in BOTH directions; `-asc` ("least") sorts rank measured zeros first, then estimated-only rows, then unknown.
- Estimated values never outrank measured values in exact-metric sorts/filters unless "include estimates" is on; show them with a leading `~`.
- Sort keys are a whitelist (`engaged|commented|viewed|liked|score|published` plus `-asc`); validate in `Parsing/UrlInput.cs`.
- Sort/direction behavior is tested in `tests/WaybackViewer.Tests/PopularitySortTests.cs`.
- Measure every extraction change by per-host `extract.summary` coverage (measured vs estimated) using `tools/Summarize-Logs.ps1`; record before/after in `experiments.md`.
- Plan and priorities: `docs/popularity/improvement-plan.md`.
- Score model rules (scoring.md v3): tiers measured > estimated > unknown; an explicit 0 is measured and ranks above every estimate.
- Never add raw counts of different units (views + likes + score). Log-scale each signal (`ln(1+x)`), then weight: comments 4, likes/score 3, shares 2, views 1.
- `score` sort uses only a real score/points value; `commented` only comments/replies; `engaged` is the weighted log combination.
- Saved-page metrics and the archive signal must not share one raw scale; compare via within-site percentiles inside a tier.
- Any change that reorders existing rankings must update the ranking snapshot test (`PopularitySnapshotTests`) and get user sign-off.
