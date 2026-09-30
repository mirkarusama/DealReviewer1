// Program.cs: regression runner for the deal review code. Runs every case in tests/cases.json and compares with tests/expected.
// Usage: dotnet run --project tests/Regression -- <repo root> [--update]
//
// Case kinds:
//   sets          Run 1 (DealEngine) on a deal folder. Saves the review, then the final tables with no agent input.
//   q3            checkWorkstreams with a given request, on one of the sets. Saves Q3's answer, summary, flags and reasons.
//   agentOutputs  a saved agent output put through FinalTables, as Main.xaml does.
//   sameAnswer    groups of q3 cases that must give the same Q3 answer: different lists a model could plausibly send.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DealReview;

static class Program
{
    static string root, outDir, expectedDir;
    static bool update;
    static readonly List<string> failures = new List<string>();
    static readonly Dictionary<string, string> snapshots = new Dictionary<string, string>();   // set name -> snapshot path
    static readonly Dictionary<string, string> reviews = new Dictionary<string, string>();     // set name -> review JSON
    static readonly Dictionary<string, string> q3Answers = new Dictionary<string, string>();   // q3 case name -> answer

    static int Main(string[] args)
    {
        root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        update = args.Contains("--update");
        string tests = Path.Combine(root, "tests");
        outDir = Path.Combine(tests, "out");
        expectedDir = Path.Combine(tests, "expected");
        Directory.CreateDirectory(Path.Combine(outDir, "snapshots"));
        Directory.CreateDirectory(expectedDir);

        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(tests, "cases.json")),
                                   documentOptions: new JsonDocumentOptions() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string defaultSettings = Path.Combine(root, (string)cases["settings"]);

        foreach (var set in cases["sets"].AsArray()) RunSet(set, defaultSettings);
        foreach (var q in cases["q3"]?.AsArray() ?? new JsonArray()) RunQ3(q);
        foreach (var a in cases["agentOutputs"]?.AsArray() ?? new JsonArray()) RunAgentOutput(a);
        foreach (var g in cases["sameAnswer"]?.AsArray() ?? new JsonArray()) CheckSameAnswer(g);

