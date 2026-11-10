# Popularity guide - step by step, no thinking

> **START HERE - which section do I need?**
>
> | I want to... | Go to |
> |---|---|
> | Fix one page that is ranked wrong | section 2 (JOB A) |
> | Find out why a page is ranked where it is | section 4 (JOB C) |
> | Teach the app to read a number from a new kind of page (comments, likes...) | **section 5c** (real hand-holding) |
> | Do the long-term "work on every site" plan | section 5b (JOB E) |
> | Make sorting good on a whole site (measured by logs) | **section 9 (JOB F)** |
> | Understand "least" sorts and estimates | section 9 rules |
> | Fix "most engaged / most score" ranking (S1-S6) | **section 10 (JOB G)** |
> | Something broke | section 8 |
>
> Section 5c is the only one that needs you to look at real HTML. Everything else is copy and paste.

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

## 5b. JOB E: "Make it work on any site" (the long-term plan)

Run these in order. One job per chat. After each one: Model **GPT-6.1 Sol**, agent **reviewer**, paste `Review the last change.`

| Order | What you want | Model + agent | Type / paste |
|---|---|---|---|
| 1 | Real example into a test | Sonnet 5.5, `fixture-curator` | `/add-real-html-fixture` then paste the trimmed HTML and the true number |
| 2 | Read counts from standard data (JSON-LD, meta, attributes, embedded JSON) | Sonnet 5.5, `extraction-engineer` | `/extract-structured-signals` |
| 3 | Stop listing/search pages ranking next to posts | Sonnet 5.5, `page-classifier` | `/classify-page-type` and paste example URLs |
| 4 | Refresh old stored rows after the extractor changes | Sonnet 5.5, `reprocessor` | `/reprocess-stored-metadata` |
| 5 | Table, filters, sort and slideshow all agree | Sonnet 5.5, `ui-harmonizer` | `/harmonize-sort-ui` |
| 6 | What sites are covered | any model | `/site-coverage-report` |

Rules for JOB E:
- Do step 1 for every wrong example you find. Fixtures are how the project learns about new sites.
- Aim for 5 fixtures per site type (forum, blog, news, shop, wiki, video) before trusting that type.
- If an agent says a change would reorder existing rankings, read what it says before you answer.
- The agents never commit unless you ask. Commit and `git push` yourself after the review passes.

---

## 5c. HOW TO EXTRACT REAL DATA FROM A SAVED PAGE (full hand-holding)

Why: the app only knows what the saved HTML says. To teach it a new site, we first look at the real HTML and find
where the true number (for example "13 comments") lives. Do this once per kind of wrong page.

You need: the app running with that page already saved, Visual Studio, and the Terminal.

### Step 1 - pick one wrong page
1. In the app, load your URL list and sort by the thing that is wrong (for example "Most commented").
2. Pick ONE page that is ranked wrong.
3. Open the real page in your browser (the archive link) and write down the TRUE number you can see.
   Example: `13 comments`. Write it on paper. This is called the "true value".

### Step 2 - copy the two things the script needs
1. In the app, click the page's card. On the right you see the URL and the Captured time.
2. Copy the URL exactly. Copy the capture time as 14 digits without dashes or colons.
   Example: `20250727121849` (2025-07-27 12:18:49).
   Tip: the table also shows this number in the page link after `/web/`.

### Step 3 - check where your pages are saved
1. Open `src\WaybackViewer\appsettings.json`.
2. Find `"HtmlRoot"`. Example: `E:\\WaybackViewer\\html-payloads`. That is the folder (write it with single slashes: `E:\WaybackViewer\html-payloads`).
3. Open that folder in File Explorer. If it is empty, the pages are not saved yet: in the app wait for "Page preload" to save them, then come back.

### Step 4 - run the extract script
1. In Visual Studio: View > Terminal. Make sure you are in the repository folder (the one that contains `tools`).
   If unsure, run: `cd "C:\Users\Filip\Desktop\Cloned Projects\Wayback Viewer Final\WaybackViewer"`
