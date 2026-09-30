// FinalTables.cs: builds the final table1 and table2 with the code's exact wording, keeping only the agent's decisions
// Used by: Run 1 (Main.xaml, right after Run Job returns the agent's output).
// Assigns (VB):  agentJson = Newtonsoft.Json.JsonConvert.SerializeObject(agentOutput)
//                finalJson = DealReview.FinalTables.Build(reviewJson, snapshotPath, agentJson)

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
    /// <summary>
    /// The agent decides; code writes. When the agent builds its tables it retypes the code's text, and it can change it.
    /// Build rebuilds both tables from the code's own results and keeps only the agent's decisions:
    /// - Question, Answer and Note come from Run 1's review. Q3 comes from rerunning checkWorkstreams with the agent's own request.
    /// - An answer the agent moved to Needs review stays moved, with the agent's one-sentence reason.
    /// - Issues lines stay as the agent wrote them. A question the agent left out of table2 gets the code's flags and hints.
    /// Every place where the agent's text differed from the code's is listed under "corrections".
    /// It never throws: on a problem it returns {"error": ..., "agentOutput": ...}, so nothing is lost.
    /// </summary>
    public static class FinalTables
    {
        private const string MovedPrefix = "Moved to Needs review:";

        /// <summary>
        /// reviewJson: what DealReader.RunAndSave returned. snapshotPath: DealReader.SnapshotPath.
        /// agentJson: the agent's output as JSON text (table1, table2 and q3Request).
        /// Returns JSON text: {"table1": [...], "table2": [...], "corrections": [...]}.
        /// </summary>
        public static string Build(string reviewJson, string snapshotPath, string agentJson)
        {
            try { return Json.Write(BuildTables(reviewJson, snapshotPath, agentJson)); }
            catch (Exception ex)
            {
                return Json.Write(new Dictionary<string, object>()
                {
                    ["error"] = "FinalTables couldn't build the tables: " + ex.Message,
                    ["agentOutput"] = agentJson ?? ""
                });
            }
        }

        private static Dictionary<string, object> BuildTables(string reviewJson, string snapshotPath, string agentJson)
        {
            using var review = JsonDocument.Parse(reviewJson);
            using var agent = JsonDocument.Parse(string.IsNullOrWhiteSpace(agentJson) ? "{}" : agentJson);
            var agentTable1 = Items(agent.RootElement, "table1");
            var agentTable2 = Items(agent.RootElement, "table2");
            var corrections = new List<string>();

            // Q3's finished result: rerun checkWorkstreams on this machine with the agent's own request.
            string q3Request = Str(agent.RootElement, "q3Request");
            JsonDocument q3Doc = null;
            JsonElement? q3 = null;
            if (q3Request.Length > 0)
            {
                q3Doc = JsonDocument.Parse(DealTools.Recheck("checkWorkstreams", snapshotPath, q3Request));
                if (Bool(q3Doc.RootElement, "ok") && Prop(q3Doc.RootElement, "question") is JsonElement finished) q3 = finished;
                else corrections.Add("Q3: rerunning the workstream check failed (" + Str(q3Doc.RootElement, "error") + "), so the agent's Q3 text was kept.");
            }

            var table1 = new List<Dictionary<string, string>>();
            var table2 = new List<Dictionary<string, string>>();
            foreach (var reviewQ in Items(review.RootElement, "questions"))
            {
                if (Str(reviewQ, "decidedBy") == "on hold") continue;
                int no = Int(reviewQ, "no");
                string question = Str(reviewQ, "question");
                bool waiting = Str(reviewQ, "decidedBy") == "waiting for checkWorkstreams";
                JsonElement code = waiting && q3.HasValue ? q3.Value : reviewQ;
                string codeAnswer = Str(code, "answer");
                string codeNote = Str(code, "summary");

                JsonElement? row1 = Find(agentTable1, question);
                JsonElement? row2 = Find(agentTable2, question);
                string agentAnswer = row1.HasValue ? Str(row1.Value, "Answer") : "";
                string agentNote = row1.HasValue ? Str(row1.Value, "Note") : "";

                string answer, note;
                if (waiting && !q3.HasValue)
                {
                    // No rerun was possible: keep Q3 as the agent wrote it.
                    answer = agentAnswer; note = agentNote;
                    if (q3Request.Length == 0) corrections.Add($"Q{no}: the agent didn't return its checkWorkstreams request, so its Q{no} text was kept.");
                    if (answer.Length == 0)
                    {
                        answer = Answers.NeedsReview; note = codeNote;
                        corrections.Add($"Q{no}: neither the agent nor the code gave an answer, so it's Needs review.");
                    }
                }
                else if (!row1.HasValue)
                {
                    answer = codeAnswer; note = codeNote;
                    corrections.Add($"Q{no}: missing from the agent's table1; added from the code.");
                }
                else if (agentAnswer == Answers.NeedsReview && (codeAnswer == Answers.Yes || codeAnswer == Answers.No))
                {
                    // The agent moved it to Needs review: keep its reason, use the code's summary.
                    answer = Answers.NeedsReview;
                    note = $"{MovedPrefix} {MoveReason(agentNote, codeNote)} {codeNote}";
                    if (!agentNote.StartsWith(MovedPrefix, StringComparison.Ordinal))
                        corrections.Add($"Q{no}: the agent moved it to Needs review without a \"{MovedPrefix}\" reason.");
                }
                else
                {
                    answer = codeAnswer; note = codeNote;
                    if (agentAnswer != codeAnswer) corrections.Add($"Q{no}: the agent's answer \"{agentAnswer}\" was replaced with the code's \"{codeAnswer}\".");
                    else if (agentNote != codeNote) corrections.Add($"Q{no}: the agent's Note differed from the code's; the code's text was used.");
                }
                table1.Add(Row(question, answer, note));

                // table2: the questions the agent chose, plus any the prompt's rule says must be there.
                bool needsLook = Items(code, "flags").Count > 0 || Items(code, "reviewReasons").Count > 0
                                 || Items(code, "settingsHints").Count > 0 || answer == Answers.NeedsReview;
                if (row2.HasValue || needsLook)
                {
                    var row = Row(question, answer, note);
                    row["Issues"] = row2.HasValue ? Str(row2.Value, "Issues") : IssuesFromCode(code);
                    if (!row2.HasValue) corrections.Add($"Q{no}: missing from the agent's table2; added with the code's flags and hints.");
                    table2.Add(row);
                }
            }
            q3Doc?.Dispose();
            return new Dictionary<string, object>() { ["table1"] = table1, ["table2"] = table2, ["corrections"] = corrections };
        }

        private static Dictionary<string, string> Row(string question, string answer, string note) =>
            new Dictionary<string, string>() { ["Question"] = question, ["Answer"] = answer, ["Note"] = note };

        /// <summary>The agent's row for this question, matched on letters and digits only, so spaces and punctuation don't matter.</summary>
        private static JsonElement? Find(List<JsonElement> rows, string question)
        {
            string key = Key(question);
            foreach (var r in rows)
                if (Key(Str(r, "Question")) == key) return r;
            return null;
        }

        private static string Key(string s) => Regex.Replace(Norm(s), "[^a-z0-9]", "");

        /// <summary>The agent's one-sentence reason, from "Moved to Needs review: reason. code summary".</summary>
        private static string MoveReason(string agentNote, string codeNote)
        {
            string rest = agentNote.StartsWith(MovedPrefix, StringComparison.Ordinal) ? agentNote.Substring(MovedPrefix.Length).Trim() : "";
            string reason;
            int at = rest.Length > 0 && codeNote.Length > 0 ? rest.IndexOf(codeNote, StringComparison.Ordinal) : -1;
            if (at > 0) reason = rest.Substring(0, at).Trim();
            else
            {
                var m = Regex.Match(rest, @"^(.+?\.)\s+(Yes|No|Needs review)\.");
                int dot = rest.IndexOf(". ", StringComparison.Ordinal);
                reason = m.Success ? m.Groups[1].Value.Trim() : dot > 0 ? rest.Substring(0, dot + 1) : rest;
            }
            if (reason.Length == 0) reason = "a recheck found a reason to look again (see Issues).";
            return reason.EndsWith(".") ? reason : reason + ".";
        }

        /// <summary>Issues for a question the agent left out of table2: review reasons, flags, then suggested settings changes.</summary>
        private static string IssuesFromCode(JsonElement code)
        {
            var lines = new List<string>();
            lines.AddRange(Items(code, "reviewReasons").Where(x => x.ValueKind == JsonValueKind.String).Select(x => "- " + x.GetString()));
            lines.AddRange(Items(code, "flags").Where(x => x.ValueKind == JsonValueKind.String).Select(x => "- " + x.GetString()));
            lines.AddRange(Items(code, "settingsHints").Select(h => "- Suggested settings change: " + Str(h, "change")));
            return string.Join("\n", lines.Distinct());
        }

        // Small JSON readers. Property names are matched ignoring case, and a missing value never throws.
        private static JsonElement? Prop(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        private static string Str(JsonElement e, string name) =>
            Prop(e, name) is JsonElement v && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static bool Bool(JsonElement e, string name) =>
            Prop(e, name) is JsonElement v && v.ValueKind == JsonValueKind.True;

        private static int Int(JsonElement e, string name) =>
            Prop(e, name) is JsonElement v && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

        private static List<JsonElement> Items(JsonElement e, string name) =>
            Prop(e, name) is JsonElement v && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : new List<JsonElement>();
    }
}