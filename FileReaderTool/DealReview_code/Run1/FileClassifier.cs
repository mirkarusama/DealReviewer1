// FileClassifier.cs: works out which file is which (pricing model, NextGen, response, RFP, PDF) by what is inside, never by name
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
    /// Labels each file by its content, not its name:
    /// PricingModel, NextGen, Response, RFP, Other, or Unsure.
    /// Required: exactly one PricingModel and one NextGen. Response is optional (Q3 step 7 is skipped without it).
    /// Anything unsure or duplicated is reported as an issue, so the process can stop before the agent runs.
    /// </summary>
    public static class FileClassifier
    {
        public static Classification Classify(string folder, DealSettings settings)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Deal folder not found: '{folder}'.");

            var result = new Classification();
            foreach (var path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("~$")) continue;   // Office lock files
                result.Files.Add(ClassifyOne(path, settings));
            }

            Require(result, FileType.PricingModel, "pricing model (.xlsm with 'Engagement Metrics' and 'Resourcing' sheets)");
            Require(result, FileType.NextGen, "NextGen effort file (.xlsm with a 'Raw Data' sheet)");
            NoDuplicates(result, FileType.Response, "response documents");
            foreach (var f in result.Files.Where(f => f.Type == FileType.Unsure))
                result.Issues.Add($"Can't tell whether '{f.FileName}' is the RFP or the response document ({f.Reason}).");
            return result;
        }

        private static void Require(Classification c, string type, string description)
        {
            int n = c.Files.Count(f => f.Type == type);
            if (n == 0) { c.Missing.Add(description); c.Issues.Add($"Missing: {description}."); }
            if (n > 1) c.Issues.Add($"Found {n} files that look like the {description}: {string.Join(", ", c.Files.Where(f => f.Type == type).Select(f => f.FileName))}. Keep only one.");
        }

        private static void NoDuplicates(Classification c, string type, string description)
        {
            var list = c.Files.Where(f => f.Type == type).ToList();
            if (list.Count > 1) c.Issues.Add($"Found {list.Count} {description}: {string.Join(", ", list.Select(f => f.FileName))}. Keep only one.");
        }

        private static ClassifiedFile ClassifyOne(string path, DealSettings settings)
        {
            var info = new FileInfo(path);
            var file = new ClassifiedFile() { FileName = Path.GetFileName(path), FullPath = path,
                                            LastSaved = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), SizeBytes = info.Length };
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".xlsx" || ext == ".xlsm") ClassifyWorkbook(path, file, settings.Classifier);
                else if (ext == ".docx" || ext == ".pptx") ClassifyDocument(path, file, settings.Classifier);
                else if (ext == ".pdf") { file.Type = FileType.Pdf; file.Reason = "PDF: code can't read it, so it could be the RFP or the response. Without a Word or PowerPoint response, Q3's workstream check can't be done."; }
                else { file.Type = FileType.Other; file.Reason = $"'{ext}' files are not used"; }
            }
            catch (Exception ex)
            {
                file.Type = FileType.Other;
                file.Reason = "could not be opened: " + ex.Message;
            }
            return file;
        }

        private static void ClassifyWorkbook(string path, ClassifiedFile file, ClassifierRules rules)
        {
            using var book = new XlsxBook(path);
            bool Has(string sheet) => book.FindSheet(sheet) != null;

            if (rules.PricingModelSheets.All(Has))
            {
                file.Type = FileType.PricingModel;
                file.Reason = "has sheets " + string.Join(" + ", rules.PricingModelSheets.Select(s => $"'{s}'"));
                return;
            }
            var raw = rules.NextGenSheets.Select(book.FindSheet).FirstOrDefault(s => s != null);
            if (raw != null)
            {
                var header = book.ReadRows(raw).FirstOrDefault(r => r.Cells.Count > 0);
                var names = header?.Cells.Values.Select(v => Text.Norm(v).Replace(" ", "")).ToHashSet() ?? new HashSet<string>();
                var missing = rules.NextGenColumns.Where(c => !names.Contains(Text.Norm(c).Replace(" ", ""))).ToList();
                if (missing.Count == 0)
                {
                    file.Type = FileType.NextGen;
                    file.Reason = $"'{raw.Name}' sheet has columns " + string.Join(", ", rules.NextGenColumns);
                    return;
                }
                file.Type = FileType.Other;
                file.Reason = $"has a '{raw.Name}' sheet but no column(s) {string.Join(", ", missing)}";
                return;
            }
            file.Type = FileType.Other;
            file.Reason = "spreadsheet without pricing model or NextGen sheets (" + string.Join(", ", book.Sheets.Where(s => s.Visible).Take(6).Select(s => s.Name)) + ")";
        }

        private static void ClassifyDocument(string path, ClassifiedFile file, ClassifierRules rules)
        {
            var blocks = OfficeText.Read(path);
            string text = OfficeText.PlainText(blocks);
            string opening = text.Length > 400 ? text.Substring(0, 400) : text;

            int firm = Text.CountWord(text, rules.OwnFirmName);
            int rfpPhrase = rules.RfpPhrases.Sum(p => Text.CountWord(text, p));
            int rfpAcronym = Text.CountWord(text, "RFP");
            bool rfpSignal = rfpPhrase > 0 || rfpAcronym >= rules.RfpMinAcronymCount;
            string signals = $"'{rules.OwnFirmName}' {firm}x, 'Request for Proposal' {rfpPhrase}x, 'RFP' {rfpAcronym}x";

            if (rules.ReadoutMarkers.Any(m => Text.HasWord(opening, m)))
            {
                file.Type = FileType.Other;
                file.Reason = "SA readout report (reviewer output, never sent to the agent)";
            }
            else if (firm >= rules.ResponseMinFirmMentions)
            {
                file.Type = FileType.Response;
                file.Reason = $"written by {rules.OwnFirmName}: {signals}";
            }
            else if (rfpSignal && firm <= rules.RfpMaxFirmMentions)
            {
                file.Type = FileType.Rfp;
                file.Reason = $"client request: {signals}";
            }
            else if (rfpSignal)
            {
                file.Type = FileType.Unsure;
                file.Reason = signals;
            }
            else
            {
                file.Type = FileType.Other;
                file.Reason = $"neither RFP nor response: {signals}";
            }
        }
    }
}
