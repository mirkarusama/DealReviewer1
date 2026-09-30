// PricingModelReader.cs: reads the Resourcing sheet (old and new layouts) into rows, finding every column by its header
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
    /// Finds the resource table by its header labels (not fixed positions), so both the
    /// old layout (header on row 13, "Level") and the new layout (header on row 16, "Title"/"Geography") work.
    /// Total hours come from "Hours by Fiscal Year" > "Total Engagement".
    /// </summary>
    public static class PricingModelReader
    {
        private const string SheetName = "Resourcing";

        public static PricingModel Read(string path)
        {
            using var book = new XlsxBook(path);
            var sheet = book.FindSheet(SheetName) ?? throw new InvalidDataException($"No '{SheetName}' sheet in {Path.GetFileName(path)}.");
            var rows = book.ReadRows(sheet).ToList();
            var model = new PricingModel() { FileName = Path.GetFileName(path), SheetName = sheet.Name, SheetVisible = sheet.Visible };
            model.LookAlikeSheets = book.Sheets.Where(x => x != sheet && Text.Norm(x.Name).Contains("resourcing"))
                                               .Select(x => $"'{x.Name}' ({x.State})").ToList();

            // 1. Header row: the first row with "Resource Cohort", "Name / Role" and either "Title" or "Level".
            var header = rows.Take(60).FirstOrDefault(r =>
            {
                var labels = r.Cells.Values.Select(Text.Norm).ToHashSet();
                return labels.Contains("resource cohort") && labels.Contains("name/role") && (labels.Contains("title") || labels.Contains("level"));
            }) ?? throw new InvalidDataException($"Couldn't find the resource table header (Resource Cohort / Name / Role / Title) in '{SheetName}'.");
            model.HeaderRow = header.Number;

            int Col(string label) => header.Cells.Where(c => Text.Norm(c.Value) == label).Select(c => c.Key).DefaultIfEmpty(0).Min();
            var cols = new Dictionary<string, int>()
            {
                ["cohort"] = Col("resource cohort"),
                ["business"] = Col("business"),
                ["geography"] = Col("geography"),
                ["title"] = Col("title"),
                ["jobLevel"] = Col("job level"),
                ["level"] = Col("level"),
                ["workTeam"] = Col("phase/work team"),
                ["role"] = Col("name/role"),
            };
            model.Layout = cols["title"] > 0 ? "new" : "old";
            if (model.Layout == "new") cols["level"] = 0;   // the new layout's "Level" labels belong to other blocks
            int titleCol = cols["title"] > 0 ? cols["title"] : cols["level"];

            // 2. Total hours column: "Total Engagement" under "Hours by Fiscal Year".
            var above = rows.Where(r => r.Number < header.Number).ToList();
            int totalCol = FindTotalColumn(above);
            if (totalCol == 0) throw new InvalidDataException("Couldn't find the 'Hours by Fiscal Year' > 'Total Engagement' column in 'Resourcing'.");
            if (header.GetNumber(totalCol, out _) is double own) model.SheetOwnTotal = own;
            model.TotalBlockLabelRow = above.Where(r => Text.Norm(r.Get(totalCol)) == "hours by fiscal year").Select(r => r.Number).DefaultIfEmpty(0).Max();
            model.TotalLabelRow = above.Where(r => Text.Norm(r.Get(totalCol)) == "total engagement").Select(r => r.Number).DefaultIfEmpty(0).Max();
            if (model.TotalBlockLabelRow == 0)   // the block label may sit over a merged range that starts left of the total column
                model.TotalBlockLabelRow = above.Where(r => r.Cells.Any(c => Text.Norm(c.Value) == "hours by fiscal year")).Select(r => r.Number).DefaultIfEmpty(0).Max();

            foreach (var kv in cols.Where(k => k.Value > 0))
            {
                model.Columns[kv.Key] = XlsxBook.ColumnName(kv.Value);
                model.ColumnNumbers[kv.Key] = kv.Value;
                model.HeaderText[kv.Key] = header.GetText(kv.Value);
            }
            model.Columns["totalHours"] = XlsxBook.ColumnName(totalCol);
            model.ColumnNumbers["totalHours"] = totalCol;
            model.HeaderText["totalHours"] = "Hours by Fiscal Year > Total Engagement";   // the header row cell holds the sheet's own total

            // 3. Period columns (4-week periods), in date order.
            model.Periods = FindPeriodColumns(above);

            // 4. Data rows.
            foreach (var r in rows.Where(r => r.Number > header.Number))
            {
                var row = new ResourceRow()
                {
                    Row = r.Number,
                    Cohort = r.GetText(cols["cohort"]),
                    Business = r.GetText(cols["business"]),
                    Geography = r.GetText(cols["geography"]),
                    Title = r.GetText(titleCol),
                    JobLevel = r.GetText(cols["jobLevel"]),
                    WorkTeam = r.GetText(cols["workTeam"]),
                    Role = r.GetText(cols["role"]),
                };
                double? h = r.GetNumber(totalCol, out bool err);
                row.Hours = h ?? 0;
                row.HoursError = err;
                if (model.Periods.Count > 0)
                    row.PeriodHours = model.Periods.Select(p => r.GetNumber(p.Column, out _) ?? 0).ToArray();
                if (!row.HasIdentity && row.Hours == 0 && !row.HoursError) continue;   // empty template row
                model.Rows.Add(row);
            }

            model.SelfCheck();
            return model;
        }


        private static int FindTotalColumn(List<SheetRow> above)
        {
            var blockCols = above.SelectMany(r => r.Cells.Where(c => Text.Norm(c.Value) == "hours by fiscal year").Select(c => c.Key)).ToHashSet();
            var candidates = above.SelectMany(r => r.Cells.Where(c => Text.Norm(c.Value) == "total engagement").Select(c => c.Key)).Distinct().OrderBy(c => c).ToList();
            return candidates.FirstOrDefault(c => blockCols.Contains(c));
        }

        private static List<PeriodColumn> FindPeriodColumns(List<SheetRow> above)
        {
            var list = new List<PeriodColumn>();
            // New layout: tag row with "FY27|P1" style labels.
            var tag = new Regex(@"^fy(\d{2,4})\|p(\d{1,2})$");
            foreach (var r in above)
                foreach (var c in r.Cells)
                {
                    var m = tag.Match(Text.Norm(c.Value));
                    if (m.Success) list.Add(new PeriodColumn() { Column = c.Key, FiscalYear = int.Parse(m.Groups[1].Value), Period = int.Parse(m.Groups[2].Value) });
                }
            if (list.Count > 0) return Order(list);

            // Old layout: "FY26 Hours by Period" block label, with P1..P13 labels in a row below it.
            var block = new Regex(@"^fy(\d{2,4}) hours by period$");
            foreach (var r in above)
                foreach (var c in r.Cells)
                {
                    var m = block.Match(Text.Norm(c.Value));
                    if (!m.Success) continue;
                    int fy = int.Parse(m.Groups[1].Value);
                    var labelRow = above.FirstOrDefault(x => x.Number > r.Number && Text.Norm(x.Get(c.Key)) == "p1");
                    if (labelRow == null) continue;
                    for (int k = 0; k < 13; k++)
                        if (Text.Norm(labelRow.Get(c.Key + k)) == $"p{k + 1}")
                            list.Add(new PeriodColumn() { Column = c.Key + k, FiscalYear = fy, Period = k + 1 });
                }
            return Order(list);
        }

        private static List<PeriodColumn> Order(List<PeriodColumn> list) =>
            list.GroupBy(p => p.Column).Select(g => g.First()).OrderBy(p => p.FiscalYear).ThenBy(p => p.Period).ToList();
    }
}
