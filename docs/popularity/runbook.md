# Runbook – operating the popularity agents

## Day to day
| Situation | Do this in Copilot Chat |
|---|---|
| "This page ranks wrong" | `/add-gold-case`, then `/improve-popularity` |
| "Why is X ranked here?" | `/diagnose-ranking` |
| Routine improvement (weekly) | `/improve-popularity` – it picks the next open roadmap item |
| Reviewing someone's change | select the `reviewer` agent |

## Semi-autopilot
- Run `/improve-popularity` on a schedule (e.g. weekly). Each run does one roadmap item end to end and commits locally.
- Or create a GitHub issue per roadmap item and assign it to the Copilot coding agent; the instructions/agents in `.github/` are picked up automatically.
- CI runs all popularity tests on every push; a failing gold case blocks the build.

## What still needs a human
- Deciding expected orderings (gold cases). Without them "better" cannot be measured.
- Approving changes that visibly reorder rankings by design.
