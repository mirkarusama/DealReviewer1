// Questions.cs: the checklist questions Q1, Q2, Q3, Q5, Q6 and Q8, and the one decision rule (Yes / No / Needs review)
// Used by: Run 1 and Run 2.
// Each question is one method in Checks, in order. The decision rule is QuestionResult.Decide.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static DealReview.Text;

namespace DealReview
{
    public static class Answers
    {
        public const string Yes = "Yes";
        public const string No = "No";
        public const string NeedsReview = "Needs review";
    }

    /// <summary>A DealSettings.json change that would handle an issue automatically next time.</summary>
    public sealed class SettingsHint
    {
        public string Issue { get; set; }
        public string Change { get; set; }
    }

    public sealed class QuestionResult
    {
        public int No { get; set; }
        public string Question { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Answer { get; set; }            // "Yes" | "No" | "Needs review" | null (waiting for checkWorkstreams, or on hold)
        public string DecidedBy { get; set; }         // "code" | "waiting for checkWorkstreams" | "code, after checkWorkstreams" | "on hold"
        public string Rule { get; set; }
        public string Summary { get; set; }           // plain-language result, ready for the Note
        public List<string> ReviewReasons { get; set; } = new List<string>();   // what couldn't be checked (makes it Needs review)
        public Dictionary<string, object> Values { get; set; } = new Dictionary<string, object>();
        public List<string> Trail { get; set; } = new List<string>();   // how the answer was reached
        public List<string> Flags { get; set; } = new List<string>();   // anything a human should look at (Table 2)
        public List<SettingsHint> SettingsHints { get; set; } = new List<SettingsHint>();
        public AgentTask AgentTask { get; set; }

        /// <summary>A reading problem: the numbers themselves can't be trusted, so even a fail is Needs review.</summary>
        [JsonIgnore] public bool Blocked { get; set; }
        /// <summary>The summary without the answer in front.</summary>
        [JsonIgnore] public string Body { get; set; }
        /// <summary>Whether everything that could be checked passed (kept so checkWorkstreams can finish Q3).</summary>
        [JsonIgnore] public bool PassedSoFar { get; set; }

        public void Unclear(string reason) { if (!ReviewReasons.Contains(reason)) ReviewReasons.Add(reason); }
        public void Block(string reason) { Blocked = true; Unclear(reason); }
        public void Hint(string issue, string change)
        {
            if (!SettingsHints.Any(h => h.Change == change)) SettingsHints.Add(new SettingsHint() { Issue = issue, Change = change });
        }

        /// <summary>
        /// The one decision rule for every question:
        /// a reading problem → Needs review; otherwise a definite fail → No;
        /// otherwise a part that couldn't be checked → Needs review; otherwise Yes.
        /// </summary>
        public void Decide(bool passedWhatCouldBeChecked)
        {
            PassedSoFar = passedWhatCouldBeChecked;
            Answer = Blocked ? Answers.NeedsReview
                   : !passedWhatCouldBeChecked ? Answers.No
                   : ReviewReasons.Count > 0 ? Answers.NeedsReview
                   : Answers.Yes;
            Summary = $"{Answer}. {Body}";
            if (Answer == Answers.NeedsReview) Summary += " Needs review because: " + string.Join(" ", ReviewReasons);
            else if (ReviewReasons.Count > 0) Summary += " Not fully checked (the fail stands anyway): " + string.Join(" ", ReviewReasons);
        }
    }

    public sealed class AgentTask
    {
        public string Action { get; set; }                  // the tool action that finishes this question
        public string Instruction { get; set; }
        public bool? HoursCheckPassed { get; set; }         // null = step 6 couldn't run
        public List<string> PdfFiles { get; set; }          // PDFs code can't read (only when there's no Word/PowerPoint response)
        public Dictionary<string, List<string>> ModelWorkstreams { get; set; }
        public List<string> ModelTeamsNotInSapList { get; set; }
        public List<WorkstreamCandidate> Candidates { get; set; }   // SAP workstreams used as a label (heading, table row, SmartArt box), for the agent to confirm or leave out
        public List<string> AlsoMentioned { get; set; }             // SAP workstreams only named in running text, with how many paragraphs ("Procurement (3)")
        [JsonIgnore] public List<string> TeamNamesNotInSapList { get; set; } = new List<string>();
    }

    /// <summary>An SAP workstream the response uses as a label (a heading, a table row's first cell, a SmartArt box) outside the ignored sections, with where.</summary>
    public sealed class WorkstreamCandidate
    {
        public string Workstream { get; set; }
        public string Label { get; set; }                  // the best label, as the document writes it ("Data Conversion")
        public bool StaffedInPricingModel { get; set; }
        public int Mentions { get; set; }
        public List<DocBlock> Where { get; set; } = new List<DocBlock>();   // the best few mentions, text cut around the match
    }

    public static class Checks
    {
        public static List<QuestionResult> RunAll(DealContext ctx)
        {
            var s = ctx.Settings;
            var rules = new Rules(s);
            var results = new List<QuestionResult>()
            {
                Q1(ctx.Pm, rules), Q2(ctx.Pm, rules), Q3(ctx, rules), Q5(ctx.Pm, ctx.Ng, rules), Q6(ctx.Pm, rules), Q8(ctx.Pm, rules)
            };
            foreach (int n in s.OnHold)
                results.Add(new QuestionResult() { No = n, Question = Q(s, n), DecidedBy = "on hold", Summary = "On hold; not answered." });
            foreach (var r in results) if (r.Question == null) r.Question = Q(s, r.No);
            return results.OrderBy(r => r.No).ToList();
        }

