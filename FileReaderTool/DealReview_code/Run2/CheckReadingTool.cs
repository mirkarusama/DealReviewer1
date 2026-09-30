// CheckReadingTool.cs: checkReading: which sheet, header row, columns and rows every number came from, and anything that looks off
// Used by: Run 2.
// Request: {} or {"rows":[245, 246]}.

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
    public sealed class ReadingRequest
    {
        public List<int> Rows { get; set; } = new List<int>();
    }

    /// <summary>checkReading: where every number came from.</summary>
    public static class CheckReadingTool
    {
        private static object Sample(ResourceRow r) => new
        {
            row = r.Row,
            cohort = r.Cohort, business = r.Business, geography = r.Geography, title = r.Title, jobLevel = r.JobLevel,
            workTeam = r.WorkTeam, role = r.Role,
            hours = r.HoursError ? (double?)null : Text.R2(r.Hours),
            hoursError = r.HoursError ? true : (bool?)null,
            periodHoursTotal = r.PeriodHours.Length > 0 ? Text.R2(r.PeriodHours.Sum()) : (double?)null
        };

        public static object Run(SnapshotLoader source, ReadingRequest req)
        {
            var ctx = DealTools.LoadOrThrow(source, source.Settings());
            var pm = ctx.Pm; var ng = ctx.Ng;
            var checks = new List<string>();

            checks.Add(pm.SheetVisible ? $"OK: read the visible sheet '{pm.SheetName}'." : $"Check: read sheet '{pm.SheetName}', which is hidden.");
            if (pm.LookAlikeSheets.Count > 0) checks.Add($"Check: other sheets with a similar name were not read: {string.Join(", ", pm.LookAlikeSheets)}.");
            checks.Add($"OK: header row {pm.HeaderRow} ({pm.Layout} layout).");

            var expected = new List<(string field, string label)> { ("cohort", "Resource Cohort"), ("workTeam", "Phase / Work Team"), ("role", "Name / Role") };
            if (pm.Layout == "new") { expected.Add(("title", "Title")); expected.Add(("geography", "Geography")); expected.Add(("jobLevel", "Job Level")); }
            else expected.Add(("level", "Level"));
            foreach (var (field, label) in expected)
                if (!pm.ColumnNumbers.ContainsKey(field)) checks.Add($"Check: no '{label}' column found on the header row.");

            int totalCol = pm.ColumnNumbers["totalHours"];
            checks.Add($"OK: total hours from column {Text.ColumnLetter(totalCol)}: 'Hours by Fiscal Year'{(pm.TotalBlockLabelRow > 0 ? $" (row {pm.TotalBlockLabelRow})" : "")} > 'Total Engagement'{(pm.TotalLabelRow > 0 ? $" (row {pm.TotalLabelRow})" : "")}.");

            double sum = pm.TotalHours;
            if (!pm.SheetOwnTotal.HasValue) checks.Add("Check: no sheet total on the header row, so the row sum couldn't be compared with it.");
            else if (!pm.TotalsDontMatch) checks.Add($"OK: rows add up to the sheet's own total ({Hrs(sum)} hrs).");
            else
            {
                double diff = sum - pm.SheetOwnTotal.Value;
                var match = pm.Rows.Where(r => Math.Abs(r.Hours - Math.Abs(diff)) <= 0.5).Select(r => r.Row).Take(5).ToList();
                checks.Add($"Check: rows add up to {Hrs(sum)} hrs but the sheet's own total says {Hrs(pm.SheetOwnTotal.Value)} (difference {Hrs(diff)})." +
                           (diff > 0 && match.Count > 0 ? $" Row(s) {string.Join(", ", match)} hold exactly the difference, so they may be totals rows counted twice (try whatIf leaveOutRows)." : ""));
            }

            // Rows that look like totals or subtotals rather than people.
            var suspects = new List<(ResourceRow r, string why)>();
            foreach (var r in pm.Rows.Where(r => r.Hours != 0))
            {
                string all = string.Join(" ", r.Cohort, r.Business, r.Title, r.WorkTeam, r.Role);
                if (!r.HasIdentity) suspects.Add((r, "hours but no cohort, title, team or role"));
                else if (Regex.IsMatch(all, @"(?<![\p{L}])(sub ?total|grand total|total)(?![\p{L}])", RegexOptions.IgnoreCase)) suspects.Add((r, "says 'total'"));
                else if (pm.Rows.Count > 4 && Math.Abs(r.Hours - (sum - r.Hours)) <= 0.5) suspects.Add((r, "equals all the other rows added up"));
                else if (pm.Rows.Count > 4 && r.Hours >= 0.5 * sum) suspects.Add((r, "holds half or more of all hours"));
            }
            if (suspects.Count > 0)
                checks.Add($"Check: {suspects.Count} row(s) may be totals, not people: " + string.Join("; ", suspects.Take(10).Select(x => $"row {x.r.Row} ({Hrs(x.r.Hours)} hrs, {x.why})")) + ".");
            else checks.Add("OK: no row looks like a totals row.");

            var errors = pm.Rows.Where(r => r.HoursError).ToList();
            if (errors.Count > 0) checks.Add($"Check: {errors.Count} row(s) show an Excel error instead of hours: {string.Join(", ", errors.Take(10).Select(r => r.Row))}.");

            object periods = null;
            if (pm.Periods.Count == 0) checks.Add("Check: no 4-week period columns found, so Q6's EFA check can't run.");
            else
            {
                var first = pm.Periods.First(); var last = pm.Periods.Last();
                checks.Add($"OK: {pm.Periods.Count} period columns, {first.Label} (column {Text.ColumnLetter(first.Column)}) to {last.Label} (column {Text.ColumnLetter(last.Column)}).");
                var off = pm.Rows.Where(r => Math.Abs(r.PeriodHours.Sum() - r.Hours) > 0.5).ToList();
                if (off.Count > 0) checks.Add($"Check: in {off.Count} row(s) the period hours don't add up to the row total (e.g. rows {string.Join(", ", off.Take(5).Select(r => r.Row))}).");
                periods = new { count = pm.Periods.Count, first = $"{first.Label} ({Text.ColumnLetter(first.Column)})", last = $"{last.Label} ({Text.ColumnLetter(last.Column)})", rowsNotAddingUp = off.Count };
            }

            checks.Add($"OK: NextGen read from sheet '{ng.SheetName}', header row {ng.HeaderRow}: " +
                       string.Join(", ", ng.Columns.Select(kv => $"{ng.HeaderText[kv.Key]} (column {kv.Value})")) + ".");
            if (!ng.HoursBySourceType.ContainsKey("D")) checks.Add("Check: NextGen has no rows with SourceGroupType = D (Deloitte).");
            foreach (var note in ng.Notes) checks.Add("Check: NextGen: " + note);

            var requested = new List<object>();
            foreach (int n in req.Rows.Distinct().Take(30))
            {
                var r = pm.Rows.FirstOrDefault(x => x.Row == n);
                if (r != null) requested.Add(Sample(r));
                else requested.Add(new { row = n, note = n <= pm.HeaderRow ? "above or on the header row, so not read as a person" : "not read as a person (empty row or outside the table)" });
            }

            return new
            {
                ok = true,
                action = "checkReading",
                snapshotId = source.SnapshotId,
                sourceFiles = ctx.Files.Files.Select(f => new { f.FileName, f.Type, f.LastSaved, f.SizeBytes }).ToList(),
                checks,
                pricingModel = new
                {
                    file = pm.FileName,
                    sheet = pm.SheetName,
                    sheetVisible = pm.SheetVisible,
                    layout = pm.Layout,
                    headerRow = pm.HeaderRow,
                    columns = pm.ColumnNumbers.Select(kv => new { field = kv.Key, column = Text.ColumnLetter(kv.Value), header = pm.HeaderText.GetValueOrDefault(kv.Key, "") }).ToList(),
                    sheetOwnTotal = pm.SheetOwnTotal.HasValue ? Text.R2(pm.SheetOwnTotal.Value) : (double?)null,
                    rowSum = Text.R2(sum),
                    dataRows = new { count = pm.Rows.Count, first = pm.Rows.Count > 0 ? pm.Rows.First().Row : 0, last = pm.Rows.Count > 0 ? pm.Rows.Last().Row : 0 },
                    firstRows = pm.Rows.Take(3).Select(Sample).ToList(),
                    lastRows = pm.Rows.Skip(Math.Max(0, pm.Rows.Count - 3)).Select(Sample).ToList(),
                    periods
                },
                nextGen = new
                {
                    file = ng.FileName,
                    sheet = ng.SheetName,
                    headerRow = ng.HeaderRow,
                    columns = ng.Columns.Select(kv => new { field = kv.Key, column = kv.Value, header = ng.HeaderText[kv.Key] }).ToList(),
                    rowsRead = ng.Rows,
                    hoursBySourceGroupType = ng.HoursBySourceType.ToDictionary(k => k.Key, k => Text.R2(k.Value)),
                    deloitteTotal = Text.R2(ng.DeloitteTotal)
                },
                requestedRows = requested.Count > 0 ? requested : null
            };
        }
    }
}
