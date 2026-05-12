using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class PortableCopyExportOptions
    {
        public string DestinationFolder = "";
        public bool IncludeReports;
        public bool IncludeLogs;
    }

    private void ExportPortableCopy()
    {
        var options = ShowPortableCopyExportDialog();
        if (options == null || string.IsNullOrWhiteSpace(options.DestinationFolder))
        {
            return;
        }

        try
        {
            SaveSettings(settings);
            Directory.CreateDirectory(options.DestinationFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var archiveName = "SensorReadout-portable-" + SafeReportFileName(Environment.MachineName) + "-" + stamp + ".zip";
            var archivePath = Path.Combine(options.DestinationFolder, archiveName);
            var tempPath = Path.Combine(Path.GetTempPath(), "SensorReadout-PortableCopy-" + Guid.NewGuid().ToString("N") + ".zip");
            var rootName = "SensorReadout-" + SafeReportFileName(Environment.MachineName);

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            CreatePortableCopyArchive(tempPath, rootName, options.IncludeReports, options.IncludeLogs);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            File.Move(tempPath, archivePath);
            statusLabel.Text = L("message.Exported portable copy to", "Exported portable copy to") + " " + archivePath + ".";

            if (MessageBox.Show(
                    this,
                    L("message.Portable copy exported to:", "Portable copy exported to:") + Environment.NewLine + archivePath + Environment.NewLine + Environment.NewLine + L("message.Open the folder now?", "Open the folder now?"),
                    L("ui.Export portable copy", "Export portable copy"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + archivePath + "\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L("message.Could not export portable copy:", "Could not export portable copy:") + " " + ex.Message, L("ui.Export portable copy", "Export portable copy"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private PortableCopyExportOptions ShowPortableCopyExportDialog()
    {
        using (var dialog = new Form())
        using (var layout = new TableLayoutPanel())
        using (var folderPanel = new TableLayoutPanel())
        using (var buttons = new FlowLayoutPanel())
        {
            dialog.Text = L("ui.Export portable copy", "Export portable copy");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.AutoSize = true;
            dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            layout.Dock = DockStyle.Fill;
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.Padding = new Padding(12);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(560, 0),
                Text = L("message.portableCopyExportIntro", "Create a ZIP you can extract on another PC. It includes Sensor Readout, settings, Plug-Ins, language files, custom sounds, and other app files. Reports and logs are optional.")
            };

            folderPanel.Dock = DockStyle.Fill;
            folderPanel.AutoSize = true;
            folderPanel.ColumnCount = 3;
            folderPanel.RowCount = 1;
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var folderLabel = new Label { Text = L("ui.Archive folder:", "Archive folder:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
            var folderBox = new TextBox
            {
                Width = 360,
                Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AccessibleName = L("a11y.Archive folder", "Archive folder")
            };
            var browseButton = new Button { Text = L("ui.&Browse...", "&Browse..."), AutoSize = true };
            browseButton.Click += delegate
            {
                using (var folderDialog = new FolderBrowserDialog())
                {
                    folderDialog.Description = L("ui.Choose archive folder", "Choose archive folder");
                    folderDialog.SelectedPath = Directory.Exists(folderBox.Text) ? folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (folderDialog.ShowDialog(dialog) == DialogResult.OK)
                    {
                        folderBox.Text = folderDialog.SelectedPath;
                    }
                }
            };

            folderPanel.Controls.Add(folderLabel, 0, 0);
            folderPanel.Controls.Add(folderBox, 1, 0);
            folderPanel.Controls.Add(browseButton, 2, 0);

            var reportsBox = new CheckBox
            {
                Text = L("ui.Include &Reports", "Include &Reports"),
                Checked = false,
                AutoSize = true,
                AccessibleName = L("a11y.Include Reports", "Include Reports")
            };
            var logsBox = new CheckBox
            {
                Text = L("ui.Include &Logs", "Include &Logs"),
                Checked = false,
                AutoSize = true,
                AccessibleName = L("a11y.Include Logs", "Include Logs")
            };

            var exportButton = new Button { Text = L("ui.&Export", "&Export"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(exportButton);

            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(folderPanel, 0, 1);
            layout.Controls.Add(reportsBox, 0, 2);
            layout.Controls.Add(logsBox, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = exportButton;
            dialog.CancelButton = cancelButton;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return null;
            }

            return new PortableCopyExportOptions
            {
                DestinationFolder = folderBox.Text.Trim(),
                IncludeReports = reportsBox.Checked,
                IncludeLogs = logsBox.Checked
            };
        }
    }

    private void CreatePortableCopyArchive(string archivePath, string rootName, bool includeReports, bool includeLogs)
    {
        var sourceRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create, Encoding.UTF8))
        {
            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (ShouldSkipPortableCopyEntry(relative, includeReports, includeLogs))
                {
                    continue;
                }

                var entryName = rootName + "/" + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = entry.Open())
                {
                    input.CopyTo(output);
                }
            }
        }
    }

    private static bool ShouldSkipPortableCopyEntry(string relativePath, bool includeReports, bool includeLogs)
    {
        var normalized = (relativePath ?? "").Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var firstSeparator = normalized.IndexOf(Path.DirectorySeparatorChar);
        var firstPart = firstSeparator >= 0 ? normalized.Substring(0, firstSeparator) : normalized;

        if (!includeReports && string.Equals(firstPart, "Reports", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!includeLogs && string.Equals(firstPart, "Logs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var updateTemp = "Config" + Path.DirectorySeparatorChar + "Update Temp";
        var updateBackups = "Config" + Path.DirectorySeparatorChar + "Update Backups";
        return string.Equals(normalized, updateTemp, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(updateTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, updateBackups, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(updateBackups + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
