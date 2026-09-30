// ResponseExtractor.cs: picks the workstream parts of the response document for the agent, and lists what was left out
// Used by: Run 1.

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
    /// The response document is long (15-20k words). Instead of sending all of it, send the tables, SmartArt and
    /// lines that mention an SAP workstream, tables about work streams / towers first. The agent reads these
    /// to list the workstreams the response promises, and uses searchResponse for anything else.
    /// </summary>
    public static class ResponseExtractor
    {
        private const int MaxChars = 30000;
        private const int MaxTableChars = 4000;
        private const int MaxLineChars = 600;
        private static readonly string[] StructureWords = { "Work Stream", "Workstream", "Work Streams", "Workstreams", "Tower", "Towers", "Team Composition", "Scope" };

        public static ResponseEvidence Extract(string fileName, List<DocBlock> blocks, string fullPath, DealSettings settings)
        {
            var keywords = settings.SapWorkstreams.SelectMany(kv => kv.Value.Append(kv.Key)).Distinct().ToList();
            bool MentionsWorkstream(string t) => keywords.Any(k => Text.HasWord(t, k));
            bool IsStructure(string t) => StructureWords.Any(w => Text.HasWord(t, w));
            var left = new LeftOutReport();

            var picked = new List<(int priority, int order, DocBlock block)>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (!MentionsWorkstream(b.Text)) continue;
                if (b.Kind == "table" || b.Kind == "smartart")
                {
                    string t = b.Text;
                    if (t.Length > MaxTableChars) { t = t.Substring(0, MaxTableChars) + " …"; left.TablesCut.Add(b.Location); }
                    picked.Add((IsStructure(b.Text) ? 0 : 1, i, new DocBlock() { Kind = b.Kind, Location = b.Location, Text = t }));
                }
                else if (b.Text.Length <= MaxLineChars)
                {
                    picked.Add((b.Kind == "heading" || IsStructure(b.Text) ? 2 : 3, i, b));
                }
                else
                {
                    left.LongParagraphsSkipped++;
                    if (!left.LongParagraphLocations.Contains(b.Location)) left.LongParagraphLocations.Add(b.Location);
                }
            }

            var evidence = new List<DocBlock>();
            var seen = new HashSet<string>();
            int used = 0;
            foreach (var p in picked.OrderBy(p => p.priority).ThenBy(p => p.order))
            {
                if (!seen.Add(p.block.Text)) continue;
                if (used + p.block.Text.Length > MaxChars)
                {
                    left.BlocksOverCap++;
                    if (!left.BlocksOverCapLocations.Contains(p.block.Location)) left.BlocksOverCapLocations.Add(p.block.Location);
                    continue;
                }
                used += p.block.Text.Length;
                evidence.Add(p.block);
            }
            // back to reading order, so the agent sees the document's flow
            var order = picked.GroupBy(p => p.block.Text).ToDictionary(g => g.Key, g => g.Min(x => x.order));
            evidence = evidence.OrderBy(e => order[e.Text]).ToList();
            left.Pictures = OfficeText.Pictures(fullPath);

            return new ResponseEvidence()
            {
                FileName = fileName,
                TotalWords = OfficeText.PlainText(blocks).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length,
                BlocksInDocument = blocks.Count,
                BlocksIncluded = evidence.Count,
                Evidence = evidence,
                LeftOut = left
            };
        }
    }
}
