// DealTools.cs: Run 2 entry points: what DealTool.xaml calls for the agent
// Used by: Run 2.
// Assign (VB):  out_Result = DealReview.DealTools.Recheck(in_Action, localSnapshotPath, in_Request)
// Actions: getReview (Run 1's review, so the agent needs only the snapshot ID), whatIf, searchResponse, checkWorkstreams, checkReading.

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
    /// Run 2 entry points, called from DealTool.xaml (the agent's tool).
    /// Recheck works only from the snapshot file run 1 saved; it never opens the deal files.
    /// getReview returns the review run 1 made (it's inside the snapshot), so the agent's only input is the snapshot ID.
    /// It never throws: every problem comes back as {"ok": false, "error": "..."} so the agent can read it and retry.
    /// </summary>
    public static class DealTools
    {
        public static readonly string[] Actions = { "getReview", "whatIf", "searchResponse", "checkWorkstreams", "checkReading" };

        /// <summary>
        /// action : getReview | whatIf | searchResponse | checkWorkstreams | checkReading (case doesn't matter)
        /// snapshotPath: the snapshot file, downloaded from the storage bucket
        /// request: JSON text with the details of the recheck ("{}" when there are none); shapes are in each tool's file
        /// </summary>
        public static string Recheck(string action, string snapshotPath, string request)
        {
            try { return Json.Write(Dispatch(action, new SnapshotLoader(snapshotPath), request)); }
            catch (Exception ex) { return ErrorJson(action, ex.Message); }
        }

        /// <summary>True if id looks like a snapshot ID (32 hex characters). Check it before downloading.</summary>
        public static bool IsSnapshotId(string id) => id != null && Regex.IsMatch(id.Trim(), "^[0-9a-fA-F]{32}$");

        /// <summary>The error JSON the agent understands, for problems in the workflow itself (e.g. the download failed).</summary>
        public static string ErrorJson(string action, string message) =>
            Json.Write(new ToolError() { Action = action ?? "", Error = message ?? "Unknown error." });

        private static object Dispatch(string action, SnapshotLoader source, string request)
        {
            string a = Text.Norm(action).Replace(" ", "");
            if (a.Length == 0) throw new ArgumentException($"No action given. Use one of: {string.Join(", ", Actions)}.");
            switch (a)
            {
                case "getreview": return source.FirstRun;   // run 1's review, as saved in the snapshot; the request is ignored
                case "whatif": return WhatIfTool.Run(source, Json.Read<WhatIfRequest>(request));
                case "searchresponse": return SearchResponseTool.Run(source, Json.Read<SearchRequest>(request));
                case "checkworkstreams": return CheckWorkstreamsTool.Run(source, Json.Read<WorkstreamRequest>(request));
                case "checkreading": return CheckReadingTool.Run(source, Json.Read<ReadingRequest>(request));
                default: throw new ArgumentException($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}.");
            }
        }

        /// <summary>The deal data from the snapshot; stops if run 1 found the files weren't right.</summary>
        public static DealContext LoadOrThrow(SnapshotLoader source, DealSettings s, ICollection<int> leaveOut = null)
        {
            var ctx = source.Load(s, leaveOut);
            if (!ctx.Files.FilesPresent)
                throw new InvalidDataException("The deal files weren't right in the first run, so nothing can be rechecked: " + string.Join(" ", ctx.Files.Issues));
            return ctx;
        }
    }

    public sealed class ToolError
    {
        public bool Ok { get; set; } = false;
        public string Action { get; set; }
        public string Error { get; set; }
        public string Hint { get; set; } = "Check the action name and the request format in the tool description, fix it and call again. If the error is about a file or the settings, report it and stop.";
    }
}
