// DealModels.cs: the data shapes: files, pricing model rows, NextGen totals, response text, the review and the snapshot
// Used by: Run 1 and Run 2.
// No logic to review here beyond two small self-checks; the rules are in Questions.cs and Rules.cs.

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
    public static class FileType
    {
        public const string PricingModel = "PricingModel";
        public const string NextGen = "NextGen";
        public const string Response = "Response";
        public const string Rfp = "RFP";
        public const string Other = "Other";
        public const string Unsure = "Unsure";
        public const string Pdf = "PDF";          // code can't read PDFs; it could be the RFP or the response
    }

    public sealed class ClassifiedFile
    {
        public string FileName { get; set; }
        public string Type { get; set; }
        public string Reason { get; set; }
        public string LastSaved { get; set; }     // when the file was last saved, so a recheck can tell it's the same file
        public long SizeBytes { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string FullPath { get; set; }
    }

    public sealed class Classification
    {
        public List<ClassifiedFile> Files { get; set; } = new List<ClassifiedFile>();
        public List<string> Issues { get; set; } = new List<string>();
        public List<string> Missing { get; set; } = new List<string>();
        public bool FilesPresent => Issues.Count == 0;
        public ClassifiedFile Single(string type) => Files.SingleOrDefault(f => f.Type == type);
    }

    public sealed class ResourceRow
    {
        public int Row { get; set; }
        public string Cohort { get; set; } = "";
        public string Business { get; set; } = "";
        public string Geography { get; set; } = "";
        public string Title { get; set; } = "";      // new layout "Title", old layout "Level"
        public string JobLevel { get; set; } = "";   // new layout only
        public string WorkTeam { get; set; } = "";
        public string Role { get; set; } = "";
        public double Hours { get; set; }
        public bool HoursError { get; set; }
        public double[] PeriodHours { get; set; } = Array.Empty<double>();

        public bool HasIdentity => Title.Length + JobLevel.Length + WorkTeam.Length + Role.Length + Cohort.Length > 0;
        public string Describe() =>
            $"row {Row}: " + string.Join(", ", new[] { Title, WorkTeam, Role }.Where(s => s.Length > 0).DefaultIfEmpty("(no title, team or role)"));
    }

    public sealed class PeriodColumn
    {
        public int Column { get; set; }
        public int FiscalYear { get; set; }
        public int Period { get; set; }
        public string Label => $"FY{FiscalYear % 100:00} P{Period}";
    }

    public sealed class PricingModel
    {
        public string FileName { get; set; }
        public string SheetName { get; set; }
        public bool SheetVisible { get; set; }
        public List<string> LookAlikeSheets { get; set; } = new List<string>();   // other sheets with a similar name, not read
        public string Layout { get; set; }             // "new" or "old"
        public int HeaderRow { get; set; }
        public Dictionary<string, string> Columns { get; set; } = new Dictionary<string, string>();  // field -> column letter
        public Dictionary<string, int> ColumnNumbers { get; set; } = new Dictionary<string, int>();  // field -> column number
        public Dictionary<string, string> HeaderText { get; set; } = new Dictionary<string, string>(); // field -> header label found
        public int TotalBlockLabelRow { get; set; }    // row holding "Hours by Fiscal Year" above the total column
        public int TotalLabelRow { get; set; }         // row holding "Total Engagement"
        public List<ResourceRow> Rows { get; set; } = new List<ResourceRow>();
        public List<PeriodColumn> Periods { get; set; } = new List<PeriodColumn>();
        public double TotalHours => Rows.Sum(r => r.Hours);
        public double? SheetOwnTotal { get; set; }
        public bool TotalsDontMatch => SheetOwnTotal.HasValue && Math.Abs(SheetOwnTotal.Value - TotalHours) > 0.5;
        public bool HasGeography => Columns.ContainsKey("geography");
        public List<string> Notes { get; set; } = new List<string>();        // affect every hour-based answer
        public List<string> PeriodNotes { get; set; } = new List<string>();  // affect only per-period checks (Q6)

        /// <summary>Self-checks, reported as notes: totals, period hours, Excel errors. Run again after rows are left out (what-if).</summary>
        public void SelfCheck()
        {
            Notes.Clear();
            PeriodNotes.Clear();
            if (TotalsDontMatch)
                Notes.Add($"Row hours add up to {Text.Hrs(TotalHours)}, but the sheet's own total says {Text.Hrs(SheetOwnTotal.Value)}.");
            if (Periods.Count > 0)
            {
                var off = Rows.Where(x => Math.Abs(x.PeriodHours.Sum() - x.Hours) > 0.5).ToList();
                if (off.Count > 0) PeriodNotes.Add($"{off.Count} row(s) where period hours don't add up to the row total (e.g. {off[0].Describe()}).");
            }
            else PeriodNotes.Add("Couldn't find the 4-week period columns, so per-period checks can't run.");
            foreach (var bad in Rows.Where(x => x.HoursError))
                Notes.Add($"Total hours unreadable (Excel error) in {bad.Describe()}; counted as 0.");
        }
    }

    public sealed class NextGenData
    {
        public string FileName { get; set; }
        public string SheetName { get; set; }
        public int HeaderRow { get; set; }
        public Dictionary<string, string> Columns { get; set; } = new Dictionary<string, string>();     // field -> column letter
        public Dictionary<string, string> HeaderText { get; set; } = new Dictionary<string, string>();  // field -> header label found
        public int Rows { get; set; }
        public Dictionary<string, double> DeloitteHoursByGroup { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> HoursBySourceType { get; set; } = new Dictionary<string, double>();
        public double DeloitteTotal => DeloitteHoursByGroup.Values.Sum();
        public List<string> Notes { get; set; } = new List<string>();
    }

    /// <summary>One piece of a Word or PowerPoint document: a heading, a paragraph, a table or a SmartArt graphic.</summary>
    public sealed class DocBlock
    {
        public string Kind { get; set; }        // heading | paragraph | table | smartart
        public string Location { get; set; }    // e.g. "Slide 70", "Table 4, section 'Our team'"
        public string Text { get; set; }        // paragraph/heading text, table rows joined with new lines, or SmartArt boxes joined with " | "
    }

    public sealed class ResponseEvidence
    {
        public string FileName { get; set; }
        public int TotalWords { get; set; }
        public int BlocksInDocument { get; set; }
        public int BlocksIncluded { get; set; }
        public List<DocBlock> Evidence { get; set; } = new List<DocBlock>();
        public LeftOutReport LeftOut { get; set; } = new LeftOutReport();
    }

    /// <summary>What the evidence doesn't contain, so the agent says "couldn't confirm" rather than "missing".</summary>
    public sealed class LeftOutReport
    {
        public string Note { get; set; } = "Everything below except pictures can still be read with searchResponse.";
        public int BlocksOverCap { get; set; }                                   // workstream blocks dropped at the character cap
        public List<string> BlocksOverCapLocations { get; set; } = new List<string>();
        public int LongParagraphsSkipped { get; set; }                           // paragraphs over the length limit that mention a workstream
        public List<string> LongParagraphLocations { get; set; } = new List<string>();
        public List<string> TablesCut { get; set; } = new List<string>();        // tables cut at the table length limit
        public List<string> Pictures { get; set; } = new List<string>();         // code can't read pictures, e.g. a team chart pasted as an image
    }

    /// <summary>Everything read from one deal folder. Shared by the review and every agent tool.</summary>
    public sealed class DealContext
    {
        public string Folder { get; set; }
        public DealSettings Settings { get; set; }
        public Classification Files { get; set; }
        public PricingModel Pm { get; set; }
        public NextGenData Ng { get; set; }
        public ClassifiedFile ResponseFile { get; set; }
        public List<DocBlock> ResponseBlocks { get; set; }         // the whole response document, for search
        public ResponseEvidence Response { get; set; }             // the workstream parts, for the review
        public List<string> PdfFiles { get; set; } = new List<string>();
        public List<int> RowsLeftOut { get; set; } = new List<int>();

        /// <summary>What-if only: drops pricing model rows and runs the totals checks again without them.</summary>
        public void LeaveOutRows(ICollection<int> rows)
        {
            if (Pm == null || rows == null || rows.Count == 0) return;
            RowsLeftOut = Pm.Rows.Where(r => rows.Contains(r.Row)).Select(r => r.Row).ToList();
            Pm.Rows.RemoveAll(r => rows.Contains(r.Row));
            Pm.SelfCheck();
        }
    }

    public sealed class DealResult
    {
        public bool Ok { get; set; } = true;
        public string Action { get; set; } = "review";
        public string SchemaVersion { get; set; } = "2.1";
        public string SnapshotId { get; set; }                      // set by RunAndSave; the agent passes it to the tool
        public string GeneratedAt { get; set; }
        public string Folder { get; set; }
        public bool FilesPresent { get; set; }
        public List<string> FileIssues { get; set; } = new List<string>();
        public List<ClassifiedFile> Files { get; set; } = new List<ClassifiedFile>();
        public string AnswerValues { get; set; } = "Yes | No | Needs review. A definite fail is No; a part that couldn't be checked makes it Needs review; a reading problem makes it Needs review even if it would fail.";
        public object PricingModel { get; set; }
        public object NextGen { get; set; }
        public List<QuestionResult> Questions { get; set; } = new List<QuestionResult>();
        public ResponseEvidence ResponseDocument { get; set; }
        public List<string> PdfFiles { get; set; }                  // PDFs code can't read; the agent reads them with Analyze Files
        public Dictionary<string, List<string>> SapWorkstreams { get; set; }
    }

    /// <summary>Everything run 1 read from the deal files, the settings it used and its answers, in one file.</summary>
    public sealed class DealSnapshot
    {
        public string SnapshotVersion { get; set; } = "1";
        public string SnapshotId { get; set; }
        public string CreatedAt { get; set; }
        public string Folder { get; set; }
        public DealContext Context { get; set; }
        public DealResult Review { get; set; }
    }
}
