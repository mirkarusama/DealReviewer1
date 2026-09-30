// XlsxBook.cs: opens .xlsx / .xlsm files directly (zip + XML), read-only, without Excel
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
    /// <summary>
    /// Reads .xlsx / .xlsm files directly (they are zip files of XML).
    /// Returns the values Excel last saved (cached values), never formulas.
    /// No Excel, no NuGet packages needed. Opens files read-only, even if Excel has them open.
    /// </summary>
    public sealed class XlsxBook : IDisposable
    {
        public sealed class SheetInfo
        {
            public string Name { get; set; }
            public string State { get; set; }   // visible | hidden | veryHidden
            public string PartPath { get; set; }
            public bool Visible => string.Equals(State, "visible", StringComparison.OrdinalIgnoreCase);
        }

        private readonly FileStream _stream;
        private readonly ZipArchive _zip;
        private List<string> _sharedStrings;

        public string FilePath { get; }
        public List<SheetInfo> Sheets { get; } = new List<SheetInfo>();

        public XlsxBook(string path)
        {
            FilePath = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _zip = new ZipArchive(_stream, ZipArchiveMode.Read);
            LoadSheetList();
        }

        public void Dispose()
        {
            _zip.Dispose();
            _stream.Dispose();
        }

        /// <summary>Finds a sheet by name, ignoring case and surrounding spaces. Prefers a visible sheet.</summary>
        public SheetInfo FindSheet(string name)
        {
            string wanted = Text.Norm(name);
            return Sheets.Where(s => Text.Norm(s.Name) == wanted)
                         .OrderByDescending(s => s.Visible)
                         .FirstOrDefault();
        }

        private void LoadSheetList()
        {
            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var workbook = LoadXml("xl/workbook.xml");
            var rels = LoadXml("xl/_rels/workbook.xml.rels");
            var targets = rels.Root.Elements()
                .Where(e => e.Name.LocalName == "Relationship")
                .ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));

            foreach (var s in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
            {
                string id = (string)s.Attribute(r + "id");
                if (id == null || !targets.TryGetValue(id, out var target)) continue;
                Sheets.Add(new SheetInfo()
                {
                    Name = (string)s.Attribute("name"),
                    State = (string)s.Attribute("state") ?? "visible",
                    PartPath = ResolvePart("xl/", target)
                });
            }
        }

        public static string ResolvePart(string baseDir, string target)
        {
            if (target.StartsWith("/")) return target.TrimStart('/');
            var parts = new List<string>(baseDir.TrimEnd('/').Split('/'));
            foreach (var seg in target.Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != "." && seg.Length > 0) parts.Add(seg);
            }
            return string.Join("/", parts);
        }

        private XDocument LoadXml(string part)
        {
            var entry = _zip.GetEntry(part) ?? throw new InvalidDataException($"'{Path.GetFileName(FilePath)}' is missing '{part}'. Is it a real Excel file?");
            using var s = entry.Open();
            return XDocument.Load(s);
        }

        private List<string> SharedStrings
        {
            get
            {
                if (_sharedStrings != null) return _sharedStrings;
                _sharedStrings = new List<string>();
                var entry = _zip.GetEntry("xl/sharedStrings.xml");
                if (entry == null) return _sharedStrings;
                using var s = entry.Open();
                using var xr = XmlReader.Create(s, new XmlReaderSettings() { DtdProcessing = DtdProcessing.Prohibit });
                while (!xr.EOF)
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
                    {
                        var sb = new StringBuilder();
                        ReadRichText(xr, sb);
                        _sharedStrings.Add(sb.ToString());
                        continue;
                    }
                    xr.Read();
                }
                return _sharedStrings;
            }
        }

        /// <summary>Streams the rows of a sheet. Each row holds only non-empty cells, keyed by 1-based column number.</summary>
        public IEnumerable<SheetRow> ReadRows(SheetInfo sheet)
        {
            var entry = _zip.GetEntry(sheet.PartPath)
                        ?? throw new InvalidDataException($"Sheet '{sheet.Name}' has no data part ({sheet.PartPath}).");
            var shared = SharedStrings;
            using var s = entry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings() { DtdProcessing = DtdProcessing.Prohibit });
            SheetRow current = null;
            int lastRow = 0;
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row")
                {
                    string r = xr.GetAttribute("r");
                    int rowNum = r != null ? int.Parse(r, CultureInfo.InvariantCulture) : lastRow + 1;
                    lastRow = rowNum;
                    current = new SheetRow(rowNum);
                    if (xr.IsEmptyElement) { yield return current; current = null; }
                }
                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "c" && current != null)
                {
                    string cellRef = xr.GetAttribute("r");
                    string type = xr.GetAttribute("t");
                    int col = cellRef != null ? ColumnNumber(cellRef) : current.LastColumn + 1;
                    current.LastColumn = col;
                    string value = ReadCellValue(xr, type, shared);
                    if (!string.IsNullOrEmpty(value)) current.Cells[col] = value;
                }
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "row" && current != null)
                {
                    yield return current;
                    current = null;
                }
            }
        }

        private static string ReadCellValue(XmlReader xr, string type, List<string> shared)
        {
            if (xr.IsEmptyElement) return null;
            int depth = xr.Depth;
            string raw = null;
            string inline = null;
            xr.Read();
            while (!xr.EOF && !(xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth))
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "v") { raw = xr.ReadElementContentAsString(); continue; }
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "is")
                {
                    var sb = new StringBuilder();
                    ReadRichText(xr, sb);
                    inline = sb.ToString();
                    continue;
                }
                if (xr.NodeType == XmlNodeType.Element) { xr.Skip(); continue; }   // e.g. <f> formula
                xr.Read();
            }
            switch (type)
            {
                case "s":
                    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count ? shared[i] : raw;
                case "inlineStr":
                    return inline;
                case "b":
                    return raw == "1" ? "TRUE" : "FALSE";
                default:
                    return raw;   // numbers (invariant format), "str" formula results, "e" errors such as #REF!
            }
        }

        /// <summary>Reads the text of an &lt;si&gt; or &lt;is&gt; element (skips phonetic runs) and moves past it.</summary>
        private static void ReadRichText(XmlReader xr, StringBuilder sb)
        {
            if (xr.IsEmptyElement) { xr.Read(); return; }
            int depth = xr.Depth;
            xr.Read();
            while (!xr.EOF && !(xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth))
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "rPh") { xr.Skip(); continue; }
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t") { sb.Append(xr.ReadElementContentAsString()); continue; }
                xr.Read();
            }
            xr.Read();
        }

        public static int ColumnNumber(string cellRef)
        {
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
                else break;
            }
            return col;
        }

        public static string ColumnName(int col) => Text.ColumnLetter(col);
    }

    public sealed class SheetRow
    {
        public SheetRow(int number) { Number = number; }
        public int Number { get; }
        public int LastColumn { get; set; }
        public Dictionary<int, string> Cells { get; } = new Dictionary<int, string>();

        public string Get(int col) => col > 0 && Cells.TryGetValue(col, out var v) ? v : null;
        public string GetText(int col) => (Get(col) ?? "").Trim();

        /// <summary>Returns the number in a cell, or null if empty. Errors such as #REF! return null and set isError.</summary>
        public double? GetNumber(int col, out bool isError)
        {
            isError = false;
            var v = Get(col);
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            isError = true;
            return null;
        }
    }
}
