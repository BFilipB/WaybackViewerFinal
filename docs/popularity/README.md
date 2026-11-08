# Popularity without metadata – overview

WaybackViewer must rank pages from **any** site, usually without real view/like counts. We treat popularity as an
*estimate built from weak evidence*, not a measured fact.

| Doc | Purpose |
|---|---|
| [ontology.md](ontology.md) | The objects and how they relate |
| [signals.md](signals.md) | Catalog of every signal: source, cost, false positives, confidence |
| [scoring.md](scoring.md) | How signals become one score; failure classes |
| [evaluation.md](evaluation.md) | Gold cases and how we know a change is better |
| [roadmap.md](roadmap.md) | Ordered open work |
| [experiments.md](experiments.md) | Append-only log of changes and outcomes |
| [runbook.md](runbook.md) | How to operate the agents and prompts |

Agents: `.github/agents/*.agent.md`. Prompts: `.github/prompts/*.prompt.md`. Rules: `.github/instructions/popularity.instructions.md`.
