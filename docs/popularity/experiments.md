# Experiments log (append-only)

| Date | Change | Failure class | Tests | Result | Decision |
|---|---|---|---|---|---|
| 2026-11-08 | Median of text count matches | false-positive-extraction | `Stray_sidebar_count_does_not_override_the_typical_value` | pass; existing popularity tests pass | kept |
| 2026-11-08 | Discussion counted in visible text only | false-positive-extraction | `Comment_markup_in_css_and_scripts_...`, `Visible_discussion_still_raises_...` | pass | kept |
| 2026-11-08 | Log-scaled archive signal | saturation/ties | `Archive_signal_keeps_separating_*`, `Archive_signal_is_bounded_and_monotonic`, gold forum ordering | pass; 114 related tests pass | kept |
| pass; 510 tests, 2 load-sensitive failures passed on isolated rerun | kept; stored rows need reprocessing |

## Video pages had no views/likes (YouTube watch pages)

- Finding: saved watch pages carry counts in `ytInitialPlayerResponse` (`viewCount`, `likeCount`) and in `<meta itemprop="interactionCount">`; the parser read neither, so min-views/likes filters dropped them as 0.
- Change: `EmbeddedJsonCounts` plus `uploadDate`/`dateCreated`/`interactionCount` in `JsonLdCounts` and `PageParser`. Tests: `VideoCountsTests`.
- Not yet done: `ExtractorVersion`-driven reprocessing of already-saved pages, visible-text relative dates, unknown-count display.

## ExtractorVersion + stale reprocess
- Added `ExtractorVersion.Current = 2`, an idempotent `PageMetadata.ExtractorVersion` column migration, `GetPagesWithStaleExtractorAsync` and `PageService.ReprocessStaleAsync` (started from the list view).
- Tests: ExtractorVersionTests (old row refreshed, current row untouched, resume after cancel, missing HTML skipped).

- 2026-11-10 S1 tiers: rows rank measured (conf>=0.5, explicit 0 included) > estimated (>=0.15) > archive signal only, before the metric value. Class: unknown-as-zero/scale-mixing. Applied in PageMetadata.Tier, HomeController.ApplyPopularityAsync, FilteredQueries and SqliteArchiveStore.Popularity (Tr column). Tests: PopularityEstimatorTests, SqlPopularityTests. No extractor change, no ExtractorVersion bump. Result: 39/39 popularity tests pass. Decision: keep; next S2 snapshot test.

## 2026-11-10 S5 partial: structured score + Norwegian forum words (ExtractorVersion 3 -> 4)
- Change: PopularityEstimator reads score/data-score attributes (median) and svar/kommentarer/visninger words.
- Tests: Structured_score_attribute_is_measured, Norwegian_forum_counts_are_measured (synthetic fixtures).
- Baseline coverage (run 3525450d): reddit 4% measured, kvinneguiden 0%, femina.mk 0%.
- Result: pending. Re-parse saved pages (ReprocessStaleAsync), then run Summarize-Logs.ps1 and record new measured %.
- Decision: keep; if kvinneguiden stays 0%, add a topic-list fixture.

## 2026-11-10 Least sorts + bounded Home catalog wait
- Change: sort=least-popular/engaged/commented/liked/viewed/score (lowest metric first; measured > estimated > archive tier still first). SQL (FilteredQueries, SqliteArchiveStore.Popularity) and HomeController in-memory path kept in parity. Home waits at most Performance:CatalogWaitMs (15000) for a cold CDX download, then defers to a background CaptureLoad.
- Tests: LeastSortTests (SQL order + controller render), HomeCatalogWaitTests.
- Decision: keep. No ranking change for existing sorts.

