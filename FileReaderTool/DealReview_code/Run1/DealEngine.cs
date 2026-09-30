// DealEngine.cs: runs one deal end to end: labels and reads the files, runs the questions, builds the review JSON and the snapshot
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
    public static class DealEngine
    {
        /// <summary>
        /// One deal folder in, one result out. If a required file is missing or unclear,
        /// FilesPresent is false, FileIssues says why, and no checks are run.
        /// </summary>
        public static DealResult Run(string folderPath, string settingsPath)
        {
            var settings = DealSettings.Load(settingsPath);
            return BuildResult(Load(folderPath, settings));
        }

        /// <summary>Run 1 with a snapshot: the review plus everything it read, the settings it used and the answers.</summary>
        public static DealSnapshot RunAndSnapshot(string folderPath, string settingsPath)
        {
            var settings = DealSettings.Load(settingsPath);
            var ctx = Load(folderPath, settings);
            var review = BuildResult(ctx);
            string id = Guid.NewGuid().ToString("N");
            review.SnapshotId = id;
            return new DealSnapshot()
            {
                SnapshotId = id,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Folder = folderPath,
                Context = ctx,
                Review = review
            };
        }

        /// <summary>Labels the files and reads them. leaveOutRows (pricing model row numbers) is only used by what-if.</summary>
        public static DealContext Load(string folderPath, DealSettings settings, ICollection<int> leaveOutRows = null)
        {
            var cls = FileClassifier.Classify(folderPath, settings);
            var ctx = new DealContext() { Folder = folderPath, Settings = settings, Files = cls };
            ctx.PdfFiles = cls.Files.Where(f => f.Type == FileType.Pdf).Select(f => f.FileName).ToList();
            if (!cls.FilesPresent) return ctx;

            ctx.Pm = PricingModelReader.Read(cls.Single(FileType.PricingModel).FullPath);
            ctx.LeaveOutRows(leaveOutRows);
            ctx.Ng = NextGenReader.Read(cls.Single(FileType.NextGen).FullPath, settings);
            ctx.ResponseFile = cls.Single(FileType.Response);
            if (ctx.ResponseFile != null)
            {
                ctx.ResponseBlocks = OfficeText.Read(ctx.ResponseFile.FullPath);
                ctx.Response = ResponseExtractor.Extract(ctx.ResponseFile.FileName, ctx.ResponseBlocks, ctx.ResponseFile.FullPath, settings);
            }
            return ctx;
        }


        public static DealResult BuildResult(DealContext ctx)
        {
            var cls = ctx.Files;
            var result = new DealResult()
            {
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Folder = ctx.Folder,
                Files = cls.Files,
                FilesPresent = cls.FilesPresent,
                FileIssues = cls.Issues.ToList()
            };
            if (!cls.FilesPresent) return result;

            var pm = ctx.Pm; var ng = ctx.Ng;
            result.PricingModel = new
            {
                file = pm.FileName,
                sheet = pm.SheetName,
                layout = pm.Layout,
                headerRow = pm.HeaderRow,
                columns = pm.Columns,
                rowsRead = pm.Rows.Count,
                rowsLeftOut = ctx.RowsLeftOut.Count > 0 ? ctx.RowsLeftOut : null,
                totalHours = Text.R2(pm.TotalHours),
                sheetOwnTotal = pm.SheetOwnTotal.HasValue ? Text.R2(pm.SheetOwnTotal.Value) : (double?)null,
                periodColumns = pm.Periods.Count,
                notes = pm.Notes.Concat(pm.PeriodNotes).ToList()
            };
            result.NextGen = new
            {
                file = ng.FileName,
                rowsRead = ng.Rows,
                hoursBySourceGroupType = ng.HoursBySourceType.ToDictionary(k => k.Key, k => Text.R2(k.Value)),
                deloitteHoursByGroup = ng.DeloitteHoursByGroup.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => Text.R2(k.Value)),
                notes = ng.Notes
            };
            result.Questions = Checks.RunAll(ctx);
            result.ResponseDocument = ctx.Response;
            result.PdfFiles = ctx.PdfFiles.Count > 0 ? ctx.PdfFiles : null;
            if (ctx.Response != null || ctx.PdfFiles.Count > 0) result.SapWorkstreams = ctx.Settings.SapWorkstreams;
            return result;
        }
    }
}