        Console.WriteLine();
        if (update) { Console.WriteLine("Expected files updated. Review them with git diff before committing."); return 0; }
        if (failures.Count == 0) { Console.WriteLine("All cases match."); return 0; }
        Console.WriteLine($"{failures.Count} case(s) differ:");
        foreach (var f in failures) Console.WriteLine("  - " + f);
        return 1;
    }

    // ---------- sets: Run 1 ----------
    static void RunSet(JsonNode set, string defaultSettings)
    {
        string name = (string)set["name"];
        string settingsPath = SettingsFor(name, defaultSettings, set["thresholds"]?.AsObject());
        try
        {
            var snap = DealEngine.RunAndSnapshot(Path.Combine(root, (string)set["folder"]), settingsPath);
            string snapPath = Path.Combine(outDir, "snapshots", name + ".json");
            File.WriteAllText(snapPath, Json.WriteCompact(snap), new UTF8Encoding(false));
            string review = Json.Write(snap.Review);
            snapshots[name] = snapPath; reviews[name] = review;
            Compare($"{name}.review.json", Stable(review));

            // The final tables with no agent input: exactly what code alone would write.
            string final = FinalTables.Build(review, snapPath, "{}");
            Compare($"{name}.code-only.final.json", Stable(final));
            Console.WriteLine($"set  {name,-22} {AnswersLine(review)}");
        }
        catch (Exception ex) { Compare($"{name}.review.json", "{\"error\": " + JsonSerializer.Serialize(ex.Message) + "}\n"); Console.WriteLine($"set  {name,-22} error: {ex.Message}"); }
    }

    // ---------- q3: checkWorkstreams with a given list ----------
    static void RunQ3(JsonNode c)
    {
        string name = (string)c["name"], set = (string)c["set"];
        if (!snapshots.ContainsKey(set)) { Fail(name, $"set '{set}' has no snapshot"); return; }
        string request = c["request"].ToJsonString();
        var result = JsonNode.Parse(DealTools.Recheck("checkWorkstreams", snapshots[set], request));
        var q = result["question"];
        var shown = new JsonObject()
        {
            ["ok"] = (bool?)result["ok"],
            ["error"] = result["error"]?.DeepClone(),
            ["answer"] = q?["answer"]?.DeepClone(),
            ["summary"] = q?["summary"]?.DeepClone(),
            ["reviewReasons"] = q?["reviewReasons"]?.DeepClone(),
            ["flags"] = q?["flags"]?.DeepClone(),
            ["settingsHints"] = q?["settingsHints"]?.DeepClone(),
            ["trail"] = q?["trail"]?.DeepClone()
        };
        q3Answers[name] = (string)q?["answer"] ?? "(error)";
        Compare($"q3.{name}.json", Pretty(shown));
        Console.WriteLine($"q3   {name,-40} {q3Answers[name]}");
    }

    // ---------- agentOutputs: FinalTables on a saved agent output ----------
    static void RunAgentOutput(JsonNode c)
    {
        string name = (string)c["name"], set = (string)c["set"];
        if (!snapshots.ContainsKey(set)) { Fail(name, $"set '{set}' has no snapshot"); return; }
        string agent = File.ReadAllText(Path.Combine(root, "tests", (string)c["file"]));
        string final = FinalTables.Build(reviews[set], snapshots[set], agent);
        Compare($"agent.{name}.final.json", Stable(final));
        var corrections = JsonNode.Parse(final)["corrections"]?.AsArray().Count ?? -1;
        Console.WriteLine($"agnt {name,-40} {AnswersLine(final, "table1")} corrections={corrections}");
    }

    static void CheckSameAnswer(JsonNode g)
    {
        var names = g.AsArray().Select(n => (string)n).ToList();
        var answers = names.Select(n => q3Answers.TryGetValue(n, out var a) ? a : "(missing)").ToList();
        bool same = answers.Distinct().Count() == 1;
        Console.WriteLine($"same {string.Join(" = ", names.Zip(answers, (n, a) => $"{n}:{a}"))} → {(same ? "OK" : "DIFFERENT")}");
        if (!same && !update) Fail("sameAnswer", string.Join(", ", names.Zip(answers, (n, a) => $"{n}={a}")));
    }

    // ---------- helpers ----------

    /// <summary>A settings file for this set: the default one, or a copy with some thresholds changed.</summary>
    static string SettingsFor(string name, string defaultSettings, JsonObject thresholds)
    {
        if (thresholds == null || thresholds.Count == 0) return defaultSettings;
        string text = File.ReadAllText(defaultSettings);
        foreach (var kv in thresholds)
        {
            var re = new Regex("(\"" + Regex.Escape(kv.Key) + "\"\\s*:\\s*)[^,}\\s]+");
            if (!re.IsMatch(text)) throw new InvalidDataException($"Threshold '{kv.Key}' isn't in {defaultSettings}.");
            text = re.Replace(text, "${1}" + kv.Value.ToJsonString(), 1);
        }
        string path = Path.Combine(outDir, name + ".settings.json");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The JSON without the parts that change on every run: IDs, times and local paths.</summary>
    static string Stable(string json)
    {
        var node = JsonNode.Parse(json);
        Strip(node);
        return Pretty(node);
    }

    static readonly HashSet<string> Volatile = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "snapshotId", "generatedAt", "createdAt", "folder", "fullPath" };

    static void Strip(JsonNode n)
    {
        if (n is JsonObject o)
        {
            foreach (var k in o.Select(p => p.Key).Where(Volatile.Contains).ToList()) o.Remove(k);
            foreach (var p in o.ToList()) Strip(p.Value);
        }
        else if (n is JsonArray a) foreach (var x in a) Strip(x);
    }

    static string Pretty(JsonNode n) =>
        n.ToJsonString(new JsonSerializerOptions() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    static string AnswersLine(string json, string table = null)
    {
        var n = JsonNode.Parse(json);
        if (table != null)
            return string.Join(" ", (n[table]?.AsArray() ?? new JsonArray()).Select((r, i) => Short((string)r["Answer"])));
        if (n["filesPresent"]?.GetValue<bool>() != true) return "files not right: " + string.Join(" ", n["fileIssues"].AsArray().Select(x => (string)x));
        return string.Join(" ", n["questions"].AsArray().Select(q => $"Q{q["no"]}={Short((string)q["answer"])}"));
    }

    static string Short(string a) => a == null ? "-" : a == "Needs review" ? "NR" : a;

    static void Compare(string file, string actual)
    {
        File.WriteAllText(Path.Combine(outDir, file), actual);
        string expectedPath = Path.Combine(expectedDir, file);
        if (update) { File.WriteAllText(expectedPath, actual); return; }
        if (!File.Exists(expectedPath)) { Fail(file, "no expected file (run with --update to create it)"); return; }
        string expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n");
        if (expected == actual) return;
        var e = expected.Split('\n'); var a = actual.Split('\n');
        int i = 0; while (i < Math.Min(e.Length, a.Length) && e[i] == a[i]) i++;
        Fail(file, $"differs from line {i + 1}:\n      expected: {Cut(i < e.Length ? e[i] : "(end)")}\n      actual:   {Cut(i < a.Length ? a[i] : "(end)")}\n      full output: tests/out/{file}");
    }

    static string Cut(string s) => s.Trim().Length > 200 ? s.Trim().Substring(0, 200) + " …" : s.Trim();
    static void Fail(string name, string why) => failures.Add($"{name}: {why}");
}
