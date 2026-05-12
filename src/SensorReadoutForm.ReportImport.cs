using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private const string ReportDataBeginMarker = "[SensorReadoutReportData]";
    private const string ReportDataEndMarker = "[/SensorReadoutReportData]";

    private sealed class ReportSnapshot
    {
        public string AppVersion = "";
        public string Title = "";
        public string MachineName = "";
        public string GeneratedLocal = "";
        public List<ReportSnapshotRow> Rows = new List<ReportSnapshotRow>();
    }

    private sealed class ReportSnapshotRow
    {
        public string Type = "";
        public string Hardware = "";
        public string Name = "";
        public string Identifier = "";
        public float? Value;
        public string DisplayValue = "";
        public string Source = "";
        public Dictionary<string, string> Details;
    }

    private void OpenReport()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = T("ui.Open Sensor Readout report", "Open Sensor Readout report");
            dialog.Filter = "Sensor Readout reports (*.html;*.htm;*.txt;*.zip)|*.html;*.htm;*.txt;*.zip|All files (*.*)|*.*";
            dialog.InitialDirectory = GetReportsFolderPath();
            dialog.CheckFileExists = true;
            dialog.Multiselect = false;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            LoadReportFile(dialog.FileName);
        }
    }

    private void LoadReportFile(string path)
    {
        try
        {
            var snapshot = ReadReportSnapshot(path);
            if (snapshot == null || snapshot.Rows == null || snapshot.Rows.Count == 0)
            {
                MessageBox.Show(this, T("message.Report did not contain readable Sensor Readout data.", "Report did not contain readable Sensor Readout data."), T("ui.Open report", "Open report"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            EnterReportView(snapshot, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("message.Could not open report:", "Could not open report:") + " " + ex.Message, T("ui.Open report", "Open report"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EnterReportView(ReportSnapshot snapshot, string path)
    {
        reportViewMode = true;
        loadedReportPath = path ?? "";
        loadedReportTitle = string.IsNullOrWhiteSpace(snapshot.Title) ? Path.GetFileName(path) : snapshot.Title;
        loadedReportMachineName = ReportMachineName(snapshot);
        loadedReportGeneratedLocal = snapshot.GeneratedLocal ?? "";
        if (timer != null)
        {
            timer.Stop();
        }
        if (visibleRefreshTimer != null)
        {
            visibleRefreshTimer.Stop();
        }

        latestRows.Clear();
        latestRows.AddRange(snapshot.Rows.Select(ToSensorRow).Where(r => r != null));
        readingTreeExpansionInitialized = false;
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        lastReadingTreeFilterKey = "";
        UpdateReportViewMenuState();
        UpdateWindowTitle();
        UpdateDeviceList();
        UpdateReadingList();
        UpdateTrayStatus();
        statusLabel.Text = T("status.viewingStaticReport", "Viewing static report:") + " " + loadedReportTitle + ". " + T("status.returnToLiveReadingsHint", "Use File, Return to live readings to resume live sensor updates.");
    }

    private void ReturnToLiveReadings()
    {
        if (!reportViewMode)
        {
            return;
        }

        reportViewMode = false;
        loadedReportPath = "";
        loadedReportTitle = "";
        loadedReportMachineName = "";
        loadedReportGeneratedLocal = "";
        latestRows.Clear();
        readingTreeExpansionInitialized = false;
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        lastReadingTreeFilterKey = "";
        UpdateReportViewMenuState();
        UpdateWindowTitle();
        UpdateDeviceList();
        UpdateReadingList();
        ApplyTimerSettings();
        RefreshSensors(true, false, "return-to-live");
    }

    private void UpdateReportViewMenuState()
    {
        if (returnToLiveReadingsMenuItem != null)
        {
            returnToLiveReadingsMenuItem.Visible = reportViewMode;
            returnToLiveReadingsMenuItem.Enabled = reportViewMode;
        }
    }

    private void UpdateWindowTitle()
    {
        var title = T("app.title", "Sensor Readout") + " " + AppVersion;
        if (reportViewMode)
        {
            var reportTitle = ReportWindowTitleText();
            if (!string.IsNullOrWhiteSpace(reportTitle))
            {
                title += " - " + T("ui.Report", "Report") + ": " + reportTitle;
            }
        }
        else
        {
            var machineName = Environment.MachineName ?? "";
            if (!string.IsNullOrWhiteSpace(machineName))
            {
                title += " - " + machineName;
            }
        }

        Text = title;
    }

    private string ReportWindowTitleText()
    {
        if (!string.IsNullOrWhiteSpace(loadedReportMachineName))
        {
            return loadedReportMachineName.Trim();
        }

        var title = loadedReportTitle ?? "";
        var prefix = "Sensor Readout report for ";
        if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return title.Substring(prefix.Length).Trim();
        }

        return title.Trim();
    }

    private ReportSnapshot BuildReportSnapshot()
    {
        return new ReportSnapshot
        {
            AppVersion = AppVersion,
            Title = BuildReportTitle(),
            MachineName = CurrentReportMachineName(),
            GeneratedLocal = CurrentReportGeneratedLocal(),
            Rows = ReportTypeGroups()
                .SelectMany(g => g)
                .Select(ToReportSnapshotRow)
                .ToList()
        };
    }

    private string CurrentReportMachineName()
    {
        if (reportViewMode && !string.IsNullOrWhiteSpace(loadedReportMachineName))
        {
            return loadedReportMachineName.Trim();
        }

        return Environment.MachineName ?? "";
    }

    private string CurrentReportGeneratedLocal()
    {
        if (reportViewMode && !string.IsNullOrWhiteSpace(loadedReportGeneratedLocal))
        {
            return loadedReportGeneratedLocal.Trim();
        }

        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string ReportMachineName(ReportSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.MachineName))
        {
            return snapshot.MachineName.Trim();
        }

        var title = snapshot.Title ?? "";
        var prefix = "Sensor Readout report for ";
        if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return title.Substring(prefix.Length).Trim();
        }

        return "";
    }

    private static ReportSnapshotRow ToReportSnapshotRow(SensorRow row)
    {
        return new ReportSnapshotRow
        {
            Type = row.Type ?? "",
            Hardware = row.Hardware ?? "",
            Name = row.Name ?? "",
            Identifier = row.Identifier ?? "",
            Value = row.Value,
            DisplayValue = row.DisplayValue ?? "",
            Source = row.Source ?? "",
            Details = row.Details == null ? null : new Dictionary<string, string>(row.Details, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static SensorRow ToSensorRow(ReportSnapshotRow row)
    {
        if (row == null)
        {
            return null;
        }

        return new SensorRow
        {
            Type = row.Type ?? "",
            Hardware = row.Hardware ?? "",
            Name = row.Name ?? "",
            Identifier = row.Identifier ?? "",
            Value = row.Value,
            DisplayValue = row.DisplayValue ?? "",
            Source = string.IsNullOrWhiteSpace(row.Source) ? "Imported report" : row.Source,
            Details = row.Details == null ? null : new Dictionary<string, string>(row.Details, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string EncodeReportSnapshot(ReportSnapshot snapshot)
    {
        var json = JsonConvert.SerializeObject(snapshot, Formatting.None);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private ReportSnapshot ReadReportSnapshot(string path)
    {
        if (IsZipReportPath(path))
        {
            return ReadReportSnapshotFromZip(path);
        }

        var text = File.ReadAllText(path);
        return ReadReportSnapshotText(text, path);
    }

    private ReportSnapshot ReadReportSnapshotText(string text, string path)
    {
        ReportSnapshot snapshot;
        if (TryReadEmbeddedReportSnapshot(text, out snapshot))
        {
            return snapshot;
        }

        return IsHtmlReportPath(path) ? ReadLegacyHtmlReport(text, path) : ReadLegacyTextReport(text, path);
    }

    private ReportSnapshot ReadReportSnapshotFromZip(string path)
    {
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var entry in archive.Entries
                .Where(IsReportArchiveEntry)
                .OrderBy(e => ReportArchiveEntryPriority(e.FullName))
                .ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Length > 50 * 1024 * 1024)
                {
                    continue;
                }

                try
                {
                    string text;
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        text = reader.ReadToEnd();
                    }

                    var snapshot = ReadReportSnapshotText(text, entry.FullName);
                    if (snapshot != null && snapshot.Rows != null && snapshot.Rows.Count > 0)
                    {
                        if (string.IsNullOrWhiteSpace(snapshot.Title))
                        {
                            snapshot.Title = Path.GetFileName(entry.FullName);
                        }

                        return snapshot;
                    }
                }
                catch
                {
                    // Keep scanning; a ZIP may contain unrelated text or HTML files.
                }
            }
        }

        return null;
    }

    private static bool TryReadEmbeddedReportSnapshot(string text, out ReportSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var payload = "";
        var textStart = text.IndexOf(ReportDataBeginMarker, StringComparison.Ordinal);
        if (textStart >= 0)
        {
            textStart += ReportDataBeginMarker.Length;
            var textEnd = text.IndexOf(ReportDataEndMarker, textStart, StringComparison.Ordinal);
            if (textEnd > textStart)
            {
                payload = text.Substring(textStart, textEnd - textStart).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            var match = Regex.Match(text, "<script[^>]+id=[\"']sensor-readout-report-data[\"'][^>]*>(?<payload>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
            {
                payload = match.Groups["payload"].Value.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            snapshot = JsonConvert.DeserializeObject<ReportSnapshot>(json);
            return snapshot != null && snapshot.Rows != null && snapshot.Rows.Count > 0;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool IsHtmlReportPath(string path)
    {
        var extension = Path.GetExtension(path) ?? "";
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZipReportPath(string path)
    {
        return string.Equals(Path.GetExtension(path) ?? "", ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReportArchiveEntry(ZipArchiveEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
        {
            return false;
        }

        var extension = Path.GetExtension(entry.Name) ?? "";
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReportArchiveEntryPriority(string path)
    {
        var extension = Path.GetExtension(path) ?? "";
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private ReportSnapshot ReadLegacyTextReport(string text, string path)
    {
        var snapshot = NewLegacySnapshot(text, path);
        var currentType = "";
        var parents = new Dictionary<int, string>();
        var rowIndex = 0;
        foreach (var rawLine in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var trimmed = rawLine.Trim();
            var type = ReportTypeFromHeading(trimmed);
            if (!string.IsNullOrWhiteSpace(type))
            {
                currentType = type;
                parents.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentType) || !rawLine.StartsWith(" ", StringComparison.Ordinal))
            {
                continue;
            }

            var level = Math.Max(1, rawLine.TakeWhile(c => c == ' ').Count() / 2);
            parents[level] = trimmed;
            foreach (var stale in parents.Keys.Where(k => k > level).ToList())
            {
                parents.Remove(stale);
            }

            var separator = trimmed.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = trimmed.Substring(0, separator).Trim();
            var value = trimmed.Substring(separator + 2).Trim();
            var hardware = parents.ContainsKey(level - 1) ? parents[level - 1] : DisplayTypeName(currentType);
            snapshot.Rows.Add(new ReportSnapshotRow
            {
                Type = currentType,
                Hardware = StripReportValue(hardware),
                Name = name,
                DisplayValue = value,
                Identifier = "imported/" + currentType.ToLowerInvariant() + "/" + rowIndex.ToString(CultureInfo.InvariantCulture),
                Source = "Imported legacy report"
            });
            rowIndex++;
        }

        return snapshot;
    }

    private ReportSnapshot ReadLegacyHtmlReport(string html, string path)
    {
        var snapshot = NewLegacySnapshot(html, path);
        var rowIndex = 0;
        var sectionMatches = Regex.Matches(html ?? "", "<h2[^>]*>(?<type>.*?)</h2>(?<body>.*?)(?=<h2[^>]*>|</body>|</html>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match section in sectionMatches)
        {
            var currentType = ReportTypeFromHeading(CleanHtmlText(section.Groups["type"].Value));
            if (string.IsNullOrWhiteSpace(currentType))
            {
                continue;
            }

            var body = section.Groups["body"].Value;
            foreach (Match item in Regex.Matches(body, "<li>(?<text>.*?)(?=<ul>|<table>|</li>)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var text = CleanHtmlText(item.Groups["text"].Value);
                var separator = text.IndexOf(": ", StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                snapshot.Rows.Add(new ReportSnapshotRow
                {
                    Type = currentType,
                    Hardware = DisplayTypeName(currentType),
                    Name = text.Substring(0, separator).Trim(),
                    DisplayValue = text.Substring(separator + 2).Trim(),
                    Identifier = "imported/" + currentType.ToLowerInvariant() + "/" + rowIndex.ToString(CultureInfo.InvariantCulture),
                    Source = "Imported legacy report"
                });
                rowIndex++;
            }
        }

        return snapshot;
    }

    private static ReportSnapshot NewLegacySnapshot(string text, string path)
    {
        var firstLine = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(CleanHtmlText(l)));
        var title = CleanHtmlText(firstLine);
        return new ReportSnapshot
        {
            AppVersion = "",
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
            MachineName = "",
            GeneratedLocal = "",
            Rows = new List<ReportSnapshotRow>()
        };
    }

    private static string ReportTypeFromHeading(string heading)
    {
        heading = (heading ?? "").Trim();
        foreach (var type in new[] { "Performance", "Temperature", "Fan", "SMART", "Battery", "Network", "USB", "Audio", "Display", "Devices" })
        {
            if (heading.Equals(type, StringComparison.OrdinalIgnoreCase) || heading.Equals(DisplayTypeName(type), StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        if (heading.Equals("Temperatures", StringComparison.OrdinalIgnoreCase))
        {
            return "Temperature";
        }
        if (heading.Equals("Fans", StringComparison.OrdinalIgnoreCase))
        {
            return "Fan";
        }
        if (heading.Equals("Performance/Overview", StringComparison.OrdinalIgnoreCase))
        {
            return "Performance";
        }

        return "";
    }

    private static string StripReportValue(string text)
    {
        var value = text ?? "";
        var separator = value.IndexOf(": ", StringComparison.Ordinal);
        return separator > 0 ? value.Substring(0, separator).Trim() : value.Trim();
    }

    private static string CleanHtmlText(string html)
    {
        var withoutTags = Regex.Replace(html ?? "", "<.*?>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(withoutTags, "\\s+", " ").Trim());
    }
}