2. If PowerShell refuses to run scripts, run this once: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`
3. Paste this, then change the three values (URL, time, true number):

```
.\tools\Export-CaptureHtml.ps1 -Url "PASTE THE URL HERE" -Timestamp 20250727121849 -Expect 13 -HtmlRoot "E:\WaybackViewer\html-payloads"
```

4. Read the output:
   - `NOT FOUND` means the page is not saved in that folder. Re-check the URL, the time, and `-HtmlRoot` (Step 3).
   - `Saved ... to ...\Desktop\capture.html` means it worked. A file `capture.html` is now on your Desktop.
   - Under "Places where the number 13 appears" you see short pieces of the HTML around your true number.
   - Under "Likely count fields" you see attributes or keys that look like counts.

### Step 5 - find the count piece by hand (if the output is not clear)
1. Open `capture.html` from the Desktop in Visual Studio (File > Open > File).
2. Press Ctrl+F and search for your true number with a word next to it, for example `13` then `comment`.
3. Look at the text around it. You are looking for one of these shapes:
   - `comment-count="13"` (an attribute on an element)
   - `"commentCount": 13` or `"num_comments":13` (JSON inside a script)
   - `13 comments` (plain text)
   - `<meta ... content="13">` (a meta tag)
4. Copy ONLY that short piece (one or two lines). Do not copy usernames, names or long text.

### Step 6 - turn it into a test and a fix
1. Model: **Claude Sonnet 5.5**, agent: **fixture-curator**. Type `/add-real-html-fixture` and press space, then paste:

```
site type: forum
true value: 13 comments
app shows: nothing (Comments is empty)
html piece:
<PASTE THE SHORT PIECE HERE>
```

2. Click Allow / Keep. It creates a test file.
3. Model: **Claude Sonnet 5.5**, agent: **extraction-engineer**. Type `/extract-structured-signals` and paste:
```
Make the fixture from the last step pass. Keep it generic, not specific to one site.
```
4. Model: **GPT-6.1 Sol**, agent: **reviewer**: `Review the last change.` Fix blocking items with Sonnet: `Fix every blocking item from the review.`

### Step 7 - make old pages use the new rule
Old pages keep their old numbers until they are re-read. Model: **Claude Sonnet 5.5**, agent: **reprocessor**.
Type `/reprocess-stored-metadata`. Until this is done, to test quickly: stop the app, delete `wayback.db`, `wayback.db-wal`,
`wayback.db-shm` in `E:\WaybackViewer`, start the app and load the URLs again.

### Step 8 - check it
1. Run the app, sort by the same thing as in Step 1.
2. Is the page you picked now in the right place? Yes: done, commit and `git push`. No: repeat from Step 1 with the same page and tell the agent what you see.

If the script fails, paste the red error text into chat with: `The extract script failed with this error. Fix it.`

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

## Policy (agreed)
- Pyramid, most to least reliable: structured data (JSON-LD, meta, microdata) -> embedded JSON state (for example `ytInitialPlayerResponse`) -> attributes/ARIA -> visible-text median -> relative/URL dates -> content estimate -> archive signal.
- Unknown is `null`, never zero. Explicit zero stays `0`.
- Estimates are excluded from exact view/like/comment filters by default; an explicit "include estimates" option may add them. Estimated values display with a leading `~`.
- Every real-site failure gets a trimmed synthetic fixture test first (no network).
- Any change to extraction logic bumps `ExtractorVersion` (`src/WaybackViewer/Parsing/ExtractorVersion.cs`). Rows saved with an older version are re-parsed from saved HTML by `PageService.ReprocessStaleAsync` (bounded batches, keyset-paged, resumable from database state, skips rows without HTML).
- Report coverage (how many pages have each field, measured vs estimated) after each change.

---

## 9. JOB F: Make sorting good on a site (repeat per site)

### How it should work on every site
Trust order: **real counts on the page** (structured data, embedded JSON, attributes) > **visible "123 replies" text** > **estimate** (always shown with `~`, never mixed with real counts).

Rules:
- Unknown is never zero. Pages without data go **last**, in both directions.
- "Least" sorts (`commented-asc` etc.) show real low values first (a real "0 comments"), then estimated, then unknown.
- Ties: newest capture first.
- A site is "good" when **Measured% >= 60** in the log summary.

### Step 1 - measure (2 min)
Run the app, use the site 5 minutes, then in PowerShell:
```
./tools/Summarize-Logs.ps1 -LogDir E:\WaybackViewer\logs
```
Find the site under "Extraction coverage". Write down Measured%, Comments%, Views%. If Measured% >= 60, stop.

### Step 2 - one command does the rest
- Model: **Claude Sonnet 5.5**
- Type `/improve-site`, press space, paste the host, for example:
```
www.reddit.com
```
- When it asks for a sample page: open an archived page of that site, right-click > Save as > "HTML only", cut it down to the part with the counts, remove names/emails, put it in `tests/WaybackViewer.Tests/Fixtures/<host>/`.
- Click Allow / Keep when asked.

### Step 3 - prove it
Restart the app, wait until reprocessing finishes, run step 1 again. Measured% must go up. It is written to `experiments.md`.

### Checklist for every sort change
- [ ] Unknown last, both directions
- [ ] Least shows real zeros first
- [ ] Estimated after measured
- [ ] Ties newest first
- [ ] Test in `PopularitySortTests.cs`

Current priorities: `docs/popularity/improvement-plan.md`.
---

## 10. JOB G: Fix "most engaged" / "most score" ranking

Problems found (details: `scoring.md` > Known defects):
- **S1** a page showing "0 comments" ranks below a page with no data.
- **S2/S3** views, likes and score are added together raw; big view counts win.
- **S4** saved and not-yet-saved pages are on different scales.
- **S5** "score" is guessed from text; real score numbers are not read.
- **S6** "top 10/25/50%" buttons mix real and estimated values.

How it will work after the fix:
- Real numbers first, then estimates (`~`), then unknown.
- Each number is "squashed" (log) so one huge count does not win alone.
- Comments count most, then likes/score, then shares, then views.
- "Most score" uses only real score numbers; "Most commented" only comments.

### Do them in this order, one per session
1. S1
2. snapshot (creates the before/after ranking test)
3. S2
4. S5
5. S4
6. S6

For each:
- Model: **Claude Sonnet 5.5**
- Type `/fix-scoring`, press space, paste the code, for example:
```
S1
```
- If it stops and shows **before/after order**: read it. If the new order looks more right, type `approve`. If not, type `reject` and say what looks wrong.
- Then review with **GPT-6.1 Sol** + `/review-changes`.

After S2 and S5: restart the app, let reprocessing finish, run `tools/Summarize-Logs.ps1` and check Measured% went up.