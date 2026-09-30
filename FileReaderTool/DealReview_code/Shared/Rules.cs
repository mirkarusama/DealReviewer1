// Rules.cs: row rules: junior or senior, USI or not, EFA, lead, MD, which NextGen group, which SAP workstream
// Used by: Run 1 and Run 2.

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
    public enum Band { Junior, Senior, Unknown }

    public sealed class LevelResult
    {
        public Band Band { get; set; }
        public string Code { get; set; } = "";
        public string Note { get; set; }    // e.g. "Job Level '#REF!' unreadable, used Title"
    }

    public sealed class GroupResult
    {
        public string Group { get; set; }           // null = unmapped
        public string Source { get; set; }          // "team" | "role keyword" | "unmapped"
        public List<string> AlsoMatched { get; set; } = new List<string>();
    }

    /// <summary>All row-level decisions, driven by DealSettings. Whole values only, never partial text.</summary>
    public sealed class Rules
    {
        private readonly DealSettings _s;
        private readonly Dictionary<string, string> _teamToGroup = new Dictionary<string, string>();

        public Rules(DealSettings s)
        {
            _s = s;
            foreach (var kv in s.NextGenTeamMapping)
                foreach (var team in kv.Value)
                {
                    string key = Text.Norm(Text.StripWavePrefix(team));
                    if (key.Length > 0) _teamToGroup[key] = kv.Key;
                }
        }

        public DealSettings Settings => _s;

        // ---------- levels ----------

        private static readonly Regex CodeOnly = new Regex(@"^L[A-Z0-9]{1,4}$", RegexOptions.IgnoreCase);
        private static readonly Regex CodePrefix = new Regex(@"^\s*(L[A-Z0-9]{1,4})\s*-\s*(.*)$", RegexOptions.IgnoreCase);

        public static string NormTitle(string t)
        {
            t = Text.Norm(t);
            t = Regex.Replace(t, @"\bsenior\b", "sr");
            t = Regex.Replace(t, @"\bsr\.", "sr");
            t = Regex.Replace(t, @"\befa\b", "");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        /// <summary>Splits "L45 - Senior Consultant" into code "L45" and title "Senior Consultant".</summary>
        public static (string code, string title, string note) LevelParts(ResourceRow r)
        {
            string note = null;
            string code = "";
            string jl = r.JobLevel.Trim();
            if (CodeOnly.IsMatch(jl)) code = jl.ToUpperInvariant();
            else if (jl.Length > 0) note = $"Job Level '{jl}' unreadable, used the Title instead";

            string title = r.Title;
            var m = CodePrefix.Match(title);
            if (m.Success)
            {
                if (code.Length == 0) code = m.Groups[1].Value.ToUpperInvariant();
                title = m.Groups[2].Value;
            }
            return (code, title, note);
        }

        private static bool Matches(LevelList list, string code, string title) =>
            (code.Length > 0 && list.Codes.Any(c => string.Equals(c.Trim(), code, StringComparison.OrdinalIgnoreCase)))
            || (title.Length > 0 && list.Titles.Any(t => NormTitle(t) == NormTitle(title)));

        public LevelResult Level(ResourceRow r)
        {
            var (code, title, note) = LevelParts(r);
            var res = new LevelResult() { Code = code, Note = note };
            // A known code decides first; otherwise the title decides.
            bool codeJr = code.Length > 0 && _s.JuniorLevels.Codes.Any(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));
            bool codeSr = code.Length > 0 && _s.SeniorLevels.Codes.Any(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (codeJr) res.Band = Band.Junior;
            else if (codeSr) res.Band = Band.Senior;
            else if (Matches(_s.JuniorLevels, "", title)) res.Band = Band.Junior;
            else if (Matches(_s.SeniorLevels, "", title)) res.Band = Band.Senior;
            else res.Band = Band.Unknown;
            return res;
        }

        public bool IsLead(ResourceRow r) { var (c, t, _) = LevelParts(r); return Matches(_s.LeadLevels, c, t); }
        public bool IsManagingDirector(ResourceRow r) { var (c, t, _) = LevelParts(r); return Matches(_s.ManagingDirectorLevels, c, t); }

        // ---------- location / EFA ----------

        public bool IsUsi(ResourceRow r, bool hasGeography) =>
            hasGeography ? Text.InList(r.Geography, _s.UsiGeographies) : Text.InList(r.Cohort, _s.UsiCohorts);

        public bool IsEfa(ResourceRow r)
        {
            string all = string.Join(" | ", r.Cohort, r.WorkTeam, r.Role, r.Title);
            return _s.EfaKeywords.Any(k => Text.HasWord(all, k));
        }

        // ---------- NextGen group ----------

        public string CanonicalNextGenGroup(string nextGenGroup)
        {
            foreach (var kv in _s.NextGenGroupAliases)
                if (Text.Norm(kv.Key) == Text.Norm(nextGenGroup)) return kv.Value;
            var key = _s.NextGenTeamMapping.Keys.FirstOrDefault(k => Text.Norm(k) == Text.Norm(nextGenGroup));
            return key;   // null = NextGen group not in the mapping
        }

        /// <summary>Team name first (exact, wave number ignored); role keywords only if the team is blank or unknown.</summary>
        public GroupResult Group(ResourceRow r)
        {
            if (r.WorkTeam.Length > 0 && _teamToGroup.TryGetValue(Text.Norm(Text.StripWavePrefix(r.WorkTeam)), out var g))
                return new GroupResult() { Group = g, Source = "team" };

            string text = string.Join(" ", r.WorkTeam, r.Role).Trim();
            var hits = new List<string>();
            foreach (var rule in _s.RoleKeywordRules)
                if (rule.Keywords.Any(k => Text.HasWord(text, k)) && !hits.Contains(rule.Group)) hits.Add(rule.Group);
            if (hits.Count == 0) return new GroupResult() { Group = null, Source = "unmapped" };
            return new GroupResult() { Group = hits[0], Source = "role keyword", AlsoMatched = hits.Skip(1).ToList() };
        }

        // ---------- SAP workstreams (Q3 step 7) ----------

        /// <summary>Returns the SAP workstreams a name refers to. Longest keyword wins ("Data Migration" beats "Data").</summary>
        public List<string> SapWorkstreams(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new List<string>();
            var matches = new List<(string ws, string kw)>();
            foreach (var kv in _s.SapWorkstreams)
                foreach (var kw in kv.Value.Append(kv.Key))
                    if (Text.HasWord(name, kw)) matches.Add((kv.Key, kw));
            // drop a match that only sits inside a longer keyword of another workstream ("Data" inside "Master Data")
            return matches.Where(m => !matches.Any(o => o.ws != m.ws && o.kw.Length > m.kw.Length && Text.HasWord(o.kw, m.kw)))
                          .Select(m => m.ws).Distinct().ToList();
        }
    }
}
