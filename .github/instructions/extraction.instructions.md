---
applyTo: "src/WaybackViewer/Parsing/**,tests/**/*Popularity*,tests/**/Fixtures/**"
---
# Extraction rules (site-independent)

- Layer order: structured data, generic attributes, embedded JSON state, visible-text median, content estimate, archive signal.
- Every extracted value carries `Source` and `Confidence`; never mix a real count with an estimate in one comparison.
- Preserve explicit zero. Missing is `null`, zero is `0`.
- Reject implausible values (for example likes far above views on the same page) instead of storing them.
- Bounded work: scan limit, `matchTimeoutMilliseconds`, bounded JSON depth and node count, `MaxMatchesPerKind`.
- Visible text only for word-based counts; attribute/JSON extraction reads named fields only.
- No site-specific hacks without a generic pattern and an entry in `docs/popularity/signals.md`.
- Fixtures: trimmed, no personal data, no network, one failure mode each, under `tests/WaybackViewer.Tests/Fixtures/<site-type>/`.
- Changing the extractor bumps `ExtractorVersion` so stored rows are reprocessed.
