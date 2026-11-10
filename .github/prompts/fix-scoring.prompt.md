---
mode: agent
description: Fix score/engagement ranking defects (scoring.md S1-S6) one at a time, with a snapshot diff.
---
Defect: ${input:defect:S1}

1. As `popularity-analyst`: read `docs/popularity/scoring.md` (Known defects, Target model v3). Confirm the defect with file:line evidence.
2. As `eval-guardian`: add a failing test in `PopularityQualityTests.cs`. If `PopularitySnapshotTests` does not exist, create it first: fixed sample pages (measured, explicit zero, estimated, unknown, mixed units) and their recorded order.
3. As `signal-engineer`: minimal change in `PopularityEstimator` and the matching SQL in `FilteredQueries` (keep both paths consistent). Bump `ExtractorVersion` if stored metadata changes.
4. Run popularity/sort/filter tests and build. Show the snapshot order before and after.
5. If the order changed, STOP and ask the user to approve before updating the snapshot.
6. Log in `experiments.md`, tick `roadmap.md`.