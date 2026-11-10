# Signal catalog

| Signal | Source | Cost | Confidence | Known false positives | Code |
|---|---|---|---|---|---|
| Meta / structured counts | page `<meta>` etc. | needs HTML | ≥0.6 | site-specific naming | `PageParser` |
| Text counts (`1,234 views`) | visible text, regex, **median** of matches | needs HTML | 0.6 | "points" in prose, sidebar counts (mitigated by median) | `PopularityEstimator.Enrich` |
| JSON-LD video/post | `uploadDate`, `dateCreated`, `datePublished`, `viewCount`, `interactionCount`, `interactionStatistic` | needs HTML | 0.6 | nested comment/reply counts (never walked) | `JsonLdCounts` |
| Embedded player JSON | first `"viewCount"` / `"likeCount"` in a `ytInitialPlayerResponse` blob | needs HTML | 0.6 | recommendation counts later in the blob (only the first value is read) | `EmbeddedJsonCounts` |
| Date attributes | `created-timestamp`, `<time datetime>`, `data-timestamp` | needs HTML | 0.5 | other posts' times in sidebars (first 3 per marker, year 1995-2100) | `PageParser.FindPublishedAttribute` |
| Content richness | text length, links, images | needs HTML | 0.2 | boilerplate-heavy templates | `PopularityEstimator.Estimate` |
| Visible discussion | "comment"/"repl" in **visible text only** | needs HTML | 0.2 | navigation labels | `PopularityEstimator.Estimate` |
| Capture frequency | capture list, **log2-scaled**, max 60 | free | 0.1 | old URLs had more time to be captured | `ArchiveSignals.RawSignal` |
| Hub size | captured URLs below this path, **log2-scaled**, max 40 | free | 0.1 | listing/category pages | `ArchiveSignals.RawSignal` |

Planned: JSON-LD `interactionStatistic`, distinct capture years (age normalization), in-site inbound links, multilingual count words.
