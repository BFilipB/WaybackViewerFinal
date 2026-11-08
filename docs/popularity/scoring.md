# Scoring

## Current model (v2)
1. **Real counts** (meta or text, median of matches): `likes*3 + comments*4 + shares*2 + views + score`, confidence 0.6.
2. **Content estimate** when no counts: richness + visible discussion, 1..300, confidence 0.2.
3. **Archive signal** when the page was not downloaded: `RawSignal = min(60, 15*log2(1+captures)) + min(40, 6*log2(1+childUrls))`,
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
