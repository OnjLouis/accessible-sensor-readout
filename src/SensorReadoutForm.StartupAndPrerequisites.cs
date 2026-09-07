using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed partial class SensorReadoutForm : Form
{
    private const string CoreTempUrl = "https://www.alcpu.com/CoreTemp/";
    private const string CoreTempWingetId = "ALCPU.CoreTemp";
    private const string CoreTempChocolateyId = "coretemp";
    private const string StartupTaskName = "Sensor Readout";

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
        var installerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install_Scripts", "Install-Prerequisites.cmd");
        if (!System.IO.File.Exists(installerPath))
        {
            installerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install-Prerequisites.cmd");
        }

        if (!System.IO.File.Exists(installerPath))
        {
            MessageBox.Show(this, "Install-Prerequisites.cmd could not be found in the Install_Scripts folder.", "Sensor Readout prerequisites", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        var arguments = startMinimized ? "--minimized" : "";
        if (enabled && IsStartupScheduledTaskCurrent(targetPath, arguments, workingDirectory) && !HasLegacyStartupRegistration(shortcutPath))
        {
            return;
        }

        string taskXml;
        var ownsTask = TryGetStartupScheduledTaskXml(out taskXml) && StartupTaskTargetsExecutable(taskXml, targetPath, false);
        var ownsRun = StartupCommandTargetsExecutable(ReadStartupRunCommand(), targetPath);
        var ownsShortcut = StartupCommandTargetsExecutable(ReadStartupShortcutTarget(shortcutPath), targetPath);
        var changes = PlanStartupRegistrationChange(enabled, ownsTask, ownsRun, ownsShortcut);
        ApplyStartupRegistrationChange(changes,
            delegate { CreateStartupScheduledTask(targetPath, arguments, workingDirectory); },
            DeleteStartupScheduledTask,
            DeleteStartupRunKey,
            delegate
            {
                if (System.IO.File.Exists(shortcutPath)) System.IO.File.Delete(shortcutPath);
                SetStartupApprovedState(shortcutPath, false);
            });
    }

    [Flags]
    private enum StartupRegistrationChange
    {
        None = 0,
        CreateTask = 1,
        DeleteTask = 2,
        DeleteRun = 4,
        DeleteShortcut = 8
    }

    private static StartupRegistrationChange PlanStartupRegistrationChange(bool enabled, bool ownsTask, bool ownsRun, bool ownsShortcut)
    {
        // An explicit opt-in may replace another copy. Disabling or uninstalling may only remove this copy.
        if (enabled) return StartupRegistrationChange.CreateTask | StartupRegistrationChange.DeleteRun | StartupRegistrationChange.DeleteShortcut;
        return (ownsTask ? StartupRegistrationChange.DeleteTask : StartupRegistrationChange.None) |
            (ownsRun ? StartupRegistrationChange.DeleteRun : StartupRegistrationChange.None) |
            (ownsShortcut ? StartupRegistrationChange.DeleteShortcut : StartupRegistrationChange.None);
    }

    private static void ApplyStartupRegistrationChange(StartupRegistrationChange changes, Action createTask, Action deleteTask, Action deleteRun, Action deleteShortcut)
    {
        // Register in place first: failed creation must leave the previous startup registration intact.
        if ((changes & StartupRegistrationChange.CreateTask) != 0) createTask();
        if ((changes & StartupRegistrationChange.DeleteTask) != 0) deleteTask();
        if ((changes & StartupRegistrationChange.DeleteRun) != 0) deleteRun();
        if ((changes & StartupRegistrationChange.DeleteShortcut) != 0) deleteShortcut();
    }

    internal static bool IsThisCopyRegisteredForStartup()
    {
        var target = Application.ExecutablePath;
        string xml;
        if (TryGetStartupScheduledTaskXml(out xml) && StartupTaskTargetsExecutable(xml, target, true)) return true;
        if (StartupCommandTargetsExecutable(ReadStartupRunCommand(), target) && IsStartupApprovalEnabled("Run", "Sensor Readout")) return true;
        var shortcut = GetStartupShortcutPath();
        return StartupCommandTargetsExecutable(ReadStartupShortcutTarget(shortcut), target) &&
            IsStartupApprovalEnabled("StartupFolder", System.IO.Path.GetFileName(shortcut));
    }

    private static bool StartupTaskTargetsExecutable(string xml, string targetPath, bool requireEnabled)
    {
        try
        {
            var document = new System.Xml.XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            var namespaces = new System.Xml.XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("task", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            var actions = document.SelectNodes("/task:Task/task:Actions/*", namespaces);
            if (actions == null || actions.Count != 1) return false;
            var command = StartupTaskNodeText(document, namespaces, "/task:Task/task:Actions/task:Exec/task:Command");
            if (string.IsNullOrWhiteSpace(targetPath) || !string.Equals(NormalizeStartupPath(command), NormalizeStartupPath(targetPath), StringComparison.OrdinalIgnoreCase)) return false;
            if (!requireEnabled) return true;
            return document.SelectSingleNode("/task:Task/task:Triggers/task:LogonTrigger", namespaces) != null &&
                !string.Equals(StartupTaskNodeText(document, namespaces, "/task:Task/task:Triggers/task:LogonTrigger/task:Enabled"), "false", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(StartupTaskNodeText(document, namespaces, "/task:Task/task:Settings/task:Enabled"), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool StartupCommandTargetsExecutable(string command, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(targetPath)) return false;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        var target = NormalizeStartupPath(targetPath);
        var prefix = command.StartsWith("\"", StringComparison.Ordinal) ? "\"" + target + "\"" : target;
        return command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (command.Length == prefix.Length || char.IsWhiteSpace(command[prefix.Length]));
    }

    private static string ReadStartupRunCommand()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                return key == null ? "" : key.GetValue("Sensor Readout") as string ?? "";
        }
        catch { return ""; }
    }

    private static string ReadStartupShortcutTarget(string path)
    {
        if (!System.IO.File.Exists(path)) return "";
        object shell = null;
        object shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return "";
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            return Convert.ToString(shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null));
        }
        catch { return ""; }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool IsStartupApprovalEnabled(string subkey, string name)
    {
        const byte DisabledStartupItem = 3;
        const byte DisabledStartupItemAlternative = 7;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + subkey, false))
            {
                var state = key == null ? null : key.GetValue(name) as byte[];
                return state == null || state.Length == 0 || (state[0] != DisabledStartupItem && state[0] != DisabledStartupItemAlternative);
            }
        }
        catch { return false; }
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

    private static void CreateStartupScheduledTask(string targetPath, string arguments, string workingDirectory)
    {
        var taskXmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SensorReadout-StartupTask-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            System.IO.File.WriteAllText(
                taskXmlPath,
                BuildStartupTaskXml(targetPath, arguments, workingDirectory),
                System.Text.Encoding.Unicode);

            string output;
            var exitCode = RunHiddenProcess(
                "schtasks.exe",
                "/Create /TN \"" + StartupTaskName + "\" /XML \"" + taskXmlPath + "\" /F",
                out output);
            if (exitCode != 0)
            {
                throw new InvalidOperationException("Could not create Windows startup task. " + output.Trim());
            }
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(taskXmlPath))
                {
                    System.IO.File.Delete(taskXmlPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string BuildStartupTaskXml(string targetPath, string arguments, string workingDirectory)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo>\r\n" +
            "    <Date>" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "</Date>\r\n" +
            "    <Author>" + XmlEscape(user) + "</Author>\r\n" +
            "    <URI>\\Sensor Readout</URI>\r\n" +
            "  </RegistrationInfo>\r\n" +
            "  <Triggers>\r\n" +
            "    <LogonTrigger>\r\n" +
            "      <Enabled>true</Enabled>\r\n" +
            "    </LogonTrigger>\r\n" +
            "  </Triggers>\r\n" +
            "  <Principals>\r\n" +
            "    <Principal id=\"Author\">\r\n" +
            "      <UserId>" + XmlEscape(user) + "</UserId>\r\n" +
            "      <LogonType>InteractiveToken</LogonType>\r\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
            "    </Principal>\r\n" +
            "  </Principals>\r\n" +
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
            "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
            "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
            "    <Enabled>true</Enabled>\r\n" +
            "    <Hidden>false</Hidden>\r\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
            "    <WakeToRun>false</WakeToRun>\r\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
            "    <Priority>7</Priority>\r\n" +
            "  </Settings>\r\n" +
            "  <Actions Context=\"Author\">\r\n" +
            "    <Exec>\r\n" +
            "      <Command>" + XmlEscape(targetPath) + "</Command>\r\n" +
            (string.IsNullOrWhiteSpace(arguments) ? "" : "      <Arguments>" + XmlEscape(arguments.Trim()) + "</Arguments>\r\n") +
            "      <WorkingDirectory>" + XmlEscape((workingDirectory ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) + "</WorkingDirectory>\r\n" +
            "    </Exec>\r\n" +
            "  </Actions>\r\n" +
            "</Task>\r\n";
    }

    private static bool IsStartupScheduledTaskCurrent(string targetPath, string arguments, string workingDirectory)
    {
        string xml;
        return TryGetStartupScheduledTaskXml(out xml) && StartupTaskXmlMatches(xml, targetPath, arguments, workingDirectory);
    }

    private static bool TryGetStartupScheduledTaskXml(out string xml)
    {
        return RunHiddenProcess("schtasks.exe", "/Query /TN \"" + StartupTaskName + "\" /XML", out xml) == 0 &&
            !string.IsNullOrWhiteSpace(xml);
    }

    private static bool StartupTaskXmlMatches(string xml, string targetPath, string arguments, string workingDirectory)
    {
        if (!StartupTaskTargetsExecutable(xml, targetPath, true)) return false;
        try
        {
            var document = new System.Xml.XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            var namespaces = new System.Xml.XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("task", "http://schemas.microsoft.com/windows/2004/02/mit/task");

            var command = StartupTaskNodeText(document, namespaces, "/task:Task/task:Actions/task:Exec/task:Command");
            var actualArguments = StartupTaskNodeText(document, namespaces, "/task:Task/task:Actions/task:Exec/task:Arguments");
            var actualWorkingDirectory = StartupTaskNodeText(document, namespaces, "/task:Task/task:Actions/task:Exec/task:WorkingDirectory");
            var triggerEnabled = StartupTaskNodeText(document, namespaces, "/task:Task/task:Triggers/task:LogonTrigger/task:Enabled");
            var taskEnabled = StartupTaskNodeText(document, namespaces, "/task:Task/task:Settings/task:Enabled");

            return string.Equals(NormalizeStartupPath(command), NormalizeStartupPath(targetPath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals((actualArguments ?? "").Trim(), (arguments ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeStartupPath(actualWorkingDirectory), NormalizeStartupPath(workingDirectory), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(triggerEnabled, "false", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(taskEnabled, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string StartupTaskNodeText(System.Xml.XmlDocument document, System.Xml.XmlNamespaceManager namespaces, string xpath)
    {
        var node = document.SelectSingleNode(xpath, namespaces);
        return node == null ? "" : node.InnerText;
    }

    private static string NormalizeStartupPath(string path)
    {
        return Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"')).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private static bool HasLegacyStartupRegistration(string shortcutPath)
    {
        if (System.IO.File.Exists(shortcutPath))
        {
            return true;
        }

        return RegistryValueExists(@"Software\Microsoft\Windows\CurrentVersion\Run", "Sensor Readout") ||
            RegistryValueExists(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Sensor Readout");
    }

    private static bool RegistryValueExists(string keyPath, string valueName)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false))
            {
                return key != null && key.GetValueNames().Any(name => string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            return false;
        }
    }

    private static string XmlEscape(string text)
    {
        return System.Security.SecurityElement.Escape(text ?? "") ?? "";
    }

    private static void DeleteStartupScheduledTask()
    {
        string output;
        if (RunHiddenProcess("schtasks.exe", "/Delete /TN \"" + StartupTaskName + "\" /F", out output) != 0)
        {
            throw new InvalidOperationException("Could not remove Windows startup task. " + output.Trim());
        }
    }

    private static void DeleteStartupRunKey()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    key.DeleteValue("Sensor Readout", false);
                }
            }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true))
            {
                if (key != null)
                {
                    key.DeleteValue("Sensor Readout", false);
                }
            }
        }
        catch
        {
        }
    }

    private static int RunHiddenProcess(string fileName, string arguments, out string output)
    {
        try
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process.Start();
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    private static string GetStartupShortcutPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Sensor Readout.lnk");
    }

}
