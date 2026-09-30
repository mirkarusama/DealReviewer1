# Deal Review Agent

Answers the SA checklist **Staffing Plan** questions for one SAP deal. It reads the deal's pricing model, NextGen effort estimate and response document, then returns **Yes**, **No** or **Needs review** for each question, with a note and a list of issues for a reviewer.

The design rests on one split:

- **Code calculates.** Every number, threshold and answer comes from C#. Business rules live in `DealSettings.json`.
- **The agent investigates.** It reads the response document to list the SAP workstreams Deloitte promised (Q3), and rechecks flagged issues with its tool. It never does maths or sets an answer itself.
- **Code writes the result.** `FinalTables` rebuilds both tables from the code's own text and keeps only the agent's decisions.

---

## How it works

```
Main.xaml  (robot that can see the deal folder)
 1. Input Dialog ........ deal folder path
 2. DealReader.RunAndSave  review JSON + snapshot file
 3. Upload Storage File . snapshot -> bucket as <id>.json
 4. Run Job ............. start the agent with snapshotId
        |
        v
    Agent  <---->  DealTool.xaml  (Run 2)
        |          downloads the snapshot by ID,
        |          runs one check, returns JSON
        v
 5. FinalTables.Build ... final table1 + table2
 6. Write Text File ..... FileContent1.txt
```

**Run 1 (Main.xaml)** reads the deal files once. It answers every question it can and saves a **snapshot**: one JSON file with everything it read, the settings it used and its answers. The snapshot ID is 32 hex characters.

**Run 2 (DealTool.xaml)** is the agent's tool. It works only from the snapshot and never opens the deal files. As a result:

- the tool doesn't need access to the deal folder
- every recheck uses exactly the data and settings Run 1 used
- the agent only has to carry a short ID

**Q3 is finished by the agent's tool.** After Run 1, Q3 has no answer yet (`decidedBy: "waiting for checkWorkstreams"`). The agent lists the workstreams the response document promises and sends them to `checkWorkstreams`. Code then does the matching and gives Q3's answer.

---

## Questions answered

The limits below are the defaults in `DealSettings.json`.

- **Q1 · US/USI mix.** USI hours must be 70–80% of total hours, and US + rest of world 20–30%.
- **Q2 · Bulge ratio.** Junior hours ÷ senior hours must be 1.9–2.1, for USI and non-USI separately.
- **Q3 · Streams adequately staffed.**
  - Step 6: NextGen Functional hours (Deloitte only) must be at least 95% of the pricing model's Functional hours.
  - Step 7: every SAP workstream the response document promises must be staffed in the pricing model.
- **Q4 · Delivery and competency pools.** On hold, not answered.
- **Q5 · Hours vs NextGen.** NextGen hours (Deloitte only) must be at least 95% of pricing model hours, overall and for each resource group.
- **Q6 · EFA and PPMD hours.**
  - Each EFA row needs 5–10 hrs in each of the first 4 periods after EFA hours start.
  - Managing Director hours must be 0.3–0.5% of total hours. Partner/Principal (LPP) hours aren't counted.
- **Q7 · Ramp-ups and ramp-downs.** On hold, not answered.
- **Q8 · Lead roles.** Senior Consultant + Manager rows must be 25% of practitioner rows, rounded to the nearest whole person. When 25% falls exactly half-way, both whole numbers pass.

**Decision rule (the same for every question):**

