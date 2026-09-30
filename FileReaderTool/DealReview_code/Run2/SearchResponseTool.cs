// SearchResponseTool.cs: searchResponse: finds text in the Word or PowerPoint response document, with its slide or section
// Used by: Run 2.
// Request: {"words":["team","work stream"]} or {"location":"Slide 70"} or {} for an outline.

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
    public sealed class SearchRequest
    {
        public List<string> Words { get; set; } = new List<string>();
        public string Location { get; set; }
        public int MaxResults { get; set; } = 20;
    }

    /// <summary>searchResponse: matching parts of the response document.</summary>
    public static class SearchResponseTool
    {
        private const int MaxSearchResults = 50;
        private const int MaxBlockChars = 3000;

        public static object Run(SnapshotLoader source, SearchRequest req)
        {
            var ctx = DealTools.LoadOrThrow(source, source.Settings());
            if (ctx.ResponseBlocks == null)
                return new
                {
                    ok = true,
                    action = "searchResponse",
                    snapshotId = source.SnapshotId,
                    found = false,
                    message = ctx.PdfFiles.Count > 0
                        ? $"No Word or PowerPoint response document. Code can't read PDFs ({string.Join(", ", ctx.PdfFiles)}), so the response can't be searched."
                        : "No response document in the deal folder."
                };

            var fileName = ctx.ResponseFile?.FileName ?? ctx.Response?.FileName ?? "";
            var blocks = ctx.ResponseBlocks;
            var words = req.Words.Select(Squash).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string loc = Text.Norm(req.Location ?? "");
            int max = Math.Max(1, Math.Min(req.MaxResults <= 0 ? 20 : req.MaxResults, MaxSearchResults));

            if (words.Count == 0 && loc.Length == 0)
            {
                // No search: an outline, so the agent knows where to look.
                var outline = blocks.Select((b, i) => (b, i)).GroupBy(x => x.b.Location)
                                    .Select(g => new
                                    {
                                        location = g.Key,
                                        kinds = string.Join(", ", g.Select(x => x.b.Kind).Distinct()),
                                        firstLine = Cut(g.First().b.Text, 120)
                                    }).ToList();
                return new { ok = true, action = "searchResponse", snapshotId = source.SnapshotId, found = true, file = fileName, totalBlocks = blocks.Count, outline = outline.Take(400).ToList(), outlineCut = outline.Count > 400 };
            }

            bool AtLocation(DocBlock b)
            {
                if (loc.Length == 0) return true;
                string l = Text.Norm(b.Location);
                return l == loc || l.StartsWith(loc + ",") || (!loc.StartsWith("slide ") && !loc.StartsWith("table ") && l.Contains(loc));
            }
            var hits = blocks.Select((b, i) => (b, i, matched: words.Where(w => Text.HasWord(b.Text, w)).ToList()))
                             .Where(x => AtLocation(x.b) && (words.Count == 0 || x.matched.Count > 0))
                             .OrderByDescending(x => x.matched.Count).ThenBy(x => x.i)
                             .ToList();
            var shown = hits.Take(max).OrderBy(x => x.i).Select(x => new
            {
                location = x.b.Location,
                kind = x.b.Kind,
                text = Cut(x.b.Text, MaxBlockChars),
                truncated = x.b.Text.Length > MaxBlockChars,
                matchedWords = words.Count > 0 ? x.matched : null
            }).ToList();
            return new
            {
                ok = true,
                action = "searchResponse",
                snapshotId = source.SnapshotId,
                found = hits.Count > 0,
                file = fileName,
                totalBlocks = blocks.Count,
                matches = hits.Count,
                shown = shown.Count,
                message = hits.Count > shown.Count ? $"Showing the {shown.Count} best matches of {hits.Count}; add words or a location to narrow it down." : null,
                results = shown
            };
        }

        private static string Cut(string s, int max) => s.Length > max ? s.Substring(0, max) + " …" : s;
    }
}
