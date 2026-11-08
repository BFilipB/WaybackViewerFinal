# Guide: improving popularity & sorting with Copilot

This guide is about **popularity of archived pages** (how popular a captured page was on its original site)
and the **"most popular" sort / popularity filter** in WaybackViewer. It tells you which agent, prompt and model
to use for each situation.

---

## 1. The toolbox

| Kind | Name | What it does | Edits code? |
|---|---|---|---|
| Agent | `popularity-analyst` | Finds *why* a ranking is wrong, with file:line evidence | No |
| Agent | `signal-engineer` | Implements one fix + test + log entry | Yes |
| Agent | `eval-guardian` | Owns the "expected ranking" tests (gold cases), blocks regressions | Tests only |
| Agent | `reviewer` | Reviews a diff against project rules | No |
| Prompt | `/diagnose-ranking` | "Why is page X ranked here?" | No |
| Prompt | `/add-gold-case` | Turns "X should rank above Y" into a test | Tests only |
| Prompt | `/improve-popularity` | One full cycle: analyze → test → fix → review → log → commit | Yes |
| Rules | `.github/instructions/popularity.instructions.md` | Applied automatically when popularity code is edited | – |
| Docs | `docs/popularity/*.md` | Model, signals, scoring, roadmap, experiment log | – |

How to pick them in Visual Studio / VS Code Copilot Chat:
- **Agent**: choose it in the chat mode / agent dropdown (it lists the files from `.github/agents/`).
- **Prompt**: type `/` in chat and pick the prompt name (from `.github/prompts/`).
- **Model**: model dropdown at the bottom of the chat box.

---

## 2. Which model for which job

| Job | Recommended model | Why |
|---|---|---|
| Diagnosing rankings, designing scoring changes (`popularity-analyst`, roadmap planning) | Strongest reasoning model available (e.g. Claude Sonnet/Opus 4.x, GPT-5, o-series) | Needs multi-step reasoning over several files |
| Implementing a fix (`signal-engineer`, `/improve-popularity`) | Strong coding model (Claude Sonnet 4.x or GPT-5 / GPT-4.1) | Reliable multi-file edits and test runs |
| Writing gold cases (`/add-gold-case`, `eval-guardian`) | Fast mid-tier (GPT-4.1, Claude Sonnet) | Small, well-defined edits |
| Reviewing (`reviewer`) | A *different* model than the one that wrote the code | Second opinion catches more |
| Quick questions ("what does RawSignal do?") | Fast/cheap model (GPT-4.1 mini or similar) | Speed |

Rule of thumb: **think with the strongest model, type with a strong coding model, review with a different one.**

---

## 3. Workflows

### A. "This page is ranked wrong"  (most common)
1. **Model:** strong reasoning. **Prompt:** `/diagnose-ranking`
   > `/diagnose-ranking https://forum.example/thread/123 ranks below https://forum.example/about on the popularity sort for forum.example/`
2. Read the failure class it reports (e.g. `false-positive-extraction`, `saturation/ties`).
3. **Model:** mid-tier. **Prompt:** `/add-gold-case`
   > `/add-gold-case forum: thread/123 (busy thread, 40 captures) should rank above /about (2 captures)`
4. **Model:** strong coding. **Prompt:** `/improve-popularity`
   > `/improve-popularity fix the failing gold case from the previous step`
5. **Agent:** `reviewer`, **different model**:
   > `Review the last commit`
6. Push when green: `git push`.

### B. Routine improvement (weekly "autopilot")
1. **Model:** strong coding. **Prompt:** `/improve-popularity` (no arguments).
   It takes the next open item from `docs/popularity/roadmap.md`, does it end to end and commits locally.
2. **Agent:** `reviewer` (different model) → fix blocking items.
3. Check `docs/popularity/experiments.md` for the logged result, then push.

### C. "Why is this page ranked here?" (no change wanted)
- **Model:** fast. **Prompt:** `/diagnose-ranking <url>`.

### D. Adding a new signal idea (e.g. JSON-LD view counts)
1. **Agent:** `popularity-analyst`, strong reasoning model:
   > `Evaluate adding JSON-LD interactionStatistic as a signal. Cost, false positives, confidence, where it plugs in.`
2. **Agent:** `eval-guardian`: `Add gold cases for pages with and without JSON-LD counts.`
3. **Agent:** `signal-engineer`: `Implement the JSON-LD signal per the analysis.`
4. **Agent:** `reviewer` (different model).

### E. Bigger design change (e.g. v3 percentile scoring, page templates)
1. Strong reasoning model, `popularity-analyst`:
   > `Design v3 scoring from docs/popularity/scoring.md. Produce ordered steps, each independently testable.`
2. Add those steps to `docs/popularity/roadmap.md`.
3. Run workflow **B** once per step. Stop after each step and look at real rankings in the app.

---

## 4. Your part (what the agents cannot do)
- **Decide what "correct" means.** When a ranking looks wrong, write down "X should be above Y and why" – that becomes a gold case.
  Aim for ~5 cases per site type (forum, blog, news, shop, wiki); see coverage in `docs/popularity/evaluation.md`.
- **Approve visible reorderings.** `/improve-popularity` stops and asks when a change reorders rankings by design.
- **Push.** Prompts commit locally; you push after reviewing.

---

## 5. Checking results
- Tests: `dotnet test tests/WaybackViewer.Tests --filter "FullyQualifiedName~Popularity|FullyQualifiedName~ArchiveSignals|FullyQualifiedName~Facet|FullyQualifiedName~Filter"`
- In the app: open a loaded URL, sort by popularity, compare top 20 with your expectation.
- History: `docs/popularity/experiments.md` (what changed, which tests, kept or reverted).
- CI: a failing gold case fails the build.

---

## 6. Current roadmap (next items)
See `docs/popularity/roadmap.md`. In order:
1. Gold cases for news, shop, wiki.
2. Confidence-weighted sort; show High/Med/Low confidence in the UI.
3. Percentile normalization across sources (fixes "any page with a number beats estimated pages").
4. JSON-LD counts. 5. Page-template grouping. 6. Age normalization. 7. In-site inbound links. 8. Multilingual count words.

---

## 7. Troubleshooting
| Symptom | Do |
|---|---|
| Agent edits unrelated files | Re-run with the scoped agent (`signal-engineer`), mention the one failure class |
| Change makes rankings worse but tests pass | Add a gold case capturing what got worse (`/add-gold-case`), then revert or fix |
| Agent invents data / calls the network | Remind it: synthetic data only, no network (rule in `popularity.instructions.md`) |
| Prompts/agents not listed | Make sure files are in `.github/agents` and `.github/prompts`, reload the solution |
