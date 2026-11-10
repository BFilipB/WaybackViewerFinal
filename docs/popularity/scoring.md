# Scoring

## Current model (v2)
1. **Real counts** (meta or text, median of matches): `likes*3 + comments*4 + shares*2 + views + score`, confidence 0.6.
2. **Content estimate** when no counts: richness + visible discussion, 1..300, confidence 0.2.
3. **Archive signal** when the page was not downloaded: `RawSignal = min(60, 7*log2(1+captures)) + min(40, 5*log2(1+childUrls))`,
   then percentile-ranked within the site to 1..100, confidence 0.1.
4. Sorting: by effective engagement, with deterministic tie-breakers.

## Failure classes
| Class | Symptom | Typical fix |
|---|---|---|
| scale-mixing | pages with any count always beat estimated pages | percentile within site/template |
| false-positive-extraction | wrong number picked from page | structured data, median, visible text only |
| saturation/ties | many equal scores, order looks random | log scaling, more signals, tie-breakers |
| template-bias | list/category pages always win | compare within template |
| age-bias | old URLs win | normalize by observed years |
| missing-signal | no evidence at all | add a cheap archive signal |
| non-deterministic-order | order changes between loads | explicit tie-breakers |

## Next model (v3, planned)
Percentile per (site, template) for every signal, confidence-weighted combination, sort key `score * (0.5 + 0.5*confidence)`.

## Known defects in v2 (analysis 2026-11-10)
| # | Defect | Evidence | Class |
|---|---|---|---|
| S1 | Explicit zero counts give engagement 0, below any estimate (min 1) | PopularityEstimator.cs:55-68, :128 | unknown-as-zero / scale-mixing |
| S2 | Linear sum `likes*3+comments*4+shares*2+views+score`; raw views dominate | PopularityEstimator.cs:61, FilteredQueries.cs:289-290 | scale-mixing / saturation |
| S3 | Default metric adds mixed units: GREATEST(V+L+C+Sh+S, engagement) | FilteredQueries.cs:300 | scale-mixing |
| S4 | Saved-page engagement and unsaved archive signal (1..100) share one sort key | FilteredQueries.cs:312-313, 327 | scale-mixing |
| S5 | "Score" only from text words points/votes/score; real score attributes not read | PopularityEstimator.cs:15-21, 83 | missing-signal / false-positive |
| S6 | Threshold filter and 10/25/50% presets use the mixed `Pm` | FilteredQueries.cs:354-358, 412-421 | scale-mixing |

## Target model (v3)
1. **Tiers, in order:** measured (any real count, including an explicit 0) > estimated (`~`) > unknown. A lower tier never outranks a higher one.
2. **Per-signal log scaling:** `ln(1+x)` before combining.
3. **Weights:** comments 4 > likes = score 3 > shares 2 > views 1 (on log values).
4. **Within-site percentile** per signal (later per template).
5. **Confidence:** `key = percentile * (0.5 + 0.5*confidence)` inside a tier.
6. **Separate sorts:**
   - `score`: only a real score/points value.
   - `commented`: comments/replies only.
   - `engaged`: the weighted log combination.
   - Each sort has a `-asc` variant.
7. **Explainability:** each row shows its reason (e.g. "128 comments, 40 likes") and measured/estimated.
8. **Presets/thresholds:** percentiles over measured rows only.

## Rollout order
S1 -> ranking snapshot test -> S2/S3 (log + weights) -> S5 (extraction) -> S4/S6 (percentile + confidence) -> ExtractorVersion bump.
Steps that reorder existing rankings need user sign-off and a snapshot diff.
