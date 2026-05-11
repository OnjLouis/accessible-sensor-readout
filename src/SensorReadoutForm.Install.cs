using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class InstallOptions
    {
        public bool CreateDesktopShortcut;
        public bool RunAtStartup;
    }

    private void InstallToLocalAppDataAndRestart()
    {
        var sourceFolder = NormalizeFolderPath(AppDomain.CurrentDomain.BaseDirectory);
        var installFolder = GetLocalInstallFolderPath();
        var targetExe = Path.Combine(installFolder, Path.GetFileName(Application.ExecutablePath));

        if (string.Equals(sourceFolder, NormalizeFolderPath(installFolder), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                L("message.Sensor Readout is already running from the local install folder.", "Sensor Readout is already running from the local install folder."),
                L("ui.Install to this PC", "Install to this PC"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var options = ShowInstallOptionsDialog(installFolder);
        if (options == null)
        {
            return;
        }

        try
        {
            statusLabel.Text = L("status.Installing Sensor Readout...", "Installing Sensor Readout...");
            Application.DoEvents();

            Directory.CreateDirectory(installFolder);
            CopyDirectoryContents(sourceFolder, installFolder);

            SetDesktopShortcut(options.CreateDesktopShortcut, targetExe, installFolder);

            settings.RunAtStartup = options.RunAtStartup;
            if (settings.RunAtStartup)
            {
                settings.StartMinimizedToTray = true;
                settings.TrayStatusEnabled = true;
            }
            SaveSettings(settings);
            SetRunAtStartup(settings.RunAtStartup, settings.StartMinimizedToTray, targetExe, installFolder);

            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = installFolder,
                UseShellExecute = false
            });

            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                L("message.Could not install Sensor Readout:", "Could not install Sensor Readout:") + " " + ex.Message,
                L("ui.Install to this PC", "Install to this PC"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            statusLabel.Text = L("status.Install failed.", "Install failed.");
        }
    }

    private void UninstallLocalInstallAndClose()
    {
        var installFolder = GetLocalInstallFolderPath();
        if (!IsRunningFromLocalInstallFolder())
        {
            MessageBox.Show(
                this,
                L("message.Sensor Readout is not running from the local install folder.", "Sensor Readout is not running from the local install folder."),
                L("ui.Uninstall from this PC", "Uninstall from this PC"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var message =
            L("message.This will remove the installed Sensor Readout app files from this PC.", "This will remove the installed Sensor Readout app files from this PC.") +
            Environment.NewLine + installFolder +
            Environment.NewLine + Environment.NewLine +
            L("message.Your Config, Logs, and Reports folders will be left in place.", "Your Config, Logs, and Reports folders will be left in place.") +
            Environment.NewLine + Environment.NewLine +
            L("message.Sensor Readout will close when uninstall starts.", "Sensor Readout will close when uninstall starts.");
        if (MessageBox.Show(this, message, L("ui.Uninstall from this PC", "Uninstall from this PC"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        try
        {
            settings.RunAtStartup = false;
            SaveSettings(settings);
            SetRunAtStartup(false, false);
            SetDesktopShortcut(false);
            StartUninstallScript(installFolder, Process.GetCurrentProcess().Id);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                L("message.Could not uninstall Sensor Readout:", "Could not uninstall Sensor Readout:") + " " + ex.Message,
                L("ui.Uninstall from this PC", "Uninstall from this PC"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private InstallOptions ShowInstallOptionsDialog(string installFolder)
    {
        using (var dialog = new Form())
        using (var layout = new TableLayoutPanel())
        using (var buttons = new FlowLayoutPanel())
        {
            dialog.Text = L("ui.Install to this PC", "Install to this PC");
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
                MaximumSize = new System.Drawing.Size(520, 0),
                Text =
                    L("message.Sensor Readout will be copied to:", "Sensor Readout will be copied to:") +
                    Environment.NewLine + installFolder +
                    Environment.NewLine + Environment.NewLine +
                    L("message.This copy will close, and the installed copy will start from that folder.", "This copy will close, and the installed copy will start from that folder.")
            };
            var desktopBox = new CheckBox
            {
                Text = L("ui.Create &desktop shortcut", "Create &desktop shortcut"),
                Checked = DesktopShortcutExists(),
                AutoSize = true,
                AccessibleName = L("a11y.Create desktop shortcut", "Create desktop shortcut")
            };
            var startupBox = new CheckBox
            {
                Text = L("ui.Run at Windows &startup", "Run at Windows &startup"),
                Checked = settings.RunAtStartup,
                AutoSize = true,
                AccessibleName = L("a11y.Run at Windows startup", "Run at Windows startup")
            };
            startupBox.CheckedChanged += delegate
            {
                if (startupBox.Checked)
                {
                    desktopBox.Checked = true;
                }
            };

            var okButton = new Button { Text = L("ui.&Install", "&Install"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);

            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(desktopBox, 0, 1);
            layout.Controls.Add(startupBox, 0, 2);
            layout.Controls.Add(buttons, 0, 4);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            return dialog.ShowDialog(this) == DialogResult.OK
                ? new InstallOptions { CreateDesktopShortcut = desktopBox.Checked, RunAtStartup = startupBox.Checked }
                : null;
        }
    }

    public static bool IsRunningFromLocalInstallFolder()
    {
        return string.Equals(
            NormalizeFolderPath(AppDomain.CurrentDomain.BaseDirectory),
            NormalizeFolderPath(GetLocalInstallFolderPath()),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string GetLocalInstallFolderPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local");
        }

        return Path.Combine(localAppData, "Programs", "Sensor Readout");
    }

    private static string NormalizeFolderPath(string path)
    {
        return Path.GetFullPath(path ?? "")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool DesktopShortcutExists()
    {
        return File.Exists(GetDesktopShortcutPath());
    }

    public static void SetDesktopShortcut(bool enabled)
    {
        SetDesktopShortcut(enabled, Application.ExecutablePath, AppDomain.CurrentDomain.BaseDirectory);
    }

    private static void SetDesktopShortcut(bool enabled, string targetExe, string workingDirectory)
    {
        var shortcutPath = GetDesktopShortcutPath();
        if (!enabled)
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }

            return;
        }

        CreateShortcut(shortcutPath, targetExe, "", workingDirectory, "Sensor Readout");
    }

    private static string GetDesktopShortcutPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "Sensor Readout.lnk");
    }

    private static void StartUninstallScript(string installFolder, int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "SensorReadout-Uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            "$pidToWait = " + processId + "\r\n" +
            "$target = " + PowerShellInstallQuote(installFolder) + "\r\n" +
            "$preserve = @('Config','Logs','Reports')\r\n" +
            "while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }\r\n" +
            "if (Test-Path -LiteralPath $target) {\r\n" +
            "  Get-ChildItem -LiteralPath $target -Force | Where-Object { $preserve -notcontains $_.Name } | ForEach-Object {\r\n" +
            "    Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "  }\r\n" +
            "}\r\n" +
            "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\r\n";
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(scriptPath),
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
    }

    private static string PowerShellInstallQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "''") + "'";
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
}
