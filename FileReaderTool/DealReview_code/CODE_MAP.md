# Deal review: code map

The code answers the SA checklist Staffing Plan questions for one deal. It runs twice:

- **Run 1 (Main.xaml, desktop robot):** reads the deal files once. It returns the review JSON and saves a **snapshot**: everything it read, the settings it used and the answers.
- **Run 2 (DealTool.xaml, the agent's tool):** rechecks from the snapshot only and never opens the deal files.

All business rules live in **Data/DealSettings.json**. Every number comes from code, never from the agent.

```
Main.xaml ── DealReader.RunAndSave ──► review JSON + snapshot file
                                            │ uploaded to storage bucket
Agent ── DealTool.xaml ── DealTools.Recheck ◄┘ downloaded by snapshot ID
```

---

## Folders

### Shared/ (used by both runs; one copy only)

- **DealModels.cs**: The data shapes: files, pricing model rows, NextGen totals, response text, the review and the snapshot
- **DealSettings.cs**: Loads DealSettings.json and checks it for mistakes
- **Questions.cs**: Q1, Q2, Q3, Q5, Q6, Q8 (one method each, in order) and the decision rule
- **Rules.cs**: Row rules: junior/senior, USI, EFA, lead, MD, NextGen group, SAP workstream
- **Helpers.cs**: JSON in/out, name comparison, whole-word search, number formatting

### Run1/ (reads the deal files)

- **DealReader.cs**: **Entry point for Main.xaml:** `RunAndSave` (and the older `Run`)
- **DealEngine.cs**: Runs one deal end to end and builds the review and the snapshot
- **FileClassifier.cs**: Works out which file is which, by content, never by name
- **PricingModelReader.cs**: Reads the Resourcing sheet, finding every column by its header
- **NextGenReader.cs**: Reads NextGen Raw Data (Deloitte rows only)
- **ResponseExtractor.cs**: Picks the workstream parts of the response document and lists what was left out
- **XlsxBook.cs**: Opens Excel files without Excel (low level; no rules)
- **OfficeText.cs**: Opens Word/PowerPoint files without Office (low level; no rules)

### Run2/ (the agent's tools)

- **DealTools.cs**: **Entry point for DealTool.xaml:** `Recheck`, plus `IsSnapshotId` and `ErrorJson`
- **SnapshotLoader.cs**: Reads the snapshot; gives a fresh copy for every recheck
- **WhatIfTool.cs**: `whatIf`: the official answer and the answer with a temporary change, side by side
- **SearchResponseTool.cs**: `searchResponse`: finds text in the response document, with slide or section
- **CheckWorkstreamsTool.cs**: `checkWorkstreams`: matches the agent's workstream list, leaving out `notSapWorkstreams` names and ignored sections, checks each quote is in the document; gives Q3's answer
- **CheckReadingTool.cs**: `checkReading`: which sheet, columns and rows each number came from

---

## The decision rule (Questions.cs, `QuestionResult.Decide`)

1. A reading problem (totals don't match, Excel errors, unknown geography) → **Needs review**
2. Otherwise, a definite fail → **No**
3. Otherwise, a part that couldn't be checked → **Needs review**
4. Otherwise → **Yes**

---

## Where to look

- **A threshold, level list, team mapping or SAP list:** DealSettings.json. No code change.
- **Why a question gave its answer:** its method in Questions.cs, and its `trail` in the output.
- **How a row is classed (junior, USI, team…):** Rules.cs.
- **A column or sheet isn't found:** PricingModelReader.cs or NextGenReader.cs.
- **A file is labeled wrongly:** FileClassifier.cs, and the `classifier` part of DealSettings.json.
- **The agent's tool gives an error:** DealTools.cs (the error text says what's wrong).

## Rules for changing the code

- Keep **one** copy of the Shared folder. Run 2's recheck must use exactly the same rules as Run 1.
- The `whatIf` result has `matchesFirstRun`. If it's `false`, the two runs used different code. Fix that before trusting the results.
- After any change, rerun the sample deals and compare with the saved outputs.
