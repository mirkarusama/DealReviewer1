// Helpers.cs: small helpers: JSON in and out, name comparison, whole-word search, number formatting
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
    /// <summary>JSON in and out, with the same options everywhere.</summary>
    public static class Json
    {
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions(WriteOptions) { WriteIndented = false };

        public static string Write(object value) =>
            value == null ? "null" : JsonSerializer.Serialize(value, value.GetType(), WriteOptions);

        public static string WriteCompact(object value) =>
            value == null ? "null" : JsonSerializer.Serialize(value, value.GetType(), CompactOptions);

        /// <summary>Reads the agent's request. Empty text means "no options".</summary>
        public static T Read<T>(string text) where T : new()
        {
            if (string.IsNullOrWhiteSpace(text)) return new T();
            try { return JsonSerializer.Deserialize<T>(text, ReadOptions) ?? new T(); }
            catch (JsonException ex) { throw new InvalidDataException("The request isn't valid JSON: " + ex.Message); }
        }
    }

    public static class Text
    {
        /// <summary>Lower-case, trimmed, single spaces, no spaces around "/". Used to compare names.</summary>
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\u00A0', ' ');
            s = Regex.Replace(s, @"\s*/\s*", "/");
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim().ToLowerInvariant();
        }

        /// <summary>Collapses whitespace but keeps the original case.</summary>
        public static string Squash(string s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

        /// <summary>Removes a leading wave number such as "1 - " or "2- " from a team name.</summary>
        public static string StripWavePrefix(string s) => Regex.Replace(s ?? "", @"^\s*\d+\s*-\s*", "");

        /// <summary>Whole-word, case-insensitive match. "AP" matches "AP team" but not "SAP", and "PP" doesn't match "PP&E" (an ampersand between letters joins one term).</summary>
        public static bool HasWord(string text, string word)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word)) return false;
            return WordRegex(word).IsMatch(text);
        }

        /// <summary>Where the first whole-word match starts, or -1.</summary>
        public static int FindWord(string text, string word)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word)) return -1;
            var m = WordRegex(word).Match(text);
            return m.Success ? m.Index : -1;
        }

        public static int CountWord(string text, string word)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word)) return 0;
            return WordRegex(word).Matches(text).Count;
        }

        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>();
        private static Regex WordRegex(string word)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(word, out var rx))
                {
                    // letters/digits on either side count as "same word", and so does "&" joined to a letter ("PP&E", "R&D");
                    // spaces inside the keyword may be any whitespace
                    string pattern = @"(?<![\p{L}\p{N}])(?<![\p{L}\p{N}]&)" + Regex.Escape(word.Trim()).Replace(@"\ ", @"\s+") + @"(?![\p{L}\p{N}])(?!&[\p{L}\p{N}])";
                    rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    Cache[word] = rx;
                }
                return rx;
            }
        }

        public static string Hrs(double h) => h.ToString("#,0.#", CultureInfo.InvariantCulture);
        public static string Pct(double p) => p.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        public static string Num(double d) => d.ToString("0.00", CultureInfo.InvariantCulture);
        public static double R2(double d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

        public static bool InList(string value, IEnumerable<string> list) =>
            list != null && list.Any(x => Norm(x) == Norm(value));

        /// <summary>Column number to letters: 1 → A, 27 → AA.</summary>
        public static string ColumnLetter(int col)
        {
            var sb = new StringBuilder();
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - 1) / 26;
            }
            return sb.ToString();
        }
    }
}