        private static string Q(DealSettings s, int n) => s.Questions.TryGetValue(n, out var q) ? q : $"Question {n}";
        private static bool Between(double v, double min, double max) => v >= min - 1e-9 && v <= max + 1e-9;
        private static string Mark(bool ok) => ok ? "pass" : "fail";
        private static bool HasLevel(ResourceRow r) => r.Title.Length > 0 || r.JobLevel.Length > 0;

        /// <summary>Problems reading the source files. They make the answer Needs review, because the numbers can't be trusted.</summary>
        private static void ReadingProblems(QuestionResult q, PricingModel pm, NextGenData ng, bool usesTotalHours)
        {
            if (usesTotalHours && pm.TotalsDontMatch)
                q.Block($"The pricing model rows add up to {Hrs(pm.TotalHours)} hrs, but the sheet's own total says {Hrs(pm.SheetOwnTotal.Value)}, so a totals row may have been counted or rows missed.");
            var errors = pm.Rows.Where(r => r.HoursError).ToList();
            if (errors.Count > 0)
                q.Block($"{errors.Count} pricing model row(s) show an Excel error instead of hours and were counted as 0 ({string.Join("; ", errors.Take(3).Select(r => r.Describe()))}).");
            if (ng != null)
                foreach (var note in ng.Notes) q.Block("NextGen: " + note);
        }

        private static string LevelHint(ResourceRow r)
        {
            var (code, title, _) = Rules.LevelParts(r);
            return code.Length > 0
                ? $"add \"{code}\" to juniorLevels.codes or seniorLevels.codes"
                : $"add \"{Squash(title)}\" to juniorLevels.titles or seniorLevels.titles";
        }

        // ---------------- Q1: US/USI mix ----------------
        private static QuestionResult Q1(PricingModel pm, Rules rules)
        {
            var s = rules.Settings; var t = s.Thresholds;
            var q = new QuestionResult() { No = 1, DecidedBy = "code" };
            q.Rule = $"USI must be {t.UsiMinPct}–{t.UsiMaxPct}% and US+RoW {t.NonUsiMinPct}–{t.NonUsiMaxPct}% of total hours.";
            var rows = pm.Rows.Where(r => r.Hours != 0).ToList();
            double total = rows.Sum(r => r.Hours);
            double usi = rows.Where(r => rules.IsUsi(r, pm.HasGeography)).Sum(r => r.Hours);
            double other = total - usi;
            double usiPct = total > 0 ? R2(usi / total * 100) : 0, otherPct = total > 0 ? R2(other / total * 100) : 0;
            bool ok = Between(usiPct, t.UsiMinPct, t.UsiMaxPct) && Between(otherPct, t.NonUsiMinPct, t.NonUsiMaxPct);

            string field = pm.HasGeography ? "Geography" : "Resource Cohort";
            string Value(ResourceRow r) => pm.HasGeography ? r.Geography : r.Cohort;
            string col = pm.HasGeography ? pm.Columns["geography"] : pm.Columns.GetValueOrDefault("cohort", "?");
            string usiName = pm.HasGeography ? string.Join("/", s.UsiGeographies) : string.Join("/", s.UsiCohorts);
            q.Trail.Add($"Hours come from Resourcing > Total Engagement (column {pm.Columns["totalHours"]}): {rows.Count} rows with hours, {Hrs(total)} hrs in total.");
            q.Trail.Add($"Split by {field} (column {col}): '{usiName}' counts as USI, everything else as US+RoW (same as the old workflow).");
            q.Trail.Add($"Hours by {field}: " + string.Join(", ", rows.GroupBy(Value)
                .OrderByDescending(g => g.Sum(x => x.Hours)).Select(g => $"{(g.Key.Length == 0 ? "(blank)" : g.Key)} {Hrs(g.Sum(x => x.Hours))}")) + ".");
            q.Trail.Add($"USI {Hrs(usi)} ÷ {Hrs(total)} = {Pct(usiPct)} ({Mark(Between(usiPct, t.UsiMinPct, t.UsiMaxPct))}); US+RoW {Hrs(other)} ÷ {Hrs(total)} = {Pct(otherPct)} ({Mark(Between(otherPct, t.NonUsiMinPct, t.NonUsiMaxPct))}).");

            var blank = rows.Where(r => Value(r).Length == 0).ToList();
            if (blank.Count > 0)
                q.Flags.Add($"{blank.Count} row(s) with hours but no {field} ({Hrs(blank.Sum(b => b.Hours))} hrs) were counted as US+RoW: {string.Join("; ", blank.Take(3).Select(b => b.Describe()))}.");

            // A value on neither list could belong to either side, so the split can't be trusted.
            var known = pm.HasGeography ? s.NonUsiGeographies : s.NonUsiCohorts;
            string usiKey = pm.HasGeography ? "usiGeographies" : "usiCohorts", nonKey = pm.HasGeography ? "nonUsiGeographies" : "nonUsiCohorts";
            if (known.Count > 0)
                foreach (var g in rows.Where(r => Value(r).Length > 0 && !rules.IsUsi(r, pm.HasGeography) && !Text.InList(Value(r), known))
                                      .GroupBy(r => Squash(Value(r)), StringComparer.OrdinalIgnoreCase))
                {
                    q.Block($"{field} '{g.Key}' ({g.Count()} row(s), {Hrs(g.Sum(x => x.Hours))} hrs) is on neither the USI list nor the non-USI list; it was counted as US+RoW.");
                    q.Hint($"{field} '{g.Key}' is on neither list", $"add \"{g.Key}\" to {usiKey} or to {nonKey}");
                }
            else
                q.Trail.Add($"{nonKey} is empty in the settings, so unknown {field} values aren't checked.");
            if (total == 0) q.Unclear("No pricing model rows with hours were found.");

            ReadingProblems(q, pm, null, true);
            q.Body = $"USI is {Pct(usiPct)} ({Hrs(usi)} of {Hrs(total)} hrs) and US+RoW is {Pct(otherPct)}. {q.Rule}";
            q.Decide(total == 0 || ok);
            q.Values["usiHours"] = R2(usi); q.Values["usPlusRowHours"] = R2(other); q.Values["totalHours"] = R2(total);
            q.Values["usiPct"] = usiPct; q.Values["usPlusRowPct"] = otherPct;
            return q;
        }

