# Improvement plan (from log run bce89be3)

Evidence = log line or file:line. Hypothesis = needs fixture/experiment.

## Evidence
| Area | Evidence | Meaning |
|---|---|---|
| reddit (5,123 pages) | extract.summary views 0%, likes 0%, comments 26%, measured 29%, estimated 71% | Ranks mostly on estimates |
| forum.kvinneguiden.no (392) | comments 0%, measured 0%, estimated 100% | Forum reply/view counts not read |
| Preload | total=755441, 4-6/s, ~105 KB/page | ~75 GB, ~2 days; 14.7 GB free |
| Memory | gcHeapMb=1051 after ~5.5k pages | Watch trend before acting |
| Workflow | workflow.slow ms=4694 | Status counts slow on huge searches |
| Classification | rows=381662 kinds=Page:381662 | A site lacks URL rules; log lacks host |

## Code facts
- FilteredQueries.MetricSql uses COALESCE(x,0): unknown == zero (wrong).
- ReadPage orders Ps DESC only: no "least" sorts.
- Threshold is Pm >= @thr only: no max.
- Estimated archive signal shares scale with measured counts (scale-mixing).

## Phase 1 - Sorting and filters
Add `-asc` variants: engaged, commented, viewed, liked, score, published.
Rules (mandatory):
1. Unknown (NULL) last in BOTH directions.
2. Measured 0 is the least.
3. Estimated-only rows after measured rows.
4. Tie-break CaptureTs DESC, Id DESC.
5. published-asc = oldest first, NULL dates last.
Plus: "Measured only" toggle, optional max threshold, coverage line ("29% measured"), disable 0%-coverage metrics with tooltip.
Tests: PopularitySortTests.cs.

## Phase 2 - Confidence UI
High/Med/Low badge, "~" prefix for estimates.

## Phase 3 - Extraction (bump ExtractorVersion, reprocess)
E1 reddit score/comment attributes (old + new markup). E2 Invision forum replies/views + topic JSON-LD. E3 generic JSON-LD interactionStatistic. Fixture first. Success = Measured% up in extract.summary.

## Phase 4 - Preload scale
Priority Thread/Listing first, Comment last; disk guard pause (preload.paused reason=disk); ETA in Preload bar; memory review.

## Phase 5 - Observability/perf
Host in classify.summary; cache workflow counts per list version; streaming Summarize-Logs.ps1.

## Risks
Ascending with COALESCE puts thousands of unknowns first. Reprocess = CPU spike; run when preload idle. Site markup changed over years: fixtures per era.
## Phase 0 - Score and engagement correctness (do first)
See `scoring.md` "Known defects in v2" (S1-S6) and "Target model (v3)".
1. S1 explicit zero above estimates (test: PopularityQualityTests).
2. Ranking snapshot test: fixed sample pages, recorded order; diffs reviewed on every scoring change.
3. S2/S3 log scaling + fixed weights (sign-off; snapshot diff).
4. S5 real score extraction (reddit `score`, forum votes) with fixtures.
5. S4/S6 within-site percentile + confidence; presets over measured rows.
