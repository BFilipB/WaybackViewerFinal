# Roadmap (ordered)

- [x] Median of regex count matches instead of first match
- [x] Visible-text-only discussion signal (ignore CSS/script "comment")
- [x] Log-scaled archive signal (no saturation at 12 captures / 20 children)
- [x] Agents, prompts, instructions, docs
- [x] Structured comment-count attribute first, median fallback, explicit zero kept (stored rows still need reprocessing)
- [ ] Gold cases for news, shop, wiki site types
- [x] Confidence-weighted sort key; expose confidence in UI (High/Med/Low)
- [x] Percentile normalization across sources within a site (fix scale-mixing)
- [ ] Generic attribute extraction (`comment-count`, `data-*count`) with real-HTML fixtures per site type
- [x] Embedded JSON state extraction (EmbeddedJsonCounts)
- [x] Page-type classifier (UrlClassifier: Thread/Comment/Listing/Profile/Wiki/Page)
- [x] Extractor version + reprocess stored metadata from saved HTML
- [x] Table / export ordering parity test (OrderingParityTests; fixed in-memory least-* sort)
- [ ] Real-HTML fixture set: 5 per site type; coverage table in evaluation.md
- [x] JSON-LD `interactionStatistic` extraction (JsonLdCounts)
- [ ] Page template clustering from URL patterns; compare within template
- [ ] Age normalization (distinct capture years / observed span)
- [ ] In-site inbound links from stored HTML
- [x] Multilingual count words (MultilingualCountTests, ExtractorVersion 7)
- [x] CI job summarizing popularity test results

- [x] ExtractorVersion + automatic stale-row reprocessing
- [x] Attribute/ARIA and visible-text count layers (attribute regexes + median of visible text)
- [x] Include-estimates toggle (default off); [ ] coverage display

## From log analysis (see improvement-plan.md)
- [x] Least (`-asc`) sorts with NULL-last rules (LeastSortTests)
- [x] Measured-only toggle, max threshold (maxPopularity, MaxThresholdTests), coverage line, disabled 0%-coverage metrics (MetricCoverage, CoverageTests)
- [x] Confidence badge High/Med/Low
- [x] Reddit score/comment attributes (RedditAttributeTests)
- [x] phpBB forum replies/views from listing rows (kvinneguiden; PhpBbListingTests, ExtractorVersion 6)
- [x] Preload priority (Thread/Listing first) + disk guard + ETA
- [x] Host in classify.summary; cache workflow counts; streaming log summarizer

## Score / engagement v3 (scoring.md S1-S6)
- [x] S1 explicit zero ranks above estimates
- [x] Ranking snapshot test (PopularitySnapshotTests)
- [x] S2/S3 log-scaled weighted engagement, no mixed-unit sums
- [x] S5 real score extraction (reddit, forums) - partial: score attr + Norwegian words done, coverage measured (see experiments.md); forum thread pages carry no counts; ~19.4k rows stale, run ReprocessStaleAsync then re-measure
- [x] S4/S6 measured-only thresholds/presets/histogram + include-estimates toggle (percentile normalization and confidence badge still open)
- [x] Row "why" explanation + measured/estimated marker

- [x] Coverage line (CoverageTests green; 558 non-browser tests pass, 2 Postgres skipped)