1. A reading problem (totals don't match, Excel errors, unknown geography) → **Needs review**
2. Otherwise, a definite fail → **No**
3. Otherwise, a part that couldn't be checked → **Needs review**
4. Otherwise → **Yes**

---

## Inputs: the deal folder

Put one deal's files in one local folder. Files are recognised by **what's inside them, never by their names**.

- **Pricing model** (required, exactly one): `.xlsx` / `.xlsm` with the sheets `Engagement Metrics` and `Resourcing`.
  - Both the old layout (header row with "Level") and the new layout (header row with "Title" and "Geography") are read.
  - Columns are found by their header labels.
  - Total hours come from "Hours by Fiscal Year" > "Total Engagement".
- **NextGen effort file** (required, exactly one): `.xlsx` / `.xlsm` with a `Raw Data` sheet containing the columns `Resource Group`, `SourceGroupType` and `Effort`. Only rows with `SourceGroupType = D` (Deloitte) are counted.
- **Response document** (optional, at most one): a `.docx` or `.pptx` that mentions Deloitte at least 10 times. It is needed only for Q3 step 7. Without it, Q3 comes out as Needs review, or No if step 6 fails.
- **RFP**: recognised but not used.
- **PDF**: can't be read. If the response is only a PDF, Q3 step 7 can't run.
- **SA readout reports** and other files are ignored. Office lock files (`~$…`) are skipped.

The run stops before the agent starts if any of these happen: a required file is missing, there are duplicates, or a document can't be identified as either the RFP or the response.

---

## Output

`FileContent1.txt` in the project folder, overwritten on every run. It contains:

```
Agent output: <the agent's raw JSON>
Final json: <FinalTables result>
```

The final JSON has three parts:

- `table1`: one row per answered question, with `Question`, `Answer` and `Note`.
- `table2`: the questions that need a look (flags, review reasons, settings hints, or Needs review). Each row has the same fields plus `Issues`.
- `corrections`: every place where the agent's text differed from the code's and was replaced.

If FinalTables can't build the tables, it returns `{"error": …, "agentOutput": …}`, so the agent's output isn't lost.

---

## Setup

### Orchestrator (folder `SRB Process`)

- **Asset** `DealStorageBucketName` (text): the storage bucket's name. Both workflows read it.
- **Storage bucket** with that name, in the same folder.
- **Agent process** `Agent`, in the folder `SRB Process/SRBReviewAgent`:
  - Input: `snapshotId` (string).
  - Output: `table1`, `table2`, `q3Request`.
  - System prompt: the agent prompt file (role, rules, Q3 steps, recheck rules, output format).
  - Tool: DealTool.xaml.
- **DealTool.xaml arguments:**
  - `in_Action` (in, String): the tool action.
  - `in_SnapshotId` (in, String): the snapshot ID.
  - `in_Request` (in, String): JSON text with the details.
  - `out_Result` (out, String): JSON result.
- **Run Job account:** the unattended account in Main.xaml's Run Job activity. It is currently hard-coded, so change it to your own.

### Project

- The C# code lives in `DealReview_code/` inside the project. It is called directly from Assign activities.
- `Main.xaml` loads the settings from `DealReview_code\DealSettings.json`, a path relative to the project folder.
- The code reads Excel, Word and PowerPoint files directly. It needs neither Office nor extra NuGet packages.
- `Main.xaml` also uses `Newtonsoft.Json` to turn the agent's output into text.

---

## Running it

1. Copy the deal's files into one local folder.
2. Run `Main.xaml` and enter the folder path.
3. Check the log line `filesPresent=… | id=… | path=… | issues=…`.
4. Open `FileContent1.txt` for the result.

If the files aren't right, the log shows `Files not right: …` and no output file is written.

---

## Settings: `DealSettings.json`

Every business rule is here. Changing a rule never needs a code change. Comments (`// …`) and trailing commas are allowed, and names are matched ignoring case and extra spaces.

- `questions`, `onHold`: question texts, and which question numbers are skipped.
- `juniorLevels`, `seniorLevels`: level titles and codes for the bulge ratio (Q2). Values must match whole, so "Senior Consultant" is never read as "Consultant".
- `leadLevels`: which levels count as leads (Q8).
- `managingDirectorLevels`: which levels count as Managing Directors (Q6).
- `usiGeographies`, `usiCohorts`: what counts as USI (new layout / old layout).
- `nonUsiGeographies`, `nonUsiCohorts`: known non-USI values. A geography on neither list makes Q1 Needs review.
- `efaKeywords`: how EFA rows are found.
- `thresholds`: every percentage and ratio limit.
- `functionalGroup`, `nextGenTeamMapping`, `nextGenGroupAliases`: how pricing model teams map to NextGen resource groups (Q3, Q5). Wave prefixes such as "1 - " are ignored.
- `roleKeywordRules`: fallback grouping by role name when a row has no known team. Rules are checked top to bottom.
- `sapWorkstreams`: SAP workstreams and their synonyms (Q3 step 7, and picking the response document's evidence).
- `classifier`: how files are recognised.

The settings are checked when they load. A team mapped to two groups, or a group name that doesn't exist, stops the run with a plain message.

---

## The agent's tool actions

Every call passes `in_Action`, `in_SnapshotId` and `in_Request` (valid JSON, `"{}"` when there are no details).

- **`getReview`** `{}`: Run 1's review, read from the snapshot. The agent's first call.
- **`whatIf`** `{"questions":[2],"changes":[{"type":"addJuniorLevel","value":"Jr Staff"}]}`: the official answer next to the answer with a temporary change, plus the settings change that would make it permanent. It is for table2 only and never replaces an answer.
  - The code supports these change types: `addJuniorLevel`, `addSeniorLevel`, `addLeadLevel`, `addUsiGeography`, `addNonUsiGeography`, `mapTeam`, `mapNextGenGroup`, `addSapSynonym`, `leaveOutRows`.
  - The system prompt lets the agent use only `addJuniorLevel`, `addSeniorLevel`, `addUsiGeography`, `addNonUsiGeography` and `leaveOutRows`.
  - `matchesFirstRun: false` means the tool is running different code from Run 1. Its results must not be used.
- **`searchResponse`** `{"words":["team","work stream"]}`, `{"location":"Slide 70"}`, or `{}` for an outline: finds text in the response document, with its slide or section.
- **`checkWorkstreams`** `{"promised":[{"name":"…","location":"…","quote":"…"}]}` or `{"couldNotRead":true,"source":"…"}`: matches the promised workstreams to the pricing model and returns Q3's finished answer.
- **`checkReading`** `{}` or `{"rows":[245,246]}`: which sheet, columns and rows every number came from, plus anything that looks off (totals rows, Excel errors, hidden sheets).

The tool never throws. Every problem comes back as `{"ok": false, "action": …, "error": …, "hint": …}`. DealTool.xaml checks the snapshot ID's format before it downloads anything.

---

## Project structure

```
Main.xaml               Run 1: the robot workflow
DealTool.xaml           Run 2: the agent's tool
DealReview_code/
  CODE_MAP.md           map of the code and where to look
  DealSettings.json     every business rule
  Shared/               used by both runs (one copy only)
    DealModels.cs       data shapes: rows, files, review, snapshot
    DealSettings.cs     loads and checks DealSettings.json
    Questions.cs        Q1-Q8 and the decision rule
    Rules.cs            junior/senior, USI, EFA, lead, MD, groups
    Helpers.cs          JSON, name matching, number formats
  Run1/                 reads the deal files
    DealReader.cs       entry point for Main.xaml
    DealEngine.cs       runs one deal end to end
    FileClassifier.cs   which file is which
    PricingModelReader.cs
    NextGenReader.cs
    ResponseExtractor.cs  workstream evidence for the agent
    FinalTables.cs      final tables after the agent
    XlsxBook.cs         reads Excel without Excel
    OfficeText.cs       reads Word/PowerPoint without Office
  Run2/                 the agent's tools
    DealTools.cs        entry point for DealTool.xaml
    SnapshotLoader.cs
    WhatIfTool.cs
    SearchResponseTool.cs
    CheckWorkstreamsTool.cs
    CheckReadingTool.cs
```

See `DealReview_code/CODE_MAP.md` for more on each file.

---

## Troubleshooting

- **"Missing: …" or "Found 2 files that look like …"**: fix the deal folder. If a file is labelled wrongly, see `FileClassifier.cs` and the `classifier` settings.
- **"DealReader failed: …"**: the reading itself failed, for example a sheet, header or column wasn't found. The message names what was missing. See `PricingModelReader.cs` or `NextGenReader.cs`.
- **A threshold, level, team or workstream is wrong**: change `DealSettings.json`, not the code.
- **Why a question got its answer**: read its `trail` in the review, and its method in `Questions.cs`.
- **Tool error "Snapshot ID … isn't valid"**: the agent didn't pass the 32-character ID exactly.
- **Tool error "Couldn't get the snapshot from the bucket"**: check the asset, the bucket and the folder, and that the upload in Main.xaml worked.
- **`matchesFirstRun: false` from whatIf**: the tool is running an older or newer copy of the code. Republish so both runs use the same code.

---

## Changing the code

- Keep **one** copy of `Shared/`. Run 2's rechecks must use exactly the same rules as Run 1.
- After any change, republish the tool, then rerun the sample deals and compare them with their saved outputs.
- Put new business rules in `DealSettings.json`, not in code.

---

## Known issues

- `CODE_MAP.md` and a comment in `DealReader.cs` still give the settings path as `Data\DealSettings.json`. The workflow actually uses `DealReview_code\DealSettings.json`.
- `Main.xaml` still has a commented-out step from an older version: `FileReaderTool` with a hard-coded path and an undeclared `summaryJson` variable. The `FileReaderTool` imports are also still there.
- When the files aren't right, the run only logs a warning and writes no output file.
- The Run Job account is hard-coded.
- Snapshot files build up in `%TEMP%\DealSnapshots`, and the tool's downloaded copies in `%TEMP%`. Nothing deletes them.
- PDFs, and pictures inside the response document, can't be read. Pictures are listed in the review under `responseDocument.leftOut`.
