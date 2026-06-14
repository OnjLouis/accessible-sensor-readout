using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void CompareReports()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = T("ui.Choose reports to compare", "Choose reports to compare");
            dialog.Filter = "Sensor Readout reports (*.html;*.htm;*.zip)|*.html;*.htm;*.zip|All files (*.*)|*.*";
            dialog.InitialDirectory = GetReportsFolderPath();
            dialog.CheckFileExists = true;
            dialog.Multiselect = true;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            if (dialog.FileNames.Length != 2)
            {
                MessageBox.Show(this, T("message.Choose exactly two reports to compare.", "Choose exactly two reports to compare."), T("ui.Compare reports", "Compare reports"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var before = ReadReportSnapshot(dialog.FileNames[0]);
                var after = ReadReportSnapshot(dialog.FileNames[1]);
                if (before == null || after == null || before.Rows == null || after.Rows == null)
                {
                    MessageBox.Show(this, T("message.One of the reports did not contain readable Sensor Readout data.", "One of the reports did not contain readable Sensor Readout data."), T("ui.Compare reports", "Compare reports"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ShowReportComparisonDialog(BuildReportComparisonText(before, dialog.FileNames[0], after, dialog.FileNames[1]));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, T("message.Could not compare reports:", "Could not compare reports:") + " " + ex.Message, T("ui.Compare reports", "Compare reports"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private string BuildReportComparisonText(ReportSnapshot before, string beforePath, ReportSnapshot after, string afterPath)
    {
        var lines = new List<string>();
        lines.Add(T("ui.Sensor Readout report comparison", "Sensor Readout report comparison"));
        lines.Add("");
        lines.Add(T("ui.Before:", "Before:") + " " + ReportComparisonHeader(before, beforePath));
        lines.Add(T("ui.After:", "After:") + " " + ReportComparisonHeader(after, afterPath));
        lines.Add("");

        var beforeRows = (before.Rows ?? new List<ReportSnapshotRow>()).Where(r => r != null).GroupBy(ReportComparisonKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var afterRows = (after.Rows ?? new List<ReportSnapshotRow>()).Where(r => r != null).GroupBy(ReportComparisonKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = afterRows.Keys.Except(beforeRows.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        var removed = beforeRows.Keys.Except(afterRows.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        var changed = beforeRows.Keys.Intersect(afterRows.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !string.Equals(ReportRowValue(beforeRows[k]), ReportRowValue(afterRows[k]), StringComparison.Ordinal))
            .OrderBy(k => k)
            .ToList();

        AddComparisonSection(lines, T("ui.Added readings", "Added readings"), added.Select(k => "+ " + ReportRowLabel(afterRows[k]) + " = " + ReportRowValue(afterRows[k])));
        AddComparisonSection(lines, T("ui.Removed readings", "Removed readings"), removed.Select(k => "- " + ReportRowLabel(beforeRows[k]) + " = " + ReportRowValue(beforeRows[k])));
        AddComparisonSection(lines, T("ui.Changed readings", "Changed readings"), changed.Select(k => "* " + ReportRowLabel(afterRows[k]) + ": " + ReportRowValue(beforeRows[k]) + " -> " + ReportRowValue(afterRows[k])));

        var detailChanges = beforeRows.Keys.Intersect(afterRows.Keys, StringComparer.OrdinalIgnoreCase)
            .SelectMany(k => ReportDetailChanges(beforeRows[k], afterRows[k]))
            .Take(200)
            .ToList();
        AddComparisonSection(lines, T("ui.Detail changes", "Detail changes"), detailChanges);
        lines.Add(T("ui.End of comparison.", "End of comparison."));
        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private static void AddComparisonSection(List<string> lines, string heading, IEnumerable<string> entries)
    {
        var items = (entries ?? Enumerable.Empty<string>()).Take(500).ToList();
        lines.Add(heading + " (" + items.Count + ")");
        if (items.Count == 0)
        {
            lines.Add("  None");
        }
        else
        {
            lines.AddRange(items.Select(i => "  " + i));
        }
        lines.Add("");
    }

    private static IEnumerable<string> ReportDetailChanges(ReportSnapshotRow before, ReportSnapshotRow after)
    {
        var beforeDetails = before.Details ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var afterDetails = after.Details ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in beforeDetails.Keys.Union(afterDetails.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k))
        {
            string beforeValue;
            string afterValue;
            beforeDetails.TryGetValue(key, out beforeValue);
            afterDetails.TryGetValue(key, out afterValue);
            if (!string.Equals(beforeValue ?? "", afterValue ?? "", StringComparison.Ordinal))
            {
                yield return ReportRowLabel(after) + " / " + key + ": " + (beforeValue ?? "") + " -> " + (afterValue ?? "");
            }
        }
    }

    private string ReportComparisonHeader(ReportSnapshot snapshot, string path)
    {
        var machine = ReportMachineName(snapshot);
        var title = string.IsNullOrWhiteSpace(machine) ? snapshot.Title : machine;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = System.IO.Path.GetFileName(path);
        }
        return title + " (" + (snapshot.Rows == null ? 0 : snapshot.Rows.Count) + " " + T("ui.readings", "readings") + ")";
    }

    private static string ReportComparisonKey(ReportSnapshotRow row)
    {
        return (row.Type ?? "") + "|" + (row.Hardware ?? "") + "|" + (row.Name ?? "") + "|" + (row.Identifier ?? "");
    }

    private static string ReportRowLabel(ReportSnapshotRow row)
    {
        return (row.Type ?? "") + " / " + (row.Hardware ?? "") + " / " + (row.Name ?? "");
    }

    private static string ReportRowValue(ReportSnapshotRow row)
    {
        return string.IsNullOrWhiteSpace(row.DisplayValue) && row.Value.HasValue ? row.Value.Value.ToString("0.###") : row.DisplayValue ?? "";
    }

    private void ShowReportComparisonDialog(string text)
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Compare reports", "Compare reports");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(820, 560);
            dialog.MinimumSize = new Size(560, 380);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text ?? "", AccessibleName = T("a11y.Report comparison results", "Report comparison results") };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var closeButton = CreateCloseButton();
            var copyButton = new Button { Text = T("ui.&Copy", "&Copy"), AutoSize = true };
            var saveButton = new Button { Text = T("ui.&Save...", "&Save..."), AutoSize = true };
            closeButton.Click += delegate { dialog.Close(); };
            copyButton.Click += delegate { Clipboard.SetText(box.Text); statusLabel.Text = T("status.Copied comparison to clipboard.", "Copied comparison to clipboard."); };
            saveButton.Click += delegate { SaveTextFromDialog(box.Text, "SensorReadout-Comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", T("ui.Save comparison", "Save comparison")); };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(saveButton);
            dialog.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { dialog.Close(); e.Handled = true; } };
            dialog.Controls.Add(box);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate { box.Focus(); box.Select(0, 0); };
            dialog.ShowDialog(this);
        }
    }

    private void SaveAnonymizedReport()
    {
        var snapshot = SanitizeReportSnapshot(BuildReportSnapshot());
        var preview = BuildSanitizationPreview(snapshot);
        if (MessageBox.Show(this, preview, T("ui.Save anonymized report", "Save anonymized report"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        using (var dialog = new SaveFileDialog())
        {
            var reportsFolder = GetReportsFolderPath();
            System.IO.Directory.CreateDirectory(reportsFolder);
            dialog.Title = T("ui.Save anonymized report", "Save anonymized report");
            dialog.Filter = "Formatted HTML report (*.html)|*.html|Text report (*.txt)|*.txt";
            dialog.FilterIndex = 1;
            dialog.FileName = "SensorReadout-Anonymized-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html";
            dialog.InitialDirectory = reportsFolder;
            dialog.DefaultExt = "html";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var html = dialog.FilterIndex == 1 || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            System.IO.File.WriteAllText(dialog.FileName, html ? BuildHtmlReport("", snapshot) : BuildTextReport("", snapshot));
            statusLabel.Text = T("status.Anonymized report saved to", "Anonymized report saved to") + " " + dialog.FileName + ".";
        }
    }

    public void SaveAnonymizedReportToFile(string path, bool html)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Report path is required.", "path");
        }

        SetLatestRows(CollectSensorRows(true));
        EnsureDirectoryForFile(path);
        var snapshot = SanitizeReportSnapshot(BuildReportSnapshot());
        System.IO.File.WriteAllText(path, html ? BuildHtmlReport("", snapshot) : BuildTextReport("", snapshot));
    }

    public string BuildReportComparisonFileText(string beforePath, string afterPath)
    {
        var before = ReadReportSnapshot(beforePath);
        var after = ReadReportSnapshot(afterPath);
        if (before == null || after == null || before.Rows == null || after.Rows == null)
        {
            throw new InvalidOperationException("One of the reports did not contain readable Sensor Readout data.");
        }

        return BuildReportComparisonText(before, beforePath, after, afterPath);
    }

    private ReportSnapshot SanitizeReportSnapshot(ReportSnapshot source)
    {
        var sourceMachineName = source == null ? CurrentReportMachineName() : source.MachineName;
        var snapshot = new ReportSnapshot
        {
            AppVersion = source == null ? AppVersion : source.AppVersion,
            Title = "Sensor Readout report for Computer",
            MachineName = "Computer",
            GeneratedLocal = source == null ? CurrentReportGeneratedLocal() : source.GeneratedLocal,
            MemoryUnitMode = source == null ? CurrentReportMemoryUnitMode() : source.MemoryUnitMode,
            StorageUnitMode = source == null ? CurrentReportStorageUnitMode() : source.StorageUnitMode,
            TransferUnitMode = source == null ? CurrentReportTransferUnitMode() : source.TransferUnitMode,
            Rows = new List<ReportSnapshotRow>()
        };

        foreach (var row in source == null || source.Rows == null ? new List<ReportSnapshotRow>() : source.Rows)
        {
            if (row == null)
            {
                continue;
            }

            if (IsOnlineIpLookupReportRow(row))
            {
                continue;
            }

            if (IsPrivateActivityReportRow(row))
            {
                continue;
            }

            snapshot.Rows.Add(new ReportSnapshotRow
            {
                Type = row.Type ?? "",
                Hardware = SanitizeReportText(row.Hardware, sourceMachineName),
                Name = SanitizeReportText(row.Name, sourceMachineName),
                Identifier = SanitizeReportText(row.Identifier, sourceMachineName),
                Value = row.Value,
                DisplayValue = SanitizeReportText(row.DisplayValue, sourceMachineName),
                Source = row.Source ?? "",
                Details = row.Details == null ? null : row.Details.ToDictionary(p => p.Key, p => SanitizeReportText(p.Value, sourceMachineName), StringComparer.OrdinalIgnoreCase)
            });
        }

        return snapshot;
    }

    private static bool IsPrivateActivityReportRow(ReportSnapshotRow row)
    {
        return row != null &&
            (string.Equals(row.Type, "Tasks", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Type, "Spoken Hotkeys", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOnlineIpLookupReportRow(ReportSnapshotRow row)
    {
        if (row == null)
        {
            return false;
        }

        return string.Equals(row.Source ?? "", PublicIpLookupSource, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Hardware ?? "", "Internet connection", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildSanitizationPreview(ReportSnapshot snapshot)
    {
        var rows = snapshot == null || snapshot.Rows == null ? 0 : snapshot.Rows.Count;
        return T("message.Anonymized report preview", "Sensor Readout will save a shareable report with the computer name replaced and common private identifiers masked, including IP addresses, MAC addresses, serial numbers, UUIDs, GUIDs, PnP IDs, hardware IDs, compatible IDs, and location paths. Online public-IP lookup rows, Tasks rows, and Spoken Hotkeys rows are removed from anonymized reports.") +
            Environment.NewLine + Environment.NewLine +
            T("ui.Rows included:", "Rows included:") + " " + rows + Environment.NewLine +
            T("ui.Computer name:", "Computer name:") + " Computer";
    }

    private static string SanitizeReportText(string value, string sourceMachineName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? "";
        }

        var text = value;
        foreach (var machineName in new[] { sourceMachineName, Environment.MachineName })
        {
            if (!string.IsNullOrWhiteSpace(machineName))
            {
                text = Regex.Replace(text, @"\b" + Regex.Escape(machineName.Trim()) + @"\b", "Computer", RegexOptions.IgnoreCase);
            }
        }

        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[IP address]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b", "[MAC address]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\b", "[UUID]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?i)\b(Serial(?: number)?|UUID|Guid|PNP device ID|Hardware IDs|Compatible IDs|Location path|Device instance ID):?\s*[^;\r\n]+", "$1: [masked]");
        text = Regex.Replace(text, @"(?i)\b(?:PCI|USB|HID|ACPI|ROOT|BTH|SWD)\\[^\s;,\r\n]+", "[device identifier]");
        return text;
    }

    private void PrepareSupportReport()
    {
        if (diagnosticsRunning)
        {
            MessageBox.Show(this, T("message.diagnosticsAlreadyRunning", "Diagnostics are already running."), T("ui.Prepare support report", "Prepare support report"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (reportViewMode)
        {
            ReturnToLiveReadings();
        }

        diagnosticsRunning = true;
        statusLabel.Text = T("status.Preparing support report...", "Preparing support report...");
        Task.Factory.StartNew(delegate { return SaveDiagnosticsToZip("", null, null); })
            .ContinueWith(delegate(Task<string> task)
            {
                diagnosticsRunning = false;
                if (task.IsFaulted)
                {
                    var message = task.Exception == null ? "Support report failed." : task.Exception.GetBaseException().Message;
                    statusLabel.Text = message;
                    MessageBox.Show(this, message, T("ui.Prepare support report", "Prepare support report"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ShowSupportReportDialog(task.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowSupportReportDialog(string zipPath)
    {
        var message = T("message.Support report ready", "Support report ready. Attach this ZIP file when you open an issue or contact support:") + Environment.NewLine + zipPath;
        BringToFrontForUserPrompt();
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Prepare support report", "Prepare support report");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 260);
            dialog.MinimumSize = new Size(520, 220);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;
            var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Text = message, ScrollBars = ScrollBars.Vertical, AccessibleName = T("a11y.Support report instructions", "Support report instructions") };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var closeButton = CreateCloseButton();
            var issueButton = new Button { Text = T("ui.Open &issue page", "Open &issue page"), AutoSize = true };
            var copyButton = new Button { Text = T("ui.&Copy path", "&Copy path"), AutoSize = true };
            var folderButton = new Button { Text = T("ui.&Open folder", "&Open folder"), AutoSize = true };
            closeButton.Click += delegate { dialog.Close(); };
            issueButton.Click += delegate { OpenExternalPage(ProjectUrl + "/issues/new", T("message.Could not open issue page.", "Could not open issue page.")); };
            copyButton.Click += delegate { Clipboard.SetText(zipPath ?? ""); statusLabel.Text = T("status.Support report path copied.", "Support report path copied."); };
            folderButton.Click += delegate { SelectFileInExplorer(zipPath); };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(issueButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(folderButton);
            dialog.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { dialog.Close(); e.Handled = true; } };
            dialog.Controls.Add(box);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.ShowDialog(this);
        }
    }

    private void SelectFileInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(GetReportsFolderPath()); } catch { }
        }
    }

    private void SaveTextFromDialog(string text, string fileName, string title)
    {
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = title;
            dialog.Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.InitialDirectory = GetReportsFolderPath();
            dialog.FileName = fileName;
            dialog.DefaultExt = "txt";
            dialog.AddExtension = true;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                System.IO.File.WriteAllText(dialog.FileName, text ?? "");
                statusLabel.Text = T("status.Saved to", "Saved to") + " " + dialog.FileName + ".";
            }
        }
    }
}
