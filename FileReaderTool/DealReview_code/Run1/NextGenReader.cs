// NextGenReader.cs: reads NextGen Raw Data: effort by resource group, Deloitte rows only
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
    /// <summary>Raw Data tab: Effort summed by Resource Group, Deloitte rows only (SourceGroupType = D), as the steps say.</summary>
    public static class NextGenReader
    {
        public static NextGenData Read(string path, DealSettings settings)
        {
            using var book = new XlsxBook(path);
            var sheet = settings.Classifier.NextGenSheets.Select(book.FindSheet).FirstOrDefault(s => s != null)
                        ?? throw new InvalidDataException($"No Raw Data sheet in {Path.GetFileName(path)}.");
            var rows = book.ReadRows(sheet).ToList();
            var header = rows.FirstOrDefault(r => r.Cells.Count > 0) ?? throw new InvalidDataException("Raw Data sheet is empty.");

            // Column names compared without spaces, so "SourceGroupType" and "Source Group Type" both work.
            int Col(string name) => header.Cells.Where(c => Text.Norm(c.Value).Replace(" ", "") == Text.Norm(name).Replace(" ", ""))
                                                .Select(c => c.Key).DefaultIfEmpty(0).Min();
            int group = Col("Resource Group"), source = Col("SourceGroupType"), effort = Col("Effort");
            if (group == 0 || source == 0 || effort == 0)
                throw new InvalidDataException("Raw Data needs the columns 'Resource Group', 'SourceGroupType' and 'Effort'.");

            var data = new NextGenData() { FileName = Path.GetFileName(path), SheetName = sheet.Name, HeaderRow = header.Number };
            foreach (var (field, col) in new[] { ("resourceGroup", group), ("sourceGroupType", source), ("effort", effort) })
            {
                data.Columns[field] = XlsxBook.ColumnName(col);
                data.HeaderText[field] = header.GetText(col);
            }
            int bad = 0;
            foreach (var r in rows.Where(r => r.Number > header.Number))
            {
                string g = r.GetText(group);
                string src = r.GetText(source).ToUpperInvariant();
                double? e = r.GetNumber(effort, out bool err);
                if (g.Length == 0 && e == null) continue;
                data.Rows++;
                if (err) { bad++; continue; }
                double h = e ?? 0;
                string srcKey = src.Length == 0 ? "(blank)" : src;
                data.HoursBySourceType[srcKey] = data.HoursBySourceType.GetValueOrDefault(srcKey) + h;
                if (src == "D") data.DeloitteHoursByGroup[g] = data.DeloitteHoursByGroup.GetValueOrDefault(g) + h;
            }
            if (bad > 0) data.Notes.Add($"{bad} Raw Data row(s) had an unreadable Effort value and were skipped.");
            if (data.DeloitteHoursByGroup.Count == 0) data.Notes.Add("No rows with SourceGroupType = D found.");
            return data;
        }
    }
}
