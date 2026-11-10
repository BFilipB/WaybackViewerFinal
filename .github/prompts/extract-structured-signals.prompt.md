---
mode: agent
description: One cycle adding a site-independent extraction layer (JSON-LD, attributes, embedded JSON).
---
Run as `extraction-engineer`:
1. Pick the next unfinished layer from `.github/agents/extraction-engineer.agent.md` (or the layer the user names).
2. Add failing tests with trimmed synthetic HTML, including a decoy count (sidebar/related) and an explicit zero.
3. Implement, run the popularity test filter, then build the solution.
4. Update `docs/popularity/signals.md`, append to `docs/popularity/experiments.md`, tick the roadmap item.
5. Do not commit. Stop and ask if the change visibly reorders existing rankings.
Follow the Policy section in docs/popularity/GUIDE.md: unknown is null, estimates excluded from exact filters by default, fixture first, bump ExtractorVersion on extractor changes, report coverage.
