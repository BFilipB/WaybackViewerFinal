# Popularity guide - step by step, no thinking

Goal: make the "most popular" sort and popularity filter rank archived pages better.
Just follow the steps. Copy and paste the text in the grey boxes.

---

## 0. One-time setup (2 minutes)

1. In Visual Studio close the solution and open it again (File > Close Solution, then reopen `WaybackViewer.slnx`).
2. Open Copilot Chat.
3. Look at the bottom of the chat box. You will see:
   - an **agent / mode** dropdown
   - a **model** dropdown

That is all you need.

---

## 1. Which model do I click?

| When | Click this model |
|---|---|
| Fixing or building things | **Claude Sonnet 5.5** |
| Checking / reviewing | **GPT-6.1 Sol** |

Other models in your list, only if you want them:

- **Claude Opus 5.5**: big design questions only (it costs more).
- **Claude Haiku 5.5**: quick, simple questions.
- **Auto**: fine if you do not want to choose, but then you cannot guarantee a different model for the review step.

---

## 2. JOB A: "A page is ranked wrong" (use this 90% of the time)

### Step 1 - find out why
- Model: **GPT-6.1 Sol**
- Agent: **popularity-analyst**
- Paste this, then change the two web addresses:

```
On forum.example, https://forum.example/thread/123 should rank above https://forum.example/about on the popularity sort. Why does it not? Show me the code that causes it.
```

- Wait for the answer. Do nothing else.

### Step 2 - save the problem as a test
- Model: **Claude Sonnet 5.5**
- Type `/add-gold-case` and press space, then paste:

```
forum: thread/123 is a busy thread and should rank above /about
```

- Click Allow / Keep when it asks to edit files.

### Step 3 - fix it
- Model: **Claude Sonnet 5.5**
- Type `/improve-popularity` and press space, then paste:

```
fix the failing test from the last step
```

- Click Allow / Keep when it asks.
- Wait until it says it is finished.

### Step 4 - check the work
- Model: **GPT-6.1 Sol** (a different one from Step 3 on purpose)
- Agent: **reviewer**
- Paste:

```
Review the last commit.
```

- If it lists "Blocking" items: switch back to **Claude Sonnet 5.5** and paste:

```
Fix every blocking item from the review.
```

### Step 5 - send it to GitHub
Open the Terminal in Visual Studio (View > Terminal) and run:

```
git push
```

Done.

---

## 3. JOB B: "Just make it better" (once a week, zero thinking)

1. Model: **Claude Sonnet 5.5**
2. Type `/improve-popularity` and press Enter. Say nothing else.
3. Click Allow / Keep when it asks.
4. Model: **GPT-6.1 Sol**, agent: **reviewer**, paste: `Review the last commit.`
5. Terminal: `git push`

---

## 4. JOB C: "Why is this page ranked here?" (just a question, nothing changes)

1. Model: any.
2. Type `/diagnose-ranking` and press space, then paste the page address.
3. Read the answer.

---

## 5. JOB D: "I have an idea for a new thing to count"

1. Model: **GPT-6.1 Sol**, agent: **popularity-analyst**.
   Paste (change the idea):
```
Should we count JSON-LD view counts as a popularity signal? Cost, risks, where it fits.
```
2. Model: **Claude Sonnet 5.5**, agent: **signal-engineer**.
   Paste:
```
Implement it as the analyst described, with tests.
```
3. Model: **GPT-6.1 Sol**, agent: **reviewer**. Paste: `Review the last commit.`
4. Terminal: `git push`

---

## 6. How do I know it worked?

Run this in the Terminal. You want to see `Passed!` and `Failed: 0`:

```
dotnet test tests/WaybackViewer.Tests --filter "FullyQualifiedName~Popularity|FullyQualifiedName~ArchiveSignals"
```

Then open the app, load a URL, sort by popularity and look at the top 20 pages. Do they look right?
- Yes: done.
- No: go back to JOB A with the page that looks wrong.

---

## 7. What only YOU can do

- Notice when a ranking looks wrong and write: "page X should be above page Y".
- Say yes when Copilot asks to change files.
- Run `git push`.

Everything else the agents do.

---

## 8. If something goes wrong

| What you see | Do this |
|---|---|
| Agent says "I can't read files" | Close and reopen Visual Studio, pick the agent again |
| Agent or `/prompt` is not in the list | Close and reopen the solution. Still missing? Open the file in `.github/agents` or `.github/prompts`, copy its text, paste it into chat and add your question |
| It changed something you don't like | In the Terminal run `git restore .` (throws away changes not yet committed) |
| Tests fail after the fix | Paste: `The tests fail. Fix them without removing any test.` |
| Rankings changed a lot and you are not sure | Don't push. Ask: `Show me what changed in the ranking and why.` |

---

## 9. Where things live (only if you are curious)

- Agents: `.github/agents/`
- Prompts: `.github/prompts/`
- Details and history: `docs/popularity/` (`roadmap.md` = what is next, `experiments.md` = what was tried)
