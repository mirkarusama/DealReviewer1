// DealReader.cs: Run 1 entry points: what Main.xaml calls
// Used by: Run 1.
// Assigns (VB):  reviewJson = DealReview.DealReader.RunAndSave(dealFolder, "Data\DealSettings.json", snapshotFolder)
//               then read DealReader.FilesPresent, FileIssues, SnapshotId and SnapshotPath.

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
    /// Run 1 entry points, called from Assign activities in Main.xaml.
    /// RunAndSave reads the deal folder once, returns the review JSON and saves the snapshot for the agent's tool.
    /// Read FilesPresent, FileIssues, SnapshotId and SnapshotPath right after it: they hold that same run's results.
    /// </summary>
    public static class DealReader
    {
        /// <summary>True only if exactly one pricing model and one NextGen file were found, and nothing was unclear.</summary>
        public static bool FilesPresent { get; private set; }
        /// <summary>What's missing or unclear (empty when FilesPresent is true).</summary>
        public static string FileIssues { get; private set; } = "";
        /// <summary>The snapshot's ID (32 letters and digits): give it to the agent.</summary>
        public static string SnapshotId { get; private set; } = "";
        /// <summary>Where the snapshot file was saved: upload it to the storage bucket as SnapshotId + ".json".</summary>
        public static string SnapshotPath { get; private set; } = "";

        /// <summary>
        /// The full review JSON, plus a snapshot file of everything read from the deal files,
        /// the settings used and the answers, saved in snapshotFolder as SnapshotId + ".json".
        /// Throws with a plain message if it can't trust its reading (wrap it in a Try Catch).
        /// </summary>
        public static string RunAndSave(string folderPath, string settingsPath, string snapshotFolder)
        {
            FilesPresent = false; FileIssues = ""; SnapshotId = ""; SnapshotPath = "";
            var snap = DealEngine.RunAndSnapshot(folderPath, settingsPath);
            Directory.CreateDirectory(snapshotFolder);
            string path = Path.Combine(snapshotFolder, snap.SnapshotId + ".json");
            File.WriteAllText(path, Json.WriteCompact(snap), new UTF8Encoding(false));
            FilesPresent = snap.Review.FilesPresent;
            FileIssues = string.Join(Environment.NewLine, snap.Review.FileIssues);
            SnapshotId = snap.SnapshotId;
            SnapshotPath = path;
            return Json.Write(snap.Review);
        }

        /// <summary>The review JSON only, without a snapshot (the older way of calling it).</summary>
        public static string Run(string folderPath, string settingsPath)
        {
            FilesPresent = false; FileIssues = ""; SnapshotId = ""; SnapshotPath = "";
            DealResult result = DealEngine.Run(folderPath, settingsPath);
            FilesPresent = result.FilesPresent;
            FileIssues = string.Join(Environment.NewLine, result.FileIssues);
            return Json.Write(result);
        }
    }
}
