// WhatIfTool.cs: whatIf: reruns the checks with a temporary change and shows the official answer next to the what-if answer
// Used by: Run 2.
// Request: {"questions":[2], "changes":[{"type":"addJuniorLevel","value":"Jr Staff"}]}. For Table 2 only, never the official answer.

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
    public sealed class WhatIfRequest
    {
        public List<int> Questions { get; set; } = new List<int>();
        public List<WhatIfChange> Changes { get; set; } = new List<WhatIfChange>();
    }

    public sealed class WhatIfChange
    {
        public string Type { get; set; }
        public string Value { get; set; }
        public string Group { get; set; }
        public List<int> Rows { get; set; } = new List<int>();
    }

    /// <summary>whatIf: the official answer and the answer with the changes, side by side.</summary>
    public static class WhatIfTool
    {
        public static readonly string[] ChangeTypes = { "addJuniorLevel", "addSeniorLevel", "addLeadLevel", "addUsiGeography", "addNonUsiGeography", "mapTeam", "mapNextGenGroup", "addSapSynonym", "leaveOutRows" };

        public static object Run(SnapshotLoader source, WhatIfRequest req)
        {
            if (req.Changes.Count == 0) throw new ArgumentException("whatIf needs at least one change in \"changes\". Types: " + string.Join(", ", ChangeTypes) + ".");
            var official = source.Settings();
            var changed = source.Settings();
            var leaveOut = new HashSet<int>();
            var applied = req.Changes.Select(c => Apply(changed, c, leaveOut)).ToList();
            changed.Validate();

            var before = Checks.RunAll(DealTools.LoadOrThrow(source, official));
            var afterCtx = DealTools.LoadOrThrow(source, changed, leaveOut);
            var after = Checks.RunAll(afterCtx);
            var wanted = req.Questions.Count > 0 ? req.Questions : before.Where(q => q.DecidedBy != "on hold").Select(q => q.No).ToList();

            // Safety check: without the change, the recheck must give exactly the first run's answers.
            var first = source.FirstRun;
            string FirstAnswer(int n) { var f = first?.Questions?.FirstOrDefault(x => x.No == n); return f == null ? null : f.Answer ?? f.DecidedBy; }
            bool? matchesFirstRun = first == null ? (bool?)null
                : wanted.All(n => { var a = before.FirstOrDefault(q => q.No == n); return a == null || a.DecidedBy == "on hold" || (a.Answer ?? a.DecidedBy) == FirstAnswer(n); });

            var results = new List<object>();
            foreach (int n in wanted)
            {
                var a = before.FirstOrDefault(q => q.No == n);
                var b = after.FirstOrDefault(q => q.No == n);
                if (a == null || b == null || a.DecidedBy == "on hold") { results.Add(new { no = n, note = "Not an answered question." }); continue; }
                results.Add(new
                {
                    no = n,
                    firstRunAnswer = first != null ? FirstAnswer(n) : null,
                    officialAnswer = a.Answer ?? a.DecidedBy,
                    whatIfAnswer = b.Answer ?? b.DecidedBy,
                    answerChanges = !string.Equals(a.Answer, b.Answer),
                    officialSummary = a.Summary,
                    whatIfSummary = b.Summary,
                    whatIfValues = b.Values
                });
            }
            var missingRows = leaveOut.Where(r => !afterCtx.RowsLeftOut.Contains(r)).ToList();
            return new
            {
                ok = true,
                action = "whatIf",
                snapshotId = source.SnapshotId,
                note = "What-if results are for Table 2 only. They never replace the official answer.",
                matchesFirstRun,
                warning = matchesFirstRun == false
                    ? "Without the change, the recheck doesn't give the first run's answers, so the tool is probably using a different version of DealReader.cs. Report this; don't use these results."
                    : null,
                changes = applied,
                rowsNotFound = missingRows.Count > 0 ? missingRows : null,
                results
            };
        }

        private static object Apply(DealSettings s, WhatIfChange c, HashSet<int> leaveOut)
        {
            string type = Text.Norm(c.Type).Replace(" ", "");
            string v = Squash(c.Value ?? "");
            bool isCode = Regex.IsMatch(v, @"^L[A-Z0-9]{1,4}$", RegexOptions.IgnoreCase);
            void Need(string what) { if (what.Length == 0) throw new ArgumentException($"Change '{c.Type}' needs a value."); }
            string AddLevel(LevelList list, string listName)
            {
                Need(v);
                if (isCode) { if (!list.Codes.Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase))) list.Codes.Add(v.ToUpperInvariant()); return $"add \"{v.ToUpperInvariant()}\" to {listName}.codes"; }
                if (!Text.InList(v, list.Titles)) list.Titles.Add(v);
                return $"add \"{v}\" to {listName}.titles";
            }
            string MappingKey(string g)
            {
                var key = s.NextGenTeamMapping.Keys.FirstOrDefault(k => Text.Norm(k) == Text.Norm(g));
                if (key == null) throw new ArgumentException($"'{g}' isn't a group in nextGenTeamMapping. Groups: {string.Join(", ", s.NextGenTeamMapping.Keys)}.");
                return key;
            }

            string line;
            switch (type)
            {
                case "addjuniorlevel": line = AddLevel(s.JuniorLevels, "juniorLevels"); break;
                case "addseniorlevel": line = AddLevel(s.SeniorLevels, "seniorLevels"); break;
                case "addleadlevel": line = AddLevel(s.LeadLevels, "leadLevels"); break;
                case "addusigeography":
                    Need(v);
                    if (!Text.InList(v, s.UsiGeographies)) s.UsiGeographies.Add(v);
                    s.NonUsiGeographies.RemoveAll(x => Text.Norm(x) == Text.Norm(v));
                    if (!Text.InList(v, s.UsiCohorts)) s.UsiCohorts.Add(v);
                    line = $"add \"{v}\" to usiGeographies (or usiCohorts for the old layout)";
                    break;
                case "addnonusigeography":
                    Need(v);
                    if (!Text.InList(v, s.NonUsiGeographies)) s.NonUsiGeographies.Add(v);
                    if (!Text.InList(v, s.NonUsiCohorts) && s.NonUsiCohorts.Count > 0) s.NonUsiCohorts.Add(v);
                    line = $"add \"{v}\" to nonUsiGeographies";
                    break;
                case "mapteam":
                {
                    Need(v);
                    string key = MappingKey(c.Group ?? "");
                    string norm = Text.Norm(Text.StripWavePrefix(v));
                    foreach (var list in s.NextGenTeamMapping.Values) list.RemoveAll(x => Text.Norm(Text.StripWavePrefix(x)) == norm);
                    s.NextGenTeamMapping[key].Add(Text.StripWavePrefix(v).Trim());
                    line = $"add \"{Text.StripWavePrefix(v).Trim()}\" to nextGenTeamMapping.\"{key}\"";
                    break;
                }
                case "mapnextgengroup":
                {
                    Need(v);
                    string key = MappingKey(c.Group ?? "");
                    foreach (var k in s.NextGenGroupAliases.Keys.Where(k => Text.Norm(k) == Text.Norm(v)).ToList()) s.NextGenGroupAliases.Remove(k);
                    s.NextGenGroupAliases[v] = key;
                    line = $"add \"{v}\": \"{key}\" to nextGenGroupAliases";
                    break;
                }
                case "addsapsynonym":
                {
                    Need(v);
                    string g = Squash(c.Group ?? "");
                    if (g.Length == 0) throw new ArgumentException("addSapSynonym needs \"group\": the SAP workstream it belongs to (new or existing).");
                    string key = s.SapWorkstreams.Keys.FirstOrDefault(k => Text.Norm(k) == Text.Norm(g)) ?? g;
                    if (!s.SapWorkstreams.ContainsKey(key)) s.SapWorkstreams[key] = new List<string>();
                    if (!Text.InList(v, s.SapWorkstreams[key])) s.SapWorkstreams[key].Add(v);
                    line = $"add \"{v}\" to sapWorkstreams.\"{key}\"";
                    break;
                }
                case "leaveoutrows":
                    if (c.Rows == null || c.Rows.Count == 0) throw new ArgumentException("leaveOutRows needs \"rows\": a list of pricing model row numbers.");
                    foreach (var r in c.Rows) leaveOut.Add(r);
                    line = $"not a settings change: fix or remove row(s) {string.Join(", ", c.Rows)} in the pricing model, or confirm they belong";
                    break;
                default:
                    throw new ArgumentException($"Unknown change type '{c.Type}'. Use one of: {string.Join(", ", ChangeTypes)}.");
            }
            return new { type = c.Type, value = c.Value, group = c.Group, rows = c.Rows.Count > 0 ? c.Rows : null, permanentChange = line };
        }
    }
}
