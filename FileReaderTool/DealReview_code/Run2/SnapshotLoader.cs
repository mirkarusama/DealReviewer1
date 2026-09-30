// SnapshotLoader.cs: reads the snapshot file run 1 saved; the tools work only from this
// Used by: Run 2.

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
    /// Reads the snapshot file made by run 1. Every Load gives a fresh copy, so a what-if never changes the original,
    /// and the settings are the ones run 1 used, so later edits to DealSettings.json can't change the recheck.
    /// </summary>
    public sealed class SnapshotLoader
    {
        private readonly string _text;
        private readonly DealSnapshot _first;

        public SnapshotLoader(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"Snapshot file not found: '{path}'. Check the snapshot ID and that the download worked.");
            _text = File.ReadAllText(path);
            _first = Parse();
            if (_first.Context?.Settings == null || _first.Context.Files == null)
                throw new InvalidDataException("This file isn't a DealReader snapshot (no deal data or settings). It must come from DealReader.RunAndSave.");
        }

        private DealSnapshot Parse()
        {
            try { return JsonSerializer.Deserialize<DealSnapshot>(_text, Json.ReadOptions) ?? throw new InvalidDataException("The snapshot file is empty."); }
            catch (JsonException ex) { throw new InvalidDataException("The snapshot file can't be read: " + ex.Message); }
        }

        /// <summary>A fresh copy of the settings run 1 used.</summary>
        public DealSettings Settings() => Parse().Context.Settings;

        /// <summary>A fresh copy of the deal data, with these settings and without these pricing model rows.</summary>
        public DealContext Load(DealSettings s, ICollection<int> leaveOutRows = null)
        {
            var ctx = Parse().Context;
            ctx.Settings = s;
            ctx.LeaveOutRows(leaveOutRows);
            return ctx;
        }

        /// <summary>Run 1's answers, used to check the recheck matches them.</summary>
        public DealResult FirstRun => _first.Review;
        public string SnapshotId => _first.SnapshotId;
    }
}
