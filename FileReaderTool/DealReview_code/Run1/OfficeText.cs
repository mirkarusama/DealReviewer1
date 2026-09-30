// OfficeText.cs: opens .docx / .pptx files directly and returns their text in reading order, including content controls and SmartArt
// Used by: Run 1.
// Low-level file reading; no business rules here.

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
    public static class OfficeText
    {
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>Reads a .docx or .pptx into blocks, in reading order. Includes content controls and SmartArt text.</summary>
        public static List<DocBlock> Read(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            if (ext == ".docx") return ReadDocx(zip);
            if (ext == ".pptx") return ReadPptx(zip);
            throw new NotSupportedException($"Unsupported document type '{ext}'.");
        }

        /// <summary>Where the pictures are (code can't read them), e.g. "Slide 12: 2 picture(s)".</summary>
        public static List<string> Pictures(string path)
        {
            var list = new List<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    var doc = Load(zip, "word/document.xml");
                    int n = doc == null ? 0 : doc.Descendants().Count(e => e.Name.LocalName == "pic" && !InFallback(e));
                    if (n > 0) list.Add($"Document: {n} picture(s)");
                }
                else
                {
                    foreach (var (loc, slide, _) in Slides(zip))
                    {
                        int n = slide.Descendants().Count(e => e.Name.LocalName == "pic" && !InFallback(e));
                        if (n > 0) list.Add($"{loc}: {n} picture(s)");
                    }
                }
            }
            catch (Exception ex) { list.Add("Couldn't count pictures: " + ex.Message); }
            return list;
        }

        public static string PlainText(List<DocBlock> blocks) => string.Join("\n", blocks.Select(b => b.Text));

        private static XDocument Load(ZipArchive zip, string part)
        {
            var e = zip.GetEntry(part);
            if (e == null) return null;
            using var s = e.Open();
            return XDocument.Load(s);
        }

        /// <summary>Relationship id -> part path, for the part at partPath (e.g. "word/document.xml").</summary>
        private static Dictionary<string, (string type, string part)> Rels(ZipArchive zip, string partPath)
        {
            string dir = partPath.Contains("/") ? partPath.Substring(0, partPath.LastIndexOf('/')) : "";
            string file = partPath.Substring(partPath.LastIndexOf('/') + 1);
            var rels = Load(zip, (dir.Length > 0 ? dir + "/" : "") + "_rels/" + file + ".rels");
            var map = new Dictionary<string, (string, string)>();
            if (rels?.Root == null) return map;
            foreach (var e in rels.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                string id = (string)e.Attribute("Id"), target = (string)e.Attribute("Target"), type = (string)e.Attribute("Type") ?? "";
                if (id == null || target == null || (string)e.Attribute("TargetMode") == "External") continue;
                map[id] = (type, XlsxBook.ResolvePart(dir + "/", target));
            }
            return map;
        }

        /// <summary>Word and PowerPoint save a copy of some shapes as a fallback; reading both would double the text.</summary>
        private static bool InFallback(XElement e) => e.Ancestors().Any(a => a.Name.LocalName == "Fallback");

        /// <summary>The text of a SmartArt graphic referenced inside el, one box per " | ".</summary>
        private static string SmartArtText(ZipArchive zip, XElement el, Dictionary<string, (string type, string part)> rels)
        {
            var parts = new List<string>();
            foreach (var relIds in el.Descendants().Where(e => e.Name.LocalName == "relIds"))
            {
                string dm = (string)relIds.Attribute(R + "dm");
                if (dm == null || !rels.TryGetValue(dm, out var rel) || !rel.type.EndsWith("/diagramData")) continue;
                var data = Load(zip, rel.part);
                if (data == null) continue;
                foreach (var pt in data.Descendants().Where(e => e.Name.LocalName == "pt"))
                {
                    // only the innermost text runs: the diagram's own <dgm:t> wrapper holds the same text again
                    string text = Text.Squash(string.Join(" ", pt.Descendants().Where(d => d.Name.LocalName == "t" && !d.HasElements).Select(d => d.Value)));
                    if (text.Length > 0) parts.Add(text);
                }
            }
            return string.Join(" | ", parts);
        }

        private static IEnumerable<XElement> BodyItems(XElement parent)
        {
            foreach (var el in parent.Elements())
            {
                string name = el.Name.LocalName;
                if (name == "sdt")   // content control: read what's inside it
                {
                    var content = el.Elements().FirstOrDefault(e => e.Name.LocalName == "sdtContent");
                    if (content != null) foreach (var x in BodyItems(content)) yield return x;
                }
                else if (name == "customXml") { foreach (var x in BodyItems(el)) yield return x; }
                else yield return el;
            }
        }

        private static List<DocBlock> ReadDocx(ZipArchive zip)
        {
            var doc = Load(zip, "word/document.xml") ?? throw new InvalidDataException("Word file has no word/document.xml.");
            var rels = Rels(zip, "word/document.xml");
            var blocks = new List<DocBlock>();
            var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body == null) return blocks;
            int tableNo = 0, artNo = 0;
            string section = "";
            string Where() => section.Length > 0 ? $"Section '{section}'" : "Start of document";
            foreach (var el in BodyItems(body))
            {
                if (el.Name.LocalName == "p")
                {
                    string art = SmartArtText(zip, el, rels);
                    if (art.Length > 0) { artNo++; blocks.Add(new DocBlock() { Kind = "smartart", Location = $"SmartArt {artNo}" + (section.Length > 0 ? $", section '{section}'" : ""), Text = art }); }
                    string text = ParagraphText(el);
                    if (text.Length == 0) continue;
                    string style = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "pStyle")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value ?? "";
                    bool heading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase);
                    if (heading) section = text.Length > 80 ? text.Substring(0, 80) + "…" : text;
                    blocks.Add(new DocBlock() { Kind = heading ? "heading" : "paragraph", Location = Where(), Text = text });
                }
                else if (el.Name.LocalName == "tbl")
                {
                    tableNo++;
                    blocks.Add(new DocBlock() { Kind = "table", Location = $"Table {tableNo}" + (section.Length > 0 ? $", section '{section}'" : ""), Text = TableText(el, "tr", "tc") });
                }
            }
            return blocks;
        }

        private static string ParagraphText(XElement p)
        {
            var sb = new StringBuilder();
            foreach (var e in p.Descendants())
            {
                switch (e.Name.LocalName)
                {
                    case "t": if (!InFallback(e)) sb.Append(e.Value); break;
                    case "tab": sb.Append(' '); break;
                    case "br": case "cr": sb.Append(' '); break;
                }
            }
            return Text.Squash(sb.ToString());
        }

        private static string TableText(XElement tbl, string rowName, string cellName)
        {
            var lines = new List<string>();
            foreach (var tr in tbl.Descendants().Where(e => e.Name.LocalName == rowName))
            {
                // only cells that belong to this row (not to a nested table)
                var cells = tr.Elements().Where(e => e.Name.LocalName == cellName)
                              .Select(tc => Text.Squash(string.Join(" ", tc.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value))))
                              .ToList();
                if (cells.Any(c => c.Length > 0)) lines.Add(string.Join(" | ", cells));
            }
            return string.Join("\n", lines);
        }

        /// <summary>The slides in presentation order: location, slide XML, slide part path.</summary>
        private static IEnumerable<(string loc, XDocument slide, string part)> Slides(ZipArchive zip)
        {
            var pres = Load(zip, "ppt/presentation.xml") ?? throw new InvalidDataException("PowerPoint file has no ppt/presentation.xml.");
            var rels = Rels(zip, "ppt/presentation.xml");
            int slideNo = 0;
            foreach (var sldId in pres.Descendants().Where(e => e.Name.LocalName == "sldId"))
            {
                slideNo++;
                string id = (string)sldId.Attribute(R + "id");
                if (id == null || !rels.TryGetValue(id, out var rel)) continue;
                var slide = Load(zip, rel.part);
                if (slide != null) yield return ($"Slide {slideNo}", slide, rel.part);
            }
        }

        private static List<DocBlock> ReadPptx(ZipArchive zip)
        {
            var blocks = new List<DocBlock>();
            foreach (var (loc, slide, part) in Slides(zip))
            {
                var rels = Rels(zip, part);
                foreach (var el in slide.Descendants())
                {
                    if (InFallback(el)) continue;
                    if (el.Name.LocalName == "sp")
                    {
                        var txBody = el.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
                        if (txBody == null) continue;
                        foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
                        {
                            string text = Text.Squash(string.Join("", p.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value)));
                            if (text.Length > 0) blocks.Add(new DocBlock() { Kind = "paragraph", Location = loc, Text = text });
                        }
                    }
                    else if (el.Name.LocalName == "tbl")
                    {
                        blocks.Add(new DocBlock() { Kind = "table", Location = loc, Text = TableText(el, "tr", "tc") });
                    }
                    else if (el.Name.LocalName == "graphicFrame")
                    {
                        string art = SmartArtText(zip, el, rels);
                        if (art.Length > 0) blocks.Add(new DocBlock() { Kind = "smartart", Location = loc, Text = art });
                    }
                }
            }
            return blocks;
        }
    }
}
