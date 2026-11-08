# Evaluation

There is no ground truth from an API, so quality is defined by **gold cases**: hand-written expectations
("on a forum, the busy thread ranks above a one-off thread").

- Location: `tests/WaybackViewer.Tests/PopularityQualityTests.cs` (plus older `PopularityEstimatorTests`, `ArchiveSignalsTests`, `SqlPopularityTests`).
- One test per expected ordering or failure mode; synthetic data only, no network.
- Run: `dotnet test tests/WaybackViewer.Tests --filter "FullyQualifiedName~Popularity|FullyQualifiedName~ArchiveSignals"`.
- A previously passing gold test that fails is a blocking regression.

## Coverage
| Site type | Cases |
|---|---|
| Forum | 1 (hub + busy thread ordering) |
| Blog | 1 (comment boilerplate) |
| News | 0 |
| Shop | 0 |
| Wiki | 0 |

Goal: ≥5 cases per site type. Add via the `add-gold-case` prompt.
