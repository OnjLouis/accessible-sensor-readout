using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed partial class SensorReadoutForm : Form
{
    private const string CoreTempUrl = "https://www.alcpu.com/CoreTemp/";
    private const string CoreTempWingetId = "ALCPU.CoreTemp";
    private const string CoreTempChocolateyId = "coretemp";

    private void CheckPrerequisitesOnFirstRun()
    {
        if (settings.PrerequisitesPromptShown || IsPawnIoInstalled())
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "PawnIO does not appear to be installed. Sensor Readout can still open, but motherboard sensors and fan controls may be missing." + Environment.NewLine + Environment.NewLine +
            "Do you want to run the prerequisite installer now?",
            "Sensor Readout prerequisites",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        settings.PrerequisitesPromptShown = true;
        SaveSettings(settings);

        if (result == DialogResult.Yes)
        {
            RunPrerequisiteInstaller();
        }
    }

    private void RunPrerequisiteInstaller()
    {
        var installerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install-Prerequisites.cmd");
        if (!System.IO.File.Exists(installerPath))
        {
            MessageBox.Show(this, "Install-Prerequisites.cmd could not be found beside Sensor Readout.", "Sensor Readout prerequisites", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not start prerequisite installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowCoreTempSupportOptions()
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Core Temp support", "Core Temp support");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.ShowIcon = false;
            dialog.Size = new Size(560, 260);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var message = T("message.coreTempSupportIntro", "Core Temp is optional. It can help Sensor Readout read CPU temperature and CPU load on systems where LibreHardwareMonitor cannot. It does not improve fan control, GPU readings, SMART data, USB, or motherboard sensors.");
            if (IsCoreTempRunning())
            {
                message += Environment.NewLine + Environment.NewLine + T("message.coreTempAlreadyRunning", "Core Temp appears to be running. Sensor Readout will use it automatically as an optional CPU fallback.");
            }

            var text = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Text = message
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };

            var closeButton = CreateCloseButton();
            closeButton.DialogResult = DialogResult.Cancel;
            var webButton = new Button { Text = T("ui.Open Core Temp website", "Open Core Temp &website"), AutoSize = true };
            var chocoButton = new Button { Text = T("ui.Install with Chocolatey", "Install with &Chocolatey"), AutoSize = true, Enabled = CommandExists("choco.exe") };
            var wingetButton = new Button { Text = T("ui.Install with winget", "Install with &winget"), AutoSize = true, Enabled = CommandExists("winget.exe") };

            webButton.Click += delegate { OpenCoreTempWebsite(); };
            chocoButton.Click += delegate { StartCoreTempInstall("Chocolatey", "choco", "install " + CoreTempChocolateyId + " -y"); };
            wingetButton.Click += delegate { StartCoreTempInstall("winget", "winget", "install --id " + CoreTempWingetId + " --exact --source winget --accept-source-agreements --accept-package-agreements"); };

            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(webButton);
            buttons.Controls.Add(chocoButton);
            buttons.Controls.Add(wingetButton);

            dialog.CancelButton = closeButton;
            layout.Controls.Add(text, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
    }

    private static bool IsCoreTempRunning()
    {
        return Process.GetProcessesByName("Core Temp").Length > 0 ||
            Process.GetProcessesByName("CoreTemp").Length > 0;
    }

    private void OpenCoreTempWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = CoreTempUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Core Temp support", "Core Temp support"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartCoreTempInstall(string installerName, string command, string arguments)
    {
        var confirm = MessageBox.Show(
            this,
            string.Format(T("message.installCoreTempConfirm", "Install Core Temp using {0}? This starts a third-party installer and may ask for administrator permission."), installerName),
            T("ui.Core Temp support", "Core Temp support"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Core Temp support", "Core Temp support"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool CommandExists(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
        var extensions = fileName.IndexOf('.') >= 0 ? new[] { "" } : pathExt.Split(';');
        foreach (var directory in path.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = System.IO.Path.Combine(directory.Trim(), fileName + extension);
                    if (System.IO.File.Exists(candidate))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static void SetRunAtStartup(bool enabled, bool startMinimized)
    {
        SetRunAtStartup(enabled, startMinimized, Application.ExecutablePath, AppDomain.CurrentDomain.BaseDirectory);
    }

    private static void SetRunAtStartup(bool enabled, bool startMinimized, string targetPath, string workingDirectory)
    {
        var shortcutPath = GetStartupShortcutPath();
        if (!enabled)
        {
            if (System.IO.File.Exists(shortcutPath))
            {
                System.IO.File.Delete(shortcutPath);
            }

            SetStartupApprovedState(shortcutPath, false);
            return;
        }

        CreateShortcut(shortcutPath, targetPath, startMinimized ? "--minimized" : "", workingDirectory, "Sensor Readout");
        SetStartupApprovedState(shortcutPath, true);
    }

    private void RepairRunAtStartupRegistration()
    {
        if (!settings.RunAtStartup)
        {
            return;
        }

        try
        {
            SetRunAtStartup(true, settings.StartMinimizedToTray);
        }
        catch (Exception ex)
        {
            LogMessage("Normal", "Could not refresh Windows startup registration: " + ex.Message);
        }
    }

    private static void SetStartupApprovedState(string shortcutPath, bool enabled)
    {
        const string startupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        var valueName = System.IO.Path.GetFileName(shortcutPath);
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return;
        }

        try
        {
            if (!enabled)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(startupApprovedKeyPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }

                return;
            }

            using (var key = Registry.CurrentUser.CreateSubKey(startupApprovedKeyPath))
            {
                if (key != null)
                {
                    key.SetValue(valueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
            }
        }
        catch
        {
            // Some managed Windows environments lock Startup Apps approval state. The shortcut is still valid.
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("Windows Script Host is not available.");
        }

        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember(
            "CreateShortcut",
            System.Reflection.BindingFlags.InvokeMethod,
            null,
            shell,
            new object[] { shortcutPath });
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
        shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? "" });
        shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
        shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
        shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static string GetStartupShortcutPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Sensor Readout.lnk");
    }

}
