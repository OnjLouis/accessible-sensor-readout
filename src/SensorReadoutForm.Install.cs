using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
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

        var confirmMessage =
            L("message.Sensor Readout will be copied to:", "Sensor Readout will be copied to:") +
            Environment.NewLine + installFolder +
            Environment.NewLine + Environment.NewLine +
            L("message.This copy will close, and the installed copy will start from that folder.", "This copy will close, and the installed copy will start from that folder.");
        if (MessageBox.Show(this, confirmMessage, L("ui.Install to this PC", "Install to this PC"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        var desktopShortcutResult = MessageBox.Show(
            this,
            L("message.Create a desktop shortcut for the installed copy?", "Create a desktop shortcut for the installed copy?"),
            L("ui.Install to this PC", "Install to this PC"),
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (desktopShortcutResult == DialogResult.Cancel)
        {
            return;
        }

        try
        {
            statusLabel.Text = L("status.Installing Sensor Readout...", "Installing Sensor Readout...");
            Application.DoEvents();

            Directory.CreateDirectory(installFolder);
            CopyDirectoryContents(sourceFolder, installFolder);

            if (desktopShortcutResult == DialogResult.Yes)
            {
                CreateDesktopShortcut(targetExe, installFolder);
            }

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

    private static string GetLocalInstallFolderPath()
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

    private static void CreateDesktopShortcut(string targetExe, string workingDirectory)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "Sensor Readout.lnk");
        CreateShortcut(shortcutPath, targetExe, "", workingDirectory, "Sensor Readout");
    }
}
