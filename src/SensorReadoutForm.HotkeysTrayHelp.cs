using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void RegisterGlobalHotKeys()
    {
        UnregisterGlobalHotKeys();
        registeredSpokenHotKeys.Clear();
        registeredFanProfileHotKeys.Clear();
        LogMessage("Debug", "Registering global hotkeys. Show/hide=" + (settings.ShowHideHotKey ?? "") + ", speak tray=" + (settings.SpeakTrayHotKey ?? "") + ", spoken profiles=" + ((settings.SpokenHotKeys == null) ? 0 : settings.SpokenHotKeys.Count) + ", fan profiles=" + ((settings.FanProfiles == null) ? 0 : settings.FanProfiles.Count) + ".");
        RegisterGlobalHotKey(settings.ShowHideHotKey, ShowHideHotKeyId, "show/hide");
        RegisterGlobalHotKey(settings.SpeakTrayHotKey, SpeakTrayHotKeyId, "speak tray status");
        var profiles = settings.SpokenHotKeys ?? new List<SpokenHotKeySetting>();
        for (var i = 0; i < profiles.Count && i < 100; i++)
        {
            var profile = profiles[i];
            if (profile == null || string.IsNullOrWhiteSpace(profile.HotKey))
            {
                continue;
            }

            var id = SpokenHotKeyBaseId + i;
            if (RegisterGlobalHotKey(profile.HotKey, id, "spoken hotkey " + (string.IsNullOrWhiteSpace(profile.Name) ? (i + 1).ToString(CultureInfo.InvariantCulture) : profile.Name)))
            {
                registeredSpokenHotKeys[id] = profile;
            }
        }

        var fanProfiles = settings.FanProfiles ?? new List<FanProfileSetting>();
        for (var i = 0; i < fanProfiles.Count && i < 100; i++)
        {
            var profile = fanProfiles[i];
            if (profile == null || string.IsNullOrWhiteSpace(profile.HotKey))
            {
                continue;
            }

            var id = FanProfileHotKeyBaseId + i;
            if (RegisterGlobalHotKey(profile.HotKey, id, "fan profile " + (string.IsNullOrWhiteSpace(profile.Name) ? (i + 1).ToString(CultureInfo.InvariantCulture) : profile.Name)))
            {
                registeredFanProfileHotKeys[id] = profile;
            }
        }
    }

    private bool RegisterGlobalHotKey(string hotKeyText, int id, string description)
    {
        var hotKey = ParseHotKey(hotKeyText);
        if (hotKey == null || !hotKey.IsValid)
        {
            if (!string.IsNullOrWhiteSpace(hotKeyText))
            {
                LogError("Invalid " + description + " hotkey setting: " + hotKeyText + ".");
            }
            return false;
        }

        var hotKeyHandle = hotKeyWindow == null ? Handle : hotKeyWindow.Handle;
        if (!NativeMethods.RegisterHotKey(hotKeyHandle, id, hotKey.Modifiers, (uint)hotKey.Key))
        {
            var error = Marshal.GetLastWin32Error();
            var message = "Could not register " + description + " hotkey " + NormalizeHotKeyText(hotKeyText) + ". Windows error " + error + ". It may already be in use.";
            statusLabel.Text = message;
            LogError(message);
            return false;
        }

        LogMessage("Normal", "Registered " + description + " hotkey " + NormalizeHotKeyText(hotKeyText) + ".");
        return true;
    }

    private void UnregisterGlobalHotKeys()
    {
        var hotKeyHandle = hotKeyWindow == null ? (IsHandleCreated ? Handle : IntPtr.Zero) : hotKeyWindow.Handle;
        if (hotKeyHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(hotKeyHandle, ShowHideHotKeyId);
        NativeMethods.UnregisterHotKey(hotKeyHandle, SpeakTrayHotKeyId);
        for (var id = SpokenHotKeyBaseId; id < SpokenHotKeyBaseId + 100; id++)
        {
            NativeMethods.UnregisterHotKey(hotKeyHandle, id);
        }
        for (var id = FanProfileHotKeyBaseId; id < FanProfileHotKeyBaseId + 100; id++)
        {
            NativeMethods.UnregisterHotKey(hotKeyHandle, id);
        }
        registeredSpokenHotKeys.Clear();
        registeredFanProfileHotKeys.Clear();
    }

    public bool HandleHotKeyMessage(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            var id = m.WParam.ToInt32();
            if (id == ShowHideHotKeyId)
            {
                LogMessage("Debug", "Show/hide hotkey pressed.");
                ToggleShowHide();
                return true;
            }

            if (id == SpeakTrayHotKeyId)
            {
                LogMessage("Debug", "Speak tray status hotkey pressed.");
                HandleTraySpeechHotKey(id);
                return true;
            }

            SpokenHotKeySetting profile;
            if (registeredSpokenHotKeys.TryGetValue(id, out profile))
            {
                LogMessage("Debug", "Spoken hotkey pressed: " + (profile == null ? "" : profile.Name));
                HandleSpokenHotKey(id, profile);
                return true;
            }

            FanProfileSetting fanProfile;
            if (registeredFanProfileHotKeys.TryGetValue(id, out fanProfile))
            {
                LogMessage("Debug", "Fan profile hotkey pressed: " + (fanProfile == null ? "" : fanProfile.Name));
                ApplyFanProfile(fanProfile, true);
                return true;
            }
        }

        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (HandleHotKeyMessage(ref m))
        {
            return;
        }

        base.WndProc(ref m);
    }

    private void ToggleShowHide()
    {
        if (Visible && WindowState != FormWindowState.Minimized && ShowInTaskbar)
        {
            LogMessage("Normal", "Hiding Sensor Readout from global hotkey.");
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            if (settings.TrayStatusEnabled)
            {
                trayIcon.Visible = true;
            }
            Hide();
            return;
        }

        LogMessage("Normal", "Showing Sensor Readout from global hotkey.");
        RestoreFromTray();
    }

    private void SpeakTrayStatus()
    {
        SpeakTextWithScreenReader(BuildCurrentSpeechStatusText(), "tray status");
    }

    private void SpeakSpokenHotKey(SpokenHotKeySetting profile)
    {
        var rows = GetSpokenHotKeyRows(profile);
        var text = BuildSpeechStatusText(rows);
        var description = profile == null || string.IsNullOrWhiteSpace(profile.Name) ? "spoken hotkey" : profile.Name.Trim();
        SpeakTextWithScreenReader(text, description);
    }

    private void HandleTraySpeechHotKey(int id)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = BuildCurrentSpeechStatusText();
        LogMessage("Debug", "Tray hotkey built speech text in " + stopwatch.ElapsedMilliseconds + " ms.");
        if (ShouldCopyFromDoublePress(id))
        {
            CopyHotKeyTextToClipboard(text, "tray status");
            LogMessage("Debug", "Tray hotkey copied in " + stopwatch.ElapsedMilliseconds + " ms.");
            return;
        }

        SpeakTextWithScreenReader(text, "tray status");
        LogMessage("Debug", "Tray hotkey completed in " + stopwatch.ElapsedMilliseconds + " ms.");
    }

    private void HandleSpokenHotKey(int id, SpokenHotKeySetting profile)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = GetSpokenHotKeyRows(profile);
        var rowResolveMs = stopwatch.ElapsedMilliseconds;
        var text = BuildSpeechStatusText(rows);
        var description = profile == null || string.IsNullOrWhiteSpace(profile.Name) ? "spoken hotkey" : profile.Name.Trim();
        LogMessage("Debug", "Spoken hotkey " + description + " resolved " + rows.Count + " row(s) and built text in " + rowResolveMs + " ms.");
        if (ShouldCopyFromDoublePress(id))
        {
            CopyHotKeyTextToClipboard(text, description);
            LogMessage("Debug", "Spoken hotkey " + description + " copied in " + stopwatch.ElapsedMilliseconds + " ms.");
            return;
        }

        SpeakTextWithScreenReader(text, description);
        LogMessage("Debug", "Spoken hotkey " + description + " completed in " + stopwatch.ElapsedMilliseconds + " ms.");
    }

    private bool ShouldCopyFromDoublePress(int id)
    {
        var timeout = EffectiveHotKeyCopyDoublePressMs();
        if (timeout <= 0)
        {
            lastSpeechHotKeyId = id;
            lastSpeechHotKeyPressedUtc = DateTime.UtcNow;
            return false;
        }

        var now = DateTime.UtcNow;
        var isDoublePress = id == lastSpeechHotKeyId && (now - lastSpeechHotKeyPressedUtc).TotalMilliseconds <= timeout;
        lastSpeechHotKeyId = id;
        lastSpeechHotKeyPressedUtc = now;
        return isDoublePress;
    }

    private int EffectiveHotKeyCopyDoublePressMs()
    {
        if (settings.HotKeyCopyDoublePressMs < 0)
        {
            return SystemInformation.DoubleClickTime;
        }

        return NormalizeHotKeyCopyDoublePressMs(settings.HotKeyCopyDoublePressMs);
    }

    private void CopyHotKeyTextToClipboard(string text, string description)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            statusLabel.Text = "Nothing to copy for " + description + ".";
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        try
        {
            Clipboard.SetText(text);
            statusLabel.Text = "Copied " + description + " to clipboard.";
            LogMessage("Normal", "Copied " + description + " to clipboard: " + text);
            AnnounceCopiedToClipboard();
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Could not copy " + description + " to clipboard.";
            LogError("Could not copy " + description + " to clipboard: " + ex.Message);
            System.Media.SystemSounds.Beep.Play();
        }
    }

    private void AnnounceCopiedToClipboard()
    {
        string error;
        var message = T("message.copiedToClipboard", "Copied to Clipboard.");
        if (ScreenReaderOutput.TrySpeak(message, out error))
        {
            LogMessage("Debug", "Announced clipboard copy with screen reader.");
        }
        else
        {
            LogMessage("Debug", "Clipboard copy announcement was not spoken. " + error);
        }
    }

    private void SpeakTextWithScreenReader(string text, string description)
    {
        SpeakTextWithScreenReader(text, description, false);
    }

    private void SpeakTextWithScreenReaderPolite(string text, string description)
    {
        SpeakTextWithScreenReader(text, description, true);
    }

    private void SpeakTextWithScreenReader(string text, string description, bool polite)
    {
        string error;
        var spoken = polite
            ? ScreenReaderOutput.TrySpeakPolite(text, out error)
            : ScreenReaderOutput.TrySpeak(text, out error);
        if (spoken)
        {
            if (statusLabel != null)
            {
                statusLabel.Text = "Spoke " + description + " with screen reader.";
            }
            LogMessage("Normal", "Spoke " + description + " with screen reader: " + text);
            return;
        }

        if (statusLabel != null)
        {
            statusLabel.Text = "Could not speak with screen reader. " + error;
        }
        LogError("Could not speak with screen reader. " + error);
        System.Media.SystemSounds.Beep.Play();
    }

    private void ScheduleStartupActiveMessage()
    {
        if (!settings.StartMinimizedToTray && !startMinimizedRequested)
        {
            return;
        }

        var startupSpeechTimer = new Timer { Interval = 1500 };
        startupSpeechTimer.Tick += delegate
        {
            startupSpeechTimer.Stop();
            startupSpeechTimer.Dispose();
            SpeakStartupActiveMessage();
        };
        startupSpeechTimer.Start();
    }

    private void SpeakStartupActiveMessage()
    {
        if (!settings.StartupSpeechEnabled)
        {
            LogMessage("Debug", "Startup active message is disabled.");
            return;
        }

        var message = string.IsNullOrWhiteSpace(settings.StartupSpeechMessage) ? DefaultStartupSpeechMessage() : settings.StartupSpeechMessage.Trim();
        string error;
        if (ScreenReaderOutput.TrySpeakPolite(message, out error))
        {
            LogMessage("Normal", "Spoke startup active message with screen reader: " + message);
        }
        else
        {
            LogMessage("Debug", "Startup active message was not spoken. " + error);
        }
    }

    private void MinimizeToTray()
    {
        minimizingToTray = true;
        BeginInvoke((MethodInvoker)delegate
        {
            try
            {
                if (WindowState == FormWindowState.Minimized && settings.TrayStatusEnabled)
                {
                    trayIcon.Visible = true;
                    ShowInTaskbar = false;
                    Hide();
                }
            }
            finally
            {
                minimizingToTray = false;
            }
        });
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        UpdateDeviceList();
        UpdateReadingList();
        UpdateFanControlBox();
        Activate();
    }

    private bool IsMinimizedOrHidden()
    {
        return !Visible || WindowState == FormWindowState.Minimized || !ShowInTaskbar;
    }

    private void ShowManual()
    {
        var manualPath = ResolveManualPath(ManualFileName());
        if (!System.IO.File.Exists(manualPath))
        {
            manualPath = ResolveManualPath("README-en.html");
        }
        if (!System.IO.File.Exists(manualPath))
        {
            manualPath = ResolveManualPath("README.md");
        }

        if (!System.IO.File.Exists(manualPath))
        {
            MessageBox.Show(this, T("message.manualNotFound", "The manual could not be found beside Sensor Readout."), T("ui.Sensor Readout manual", "Sensor Readout manual"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = manualPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not open manual", "Could not open manual"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ManualFileName()
    {
        var configured = T("manual.file", "");
        if (!string.IsNullOrWhiteSpace(SanitizeManualFileName(configured)))
        {
            return configured;
        }

        var languageFile = activeLanguage == null ? "" : activeLanguage.FileName;
        languageFile = System.IO.Path.GetFileNameWithoutExtension(languageFile);
        return string.IsNullOrWhiteSpace(languageFile) ? "README-en.html" : languageFile + ".html";
    }

    private static string ResolveManualPath(string fileName)
    {
        fileName = SanitizeManualFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "README-en.html";
        }

        var path = System.IO.Path.Combine(GetDocsFolderPath(), fileName);
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Docs", fileName));
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", fileName));
    }

    private static string SanitizeManualFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        fileName = System.IO.Path.GetFileName(fileName.Trim());
        return fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : "";
    }

    private void BuildHelpMenu()
    {
        if (helpMenu == null)
        {
            return;
        }

        helpMenu.DropDownItems.Clear();
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Manual", Keys.F1, delegate { ShowManual(); }));
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Check for updates...", Keys.Shift | Keys.F1, delegate { CheckForUpdates(); }));
        helpMenu.DropDownItems.Add("Version &history...", null, delegate { ShowVersionHistoryDialog(); });
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Project on GitHub", Keys.Control | Keys.F1, delegate { OpenProjectPage(); }));
        helpMenu.DropDownItems.Add("Con&tact", null, delegate { OpenContactPage(); });
        helpMenu.DropDownItems.Add("&Donate", null, delegate { OpenDonatePage(); });
        helpMenu.DropDownItems.Add("Install Core Temp &support...", null, delegate { ShowCoreTempSupportOptions(); });
        helpMenu.DropDownItems.Add("&Install prerequisites...", null, delegate { RunPrerequisiteInstaller(); });
        AddPlugInHelpMenuItems(helpMenu);
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("Run &diagnostics...", Keys.Alt | Keys.F1, delegate { RunDiagnostics(); }));
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add("&About Sensor Readout", null, delegate { ShowAbout(); });
    }

    private void AddPlugInHelpMenuItems(ToolStripMenuItem menu)
    {
        var links = LoadEnabledPlugInHelpLinks();
        if (links.Count == 0)
        {
            return;
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var helpLink in links)
        {
            var link = helpLink;
            menu.DropDownItems.Add(link.Label, null, delegate { OpenPlugInHelpLink(link); });
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
    }

    private void OpenPlugInHelpLink(PlugInHelpLink link)
    {
        if (link == null || string.IsNullOrWhiteSpace(link.Url))
        {
            return;
        }

        OpenExternalPage(link.Url, "Could not open " + (string.IsNullOrWhiteSpace(link.Label) ? "Plug-In help page" : link.Label.Replace("&", "")));
    }

    private void ShowAbout()
    {
        using (var dialog = new Form())
        {
            dialog.Text = "About Sensor Readout";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.Size = new Size(560, 330);
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

            var text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                AccessibleName = "About Sensor Readout text",
                Text =
                    "Sensor Readout " + AppVersion + Environment.NewLine + Environment.NewLine +
                    "Project page:" + Environment.NewLine +
                    ProjectUrl + Environment.NewLine + Environment.NewLine +
                    "Created by Codex." + Environment.NewLine +
                    "Ideas by Andre Louis." + Environment.NewLine + Environment.NewLine +
                    "Bundled and referenced components:" + Environment.NewLine +
                    "LibreHardwareMonitorLib, Newtonsoft.Json, PawnIO, HidSharp, DiskInfoToolkit, RAMSPDToolkit, BlackSharp.Core, Tolk screen reader library, usb.ids, and Microsoft .NET Framework support libraries."
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var projectButton = new Button { Text = "Project page", AutoSize = true, AccessibleName = "Open project page" };
            projectButton.Click += delegate { OpenProjectPage(); };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(projectButton);

            dialog.AcceptButton = okButton;
            dialog.CancelButton = okButton;
            layout.Controls.Add(text, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
    }

    private void OpenProjectPage()
    {
        OpenExternalPage(ProjectUrl, "Could not open project page");
    }

    private void OpenDonatePage()
    {
        OpenExternalPage("https://www.paypal.me/AndreLouis", T("ui.Could not open donate page", "Could not open donate page"));
    }

    private void OpenContactPage()
    {
        OpenExternalPage("https://onj.me/contact", T("ui.Could not open contact page", "Could not open contact page"));
    }

    private void OpenExternalPage(string url, string errorTitle)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, errorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTrayStatus()
    {
        if (trayIcon == null)
        {
            return;
        }

        trayIcon.Visible = settings.TrayStatusEnabled;
        if (!settings.TrayStatusEnabled)
        {
            return;
        }

        var selectedRows = GetTrayStatusRows();
        var text = BuildTrayStatusText(selectedRows);
        currentTrayStatusText = text;
        trayIcon.Text = ShortenTrayText(text, selectedRows.Count > 1);
        SetTrayIcon(selectedRows.FirstOrDefault());
    }

    private string BuildCurrentSpeechStatusText()
    {
        return BuildSpeechStatusText(GetTrayStatusRows());
    }

    private List<SensorRow> GetTrayStatusRows()
    {
        var selectedKeys = settings.TrayItemKeys ?? new List<string>();
        var selectedRows = selectedKeys
            .Select(FindTrayRowByKey)
            .Where(r => r != null)
            .Take(MaxTrayStatusReadings)
            .ToList();

        if (selectedRows.Count == 0)
        {
            selectedRows = latestRows
                .Where(r => r.Type == "Temperature")
                .OrderBy(r => ShortHardwareName(r.Hardware))
                .ThenBy(r => CleanSensorName(r.Name))
                .Take(2)
                .ToList();
        }

        return selectedRows;
    }

    private List<SensorRow> GetSpokenHotKeyRows(SpokenHotKeySetting profile)
    {
        var selectedKeys = profile == null || profile.ReadingKeys == null ? new List<string>() : profile.ReadingKeys;
        var rows = new List<SensorRow>();
        foreach (var key in selectedKeys)
        {
            var row = FindTrayRowByKey(key);
            if (row == null)
            {
                LogMessage("Debug", "Spoken hotkey " + (profile == null ? "" : profile.Name ?? "") + " missing row key: " + key);
                continue;
            }

            rows.Add(row);
        }

        return rows;
    }

    private SensorRow FindTrayRowByKey(string key)
    {
        return FindTrayRowByKey(key, true);
    }

    private SensorRow FindTrayRowByKey(string key, bool promoteLabels)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var exact = latestRows.FirstOrDefault(r => RowSettingsKey(r) == key);
        if (exact != null)
        {
            return exact;
        }

        var parts = key.Split('|');
        if (parts.Length < 3)
        {
            return null;
        }

        var type = parts[0];
        var hardware = parts[1];
        var name = parts[2];
        var identifier = parts.Length > 3 ? parts[3] : "";
        var normalizedHardware = NormalizeHardwareName(hardware);
        var cleanName = CleanSensorName(name);
        if (string.Equals(type, "SMART", StringComparison.OrdinalIgnoreCase) && IsStoragePerformanceName(cleanName))
        {
            var driveLetter = DriveLetterFromSpeechLabel(key);
            if (string.IsNullOrWhiteSpace(driveLetter))
            {
                driveLetter = DriveLetterFromRelatedSpeechLabels(hardware);
            }

            if (!string.IsNullOrWhiteSpace(driveLetter))
            {
                var driveRow = latestRows.FirstOrDefault(r =>
                    string.Equals(r.Type ?? "", "Performance", StringComparison.OrdinalIgnoreCase) &&
                    (r.Hardware ?? "").StartsWith(driveLetter + ":", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanSensorName(r.Name), cleanName, StringComparison.OrdinalIgnoreCase));
                if (driveRow != null)
                {
                    if (promoteLabels)
                    {
                        PromoteSpeechLabelToResolvedRow(key, driveRow);
                    }
                    return driveRow;
                }
            }

            var storagePerformance = latestRows.FirstOrDefault(r =>
                string.Equals(r.Type ?? "", "Performance", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeHardwareName(r.Hardware), normalizedHardware, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(identifier) || string.Equals(r.Identifier ?? "", identifier, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(CleanSensorName(r.Name), cleanName, StringComparison.OrdinalIgnoreCase));
            if (storagePerformance != null)
            {
                if (promoteLabels)
                {
                    PromoteSpeechLabelToResolvedRow(key, storagePerformance);
                }
                return storagePerformance;
            }

            storagePerformance = latestRows.FirstOrDefault(r =>
                string.Equals(r.Type ?? "", "Performance", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeHardwareName(r.Hardware), normalizedHardware, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(CleanSensorName(r.Name), cleanName, StringComparison.OrdinalIgnoreCase));
            if (storagePerformance != null)
            {
                if (promoteLabels)
                {
                    PromoteSpeechLabelToResolvedRow(key, storagePerformance);
                }
                return storagePerformance;
            }
        }

        return latestRows.FirstOrDefault(r =>
            string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeHardwareName(r.Hardware), normalizedHardware, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(identifier) || string.Equals(r.Identifier ?? "", identifier, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(CleanSensorName(r.Name), cleanName, StringComparison.OrdinalIgnoreCase));
    }

    private void PromoteSpeechLabelToResolvedRow(string oldKey, SensorRow resolvedRow)
    {
        if (settings.ReadingSpeechLabels == null || string.IsNullOrWhiteSpace(oldKey) || resolvedRow == null)
        {
            return;
        }

        string label;
        if (!settings.ReadingSpeechLabels.TryGetValue(oldKey, out label) || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var newKey = RowSettingsKey(resolvedRow);
        if (string.IsNullOrWhiteSpace(newKey) || string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string existing;
        if (settings.ReadingSpeechLabels.TryGetValue(newKey, out existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return;
        }

        settings.ReadingSpeechLabels[newKey] = label.Trim();
        SaveSettings(settings);
        LogMessage("Debug", "Migrated spoken label from " + oldKey + " to " + newKey + ".");
    }

    private void MigrateLegacyStoragePerformanceSettings()
    {
        if (latestRows.Count == 0)
        {
            return;
        }

        var changed = false;
        if (settings.TrayItemKeys != null)
        {
            changed |= MigrateLegacyStorageKeyList(settings.TrayItemKeys);
        }

        if (settings.ReadingSpeechLabels != null)
        {
            foreach (var key in settings.ReadingSpeechLabels.Keys.ToList())
            {
                if (WouldAddMigratedSpeechLabel(key))
                {
                    MigratedLegacyStorageKey(key);
                    changed = true;
                }
            }
        }

        if (settings.SpokenHotKeys != null)
        {
            foreach (var profile in settings.SpokenHotKeys.Where(p => p != null && p.ReadingKeys != null))
            {
                changed |= MigrateLegacyStorageKeyList(profile.ReadingKeys);
            }
        }

        if (settings.Alarms != null)
        {
            foreach (var alarm in settings.Alarms.Where(a => a != null && !string.IsNullOrWhiteSpace(a.ReadingKey)))
            {
                var migrated = MigratedLegacyStorageKey(alarm.ReadingKey);
                if (!string.IsNullOrWhiteSpace(migrated) && !string.Equals(migrated, alarm.ReadingKey, StringComparison.OrdinalIgnoreCase))
                {
                    alarm.ReadingKey = migrated;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            SaveSettings(settings);
            LogMessage("Debug", "Migrated legacy storage performance setting keys to live logical disk rows.");
        }
    }

    private bool MigrateLegacyStorageKeyList(List<string> keys)
    {
        var changed = false;
        for (var i = 0; i < keys.Count; i++)
        {
            var migrated = MigratedLegacyStorageKey(keys[i]);
            if (!string.IsNullOrWhiteSpace(migrated) && !string.Equals(migrated, keys[i], StringComparison.OrdinalIgnoreCase))
            {
                keys[i] = migrated;
                changed = true;
            }
        }

        return changed;
    }

    private string MigratedLegacyStorageKey(string key)
    {
        if (!IsLegacyStoragePerformanceKey(key))
        {
            return key;
        }

        var row = FindTrayRowByKey(key);
        if (row == null)
        {
            return key;
        }

        var migrated = RowSettingsKey(row);
        return string.IsNullOrWhiteSpace(migrated) ? key : migrated;
    }

    private bool WouldAddMigratedSpeechLabel(string key)
    {
        if (!IsLegacyStoragePerformanceKey(key) || settings.ReadingSpeechLabels == null)
        {
            return false;
        }

        var row = FindTrayRowByKey(key, false);
        if (row == null)
        {
            return false;
        }

        var migrated = RowSettingsKey(row);
        if (string.IsNullOrWhiteSpace(migrated) || string.Equals(migrated, key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string existing;
        return !settings.ReadingSpeechLabels.TryGetValue(migrated, out existing) || string.IsNullOrWhiteSpace(existing);
    }

    private static bool IsLegacyStoragePerformanceKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split('|');
        if (parts.Length < 3 || !string.Equals(parts[0], "SMART", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = CleanSensorName(parts[2]);
        return name.Equals("Read rate", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Write rate", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Read Rate", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Write Rate", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Space used", StringComparison.OrdinalIgnoreCase);
    }

    private string DriveLetterFromSpeechLabel(string key)
    {
        string label;
        if (settings.ReadingSpeechLabels == null ||
            !settings.ReadingSpeechLabels.TryGetValue(key, out label) ||
            string.IsNullOrWhiteSpace(label))
        {
            return "";
        }

        label = label.Trim();
        return label.Length >= 2 && char.IsLetter(label[0]) && label[1] == ':'
            ? label.Substring(0, 1).ToUpperInvariant()
            : "";
    }

    private string DriveLetterFromRelatedSpeechLabels(string hardware)
    {
        if (settings.ReadingSpeechLabels == null || string.IsNullOrWhiteSpace(hardware))
        {
            return "";
        }

        var normalizedHardware = NormalizeHardwareName(hardware);
        foreach (var item in settings.ReadingSpeechLabels)
        {
            var parts = item.Key.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            if (!string.Equals(parts[1], hardware, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(NormalizeHardwareName(parts[1]), normalizedHardware, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = item.Value == null ? "" : item.Value.Trim();
            if (label.Length >= 2 && char.IsLetter(label[0]) && label[1] == ':')
            {
                return label.Substring(0, 1).ToUpperInvariant();
            }
        }

        return "";
    }

    private static string BuildTrayStatusText(List<SensorRow> selectedRows)
    {
        return selectedRows == null || selectedRows.Count == 0
            ? "Sensor Readout"
            : string.Join("; ", selectedRows.Select(ShortTrayReadingText).ToArray());
    }

    private string BuildSpeechStatusText(List<SensorRow> selectedRows)
    {
        if (selectedRows == null || selectedRows.Count == 0)
        {
            return T("speech.dataNotReady", "Sensor data is not ready yet. Please wait.");
        }

        var items = selectedRows.Select(r => ShortSpeechReadingText(r, settings.SpeechIncludesDeviceNames, settings.ReadingSpeechLabels)).ToList();
        return settings.SpeechIncludesDeviceNames
            ? string.Join("; ", items.ToArray())
            : CompactRepeatedSpeechLabels(items);
    }

    private static string ShortSpeechReadingText(SensorRow row, bool includeHardwareName, Dictionary<string, string> speechLabels)
    {
        if (row == null)
        {
            return "";
        }

        string custom;
        var key = RowSettingsKey(row);
        if (speechLabels != null && speechLabels.TryGetValue(key, out custom) && !string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim() + " " + FormatValue(row);
        }

        return ShortTrayReadingText(row, includeHardwareName);
    }

    private static string CompactRepeatedSpeechLabels(List<string> items)
    {
        if (items == null || items.Count < 2)
        {
            return items == null || items.Count == 0 ? T("speech.dataNotReady", "Sensor data is not ready yet. Please wait.") : items[0];
        }

        var labels = items.Select(LeadingSpeechLabel).ToList();
        var label = labels.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(label) || labels.Any(i => !string.Equals(i, label, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Join("; ", items.ToArray());
        }

        var values = items.Select(i => RemoveLeadingSpeechLabel(i, label)).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        return values.Length == items.Count ? label + ": " + string.Join("; ", values) : string.Join("; ", items.ToArray());
    }

    private static string LeadingSpeechLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var trimmed = text.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace <= 0 ? "" : trimmed.Substring(0, firstSpace);
    }

    private static string RemoveLeadingSpeechLabel(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label))
        {
            return text ?? "";
        }

        var trimmed = text.Trim();
        return trimmed.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(label.Length + 1).Trim()
            : trimmed;
    }

    private static string ShortenTrayText(string text, bool showEllipsis)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Sensor Readout";
        }

        if (text.Length <= 63)
        {
            return text;
        }

        return showEllipsis ? text.Substring(0, 60) + "..." : text.Substring(0, 63);
    }

    private void SetTrayIcon(SensorRow row)
    {
        var oldIcon = trayStatusIcon;
        try
        {
            trayStatusIcon = CreateTrayIcon(row);
            trayIcon.Icon = trayStatusIcon;
        }
        catch (Exception ex)
        {
            LogError("Could not create tray status icon; using fallback icon. " + ex.Message);
            trayStatusIcon = (Icon)SystemIcons.Application.Clone();
            trayIcon.Icon = trayStatusIcon;
        }

        if (oldIcon != null)
        {
            oldIcon.Dispose();
        }
    }

    private static Icon CreateTrayIcon(SensorRow row)
    {
        using (var bitmap = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            var color = TrayColor(row);
            using (var background = new SolidBrush(color))
            using (var border = new Pen(Color.White))
            {
                graphics.FillEllipse(background, 0, 0, 15, 15);
                graphics.DrawEllipse(border, 0, 0, 15, 15);
            }

            var text = TrayIconText(row);
            using (var font = new Font("Segoe UI", text.Length > 2 ? 5.5f : 6.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 1, 16, 14), format);
            }

            return IconFromBitmap(bitmap);
        }
    }

    private static Icon IconFromBitmap(Bitmap bitmap)
    {
        if (bitmap == null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = bitmap.GetHicon();
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    private static Color TrayColor(SensorRow row)
    {
        if (row == null)
        {
            return Color.DimGray;
        }

        if (row.Type == "Temperature")
        {
            var value = row.Value.GetValueOrDefault();
            if (value >= 80) return Color.FromArgb(180, 40, 35);
            if (value >= 65) return Color.FromArgb(210, 116, 0);
            return Color.FromArgb(25, 120, 75);
        }

        if (row.Type == "Fan")
        {
            return Color.FromArgb(40, 95, 170);
        }

        if (row.Type == "SMART")
        {
            return Color.FromArgb(105, 80, 155);
        }

        return Color.DimGray;
    }

    private static string TrayIconText(SensorRow row)
    {
        if (row == null || !row.Value.HasValue)
        {
            return "SR";
        }

        if (row.Type == "Temperature")
        {
            var unit = NormalizeTemperatureUnit(activeTemperatureUnit);
            var value = unit == "F" || unit == "FC"
                ? (row.Value.Value * 9.0 / 5.0) + 32.0
                : row.Value.Value;
            return FormatNumber(Math.Round(value, 0), "0");
        }

        if (row.Type == "Fan")
        {
            var rpm = row.Value.Value;
            return rpm >= 1000 ? FormatNumber(Math.Round(rpm / 1000, 1), "0.#") + "k" : FormatNumber(Math.Round(rpm, 0), "0");
        }

        var display = FormatNumber(Math.Round(row.Value.Value, 0), "0");
        return display.Length <= 3 ? display : display.Substring(0, 3);
    }

}
