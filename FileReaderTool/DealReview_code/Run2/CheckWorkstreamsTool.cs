// CheckWorkstreamsTool.cs: checkWorkstreams: matches the workstreams the agent found in the response to the pricing model and gives Q3's answer
// Used by: Run 2.
// Request: {"promised":[{"name":"Record to Report","location":"Slide 70","quote":"..."}]} or {"couldNotRead":true}.

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
    public sealed class WorkstreamRequest
    {
        public List<PromisedWorkstream> Promised { get; set; } = new List<PromisedWorkstream>();
        public bool CouldNotRead { get; set; }
        public string Source { get; set; }     // which file the list came from (useful for PDFs)
    }

    public sealed class PromisedWorkstream
    {
        public string Name { get; set; }
        public string Location { get; set; }
        public string Quote { get; set; }
    }

    /// <summary>checkWorkstreams: code does the matching and Q3's Yes / No / Needs review.</summary>
    public static class CheckWorkstreamsTool
    {
        public static object Run(SnapshotLoader source, WorkstreamRequest req)
        {
            var settings = source.Settings();
            var ctx = DealTools.LoadOrThrow(source, settings);
            var rules = new Rules(settings);
            var q = Checks.Q3(ctx, rules);
            q.Question = settings.Questions.TryGetValue(3, out var text) ? text : "Question 3";
            if (q.AgentTask == null)
                return new { ok = true, action = "checkWorkstreams", snapshotId = source.SnapshotId, note = "There is no response document or PDF, so Q3 was decided without step 7.", question = q };

            var model = q.AgentTask.ModelWorkstreams;
            var teamsOutside = q.AgentTask.TeamNamesNotInSapList;
            var items = new List<object>();
            var missing = new List<string>();
            var checkedWs = new List<string>();     // SAP workstreams checked, for the Note
            var byTeamName = new List<string>();    // names outside the SAP list, matched to a team by name
            var leftOut = new List<object>();       // names code leaves out: not SAP workstreams, or quoted from an ignored section
            var blocks = ctx.ResponseBlocks ?? new List<DocBlock>();

            if (req.CouldNotRead)
                q.Unclear("The agent couldn't read the promised workstreams from the response document" + (string.IsNullOrWhiteSpace(req.Source) ? "." : $" ({req.Source})."));
            else if (req.Promised.Count == 0)
                q.Unclear("No promised workstreams were given, so step 7 couldn't be checked.");

            var seen = new HashSet<string>();
            int unmatched = 0;
            foreach (var p in req.CouldNotRead ? new List<PromisedWorkstream>() : req.Promised)
            {
                string name = Squash(p.Name ?? "");
                if (name.Length == 0 || !seen.Add(Text.Norm(name))) continue;

                // Where the quote really is. The document's own location beats the agent's, so the section check can't drift.
                var found = FindQuote(blocks, p.Quote);
                string where = found?.Location ?? p.Location ?? "";
                string ignored = rules.IgnoredSection(where);
                string notSap = rules.NotSapWorkstream(name);
                if (ignored != null || notSap != null)
                {
                    string why = ignored != null
                        ? $"quoted from section '{Rules.SectionOf(where)}', which doesn't say what this deal includes (workstreamIgnoreSections: {ignored})"
                        : $"not an SAP workstream (notSapWorkstreams: {notSap})";
                    leftOut.Add(new { name, location = where, reason = why });
                    q.Trail.Add($"Step 7: '{name}' left out: {why}.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(p.Quote) && blocks.Count > 0 && found == null)
                    q.Flags.Add($"'{name}': the quote wasn't found in the response document, so a reviewer should check it (\"{Squash(p.Quote)}\").");

                var ws = rules.SapWorkstreams(name);
                string status;
                List<string> staffedBy = null;
                if (ws.Count > 0)
                {
                    var hits = ws.Where(model.ContainsKey).ToList();
                    if (hits.Count > 0)
                    {
                        status = "staffed";
                        staffedBy = hits.SelectMany(h => model[h]).Distinct().ToList();
                        checkedWs.AddRange(hits);
                        // A name can cover several workstreams. It counts as staffed if any of them is,
                        // so flag the ones that aren't for a reviewer (the answer doesn't change).
                        var notStaffed = ws.Where(w => !model.ContainsKey(w)).ToList();
                        if (notStaffed.Count > 0)
                            q.Flags.Add($"'{name}' covers {JoinAnd(ws)}, but {JoinAnd(notStaffed)} {(notStaffed.Count == 1 ? "isn't" : "aren't")} staffed (the name still counts as staffed).");
                    }
                    else { status = "not staffed"; missing.Add($"{name} ({string.Join("/", ws)})"); checkedWs.AddRange(ws); }
                }
                else
                {
                    var team = teamsOutside.FirstOrDefault(tn => Text.HasWord(tn, name) || Text.HasWord(name, Text.StripWavePrefix(tn).Trim()));
                    if (team != null)
                    {
                        status = "staffed, but outside the SAP list";
                        staffedBy = new List<string>() { team };
                        byTeamName.Add(name);
                        q.Flags.Add($"'{name}' isn't in the SAP list; matched to pricing team '{team}' by name.");
                        q.Hint($"Workstream '{name}' isn't in the SAP list", $"add \"{name}\" to sapWorkstreams (as a new workstream or a synonym of an existing one)");
                    }
                    else
                    {
                        status = "can't be matched";
                        unmatched++;
                        q.Unclear($"'{name}' isn't in the SAP list and no pricing model team has the same name, so it can't be checked.");
                        q.Hint($"Workstream '{name}' isn't in the SAP list", $"add \"{name}\" to sapWorkstreams (as a new workstream or a synonym of an existing one)");
                    }
                }
                if (string.IsNullOrWhiteSpace(p.Location) || string.IsNullOrWhiteSpace(p.Quote))
                    q.Flags.Add($"'{name}' was given without {(string.IsNullOrWhiteSpace(p.Location) ? "a location" : "a quote")}, so a reviewer can't easily find it.");
                items.Add(new { name, location = where, quote = p.Quote, quoteFound = blocks.Count > 0 ? found != null : (bool?)null, sapWorkstreams = ws, status, staffedBy });
                q.Trail.Add($"Step 7: '{name}'{(string.IsNullOrWhiteSpace(p.Location) ? "" : " (" + p.Location + ")")} → {(ws.Count > 0 ? string.Join("/", ws) : "not in the SAP list")} → {status}{(staffedBy != null ? ": " + string.Join(", ", staffedBy) : "")}.");
            }

            q.DecidedBy = "code, after checkWorkstreams";
            q.AgentTask = null;
            // The Note names SAP workstreams in settings order, never how many names the agent sent or in what order,
            // so two runs that find the same workstreams write the same Note.
            if (!req.CouldNotRead && req.Promised.Count > 0 && items.Count == 0)
                q.Unclear("Every name given was left out (see the trail), so step 7 couldn't be checked.");
            missing.Sort(StringComparer.Ordinal);
            string covered = string.Join(", ", rules.InSapOrder(checkedWs));
            string byTeam = string.Join(", ", byTeamName.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => $"'{n}'"));
            string step7 = req.CouldNotRead ? "Step 7: the response document couldn't be read."
                         : items.Count == 0 ? "Step 7: no promised workstreams were given."
                         : "Step 7: the response's workstreams" +
                           (covered.Length > 0 ? $" cover {covered}" : "") +
                           (byTeam.Length > 0 ? (covered.Length > 0 ? $", plus {byTeam} by team name" : $" cover {byTeam} by team name") : "") +
                           "; " + (missing.Count > 0 ? $"not staffed: {string.Join(", ", missing)}." : "none is missing from the pricing model.") +
                           (unmatched > 0 ? $" {unmatched} couldn't be matched." : "");
            q.Body = q.Body + " " + step7;
            q.Decide(q.PassedSoFar && missing.Count == 0);
            q.Values["promisedWorkstreams"] = items;
            q.Values["missingWorkstreams"] = missing;
            q.Values["checkedWorkstreams"] = rules.InSapOrder(checkedWs);
            q.Values["leftOutWorkstreams"] = leftOut;
            return new { ok = true, action = "checkWorkstreams", snapshotId = source.SnapshotId, question = q };
        }

        /// <summary>The block the quote comes from, ignoring case, spaces and punctuation, or null.</summary>
        private static DocBlock FindQuote(List<DocBlock> blocks, string quote)
        {
            string key = Letters(quote);
            if (key.Length == 0) return null;
            return blocks.FirstOrDefault(b => (" " + Letters(b.Text) + " ").Contains(" " + key + " "));
        }

        private static string Letters(string s) => Regex.Replace(Text.Norm(s ?? ""), @"[^\p{L}\p{N}]+", " ").Trim();

        /// <summary>"A", "A and B", "A, B and C".</summary>
        private static string JoinAnd(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
        }
    }
}
