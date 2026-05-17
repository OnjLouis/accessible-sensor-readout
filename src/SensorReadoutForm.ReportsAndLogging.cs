using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private bool forceDebugLogging;

    private void SaveReport()
    {
        using (var dialog = new SaveFileDialog())
        {
            var reportsFolder = GetReportsFolderPath();
            System.IO.Directory.CreateDirectory(reportsFolder);
            dialog.Title = "Save Sensor Report";
            dialog.Filter = "Formatted HTML report (*.html)|*.html|Text report (*.txt)|*.txt";
            dialog.FilterIndex = 1;
            dialog.FileName = DefaultReportFileName(true, CurrentReportMachineName());
            dialog.InitialDirectory = reportsFolder;
            dialog.DefaultExt = "html";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var html = dialog.FilterIndex == 1 || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            if (html && !dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                dialog.FileName = System.IO.Path.ChangeExtension(dialog.FileName, ".html");
            }
            else if (!html && !dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                dialog.FileName = System.IO.Path.ChangeExtension(dialog.FileName, ".txt");
            }

            var stopwatch = Stopwatch.StartNew();
            SaveReportToFile(dialog.FileName, html, false);
            stopwatch.Stop();
            statusLabel.Text = "Saved report to " + dialog.FileName + " in " + FormatElapsed(stopwatch.Elapsed) + ".";
        }
    }

    private void OpenReportsFolder()
    {
        OpenFolder(GetReportsFolderPath(), T("ui.Open Reports folder", "Open Reports folder"));
    }

    private void OpenLogsFolder()
    {
        OpenFolder(GetLogsFolderPath(), T("ui.Open Logs folder", "Open Logs folder"));
    }

    private void OpenFolder(string folder, string title)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("message.Could not open folder:", "Could not open folder:") + " " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void SaveReportToFile(string path, bool html, bool refreshFirst)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Report path is required.", "path");
        }

        var totalStopwatch = Stopwatch.StartNew();
        var refreshStopwatch = Stopwatch.StartNew();
        if (refreshFirst)
        {
            SetLatestRows(CollectSensorRows(true));
        }
        refreshStopwatch.Stop();

        EnsureDirectoryForFile(path);
        var renderStopwatch = Stopwatch.StartNew();
        var timingLine = BuildReportTimingLine(refreshFirst, refreshStopwatch.Elapsed, renderStopwatch.Elapsed, totalStopwatch.Elapsed, false);
        var report = html ? BuildHtmlReport(timingLine) : BuildTextReport(timingLine);
        renderStopwatch.Stop();

        timingLine = BuildReportTimingLine(refreshFirst, refreshStopwatch.Elapsed, renderStopwatch.Elapsed, totalStopwatch.Elapsed, false);
        report = html ? BuildHtmlReport(timingLine) : BuildTextReport(timingLine);

        var writeStopwatch = Stopwatch.StartNew();
        System.IO.File.WriteAllText(path, report);
        writeStopwatch.Stop();
        totalStopwatch.Stop();

        LogMessage(
            "Debug",
            "Saved " + (html ? "HTML" : "text") + " report to " + path +
            " with " + latestRows.Count + " rows in " + FormatElapsed(totalStopwatch.Elapsed) +
            ". Refresh=" + (refreshFirst ? FormatElapsed(refreshStopwatch.Elapsed) : "not requested") +
            "; render=" + FormatElapsed(renderStopwatch.Elapsed) +
            "; write=" + FormatElapsed(writeStopwatch.Elapsed) + ".");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds >= 1)
        {
            return FormatNumber(elapsed.TotalSeconds, "0.00") + " seconds";
        }

        return FormatNumber(elapsed.TotalMilliseconds, "0") + " ms";
    }

    public static string DefaultReportFileName(bool html)
    {
        return DefaultReportFileName(html, Environment.MachineName);
    }

    private static string DefaultReportFileName(bool html, string machineName)
    {
        return "SensorReadout-" + SafeReportFileName(machineName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (html ? ".html" : ".txt");
    }

    private static string SafeReportFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Computer";
        }

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "Computer" : value.Trim();
    }

    private static string BuildReportTimingLine(bool refreshed, TimeSpan refreshElapsed, TimeSpan renderElapsed, TimeSpan totalElapsed, bool includeWrite)
    {
        var parts = new List<string>();
        if (refreshed)
        {
            parts.Add("refresh " + FormatElapsed(refreshElapsed));
        }

        parts.Add("render " + FormatElapsed(renderElapsed));
        parts.Add((includeWrite ? "total" : "prepared") + " " + FormatElapsed(totalElapsed));
        return "Report timing: " + string.Join(", ", parts.ToArray()) + ".";
    }

    private string BuildTextReport(string timingLine)
    {
        var lines = new List<string>();
        lines.Add(BuildReportTitle());
        lines.Add("Generated " + CurrentReportGeneratedLocal());
        if (!string.IsNullOrWhiteSpace(timingLine))
        {
            lines.Add(timingLine);
        }
        lines.Add("");

        foreach (var typeGroup in ReportTypeGroups())
        {
            lines.Add(DisplayTypeName(typeGroup.Key));
            var items = BuildReadingTree(typeGroup.ToList(), new DeviceFilter { Type = typeGroup.Key });
            AddTextReportTreeLines(lines, items, 1);
            lines.Add("");
        }

        lines.Add("Internal Sensor Readout data below this line. This helps Sensor Readout reopen the report exactly.");
        lines.Add(ReportDataBeginMarker);
        lines.AddRange(WrapReportPayload(EncodeReportSnapshot(BuildReportSnapshot()), 76));
        lines.Add(ReportDataEndMarker);

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private static IEnumerable<string> WrapReportPayload(string payload, int width)
    {
        if (string.IsNullOrEmpty(payload))
        {
            yield break;
        }

        width = Math.Max(16, width);
        for (var index = 0; index < payload.Length; index += width)
        {
            yield return payload.Substring(index, Math.Min(width, payload.Length - index));
        }
    }

    private string BuildHtmlReport(string timingLine)
    {
        var title = BuildReportTitle();
        var snapshot = BuildReportSnapshot();
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\"><title>" + HtmlEncode(title) + "</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.35} table{border-collapse:collapse;margin:0 0 1.2em 0} th,td{border:1px solid #888;padding:4px 8px;text-align:left} h2{margin-top:1.4em} h3{margin-bottom:.4em} h4{margin:.8em 0 .3em 0}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>" + HtmlEncode(title) + "</h1>");
        html.AppendLine("<p>Generated " + HtmlEncode(CurrentReportGeneratedLocal()) + "</p>");
        if (!string.IsNullOrWhiteSpace(timingLine))
        {
            html.AppendLine("<p>" + HtmlEncode(timingLine) + "</p>");
        }
        foreach (var typeGroup in ReportTypeGroups())
        {
            html.AppendLine("<h2>" + HtmlEncode(DisplayTypeName(typeGroup.Key)) + "</h2>");
            var items = BuildReadingTree(typeGroup.ToList(), new DeviceFilter { Type = typeGroup.Key });
            AddHtmlReportTree(html, items);
        }

        html.AppendLine("<script type=\"application/sensor-readout-report\" id=\"sensor-readout-report-data\">" + EncodeReportSnapshot(snapshot) + "</script>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private IEnumerable<IGrouping<string, SensorRow>> ReportTypeGroups()
    {
        return latestRows
            .Where(r => r.Type != "Fan Control")
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => r.Type)
            .GroupBy(r => r.Type)
            .OrderBy(g => TypeSortIndex(g.Key))
            .ThenBy(g => g.Key);
    }

    private static void AddTextReportTreeLines(List<string> lines, IEnumerable<ReadingTreeItem> items, int level)
    {
        foreach (var item in items)
        {
            var indent = new string(' ', level * 2);
            AddWrappedTextReportLine(lines, indent, item.Text);
            if (item.Row != null && item.Row.Details != null && item.Row.Details.Count > 0)
            {
                foreach (var detail in OrderedReportDetails(item.Row))
                {
                    AddWrappedTextReportLine(lines, indent + "  ", detail.Key + ": " + detail.Value);
                }
            }

            AddTextReportTreeLines(lines, item.Children, level + 1);
        }
    }

    private static void AddWrappedTextReportLine(List<string> lines, string indent, string text)
    {
        if (lines == null)
        {
            return;
        }

        indent = indent ?? "";
        text = text ?? "";
        const int width = 160;
        var firstPrefix = indent;
        var continuationPrefix = indent + "  ";
        var remaining = text.TrimEnd();
        var prefix = firstPrefix;
        if (remaining.Length == 0)
        {
            lines.Add(prefix);
            return;
        }

        while (prefix.Length + remaining.Length > width)
        {
            var available = Math.Max(40, width - prefix.Length);
            var split = FindReportWrapPoint(remaining, available);
            lines.Add(prefix + remaining.Substring(0, split).TrimEnd());
            remaining = remaining.Substring(split).TrimStart();
            prefix = continuationPrefix;
        }

        lines.Add(prefix + remaining);
    }

    private static int FindReportWrapPoint(string text, int available)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= available)
        {
            return string.IsNullOrEmpty(text) ? 0 : text.Length;
        }

        for (var i = Math.Min(available, text.Length - 1); i >= Math.Max(1, available / 2); i--)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ';' || text[i] == ',' || text[i] == '|')
            {
                return i + 1;
            }
        }

        return available;
    }

    private static void AddHtmlReportTree(System.Text.StringBuilder html, IEnumerable<ReadingTreeItem> items)
    {
        html.AppendLine("<ul>");
        foreach (var item in items)
        {
            html.AppendLine("<li>" + HtmlEncode(item.Text));
            if (item.Row != null && item.Row.Details != null && item.Row.Details.Count > 0)
            {
                html.AppendLine("<table><thead><tr><th>Field</th><th>Value</th></tr></thead><tbody>");
                foreach (var detail in OrderedReportDetails(item.Row))
                {
                    html.AppendLine("<tr><td>" + HtmlEncode(detail.Key) + "</td><td>" + HtmlEncode(detail.Value) + "</td></tr>");
                }
                html.AppendLine("</tbody></table>");
            }

            if (item.Children.Count > 0)
            {
                AddHtmlReportTree(html, item.Children);
            }

            html.AppendLine("</li>");
        }
        html.AppendLine("</ul>");
    }

    private string BuildReportTitle()
    {
        if (reportViewMode && !string.IsNullOrWhiteSpace(loadedReportTitle))
        {
            return loadedReportTitle.Trim();
        }

        var machineName = CurrentReportMachineName();
        return string.IsNullOrWhiteSpace(machineName)
            ? "Sensor Readout report"
            : "Sensor Readout report for " + machineName;
    }

    private static IEnumerable<KeyValuePair<string, string>> OrderedReportDetails(SensorRow row)
    {
        if (row == null || row.Details == null)
        {
            return Enumerable.Empty<KeyValuePair<string, string>>();
        }

        return row.Details
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => UsbDetailSortIndex(p.Key))
            .ThenBy(p => p.Key);
    }

    private SensorRow GetSelectedReadingRow()
    {
        return readingTree.SelectedNode == null ? null : readingTree.SelectedNode.Tag as SensorRow;
    }

    private void SetLibreHardwareMonitorControl(string controlIdentifier, int percent, bool manual)
    {
        controlIdentifier = IdentifierFromSettingsKey(controlIdentifier);
        if (TryPlugInFanControl(controlIdentifier, percent, manual))
        {
            return;
        }
        if (!controlIdentifier.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Plug-in fan control did not accept the requested change: " + controlIdentifier);
        }

        lock (lhmLock)
        {
            EnsureLibreHardwareMonitorComputerOpen();
            var sensor = FindControlSensor(lhmComputer, controlIdentifier);
            if (sensor == null || sensor.Control == null)
            {
                throw new InvalidOperationException("Could not find direct LibreHardwareMonitor control: " + controlIdentifier);
            }

            if (manual)
            {
                sensor.Control.SetSoftware(Math.Max(0, Math.Min(100, percent)));
            }
            else
            {
                sensor.Control.SetDefault();
            }
        }
    }

    private int SetAllLibreHardwareMonitorControlsDefault()
    {
        lock (lhmLock)
        {
            EnsureLibreHardwareMonitorComputerOpen();
            var sensors = GetAllSensors(lhmComputer.Hardware)
                .Where(s => s.SensorType.ToString() == "Control" && s.Control != null)
                .ToList();
            foreach (var sensor in sensors)
            {
                sensor.Control.SetDefault();
            }

            foreach (var hardware in lhmComputer.Hardware)
            {
                UpdateHardware(hardware);
            }

            return sensors.Count;
        }
    }

    private static Computer CreateLibreHardwareMonitorComputer()
    {
        return new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true
        };
    }

    private static ISensor FindControlSensor(Computer computer, string identifier)
    {
        foreach (var hardware in computer.Hardware)
        {
            UpdateHardware(hardware, false);
        }

        return GetAllSensorsWithoutUpdating(computer.Hardware)
            .FirstOrDefault(s => s.Control != null && string.Equals(s.Identifier == null ? "" : s.Identifier.ToString(), identifier, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ISensor> GetAllSensorsWithoutUpdating(IEnumerable<IHardware> hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            foreach (var sensor in hardware.Sensors)
            {
                yield return sensor;
            }

            foreach (var sensor in GetAllSensorsWithoutUpdating(hardware.SubHardware))
            {
                yield return sensor;
            }
        }
    }

    private static IEnumerable<ISensor> GetAllSensors(IEnumerable<IHardware> hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            UpdateHardware(hardware);
            foreach (var sensor in hardware.Sensors)
            {
                yield return sensor;
            }

            foreach (var sensor in GetAllSensors(hardware.SubHardware))
            {
                yield return sensor;
            }
        }
    }

    private static string CleanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name
            .Replace("Control 1 - NVIDIA GeForce RTX 4060", "Fan 1 - NVIDIA GeForce RTX 4060")
            .Trim();
    }

    internal static string BaseFanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var comma = name.IndexOf(',');
        if (comma > -1)
        {
            name = name.Substring(0, comma);
        }

        return CleanControlName(name);
    }

    internal static string BaseFanReadingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var comma = name.IndexOf(',');
        if (comma > -1 && comma + 1 < name.Length)
        {
            var suffix = name.Substring(comma + 1).Trim();
            if (suffix.StartsWith("Fan", StringComparison.OrdinalIgnoreCase))
            {
                return CleanControlName(suffix);
            }
        }

        return BaseFanControlName(name);
    }

    internal static bool IsGpuControl(string identifier)
    {
        return !string.IsNullOrWhiteSpace(identifier) &&
            (identifier.IndexOf("NVApi", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("/gpu-", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("gpu-nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("gpu-amd", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    internal static int ControlSortKey(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return 100000;
        }

        var marker = "/control/";
        var index = identifier.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            int value;
            if (int.TryParse(identifier.Substring(index + marker.Length), out value))
            {
                return value;
            }
        }

        return IsGpuControl(identifier) ? 90000 : 80000;
    }

    internal static string GuessControlIdentifier(string fanIdentifier)
    {
        if (string.IsNullOrWhiteSpace(fanIdentifier))
        {
            return "";
        }

        return fanIdentifier
            .Replace("/fan/", "/control/")
            .Replace("/fan/", "/control/")
            .Replace("-A/fan/", "-A/control/");
    }

    internal static string GuessFanIdentifier(string controlIdentifier)
    {
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            return "";
        }

        return controlIdentifier
            .Replace("/control/", "/fan/")
            .Replace("-A/control/", "-A/fan/");
    }

    private string GetFanLabel(string identifier, string fallback)
    {
        var labels = LoadFanLabels();
        return labels.ContainsKey(identifier) ? labels[identifier] : fallback;
    }

    private Dictionary<string, string> LoadFanLabels()
    {
        return new Dictionary<string, string>(settings.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    private void SaveFanLabels(Dictionary<string, string> labels)
    {
        settings.FanLabels = new Dictionary<string, string>(labels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        SaveSettings(settings);
    }

    private Dictionary<string, FanControlSetting> LoadFanControlSettings()
    {
        return new Dictionary<string, FanControlSetting>(settings.FanControlSettings ?? new Dictionary<string, FanControlSetting>(), StringComparer.OrdinalIgnoreCase);
    }

    private void SaveFanControlSetting(string identifier, bool manual, int percent)
    {
        SaveFanControlSettings(new[] { identifier }, manual, percent);
    }

    private void SaveFanControlSettingsForAllKnownControls(bool manual, int percent)
    {
        SaveFanControlSettings(latestRows.Where(r => r.Type == "Fan Control").Select(r => r.Identifier), manual, percent);
    }

    private void SaveFanControlSettings(IEnumerable<string> identifiers, bool manual, int percent)
    {
        var saved = LoadFanControlSettings();
        foreach (var identifier in identifiers ?? new string[0])
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            saved[identifier] = new FanControlSetting
            {
                Manual = manual,
                Percent = Math.Max(0, Math.Min(100, percent))
            };
        }

        settings.FanControlSettings = saved;
        SaveSettings(settings);
    }

    private void LogFanAction(string message)
    {
        LogMessage("Normal", message);
    }

    private void LogError(string message)
    {
        LogMessage("Error", message);
    }

    private void LogMessage(string level, string message)
    {
        try
        {
            if (!ShouldLog(level))
            {
                return;
            }

            var path = GetLogFilePath();
            EnsureDirectoryForFile(path);
            MigrateProgramDataFiles();
            RotateLogIfNeeded(path);
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + NormalizeLoggingLevel(level) + "] " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private bool ShouldLog(string level)
    {
        if (forceDebugLogging)
        {
            return LoggingRank(level) <= LoggingRank("Debug");
        }

        var configured = string.IsNullOrWhiteSpace(settings.LoggingLevel) ? "Off" : settings.LoggingLevel;
        var configuredRank = LoggingRank(configured);
        var levelRank = LoggingRank(level);
        return configuredRank > 0 && levelRank <= configuredRank;
    }

    private static int LoggingRank(string level)
    {
        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(level, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(level, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 0;
    }

    private static void RotateLogIfNeeded(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return;
            }

            var info = new System.IO.FileInfo(path);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            var oldPath = path + ".old";
            if (System.IO.File.Exists(oldPath))
            {
                System.IO.File.Delete(oldPath);
            }

            System.IO.File.Move(path, oldPath);
        }
        catch
        {
        }
    }

}