## 2026-11-10 Capture-load retry (CDX 504/network failures)
- Change: CaptureLoadRunner retries a whole capture-list load on transient failures (network, timeout, 408/429/5xx) with doubling backoff (Archive:LoadRetries=3, Archive:LoadRetryDelaySeconds=20, cap 5 min). Permanent 4xx fail immediately; shutdown cancels. Each retry logs captureload.retry; the final failure is recorded on the operation, never swallowed. Per-request retries in ArchiveClient are unchanged.
- Tests: CaptureLoadRetryTests (transient then success with 20s/40s backoff, permanent not retried, exhausted retries recorded as Failed).
- Evidence: run 3525450d had CDX 504 after 5/5 attempts (60s) for example.com and read failures for forum.femina.mk / forum.kajgana.com.
- Decision: keep.

## 2026-11-10 S2/S3 log-scaled engagement (ExtractorVersion 4 -> 5)
- Change: PageMetadata.Engagement = round(10*(ln1p(views)+3 ln1p(likes)+4 ln1p(comments)+2 ln1p(shares)+3 ln1p(score))). Used by PageParser and PopularityEstimator, so stored EngagementScore is log-scaled; SQL and in-memory ranking read it unchanged (parity kept). Stale rows are re-parsed (ExtractorVersion 5).
- Before (snapshot): views5000, likes100, score50, comments10, zero, estimate. After: likes100, score50, comments10, views5000, zero, estimate.
- Tests: PopularitySnapshotTests updated. Signed off by user.
- Decision: keep.

## S4/S6 tier-aware thresholds, presets, histogram + include-estimates option
- Defects: threshold and Top N% presets/histogram mixed measured values, estimates and the 1..100 archive signal in one key (FilteredQueries Pm).
- Change: PopularityQuery.MinTier (2 = measured only; 0 with IncludeEstimates). SQL (FilteredQueries + SqliteArchiveStore.Popularity) and in-memory (HomeController.Eligible) filter, presets and histogram only over eligible rows. UI checkbox 'include estimates' (query param includeEstimates, kept in links, saved views and export).
- Ranking order unchanged (tier first, then metric), so no snapshot diff. Behavior change: with a threshold, estimated and archive-signal rows are hidden unless the box is ticked.
- Tests: MeasuredOnlyFilterTests (3); legacy fixtures now declare measured Confidence.
- Coverage: not re-measured; needs stale reprocessing on real data.

## Row confidence badge + why tooltip
- Each row with popularity data shows High/Med/Low (measured / estimated / archive signal), a tooltip explaining why, and a leading ~ on non-measured counts. Display only: ranking and ExtractorVersion unchanged. Tests: ConfidenceBadgeTests.

## S5 real coverage measurement (no code change)
- Source: read-only query of saved metadata (ExtractorVersion 2:17615, 3:313, 4:1530, 5:1853 rows - most rows still stale).
- reddit.com 17803 pages: 2697 measured (15%), score 1927, comments 2406, likes 513, views 7.
- forum.kvinneguiden.no 1767 pages: 0 measured. forum.femina.mk 1714 pages: 16 measured.
- Sampled 400 saved pages per forum: thread pages (postbody) carry no per-thread counts; only listing pages do. femina listings use 'N Replies'/'N Views' (already extracted). kvinneguiden listings use unlabeled postdetails cells (no words), so text extraction cannot read them.
- Conclusion: forum gaps are genuine absence of data on thread pages, not extractor bugs; no extractor change, ExtractorVersion unchanged (5). Next lever: run ReprocessStaleAsync on the ~19.4k stale rows, then re-measure. Deriving replies from post count/pagination would be an estimate (tier 1), not measured.

## S5 phpBB listing counts
- Added PhpBbListingRow regex (replies/views from unlabeled postdetails cells, median per page, explicit 0 kept). ExtractorVersion 5 -> 6 so stale rows re-parse. Coverage re-measure pending ReprocessStaleAsync.

## Coverage line (verified)
- Added CoverageMeasured/CoverageTotal to FilteredPage and CaptureListViewModel, computed in FilteredQueries, SqliteArchiveStore.Popularity and the in-memory path; shown in Index.cshtml as 'Measured counts on N of M captures'. Build OK. CoverageTests not yet run.

