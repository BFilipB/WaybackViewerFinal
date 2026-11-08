---
mode: agent
description: One full improvement cycle for popularity/sorting (analyze, fix, test, review, log).
---
Run one cycle, in order:

1. **Analyze** (as `popularity-analyst`): read `docs/popularity/roadmap.md`, pick the highest-priority open item
   (or the user's complaint if given). Produce the failure class and evidence.
2. **Gold case** (as `eval-guardian`): add a failing test for it to `PopularityQualityTests.cs`.
3. **Implement** (as `signal-engineer`): minimal change; run the popularity test filter; then build the solution.
4. **Review** (as `reviewer`): fix every blocking item.
5. **Log**: append to `docs/popularity/experiments.md` (date, change, failure class, tests, result, decision)
   and tick the item in `docs/popularity/roadmap.md`.
6. Commit with message `popularity: <change>`. Do not push unless asked.

Stop and ask the user if a change would visibly reorder existing rankings by design.
