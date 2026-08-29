using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class InstallOptions
    {
        public bool CreateDesktopShortcut;
        public bool RunAtStartup;
    }

    private sealed class UninstallOptions
    {
        public bool DeleteUserData;
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

            settings.RunAtStartup = options.RunAtStartup;
            if (settings.RunAtStartup)
            {
                settings.StartMinimizedToTray = true;
                settings.TrayStatusEnabled = true;
            }
            SaveSettings(settings);

            Directory.CreateDirectory(installFolder);
            CopyDirectoryContents(sourceFolder, installFolder);

            RegisterInstalledAppEntry(targetExe, installFolder);
            RegisterRemoteConnectionFileAssociation(targetExe);
            SetDesktopShortcut(options.CreateDesktopShortcut, targetExe, installFolder);
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

        var options = ShowUninstallOptionsDialog(installFolder);
        if (options == null)
        {
            return;
        }

        try
        {
            settings.RunAtStartup = false;
            SaveSettings(settings);
            SetRunAtStartup(false, false);
            SetDesktopShortcut(false);
            UnregisterRemoteConnectionFileAssociation();
            UnregisterInstalledAppEntry();
            string firewallError;
            if (!RemoteFirewallManager.TryRemoveInboundRule(out firewallError))
            {
                LogMessage("Normal", "Windows Firewall access for remote monitoring could not be removed during uninstall: " + firewallError);
            }
            StartUninstallScript(installFolder, Process.GetCurrentProcess().Id, options.DeleteUserData);
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

    private UninstallOptions ShowUninstallOptionsDialog(string installFolder)
    {
        using (var dialog = new Form())
        using (var layout = new TableLayoutPanel())
        using (var buttons = new FlowLayoutPanel())
        {
            dialog.Text = L("ui.Uninstall from this PC", "Uninstall from this PC");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.AutoSize = true;
            dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            layout.Dock = DockStyle.Fill;
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.Padding = new Padding(12);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(560, 0),
                Text =
                    L("message.This will remove the installed Sensor Readout app files from this PC.", "This will remove the installed Sensor Readout app files from this PC.") +
                    Environment.NewLine + installFolder +
                    Environment.NewLine + Environment.NewLine +
                    L("message.Sensor Readout will close when uninstall starts.", "Sensor Readout will close when uninstall starts.")
            };
            var deleteUserDataBox = new CheckBox
            {
                Text = L("ui.Also delete &Config, Logs, and Reports", "Also delete &Config, Logs, and Reports"),
                Checked = false,
                AutoSize = true,
                AccessibleName = L("a11y.Also delete Config, Logs, and Reports", "Also delete Config, Logs, and Reports"),
                AccessibleDescription = L("a11y.Leave this unchecked to keep settings, logs, and saved reports after uninstalling.", "Leave this unchecked to keep settings, logs, and saved reports after uninstalling.")
            };

            var uninstallButton = new Button { Text = L("ui.&Uninstall", "&Uninstall"), AccessibleName = PlainMnemonic(L("ui.&Uninstall", "&Uninstall")), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), AccessibleName = PlainMnemonic(L("ui.&Cancel", "&Cancel")), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(uninstallButton);

            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(deleteUserDataBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = uninstallButton;
            dialog.CancelButton = cancelButton;

            return dialog.ShowDialog(Visible ? this : null) == DialogResult.OK
                ? new UninstallOptions { DeleteUserData = deleteUserDataBox.Checked }
                : null;
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

            var okButton = new Button { Text = L("ui.&Install", "&Install"), AccessibleName = PlainMnemonic(L("ui.&Install", "&Install")), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), AccessibleName = PlainMnemonic(L("ui.&Cancel", "&Cancel")), DialogResult = DialogResult.Cancel, AutoSize = true };
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

    public static void RefreshInstalledAppRegistration()
    {
        if (!IsRunningFromLocalInstallFolder())
        {
            return;
        }

        RegisterInstalledAppEntry(Application.ExecutablePath, AppDomain.CurrentDomain.BaseDirectory);
        RegisterRemoteConnectionFileAssociation(Application.ExecutablePath);
    }

    public static void RunUninstallFromCommandLine()
    {
        using (var form = new SensorReadoutForm(false))
        {
            form.UninstallLocalInstallAndClose();
        }
    }

    public static void RunInstallFromCommandLine()
    {
        using (var form = new SensorReadoutForm(false))
        {
            form.InstallToLocalAppDataAndRestart();
        }
    }

    private const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sensor Readout";
    private const string RemoteConnectionExtensionKeyPath = @"Software\Classes\.srconnection";
    private const string RemoteConnectionProgId = "SensorReadout.RemoteConnection";
    private const string RemoteConnectionProgIdKeyPath = @"Software\Classes\SensorReadout.RemoteConnection";
    private const string RemoteConnectionContentType = "application/vnd.sensor-readout.remote-connection+json";
    private const uint ShellAssociationChanged = 0x08000000;
    private const uint ShellNotifyIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void RegisterRemoteConnectionFileAssociation(string targetExe)
    {
        RegisterRemoteConnectionFileAssociation(
            targetExe,
            RemoteConnectionExtensionKeyPath,
            RemoteConnectionProgId,
            RemoteConnectionProgIdKeyPath,
            true);
    }

    private static void RegisterRemoteConnectionFileAssociation(
        string targetExe,
        string extensionKeyPath,
        string progId,
        string progIdKeyPath,
        bool notifyShell)
    {
        if (string.IsNullOrWhiteSpace(targetExe))
        {
            return;
        }
        if (IsRemoteConnectionFileAssociationCurrent(targetExe, extensionKeyPath, progId, progIdKeyPath))
        {
            return;
        }
        try
        {
            using (var extensionKey = Registry.CurrentUser.CreateSubKey(extensionKeyPath))
            {
                if (extensionKey != null)
                {
                    extensionKey.SetValue("", progId, RegistryValueKind.String);
                    extensionKey.SetValue("Content Type", RemoteConnectionContentType, RegistryValueKind.String);
                    using (var openWithKey = extensionKey.CreateSubKey("OpenWithProgids"))
                    {
                        if (openWithKey != null)
                        {
                            openWithKey.SetValue(progId, new byte[0], RegistryValueKind.None);
                        }
                    }
                }
            }
            using (var progIdKey = Registry.CurrentUser.CreateSubKey(progIdKeyPath))
            {
                if (progIdKey != null)
                {
                    progIdKey.SetValue("", "Sensor Readout remote connection", RegistryValueKind.String);
                    using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                    {
                        if (iconKey != null)
                        {
                            iconKey.SetValue("", QuoteArgument(targetExe) + ",0", RegistryValueKind.String);
                        }
                    }
                    using (var commandKey = progIdKey.CreateSubKey(@"shell\open\command"))
                    {
                        if (commandKey != null)
                        {
                            commandKey.SetValue("", QuoteArgument(targetExe) + " --import-remote-connection \"%1\"", RegistryValueKind.String);
                        }
                    }
                }
            }
            if (notifyShell)
            {
                NotifyShellAssociationChanged();
            }
        }
        catch
        {
        }
    }

    private static bool IsRemoteConnectionFileAssociationCurrent(
        string targetExe,
        string extensionKeyPath,
        string progId,
        string progIdKeyPath)
    {
        try
        {
            using (var extensionKey = Registry.CurrentUser.OpenSubKey(extensionKeyPath, false))
            using (var progIdKey = Registry.CurrentUser.OpenSubKey(progIdKeyPath, false))
            using (var iconKey = Registry.CurrentUser.OpenSubKey(progIdKeyPath + @"\DefaultIcon", false))
            using (var commandKey = Registry.CurrentUser.OpenSubKey(progIdKeyPath + @"\shell\open\command", false))
            {
                if (extensionKey == null || progIdKey == null || iconKey == null || commandKey == null)
                {
                    return false;
                }

                using (var openWithKey = extensionKey.OpenSubKey("OpenWithProgids", false))
                {
                    if (openWithKey == null || !openWithKey.GetValueNames().Any(name => string.Equals(name, progId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }
                }

                return string.Equals(Convert.ToString(extensionKey.GetValue("", "")), progId, StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(extensionKey.GetValue("Content Type", "")), RemoteConnectionContentType, StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(progIdKey.GetValue("", "")), "Sensor Readout remote connection", StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(iconKey.GetValue("", "")), QuoteArgument(targetExe) + ",0", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(commandKey.GetValue("", "")), QuoteArgument(targetExe) + " --import-remote-connection \"%1\"", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void UnregisterRemoteConnectionFileAssociation()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RemoteConnectionProgIdKeyPath, false);
            using (var extensionKey = Registry.CurrentUser.OpenSubKey(RemoteConnectionExtensionKeyPath, true))
            {
                if (extensionKey != null)
                {
                    if (string.Equals(Convert.ToString(extensionKey.GetValue("", "")), RemoteConnectionProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        extensionKey.DeleteValue("", false);
                    }
                    if (string.Equals(Convert.ToString(extensionKey.GetValue("Content Type", "")), RemoteConnectionContentType, StringComparison.OrdinalIgnoreCase))
                    {
                        extensionKey.DeleteValue("Content Type", false);
                    }
                    using (var openWithKey = extensionKey.OpenSubKey("OpenWithProgids", true))
                    {
                        if (openWithKey != null)
                        {
                            openWithKey.DeleteValue(RemoteConnectionProgId, false);
                        }
                    }
                }
            }
            RemoveEmptyRegistryKey(RemoteConnectionExtensionKeyPath + @"\OpenWithProgids");
            RemoveEmptyRegistryKey(RemoteConnectionExtensionKeyPath);
            NotifyShellAssociationChanged();
        }
        catch
        {
        }
    }

    private static void RemoveEmptyRegistryKey(string keyPath)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false))
        {
            if (key == null || key.ValueCount != 0 || key.SubKeyCount != 0)
            {
                return;
            }
        }
        Registry.CurrentUser.DeleteSubKey(keyPath, false);
    }

    private static void NotifyShellAssociationChanged()
    {
        try
        {
            SHChangeNotify(ShellAssociationChanged, ShellNotifyIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    private static void RegisterInstalledAppEntry(string targetExe, string installFolder)
    {
        RegisterInstalledAppEntry(targetExe, installFolder, UninstallRegistryKeyPath);
    }

    private static void RegisterInstalledAppEntry(string targetExe, string installFolder, string registryKeyPath)
    {
        if (string.IsNullOrWhiteSpace(targetExe) || string.IsNullOrWhiteSpace(installFolder))
        {
            return;
        }
        if (IsInstalledAppEntryCurrent(targetExe, installFolder, registryKeyPath))
        {
            return;
        }

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(registryKeyPath))
            {
                if (key == null)
                {
                    return;
                }

                key.SetValue("DisplayName", "Sensor Readout", RegistryValueKind.String);
                key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
                key.SetValue("Publisher", "Andre Louis", RegistryValueKind.String);
                key.SetValue("InstallLocation", installFolder, RegistryValueKind.String);
                key.SetValue("DisplayIcon", targetExe, RegistryValueKind.String);
                key.SetValue("UninstallString", QuoteArgument(targetExe) + " --uninstall", RegistryValueKind.String);
                key.SetValue("URLInfoAbout", ProjectUrl, RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", EstimateInstallSizeKb(installFolder), RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    private static bool IsInstalledAppEntryCurrent(string targetExe, string installFolder, string registryKeyPath)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(registryKeyPath, false))
            {
                return key != null &&
                    string.Equals(Convert.ToString(key.GetValue("DisplayName", "")), "Sensor Readout", StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(key.GetValue("DisplayVersion", "")), AppVersion, StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(key.GetValue("Publisher", "")), "Andre Louis", StringComparison.Ordinal) &&
                    string.Equals(Convert.ToString(key.GetValue("InstallLocation", "")), installFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(key.GetValue("DisplayIcon", "")), targetExe, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(key.GetValue("UninstallString", "")), QuoteArgument(targetExe) + " --uninstall", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(key.GetValue("URLInfoAbout", "")), ProjectUrl, StringComparison.OrdinalIgnoreCase) &&
                    Convert.ToInt32(key.GetValue("NoModify", 0)) == 1 &&
                    Convert.ToInt32(key.GetValue("NoRepair", 0)) == 1;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void UnregisterInstalledAppEntry()
    {
        UnregisterInstalledAppEntry(UninstallRegistryKeyPath);
    }

    private static void UnregisterInstalledAppEntry(string registryKeyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryKeyPath, false);
        }
        catch
        {
        }
    }

    private static int EstimateInstallSizeKb(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            long bytes = 0;
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                }
            }

            var kb = Math.Max(1, (bytes + 1023) / 1024);
            return kb > int.MaxValue ? int.MaxValue : (int)kb;
        }
        catch
        {
            return 0;
        }
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

        var shortcutFolder = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(shortcutFolder))
        {
            Directory.CreateDirectory(shortcutFolder);
        }

        CreateShortcut(shortcutPath, targetExe, "", workingDirectory, "Sensor Readout");
        if (!File.Exists(shortcutPath))
        {
            throw new IOException("The desktop shortcut was not created.");
        }
    }

    private static string GetDesktopShortcutPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "Sensor Readout.lnk");
    }

    private static void StartUninstallScript(string installFolder, int processId, bool deleteUserData)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "SensorReadout-Uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            "$pidToWait = " + processId + "\r\n" +
            "$target = " + PowerShellInstallQuote(installFolder) + "\r\n" +
            "$deleteUserData = $" + (deleteUserData ? "true" : "false") + "\r\n" +
            "$preserve = @('Config','Logs','Reports')\r\n" +
            "while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }\r\n" +
            "Remove-Item -LiteralPath 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Sensor Readout' -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "Remove-Item -LiteralPath 'HKCU:\\Software\\Classes\\SensorReadout.RemoteConnection' -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "if (Test-Path -LiteralPath $target) {\r\n" +
            "  if ($deleteUserData) {\r\n" +
            "    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "  } else {\r\n" +
            "    Get-ChildItem -LiteralPath $target -Force | Where-Object { $preserve -notcontains $_.Name } | ForEach-Object {\r\n" +
            "      Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "    }\r\n" +
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

    private static string PlainMnemonic(string value)
    {
        return (value ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
    }
}
