// DealSettings.cs: loads DealSettings.json (every business rule, list and threshold lives in that file)
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
    /// <summary>
    /// Everything that is a business rule lives in DealSettings.json, not in code:
    /// level lists, thresholds, team mappings, workstream synonyms and classifier rules.
    /// </summary>
    public sealed class DealSettings
    {
        public Dictionary<int, string> Questions { get; set; } = new Dictionary<int, string>();
        public List<int> OnHold { get; set; } = new List<int>();

        public LevelList JuniorLevels { get; set; } = new LevelList();
        public LevelList SeniorLevels { get; set; } = new LevelList();
        public LevelList LeadLevels { get; set; } = new LevelList();              // Q8: SC and M
        public LevelList ManagingDirectorLevels { get; set; } = new LevelList();  // Q6: PPMD hours

        public List<string> UsiGeographies { get; set; } = new List<string>();     // new layout: Geography column
        public List<string> UsiCohorts { get; set; } = new List<string>();         // old layout: Resource Cohort column
        public List<string> NonUsiGeographies { get; set; } = new List<string>();  // known non-USI values; anything on neither list makes Q1 Needs review
        public List<string> NonUsiCohorts { get; set; } = new List<string>();      // same for the old layout; empty = not checked
        public List<string> EfaKeywords { get; set; } = new List<string>();

        public Thresholds Thresholds { get; set; } = new Thresholds();

        public string FunctionalGroup { get; set; } = "Functional";
        public Dictionary<string, List<string>> NextGenTeamMapping { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, string> NextGenGroupAliases { get; set; } = new Dictionary<string, string>();
        public List<KeywordRule> RoleKeywordRules { get; set; } = new List<KeywordRule>();

        public Dictionary<string, List<string>> SapWorkstreams { get; set; } = new Dictionary<string, List<string>>();

        public ClassifierRules Classifier { get; set; } = new ClassifierRules();

        public static DealSettings Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"DealSettings.json not found at '{path}'.");
            var options = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var s = JsonSerializer.Deserialize<DealSettings>(File.ReadAllText(path), options)
                    ?? throw new InvalidDataException("DealSettings.json is empty.");
            s.Validate();
            return s;
        }

        /// <summary>Catches setting mistakes early, e.g. the same team mapped to two groups.</summary>
        public void Validate()
        {
            var seen = new Dictionary<string, string>();
            foreach (var kv in NextGenTeamMapping)
                foreach (var team in kv.Value)
                {
                    string key = Text.Norm(Text.StripWavePrefix(team));
                    if (key.Length == 0) continue;
                    if (seen.TryGetValue(key, out var other) && other != kv.Key)
                        throw new InvalidDataException($"DealSettings: team '{team}' is mapped to both '{other}' and '{kv.Key}'.");
                    seen[key] = kv.Key;
                }
            if (!NextGenTeamMapping.ContainsKey(FunctionalGroup))
                throw new InvalidDataException($"DealSettings: functionalGroup '{FunctionalGroup}' is not in nextGenTeamMapping.");
            foreach (var kv in NextGenGroupAliases)
                if (!NextGenTeamMapping.ContainsKey(kv.Value ?? ""))
                    throw new InvalidDataException($"DealSettings: nextGenGroupAliases '{kv.Key}' points to '{kv.Value}', which isn't a group in nextGenTeamMapping (names must match exactly, including capitals).");
            foreach (var rule in RoleKeywordRules)
                if (!NextGenTeamMapping.ContainsKey(rule.Group ?? ""))
                    throw new InvalidDataException($"DealSettings: roleKeywordRules group '{rule.Group}' isn't a group in nextGenTeamMapping (names must match exactly, including capitals).");
        }
    }

    public sealed class LevelList
    {
        public List<string> Titles { get; set; } = new List<string>();  // e.g. "Sr Consultant"
        public List<string> Codes { get; set; } = new List<string>();   // e.g. "L45"
    }

    public sealed class Thresholds
    {
        public double UsiMinPct { get; set; } = 70;
        public double UsiMaxPct { get; set; } = 80;
        public double NonUsiMinPct { get; set; } = 20;
        public double NonUsiMaxPct { get; set; } = 30;
        public double BulgeMin { get; set; } = 1.9;
        public double BulgeMax { get; set; } = 2.1;
        public double NextGenMinPct { get; set; } = 95;
        public double EfaMinHoursPerPeriod { get; set; } = 5;
        public double EfaMaxHoursPerPeriod { get; set; } = 10;
        public int EfaPeriodsToCheck { get; set; } = 4;
        public double MdMinPct { get; set; } = 0.3;
        public double MdMaxPct { get; set; } = 0.5;
        public double LeadPct { get; set; } = 25;
        public bool LeadPctMustBeExact { get; set; } = true;
    }

    public sealed class KeywordRule
    {
        public string Group { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
    }

    public sealed class ClassifierRules
    {
        public string OwnFirmName { get; set; } = "Deloitte";
        public int ResponseMinFirmMentions { get; set; } = 10;
        public int RfpMaxFirmMentions { get; set; } = 2;
        public List<string> RfpPhrases { get; set; } = new List<string>() { "Request for Proposal" };
        public int RfpMinAcronymCount { get; set; } = 3;
        public List<string> ReadoutMarkers { get; set; } = new List<string>() { "SA Readout" };
        public List<string> PricingModelSheets { get; set; } = new List<string>() { "Engagement Metrics", "Resourcing" };
        public List<string> NextGenSheets { get; set; } = new List<string>() { "Raw Data" };
        public List<string> NextGenColumns { get; set; } = new List<string>() { "Resource Group", "SourceGroupType", "Effort" };
    }
}
