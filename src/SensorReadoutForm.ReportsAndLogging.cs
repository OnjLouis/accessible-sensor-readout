using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private void SaveReport()
    {
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "Save Sensor Report";
            dialog.Filter = "Text report (*.txt)|*.txt|Formatted HTML report (*.html)|*.html";
            dialog.FileName = "SensorReadout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            dialog.DefaultExt = "txt";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var html = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            if (html && !dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                dialog.FileName = System.IO.Path.ChangeExtension(dialog.FileName, ".html");
            }

            var stopwatch = Stopwatch.StartNew();
            SaveReportToFile(dialog.FileName, html, false);
            stopwatch.Stop();
            statusLabel.Text = "Saved report to " + dialog.FileName + " in " + FormatElapsed(stopwatch.Elapsed) + ".";
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
            latestRows.Clear();
            latestRows.AddRange(CollectSensorRows(true));
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
        lines.Add("Sensor Readout report");
        lines.Add("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!string.IsNullOrWhiteSpace(timingLine))
        {
            lines.Add(timingLine);
        }
        lines.Add("");

        foreach (var typeGroup in latestRows
            .Where(r => r.Type != "Fan Control")
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => r.Type)
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .GroupBy(r => r.Type))
        {
            lines.Add(typeGroup.Key);
            foreach (var hardwareGroup in typeGroup.GroupBy(r => ShortHardwareName(r.Hardware)))
            {
                lines.Add("  " + hardwareGroup.Key);
                foreach (var row in hardwareGroup)
                {
                    lines.Add("    " + CleanSensorName(row.Name) + ": " + FormatValue(row));
                    if (row.Details != null && row.Details.Count > 0)
                    {
                        foreach (var detail in OrderedReportDetails(row))
                        {
                            lines.Add("      " + detail.Key + ": " + detail.Value);
                        }
                    }
                }
            }

            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private string BuildHtmlReport(string timingLine)
    {
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\"><title>Sensor Readout report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.35} table{border-collapse:collapse;margin:0 0 1.2em 0} th,td{border:1px solid #888;padding:4px 8px;text-align:left} h2{margin-top:1.4em} h3{margin-bottom:.4em}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>Sensor Readout report</h1>");
        html.AppendLine("<p>Generated " + HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "</p>");
        if (!string.IsNullOrWhiteSpace(timingLine))
        {
            html.AppendLine("<p>" + HtmlEncode(timingLine) + "</p>");
        }
        foreach (var typeGroup in latestRows
            .Where(r => r.Type != "Fan Control")
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => r.Type)
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .GroupBy(r => r.Type))
        {
            html.AppendLine("<h2>" + HtmlEncode(typeGroup.Key) + "</h2>");
            foreach (var hardwareGroup in typeGroup.GroupBy(r => ShortHardwareName(r.Hardware)))
            {
                html.AppendLine("<h3>" + HtmlEncode(hardwareGroup.Key) + "</h3>");
                html.AppendLine("<table><thead><tr><th>Sensor</th><th>Value</th><th>Source</th></tr></thead><tbody>");
                foreach (var row in hardwareGroup)
                {
                    html.AppendLine("<tr><td>" + HtmlEncode(CleanSensorName(row.Name)) + "</td><td>" + HtmlEncode(FormatValue(row)) + "</td><td>" + HtmlEncode(row.Source) + "</td></tr>");
                    if (row.Details != null && row.Details.Count > 0)
                    {
                        html.AppendLine("<tr><td colspan=\"3\"><table><thead><tr><th>Field</th><th>Value</th></tr></thead><tbody>");
                        foreach (var detail in OrderedReportDetails(row))
                        {
                            html.AppendLine("<tr><td>" + HtmlEncode(detail.Key) + "</td><td>" + HtmlEncode(detail.Value) + "</td></tr>");
                        }
                        html.AppendLine("</tbody></table></td></tr>");
                    }
                }

                html.AppendLine("</tbody></table>");
            }
        }

        html.AppendLine("</body></html>");
        return html.ToString();
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