        // ---------------- Q2: bulge ratio ----------------
        private static QuestionResult Q2(PricingModel pm, Rules rules)
        {
            var s = rules.Settings; var t = s.Thresholds;
            var q = new QuestionResult() { No = 2, DecidedBy = "code" };
            q.Rule = $"USI and non-USI bulge ratios (junior hours ÷ senior hours) must both be {Num(t.BulgeMin)}–{Num(t.BulgeMax)}, compared at 2 decimals.";
            double uj = 0, us = 0, nj = 0, ns = 0;
            var unknown = new List<(ResourceRow r, string label)>();
            var noLevel = pm.Rows.Where(r => r.Hours > 0 && !HasLevel(r)).ToList();

            foreach (var r in pm.Rows.Where(r => r.Hours > 0 && HasLevel(r)))
            {
                var lv = rules.Level(r);
                if (lv.Note != null) q.Trail.Add($"{r.Describe()}: {lv.Note} → {lv.Band}.");
                if (lv.Band == Band.Unknown) { unknown.Add((r, r.Title.Length > 0 ? r.Title : r.JobLevel)); continue; }
                bool usi = rules.IsUsi(r, pm.HasGeography);
                if (rules.IsEfa(r)) q.Trail.Add($"EFA {r.Describe()} counted as {lv.Band.ToString().ToLowerInvariant()} ({Hrs(r.Hours)} hrs), as the old workflow did.");
                if (lv.Band == Band.Junior) { if (usi) uj += r.Hours; else nj += r.Hours; }
                else { if (usi) us += r.Hours; else ns += r.Hours; }
            }

            double? usiRatio = us > 0 ? R2(uj / us) : (double?)null;
            double? nonRatio = ns > 0 ? R2(nj / ns) : (double?)null;
            bool usiOk = usiRatio.HasValue && Between(usiRatio.Value, t.BulgeMin, t.BulgeMax);
            bool nonOk = nonRatio.HasValue && Between(nonRatio.Value, t.BulgeMin, t.BulgeMax);

            q.Trail.Insert(0, $"Junior = codes {string.Join("/", s.JuniorLevels.Codes)} or titles {string.Join(", ", s.JuniorLevels.Titles)}; senior = codes {string.Join("/", s.SeniorLevels.Codes)} or titles {string.Join(", ", s.SeniorLevels.Titles)}. Whole values only, so 'Senior Consultant' is never read as 'Consultant'.");
            q.Trail.Insert(1, $"USI = {(pm.HasGeography ? "Geography " + string.Join("/", s.UsiGeographies) : "Resource Cohort " + string.Join("/", s.UsiCohorts))}; everything else is non-USI.");
            q.Trail.Add($"USI: {Hrs(uj)} junior ÷ {Hrs(us)} senior = {(usiRatio.HasValue ? Num(usiRatio.Value) : "n/a")} ({(usiRatio.HasValue ? Mark(usiOk) : "can't be worked out")}).");
            q.Trail.Add($"Non-USI: {Hrs(nj)} junior ÷ {Hrs(ns)} senior = {(nonRatio.HasValue ? Num(nonRatio.Value) : "n/a")} ({(nonRatio.HasValue ? Mark(nonOk) : "can't be worked out")}).");
            if (noLevel.Count > 0)
                q.Trail.Add($"Left out, no title ({noLevel.Count} rows, {Hrs(noLevel.Sum(r => r.Hours))} hrs): " +
                            string.Join("; ", noLevel.Take(5).Select(r => $"row {r.Row} {Hrs(r.Hours)} hrs{(r.Business.Length > 0 ? " (" + r.Business + (r.Geography.Length > 0 ? ", " + r.Geography : "") + ")" : "")}")) + ".");
            if (unknown.Count > 0)
            {
                q.Flags.Add($"Level not on your lists, left out of the ratio ({unknown.Count} rows, {Hrs(unknown.Sum(u => u.r.Hours))} hrs): " +
                            string.Join("; ", unknown.GroupBy(u => u.label).Select(g => $"'{g.Key}' x{g.Count()} ({Hrs(g.Sum(x => x.r.Hours))} hrs)")) + ".");
                foreach (var g in unknown.GroupBy(u => LevelHint(u.r)))
                    q.Hint($"Level '{g.First().label}' ({g.Count()} rows, {Hrs(g.Sum(x => x.r.Hours))} hrs) is on neither level list", g.Key);
            }
            if (!usiRatio.HasValue) q.Unclear("The USI bulge can't be worked out because there are no USI senior hours.");
            if (!nonRatio.HasValue) q.Unclear("The non-USI bulge can't be worked out because there are no non-USI senior hours.");

            ReadingProblems(q, pm, null, true);
            q.Body = $"USI bulge is {(usiRatio.HasValue ? Num(usiRatio.Value) : "n/a")} ({Hrs(uj)} junior ÷ {Hrs(us)} senior hrs) and non-USI is {(nonRatio.HasValue ? Num(nonRatio.Value) : "n/a")} ({Hrs(nj)} ÷ {Hrs(ns)}); both must be {Num(t.BulgeMin)}–{Num(t.BulgeMax)}.";
            q.Decide((!usiRatio.HasValue || usiOk) && (!nonRatio.HasValue || nonOk));
            q.Values["usiBulge"] = usiRatio; q.Values["nonUsiBulge"] = nonRatio;
            q.Values["usiJuniorHours"] = R2(uj); q.Values["usiSeniorHours"] = R2(us); q.Values["nonUsiJuniorHours"] = R2(nj); q.Values["nonUsiSeniorHours"] = R2(ns);
            return q;
        }

        // ---------------- shared: pricing hours by NextGen group ----------------
        private sealed class Grouped { public ResourceRow Row; public GroupResult G; }

        private static List<Grouped> GroupRows(PricingModel pm, Rules rules) =>
            pm.Rows.Where(r => r.Hours != 0).Select(r => new Grouped() { Row = r, G = rules.Group(r) }).ToList();

        public static string NameOf(ResourceRow r) => r.WorkTeam.Length > 0 ? r.WorkTeam : (r.Role.Length > 0 ? r.Role : "(no team or role)");

        private static void MappingNotes(QuestionResult q, List<Grouped> rows, Rules rules, string onlyGroup = null)
        {
            var scope = onlyGroup == null ? rows : rows.Where(x => x.G.Group == onlyGroup).ToList();
            var byRole = scope.Where(x => x.G.Source == "role keyword").ToList();
            if (byRole.Count > 0)
                q.Trail.Add($"{byRole.Count} row(s) had no known Work Team and were placed by role name: " +
                            string.Join("; ", byRole.GroupBy(x => NameOf(x.Row) + " → " + x.G.Group).Take(12).Select(g => g.Key)) + ".");
            var multi = scope.Where(x => x.G.AlsoMatched.Count > 0).GroupBy(x => NameOf(x.Row)).ToList();
            if (multi.Count > 0)
                q.Flags.Add("Role names that fit more than one group, placed by rule order: " +
                            string.Join("; ", multi.Select(x => $"'{x.Key}' → {x.First().G.Group} (also {string.Join(", ", x.First().G.AlsoMatched)})")) + ".");
        }

        // ---------------- Q3: streams adequately staffed ----------------
        /// <summary>
        /// Step 6 (hours) is done here. Step 7 needs the agent to list the promised workstreams;
        /// checkWorkstreams then matches them and gives the final answer (see Run2/CheckWorkstreamsTool.cs).
        /// </summary>
        public static QuestionResult Q3(DealContext ctx, Rules rules)
        {
            var pm = ctx.Pm; var ng = ctx.Ng;
            var s = rules.Settings; var t = s.Thresholds; string fg = s.FunctionalGroup;
            var q = new QuestionResult() { No = 3 };
            q.Rule = $"Step 6: NextGen {fg} hours (Deloitte only) must be at least {t.NextGenMinPct}% of the pricing model's {fg} hours. Step 7: every workstream the response document promises must be staffed in the pricing model. Yes only if both pass.";

            double ngF = ng.DeloitteHoursByGroup.Where(kv => rules.CanonicalNextGenGroup(kv.Key) == fg).Sum(kv => kv.Value);
            var rows = GroupRows(pm, rules);
            var func = rows.Where(x => x.G.Group == fg).ToList();
            double pmF = func.Sum(x => x.Row.Hours);
            bool step6Runs = pmF > 0;
            double pct = step6Runs ? R2(ngF / pmF * 100) : 0;
            bool step6Ok = step6Runs && pct >= t.NextGenMinPct - 1e-9;

            q.Trail.Add($"NextGen: Raw Data, Resource Group '{fg}', SourceGroupType = D → {Hrs(ngF)} hrs.");
            q.Trail.Add($"Pricing model {fg} hours: {Hrs(pmF)} hrs from " +
                        string.Join(", ", func.GroupBy(x => NameOf(x.Row)).OrderByDescending(g => g.Sum(x => x.Row.Hours)).Select(g => $"{g.Key} {Hrs(g.Sum(x => x.Row.Hours))}")) + ".");
            MappingNotes(q, rows, rules, fg);
            if (!step6Runs)
            {
                q.Unclear($"Step 6 couldn't run: no {fg} hours were found in the pricing model.");
                q.Hint($"No pricing model rows map to '{fg}'", $"add the pricing model's functional team names under \"{fg}\" in nextGenTeamMapping");
            }
            else q.Trail.Add($"Step 6: {Hrs(ngF)} ÷ {Hrs(pmF)} = {Pct(pct)} ({Mark(step6Ok)}; needs at least {t.NextGenMinPct}%).");

            // Model side of step 7, done in code: which SAP workstreams the pricing model staffs.
            var model = new Dictionary<string, List<string>>();
            var notInList = new List<string>();
            var notInListNames = new List<string>();
            foreach (var g in rows.GroupBy(x => NameOf(x.Row)).OrderByDescending(g => g.Sum(x => x.Row.Hours)))
            {
                var ws = rules.SapWorkstreams(g.Key);
                string label = $"{g.Key} ({Hrs(g.Sum(x => x.Row.Hours))} hrs)";
                if (ws.Count == 0) { notInList.Add(label); notInListNames.Add(g.Key); continue; }
                foreach (var w in ws) { if (!model.ContainsKey(w)) model[w] = new List<string>(); model[w].Add(label); }
            }

            q.Values["nextGenFunctionalHours"] = R2(ngF); q.Values["pricingFunctionalHours"] = R2(pmF);
            q.Values["functionalPct"] = step6Runs ? pct : (double?)null;
            q.Values["step6Passed"] = step6Runs ? step6Ok : (bool?)null;
            ReadingProblems(q, pm, ng, true);

            string step6Text = step6Runs
                ? $"Step 6 {Mark(step6Ok)}: NextGen {fg} hours are {Pct(pct)} of the pricing model's ({Hrs(ngF)} ÷ {Hrs(pmF)}); at least {t.NextGenMinPct}% is needed."
                : $"Step 6 couldn't run: no {fg} hours were found in the pricing model.";
            bool step6DefiniteFail = step6Runs && !step6Ok;

            if (ctx.Response == null && ctx.PdfFiles.Count == 0)
            {
                q.DecidedBy = "code";
                q.Unclear("Step 7 couldn't run: there is no response document in the folder.");
                q.Body = step6Text + " Step 7 (workstreams) was skipped because there is no response document.";
                q.Decide(!step6DefiniteFail);
                return q;
            }

            q.DecidedBy = "waiting for checkWorkstreams";
            q.Answer = null;
            q.PassedSoFar = !step6DefiniteFail;
            q.Body = step6Text;
            q.Summary = step6Text + " Step 7 waits for the agent: call checkWorkstreams to get Q3's answer.";
            q.AgentTask = new AgentTask()
            {
                Action = "checkWorkstreams",
                HoursCheckPassed = step6Runs ? step6Ok : (bool?)null,
                ModelWorkstreams = model,
                ModelTeamsNotInSapList = notInList,
                TeamNamesNotInSapList = notInListNames
            };
            if (ctx.Response != null)
            {
                var (candidates, also) = WorkstreamCandidates(ctx.ResponseBlocks ?? new List<DocBlock>(), rules, model);
                q.AgentTask.Candidates = candidates;
                q.AgentTask.AlsoMentioned = also;
                q.AgentTask.Instruction =
                    "Step 7: code counts every agentTask.candidates entry as promised: each is an SAP workstream the response uses as a heading, a table row label or a SmartArt box. " +
                    "Take one out only if where[] shows it isn't something Deloitte delivers on this deal (for example a client's own role or another client's project): list it in notPromised with a one-sentence reason. " +
                    "Then add any other SAP workstream the response promises that isn't a candidate (agentTask.alsoMentioned lists the ones only named in running text). Use searchResponse to look further. " +
                    "Check responseDocument.leftOut too: pictures can't be read, so a team chart pasted as a picture may be missed. " +
                    "Then call checkWorkstreams with the workstreams you added (promised: name, location, a quote copied exactly from the document) and any notPromised candidates. " +
                    "Code does the matching and gives Q3's answer; don't match or decide yourself.";
            }
            else
            {
                q.AgentTask.PdfFiles = ctx.PdfFiles.ToList();
                q.AgentTask.Instruction =
                    "Step 7: there is no Word or PowerPoint response, only PDFs, which code can't read: " + string.Join(", ", ctx.PdfFiles) + ". " +
                    "Call checkWorkstreams with {\"couldNotRead\": true, \"source\": \"<the PDF file name>\"}.";
            }
            return q;
        }

        private const int MaxCandidateMentions = 3;
        private const int CandidateSnippetChars = 240;
        private static readonly string[] StructureWords = { "Work Stream", "Workstream", "Work Streams", "Workstreams", "Tower", "Towers", "Team", "Scope" };

        /// <summary>
        /// Step 7's checklist for the agent: every SAP workstream the response uses as a label — a heading, the first cell
        /// of a table row, or a SmartArt box — outside the ignored sections, with its best mentions (rows of team / scope /
        /// work stream tables first). Labels are where a response lists what it delivers ("Data Conversion | ...");
        /// running text mentions everything ("billing", "procurement"), so those go in the second list, by name only.
        /// The agent confirms or leaves out each candidate, so every run starts from the same list.
        /// </summary>
        private static (List<WorkstreamCandidate>, List<string>) WorkstreamCandidates(List<DocBlock> blocks, Rules rules, Dictionary<string, List<string>> model)
        {
            // Every label in the document, with a rank: 0 = row of a team / scope / work stream table, 1 = other table row or SmartArt box, 2 = heading.
            var labels = new List<(int rank, int order, string label, DocBlock shown)>();
            var prose = new List<string>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (rules.IgnoredSection(b.Location) != null) continue;
                if (b.Kind == "heading") labels.Add((2, i, b.Text, new DocBlock() { Kind = b.Kind, Location = b.Location, Text = Around(b.Text, 0) }));
                else if (b.Kind == "table")
                {
                    var rows = (b.Text ?? "").Split('\n');
                    bool structure = StructureWords.Any(w => Text.HasWord(rows[0], w) || Text.HasWord(Rules.SectionOf(b.Location), w));
                    foreach (var row in rows)
                    {
                        string first = row.Split(new[] { " | " }, StringSplitOptions.None)[0];
                        labels.Add((structure ? 0 : 1, i, first, new DocBlock() { Kind = "table row", Location = b.Location, Text = Around(row, 0) }));
                    }
                }
                else if (b.Kind == "smartart")
                    foreach (var box in (b.Text ?? "").Split(new[] { " | " }, StringSplitOptions.None))
                        labels.Add((1, i, box, new DocBlock() { Kind = b.Kind, Location = b.Location, Text = Around(box, 0) }));
                else prose.Add(b.Text);
            }

            var list = new List<WorkstreamCandidate>();
            var also = new List<string>();
            foreach (var kv in rules.Settings.SapWorkstreams)
            {
                // A label counts only if this workstream is what it names ("Master Data" names MDG, not Data).
                var hits = labels.Where(l => rules.SapWorkstreams(l.label).Contains(kv.Key)).ToList();
                if (hits.Count == 0)
                {
                    var keywords = kv.Value.Append(kv.Key).ToList();
                    int n = prose.Count(t => keywords.Any(k => Text.HasWord(t, k)));
                    if (n > 0) also.Add($"{kv.Key} ({n})");
                    continue;
                }
                var best = hits.OrderBy(h => h.rank).ThenBy(h => h.order).First();
                list.Add(new WorkstreamCandidate()
                {
                    Workstream = kv.Key,
                    Label = Text.Squash(best.label),
                    StaffedInPricingModel = model.ContainsKey(kv.Key),
                    Mentions = hits.Count,
                    Where = hits.OrderBy(h => h.rank).ThenBy(h => h.order).Select(h => h.shown)
                                .GroupBy(d => d.Location + "\u0001" + d.Text).Select(g => g.First())
                                .Take(MaxCandidateMentions).ToList()
                });
            }
            return (list, also);
        }

        /// <summary>The text around position at, cut to CandidateSnippetChars, on whitespace.</summary>
        private static string Around(string text, int at)
        {
            text = Text.Squash(text);
            if (text.Length <= CandidateSnippetChars) return text;
            at = Math.Max(0, Math.Min(at, text.Length - 1));
            int start = Math.Max(0, at - CandidateSnippetChars / 3);
            int end = Math.Min(text.Length, start + CandidateSnippetChars);
            start = Math.Max(0, end - CandidateSnippetChars);
            if (start > 0) { int sp = text.IndexOf(' ', start); if (sp > 0 && sp < at) start = sp + 1; }
            if (end < text.Length) { int sp = text.LastIndexOf(' ', end); if (sp > at) end = sp; }
            return (start > 0 ? "… " : "") + text.Substring(start, end - start) + (end < text.Length ? " …" : "");
        }

        // ---------------- Q5: staffing hours vs NextGen ----------------
        private static QuestionResult Q5(PricingModel pm, NextGenData ng, Rules rules)
        {
            var t = rules.Settings.Thresholds;
            var q = new QuestionResult() { No = 5, DecidedBy = "code" };
            q.Rule = $"NextGen hours (Deloitte only) must be at least {t.NextGenMinPct}% of pricing model hours, overall and for each NextGen resource group.";
            double ngTotal = ng.DeloitteTotal, pmTotal = pm.TotalHours;
            bool overallRuns = pmTotal > 0;
            double overallPct = overallRuns ? R2(ngTotal / pmTotal * 100) : 0;
            bool overallOk = overallRuns && overallPct >= t.NextGenMinPct - 1e-9;
            if (overallRuns) q.Trail.Add($"Overall: NextGen {Hrs(ngTotal)} ÷ pricing {Hrs(pmTotal)} = {Pct(overallPct)} ({Mark(overallOk)}).");
            else q.Unclear("The overall check couldn't run: the pricing model has no hours.");

            var rows = GroupRows(pm, rules);
            var byGroup = rows.Where(x => x.G.Group != null).GroupBy(x => x.G.Group).ToDictionary(g => g.Key, g => g.Sum(x => x.Row.Hours));
            bool hasWorkTeams = pm.Rows.Any(r => r.WorkTeam.Length > 0);
            var ngByCanonical = new Dictionary<string, double>();
            foreach (var kv in ng.DeloitteHoursByGroup)
            {
                string c = rules.CanonicalNextGenGroup(kv.Key);
                if (c == null)
                {
                    q.Flags.Add($"NextGen group '{kv.Key}' ({Hrs(kv.Value)} hrs) isn't in the team mapping, so it wasn't compared.");
                    q.Hint($"NextGen group '{kv.Key}' isn't in the team mapping",
                           hasWorkTeams
                               ? $"add \"{kv.Key}\": [<its pricing model team names>] to nextGenTeamMapping, or \"{kv.Key}\": \"<existing group>\" to nextGenGroupAliases"
                               : $"add \"{kv.Key}\": \"<existing group>\" to nextGenGroupAliases, or make it a new group in nextGenTeamMapping with a roleKeywordRules entry for its roles (this pricing model has no Work Team values)");
                    continue;
                }
                ngByCanonical[c] = ngByCanonical.GetValueOrDefault(c) + kv.Value;
            }

            var failing = new List<string>();
            var perGroup = new List<Dictionary<string, object>>();
            foreach (var kv in ngByCanonical.OrderByDescending(k => k.Value))
            {
                double pmH = byGroup.GetValueOrDefault(kv.Key);
                if (pmH == 0)
                {
                    q.Trail.Add($"{kv.Key}: NextGen {Hrs(kv.Value)} vs pricing 0 → no staffed hours, so it can't be compared.");
                    q.Unclear($"No staffed hours for '{kv.Key}' in the pricing model, while NextGen estimates {Hrs(kv.Value)} hrs.");
                    perGroup.Add(new Dictionary<string, object>() { ["group"] = kv.Key, ["nextGenHours"] = R2(kv.Value), ["pricingHours"] = 0.0, ["pct"] = null, ["passed"] = null });
                    continue;
                }
                double p = R2(kv.Value / pmH * 100);
                bool ok = p >= t.NextGenMinPct - 1e-9;
                if (!ok) failing.Add($"{kv.Key} {Pct(p)} (gap {Num(R2(t.NextGenMinPct - p))} points)");
                q.Trail.Add($"{kv.Key}: NextGen {Hrs(kv.Value)} ÷ pricing {Hrs(pmH)} = {Pct(p)} ({Mark(ok)}).");
                perGroup.Add(new Dictionary<string, object>() { ["group"] = kv.Key, ["nextGenHours"] = R2(kv.Value), ["pricingHours"] = R2(pmH), ["pct"] = p, ["passed"] = ok });
            }

            var unmapped = rows.Where(x => x.G.Group == null).ToList();
            if (unmapped.Count > 0)
            {
                q.Flags.Add($"{unmapped.Count} pricing row(s) ({Hrs(unmapped.Sum(x => x.Row.Hours))} hrs) match no NextGen group, so they count in the overall total only: " +
                            string.Join("; ", unmapped.GroupBy(x => NameOf(x.Row)).Select(g => $"{g.Key} {Hrs(g.Sum(x => x.Row.Hours))}")) + ".");
                foreach (var g in unmapped.GroupBy(x => NameOf(x.Row)).OrderByDescending(g => g.Sum(x => x.Row.Hours)).Take(5))
                {
                    if (g.Key == "(no team or role)") continue;
                    if (g.All(x => rules.IsEfa(x.Row))) continue;   // EFA never has a NextGen group
                    string name = Text.StripWavePrefix(g.Key).Trim();
                    string hrs = Hrs(g.Sum(x => x.Row.Hours));
                    if (g.All(x => x.Row.WorkTeam.Length == 0))
                        q.Hint($"Role '{g.Key}' ({hrs} hrs) has no Work Team and matches no NextGen group",
                               $"add a keyword from \"{name}\" to roleKeywordRules under its NextGen group, or leave it if the role has no NextGen group");
                    else
                        q.Hint($"Pricing team '{g.Key}' ({hrs} hrs) matches no NextGen group",
                               $"add \"{name}\" under its group in nextGenTeamMapping, or leave it if the team has no NextGen group");
                }
            }
            foreach (var kv in byGroup.Where(kv => !ngByCanonical.ContainsKey(kv.Key)))
                q.Trail.Add($"Pricing group '{kv.Key}' ({Hrs(kv.Value)} hrs) has no NextGen estimate.");
            MappingNotes(q, rows, rules);

            ReadingProblems(q, pm, ng, true);
            q.Body = (overallRuns ? $"Overall NextGen is {Pct(overallPct)} of pricing hours ({Hrs(ngTotal)} ÷ {Hrs(pmTotal)}; needs {t.NextGenMinPct}%)." : "The pricing model has no hours.") +
                     (failing.Count > 0 ? $" Groups below {t.NextGenMinPct}%: {string.Join(", ", failing)}." : " Every compared group is at or above the threshold.");
            q.Decide((!overallRuns || overallOk) && failing.Count == 0);
            q.Values["overallPct"] = overallRuns ? overallPct : (double?)null; q.Values["nextGenHours"] = R2(ngTotal); q.Values["pricingHours"] = R2(pmTotal);
            q.Values["groups"] = perGroup;
            return q;
        }

        // ---------------- Q6: EFA and PPMD hours ----------------
        private static QuestionResult Q6(PricingModel pm, Rules rules)
        {
            var t = rules.Settings.Thresholds;
            var q = new QuestionResult() { No = 6, DecidedBy = "code" };
            q.Rule = $"Each EFA row needs {Num(t.EfaMinHoursPerPeriod)}–{Num(t.EfaMaxHoursPerPeriod)} hrs in each of the first {t.EfaPeriodsToCheck} periods after EFA hours start, and Managing Director hours must be {Num(t.MdMinPct)}–{Num(t.MdMaxPct)}% of total hours. Both must pass.";
            int reasonsBefore = q.ReviewReasons.Count;

            // EFA
            var efa = pm.Rows.Where(rules.IsEfa).ToList();
            var efaWithHours = efa.Where(r => r.Hours > 0).ToList();
            bool efaFail = false;
            string efaText;
            if (efa.Count == 0) { efaText = "no EFA role found"; efaFail = true; }
            else if (efaWithHours.Count == 0) { efaText = "EFA role found but it has no hours"; efaFail = true; }
            else if (pm.Periods.Count == 0)
            {
                efaText = "EFA found, but the period columns couldn't be read";
                q.Unclear("EFA hours per period couldn't be checked: the 4-week period columns weren't found.");
            }
            else
            {
                int start = efaWithHours.Select(r => Array.FindIndex(r.PeriodHours, h => h > 0)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
                if (start < 0)
                {
                    efaText = "EFA rows have total hours but none in the period columns";
                    q.Unclear("EFA rows have total hours but none in the period columns, so hours per period couldn't be checked.");
                }
                else
                {
                    int end = Math.Min(start + t.EfaPeriodsToCheck, pm.Periods.Count);
                    var window = Enumerable.Range(start, end - start).ToList();
                    string span = $"{pm.Periods[window.First()].Label}–{pm.Periods[window.Last()].Label}";
                    if (window.Count < t.EfaPeriodsToCheck)
                        q.Unclear($"Only {window.Count} of the {t.EfaPeriodsToCheck} periods to check exist after EFA hours start ({span}).");
                    var parts = new List<string>();
                    foreach (var r in efaWithHours)
                    {
                        var vals = window.Select(i => r.PeriodHours[i]).ToList();
                        bool rowOk = vals.All(v => Between(v, t.EfaMinHoursPerPeriod, t.EfaMaxHoursPerPeriod));
                        if (!rowOk) efaFail = true;
                        parts.Add($"{r.Describe()}: {string.Join(", ", vals.Select(Hrs))} ({Mark(rowOk)})");
                        double sum = r.PeriodHours.Sum();
                        if (Math.Abs(sum - r.Hours) > 0.5)
                            q.Unclear($"Period hours for EFA {r.Describe()} add up to {Hrs(sum)}, not its total of {Hrs(r.Hours)}, so the period columns may be misread.");
                    }
                    q.Trail.Add($"EFA rows found by 'EFA' / 'Engagement Financial Advisor' in cohort, team, role or title: {efa.Count}. Periods checked: {span}.");
                    q.Trail.AddRange(parts);
                    efaText = $"EFA hours per period in {span}: " + string.Join("; ", efaWithHours.Select(r => string.Join(", ", window.Select(i => Hrs(r.PeriodHours[i])))));
                }
            }
            bool efaUnclear = q.ReviewReasons.Count > reasonsBefore;
            if (efa.Count == 0 || efaWithHours.Count == 0) q.Trail.Add("EFA: " + efaText + ".");

            // Managing Director
            var md = pm.Rows.Where(rules.IsManagingDirector).ToList();
            double mdHours = md.Sum(r => r.Hours), total = pm.TotalHours;
            double mdPct = total > 0 ? R2(mdHours / total * 100) : 0;
            bool mdOk = md.Count > 0 && Between(mdPct, t.MdMinPct, t.MdMaxPct);
            if (md.Count == 0) q.Trail.Add("Managing Director: none found.");
            else q.Trail.Add($"Managing Director rows: {md.Count} ({string.Join("; ", md.Select(r => $"row {r.Row} {Hrs(r.Hours)} hrs"))}); {Hrs(mdHours)} ÷ {Hrs(total)} = {Pct(mdPct)} ({Mark(mdOk)}).");
            var ppOnly = pm.Rows.Where(r => r.Hours > 0 && !rules.IsManagingDirector(r) && Rules.LevelParts(r).code == "LPP").ToList();
            if (ppOnly.Count > 0) q.Trail.Add($"Partner/Principal (LPP) hours not counted, as agreed (MD only): {Hrs(ppOnly.Sum(r => r.Hours))} hrs.");
            foreach (var note in pm.PeriodNotes) q.Flags.Add("Source file: " + note);

            ReadingProblems(q, pm, null, true);
            string efaMark = efaFail ? "fail" : efaUnclear ? "not fully checked" : "pass";
            string mdText = md.Count == 0 ? "no Managing Director found" : $"MD hours are {Pct(mdPct)} of total ({Hrs(mdHours)} ÷ {Hrs(total)})";
            q.Body = $"EFA: {efaText} (needs {Num(t.EfaMinHoursPerPeriod)}–{Num(t.EfaMaxHoursPerPeriod)} each) → {efaMark}. {mdText} (needs {Num(t.MdMinPct)}–{Num(t.MdMaxPct)}%) → {Mark(mdOk)}.";
            q.Decide(!efaFail && mdOk);
            q.Values["efaRows"] = efa.Count; q.Values["efaPassed"] = efaFail ? false : efaUnclear ? (bool?)null : true;
            q.Values["mdHours"] = R2(mdHours); q.Values["mdPct"] = mdPct; q.Values["mdPassed"] = mdOk;
            return q;
        }

        // ---------------- Q8: lead FTE roles ----------------
        /// <summary>25% of the practitioners, rounded to whole people. Exactly half-way (18 → 4.5) accepts both 4 and 5.</summary>
        public static List<int> AcceptableLeadCounts(double needed)
        {
            double floor = Math.Floor(needed + 1e-9);
            if (Math.Abs(needed - floor) < 1e-9) return new List<int>() { (int)floor };
            if (Math.Abs(needed - floor - 0.5) < 1e-9) return new List<int>() { (int)floor, (int)floor + 1 };
            return new List<int>() { (int)Math.Round(needed, MidpointRounding.AwayFromZero) };
        }

        private static QuestionResult Q8(PricingModel pm, Rules rules)
        {
            var s = rules.Settings; var t = s.Thresholds;
            var q = new QuestionResult() { No = 8, DecidedBy = "code" };
            q.Rule = t.LeadPctMustBeExact
                ? $"SC + M rows must be {t.LeadPct}% of practitioner rows, rounded to the nearest whole person (exactly half-way accepts both)."
                : $"SC + M rows must be at least {t.LeadPct}% of practitioner rows.";
            var practitioners = pm.Rows.Where(r => r.Hours > 0 && HasLevel(r)).ToList();
            var leads = practitioners.Where(rules.IsLead).ToList();
            int n = practitioners.Count, l = leads.Count;
            double pct = n > 0 ? R2(l * 100.0 / n) : 0;
            double needed = t.LeadPct * n / 100.0;
            var accept = AcceptableLeadCounts(needed);
            string neededText = needed.ToString("0.##", CultureInfo.InvariantCulture);
            bool ok = n > 0 && (t.LeadPctMustBeExact ? accept.Contains(l) : pct >= t.LeadPct - 1e-9);

            q.Trail.Add($"Practitioners = rows with a title and hours: {n} (EFA rows included, rows with no title left out, as the old workflow did).");
            q.Trail.Add($"Leads = codes {string.Join("/", s.LeadLevels.Codes)} or titles {string.Join(", ", s.LeadLevels.Titles)} (Senior Managers not counted): {l} rows (" +
                        string.Join(", ", leads.GroupBy(r => { var p = Rules.LevelParts(r); return p.code.Length > 0 ? p.code : p.title; })
                                                .OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}")) + ").");
            q.Trail.Add(t.LeadPctMustBeExact
                ? $"{t.LeadPct}% of {n} = {neededText} people, so {string.Join(" or ", accept)} lead(s) pass; found {l} = {Pct(pct)} ({Mark(ok)})."
                : $"{l} ÷ {n} = {Pct(pct)}; at least {t.LeadPct}% ({neededText} people) needed ({Mark(ok)}).");
            if (n == 0) q.Unclear("No practitioner rows (a title and hours) were found.");

            ReadingProblems(q, pm, null, false);
            int diff = l - accept.OrderBy(a => Math.Abs(a - l)).First();
            string gap = diff == 0 ? "on target" : diff > 0 ? $"{diff} more than needed" : $"{-diff} short";
            q.Body = t.LeadPctMustBeExact
                ? $"SC + M = {l} of {n} practitioners ({Pct(pct)}); the rule needs {string.Join(" or ", accept)} ({t.LeadPct}% of {n} = {neededText}), so {gap}."
                : $"SC + M = {l} of {n} practitioners ({Pct(pct)}); the rule is at least {t.LeadPct}% ({neededText} people), a gap of {Num(R2(pct - t.LeadPct))} points.";
            q.Decide(n == 0 || ok);
            q.Values["practitioners"] = n; q.Values["leads"] = l; q.Values["leadPct"] = pct;
            q.Values["leadsNeeded"] = t.LeadPctMustBeExact ? accept : null;
            return q;
        }
    }
}