## Max threshold + disabled zero-coverage metrics
- maxPopularity (0 = no limit) applies in FilteredQueries, SqliteArchiveStore.Popularity and in-memory paths; MetricCoverage disables Min views/likes/comments/score options that no measured row in the view has. Tests: MaxThresholdTests, CoverageTests. Ranking order unchanged (snapshot untouched).

## Multilingual counts, Reddit fixtures, parity, CI summary
- ExtractorVersion 6 -> 7 (more count words, case-insensitive). Stale rows re-parse via ReprocessStaleAsync; real DB not touched.
- OrderingParityTests found the in-memory path ignoring least-* sorts; fixed. Snapshot order unchanged.
- Deferred (ranking-changing, need sign-off + snapshot diff): percentile normalization, confidence-weighted sort, template clustering, age normalization, inbound links. Also deferred: real-HTML gold fixtures, preload disk guard.

## Preload disk guard
- PreloadOptions.MinFreeDiskMb (default 500, 0 = off) and DiskGuardPath; workers pause preload jobs (resumable) when free space is low, checked every 5 s. DiskGuardTests.

## Confidence-weighted sort (scoring.md step 5)
- Inside a tier the sort key is metric * (0.5 + 0.5*confidence) in SQLite, generic SQL and in-memory paths. Snapshot before/after identical (all fixture rows confidence 0.6, order unchanged; snapshot NOT edited). ConfidenceWeightTests added.
- Still open and NOT started (each reorders rankings or needs new data): within-site percentile, template clustering, age normalization, inbound links, real-HTML gold fixtures.

## Within-tier percentile (scoring.md step 4)
- Sort key: CUME_DIST of the sort metric within the tier (SQLite, generic SQL, in-memory), times 0.5+0.5*confidence; raw weighted value breaks ties. Scope = rows of the current view (a search prefix = a site).
- Snapshot before/after identical (PopularitySnapshotTests unchanged and passing). PercentileNormalizationTests: 1000 comments @0.5 conf now ranks below 10 comments @1.0.
- Filters, thresholds and presets still use raw values.

## S7: unknown metric sorted as zero (most/least viewed looked identical)
- Single-metric sorts (views/likes/comments/score) treated a missing metric as 0, so most/least viewed tied across unknown rows and fell to the same CaptureTs tiebreak; least-* listed unknown rows first.
- Fix: rows lacking the sorted metric rank after rows that have it (explicit 0 counts as known), for most and least, in SQLite, generic SQL and in-memory paths. Percentile partition is now (known, tier). No stored metadata changed, ExtractorVersion unchanged.
- Snapshot (engaged ranking) unchanged and passing; test: PopularityQualityTests.Single_metric_sort_puts_unknown_last_and_least_is_not_the_same_as_most.

## S8: views sort on sites without view counts (reddit)
- Reddit exposes no views (extract.summary views=0), so S7 left every row tied. Views sort now has levels: real views (2) > estimated proxy from engagement of rows with other counts (1) > no counts (0). Proxy never mixes with real views; thresholds/filters still use real values only. SQLite, generic SQL and in-memory paths.
- Test: PopularityQualityTests.Views_sort_ranks_rows_without_views_by_an_estimated_proxy_after_real_views. Engaged snapshot unchanged. ExtractorVersion unchanged.
- Open: show a leading ~ on proxy-ranked rows in the UI.

## S8 UI and documentation follow-up
- The Home view now labels proxy-ranked rows with an eye/~ engagement-score badge under viewed and least-viewed sorts. The tooltip explains the fallback; real view counts and other sorts remain unchanged. IntegrationTests.Viewed_sort_marks_rows_without_views_with_an_estimated_tilde_badge covers the rendering distinction.
- The root README explains the three sort levels, confidence weighting, and why ~N is a ranking proxy rather than a predicted visitor count. Views filters still use the real field; the fallback does not populate it. No scoring, snapshot, stored metadata or ExtractorVersion changes in this documentation follow-up.
