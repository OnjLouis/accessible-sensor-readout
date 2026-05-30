using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private const string CommunityStatsUploadUrl = "https://onj.me/srstats/submit.php";
    private const string CommunityStatsPageUrl = "https://onj.me/srstats/";

    private void ShowCommunityStatsDialog()
    {
        if (reportViewMode)
        {
            MessageBox.Show(this,
                T("message.Community stats live only", "Community stats can only be shared from live readings, not from an opened report."),
                T("ui.Share anonymous community stats", "Share anonymous community stats"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var generatingMessage = T("status.Generating community stats payload.", "Generating community stats payload.");
        statusLabel.Text = generatingMessage;
        SpeakCommunityStatsStatus(generatingMessage);
        PlayDiagnosticSound(settings.DiagnosticsStartSoundFile, true, true);
        SetLatestRows(CollectCommunityStatsRows());
        var payload = BuildCommunityStatsPayload();
        var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        var intro = T("message.Community stats preview intro", "Review the exact anonymous community stats payload below. Sensor Readout will only upload this small allow-listed payload if you press Upload. It does not include your computer name, username, serial numbers, MAC or IP addresses, paths, drive labels, device IDs, PnP IDs, raw details, installed programs, program usage, or full report rows.");

        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Share anonymous community stats", "Share anonymous community stats");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new System.Drawing.Size(820, 620);
            dialog.MinimumSize = new System.Drawing.Size(560, 420);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var introBox = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = dialog.BackColor,
                Text = intro,
                Height = 86,
                AccessibleName = T("a11y.Community stats privacy summary", "Community stats privacy summary")
            };

            var previewBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = json,
                AccessibleName = T("a11y.Community stats payload preview", "Community stats payload preview")
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };
            var closeButton = CreateCloseButton();
            var submitButton = new Button { Text = T("ui.&Upload", "&Upload"), AutoSize = true };
            var copyButton = new Button { Text = T("ui.&Copy", "&Copy"), AutoSize = true };
            var saveButton = new Button { Text = T("ui.&Save...", "&Save..."), AutoSize = true };
            var status = new Label
            {
                AutoSize = true,
                Text = T("status.Not uploaded.", "Not uploaded."),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 7, 16, 0)
            };

            closeButton.Click += delegate { dialog.Close(); };
            copyButton.Click += delegate
            {
                Clipboard.SetText(previewBox.Text);
                status.Text = T("status.Community stats payload copied.", "Community stats payload copied.");
                SpeakCommunityStatsStatus(status.Text);
            };
            saveButton.Click += delegate
            {
                SaveTextFromDialog(previewBox.Text, "SensorReadout-CommunityStats-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", T("ui.Save community stats payload", "Save community stats payload"));
            };
            submitButton.Click += delegate
            {
                submitButton.Enabled = false;
                closeButton.Enabled = true;
                status.Text = T("status.Uploading community stats...", "Uploading community stats...");
                SpeakCommunityStatsStatus(status.Text);
                Task.Factory.StartNew(delegate { UploadCommunityStatsJson(previewBox.Text); })
                    .ContinueWith(delegate(Task task)
                    {
                        if (task.IsFaulted)
                        {
                            submitButton.Enabled = true;
                            var message = task.Exception == null ? T("message.Community stats upload failed.", "Community stats upload failed.") : task.Exception.GetBaseException().Message;
                            status.Text = T("status.Community stats upload failed.", "Community stats upload failed.") + " " + message;
                            SpeakCommunityStatsStatus(status.Text);
                            return;
                        }

                        submitButton.Enabled = false;
                        status.Text = T("message.Community stats uploaded.", "Community stats uploaded. Thank you for helping improve Sensor Readout hardware coverage.");
                        SpeakCommunityStatsStatus(status.Text);
                        OpenExternalPage(CommunityStatsPageUrl, T("ui.Could not open community stats page", "Could not open community stats page"));
                        dialog.Close();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            };

            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(submitButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(status);

            layout.Controls.Add(introBox, 0, 0);
            layout.Controls.Add(previewBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.CancelButton = closeButton;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            };
            dialog.Shown += delegate
            {
                previewBox.Focus();
                previewBox.Select(0, 0);
                SpeakCommunityStatsStatus(T("status.Community stats payload ready for review.", "Community stats payload ready for review. Review the payload, then press Alt+U to upload or Escape to close."));
            };
            dialog.ShowDialog(this);
        }
    }

    private static void SpeakCommunityStatsStatus(string text)
    {
        string error;
        ScreenReaderOutput.TrySpeakPolite(text, out error);
    }

    private List<SensorRow> CollectCommunityStatsRows()
    {
        var rows = CollectSensorRows(true);
        if (HasTemperatureOrFanRows(rows))
        {
            return rows;
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1000);
            if (HasTemperatureOrFanRows(GetCachedLibreHardwareMonitorRowsSnapshot()))
            {
                return CollectSensorRows(false);
            }
        }

        return rows;
    }

    private static bool HasTemperatureOrFanRows(IEnumerable<SensorRow> rows)
    {
        return (rows ?? Enumerable.Empty<SensorRow>()).Any(r =>
            r != null &&
            (string.Equals(r.Type ?? "", "Temperature", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Type ?? "", "Fan", StringComparison.OrdinalIgnoreCase)));
    }

    public void SaveCommunityStatsPayloadToFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Community stats payload path is required.", "path");
        }

        SetLatestRows(CollectCommunityStatsRows());
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var payload = BuildCommunityStatsPayload();
        File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented), new UTF8Encoding(false));
    }

    private Dictionary<string, object> BuildCommunityStatsPayload()
    {
        EnsureCommunityStatsClientId();
        var rows = latestRows == null ? new List<SensorRow>() : latestRows.Where(r => r != null).ToList();
        var selectableRows = rows.Where(IsSelectableReadoutRow).ToList();
        var rowsByCategory = rows
            .GroupBy(r => r.Type ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (object)g.Count(), StringComparer.OrdinalIgnoreCase);
        var selectableByCategory = selectableRows
            .GroupBy(r => r.Type ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (object)g.Count(), StringComparer.OrdinalIgnoreCase);
        var enabledPlugIns = (settings.PlugInsEnabled ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
            .Where(p => p.Value && IsSafeCommunityStatsToken(p.Key))
            .Select(p => p.Key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var windowsInfo = GetCommunityStatsWindowsInfo();
        payload["schemaVersion"] = 2;
        payload["appVersion"] = AppVersion;
        payload["generatedUtc"] = DateTime.UtcNow.ToString("o");
        payload["anonymousClientIdHash"] = Sha256Hex(settings.CommunityStatsClientId);
        payload["privacy"] = "Allow-listed aggregate community stats only. No computer name, username, serials, MAC/IP, paths, drive labels, device IDs, PnP IDs, raw details, installed programs, program usage, per-drive rows, or full report rows.";
        payload["system"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "windowsCaption", windowsInfo.Caption },
            { "windowsVersion", windowsInfo.Version },
            { "windowsBuild", windowsInfo.Build },
            { "windowsArchitecture", windowsInfo.Architecture },
            { "is64BitOperatingSystem", Environment.Is64BitOperatingSystem },
            { "is64BitProcess", Environment.Is64BitProcess },
            { "logicalProcessorCount", Environment.ProcessorCount },
            { "refreshIntervalSeconds", settings.RefreshIntervalSeconds },
            { "temperatureUnit", settings.TemperatureUnit ?? "" },
            { "language", SafeCommunityStatsValue(string.IsNullOrWhiteSpace(settings.LanguageFile) ? "English" : Path.GetFileNameWithoutExtension(settings.LanguageFile)) },
            { "installMode", IsRunningFromLocalInstallFolder() ? "Installed" : "Portable" }
        };
        payload["counts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "totalRows", rows.Count },
            { "selectableRows", selectableRows.Count },
            { "rowsWithDetails", rows.Count(r => r.Details != null && r.Details.Count > 0) },
            { "rowsByCategory", rowsByCategory },
            { "selectableRowsByCategory", selectableByCategory }
        };
        payload["availability"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "hasTemperatures", rows.Any(r => string.Equals(r.Type, "Temperature", StringComparison.OrdinalIgnoreCase)) },
            { "hasFans", rows.Any(r => string.Equals(r.Type, "Fan", StringComparison.OrdinalIgnoreCase)) },
            { "hasSmart", rows.Any(r => string.Equals(r.Type, "SMART", StringComparison.OrdinalIgnoreCase)) },
            { "hasNetwork", rows.Any(r => string.Equals(r.Type, "Network", StringComparison.OrdinalIgnoreCase)) },
            { "hasUsb", rows.Any(r => string.Equals(r.Type, "USB", StringComparison.OrdinalIgnoreCase)) },
            { "hasAudio", rows.Any(r => string.Equals(r.Type, "Audio", StringComparison.OrdinalIgnoreCase)) },
            { "hasDisplay", rows.Any(r => string.Equals(r.Type, "Display", StringComparison.OrdinalIgnoreCase)) },
            { "hasBattery", rows.Any(r => string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase)) },
            { "hasDevices", rows.Any(r => string.Equals(r.Type, "Devices", StringComparison.OrdinalIgnoreCase)) },
            { "hasGpuMemory", rows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && CleanSensorName(r.Name).IndexOf("GPU memory", StringComparison.OrdinalIgnoreCase) >= 0) },
            { "hasConnectedDiskTotals", !string.IsNullOrWhiteSpace(FindDisplayValue(rows, "Performance", "Connected disks total space")) },
            { "hasBitLockerStatus", rows.Any(r => string.Equals(r.Type, "SMART", StringComparison.OrdinalIgnoreCase) && CleanSensorName(r.Name).Equals("BitLocker status", StringComparison.OrdinalIgnoreCase)) },
            { "hasPrinterRows", rows.Any(IsPrinterCommunityStatsRow) },
            { "hasNonWorkingDevices", rows.Any(r => string.Equals(r.Type, "Devices", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Hardware ?? "", "Non-working devices", StringComparison.OrdinalIgnoreCase)) },
            { "enabledPlugInCount", enabledPlugIns.Count },
            { "enabledPlugIns", enabledPlugIns }
        };
        payload["hardwareSummary"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "cpuVendor", CpuVendorBucket(FindDisplayValue(rows, "Performance", "CPU vendor")) },
            { "cpuArchitecture", SafeCommunityStatsValue(FindDisplayValue(rows, "Performance", "CPU architecture")) },
            { "cpuProcessorType", CpuProcessorTypeText(FirstNonEmpty(FindDisplayValue(rows, "Performance", "CPU processor type"), FindDetailValue(rows, "Performance", "CPU processor type"))) },
            { "cpuCoreCount", SafeCommunityStatsInt(FindDisplayValue(rows, "Performance", "CPU cores"), 0, 1024) },
            { "cpuThreadCount", SafeCommunityStatsInt(FindDisplayValue(rows, "Performance", "CPU threads"), 0, 1024) },
            { "gpuVendorCounts", GpuVendorCounts(rows) },
            { "memoryTotal", SafeCommunityStatsValue(FindDisplayValue(rows, "Performance", "Memory total")) },
            { "pagingFileTotal", SafeCommunityStatsValue(FindDisplayValue(rows, "Performance", "Paging file total")) },
            { "connectedDiskTotal", SafeCommunityStatsValue(FindDisplayValue(rows, "Performance", "Connected disks total space")) },
            { "connectedDiskUsed", SafeCommunityStatsSizeWithoutPercent(FindDisplayValue(rows, "Performance", "Connected disks used space")) },
            { "connectedDiskFree", SafeCommunityStatsSizeWithoutPercent(FindDisplayValue(rows, "Performance", "Connected disks free space")) },
            { "connectedDiskCount", SafeCommunityStatsInt(FindDetailValue(rows, "Performance", "Included drives"), 0, 1000) },
            { "dedicatedGpuMemoryTotal", SafeCommunityStatsValue(FindDisplayValue(rows, "Performance", "Dedicated GPU memory total")) },
            { "smartDeviceCount", CountDistinctHardware(rows, "SMART") },
            { "networkAdapterGroupCount", CountDistinctHardware(rows, "Network") },
            { "usbGroupCount", CountDistinctHardware(rows, "USB") },
            { "audioGroupCount", CountDistinctHardware(rows, "Audio") },
            { "displayGroupCount", CountDistinctHardware(rows, "Display") },
            { "deviceInventoryGroupCount", CountDistinctHardware(rows, "Devices") }
        };
        payload["accessibility"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "screenReaderOutputAvailable", ScreenReaderOutput.IsAvailable },
            { "detectedScreenReaders", DetectScreenReaders() },
            { "highContrastEnabled", CommunityStatsAccessibilityFlag(TryGetHighContrastEnabled) },
            { "stickyKeysEnabled", CommunityStatsAccessibilityFlag(TryGetStickyKeysEnabled) },
            { "toggleKeysEnabled", CommunityStatsAccessibilityFlag(TryGetToggleKeysEnabled) },
            { "filterKeysEnabled", CommunityStatsAccessibilityFlag(TryGetFilterKeysEnabled) },
            { "showSoundsEnabled", CommunityStatsRegistryOnOff(@"Control Panel\Accessibility\ShowSounds", "On") },
            { "audioDescriptionsEnabled", CommunityStatsRegistryOnOff(@"Control Panel\Accessibility\AudioDescription", "On") },
            { "startupSpeechEnabled", settings.StartupSpeechEnabled },
            { "showHideHotKeyConfigured", !string.IsNullOrWhiteSpace(settings.ShowHideHotKey) },
            { "speakTrayHotKeyConfigured", !string.IsNullOrWhiteSpace(settings.SpeakTrayHotKey) },
            { "trayStatusEnabled", settings.TrayStatusEnabled },
            { "trayReadoutCount", settings.TrayItemKeys == null ? 0 : settings.TrayItemKeys.Count },
            { "spokenHotKeyProfileCount", settings.SpokenHotKeys == null ? 0 : settings.SpokenHotKeys.Count(p => p != null && !string.IsNullOrWhiteSpace(p.HotKey)) },
            { "startMinimizedToTray", settings.StartMinimizedToTray },
            { "tipsOnStartupEnabled", settings.ShowTipsOnStartup }
        };
        payload["configuration"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "autoRefreshEnabled", settings.AutoRefreshEnabled },
            { "refreshWhileFocused", settings.RefreshWhileFocused },
            { "checkForUpdatesAtStartup", settings.CheckForUpdatesAtStartup },
            { "quietUpdatesEnabled", settings.InstallUpdatesQuietly },
            { "alarmCount", settings.Alarms == null ? 0 : settings.Alarms.Count(a => a != null) },
            { "fanProfileCount", settings.FanProfiles == null ? 0 : settings.FanProfiles.Count(p => p != null) },
            { "fanProfileHotKeyCount", settings.FanProfiles == null ? 0 : settings.FanProfiles.Count(p => p != null && !string.IsNullOrWhiteSpace(p.HotKey)) },
            { "hiddenReadingCount", settings.HiddenReadingKeys == null ? 0 : settings.HiddenReadingKeys.Count }
        };
        payload["coverage"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "temperatureRowCount", rows.Count(r => string.Equals(r.Type, "Temperature", StringComparison.OrdinalIgnoreCase)) },
            { "fanRowCount", rows.Count(r => string.Equals(r.Type, "Fan", StringComparison.OrdinalIgnoreCase)) },
            { "smartRowCount", rows.Count(r => string.Equals(r.Type, "SMART", StringComparison.OrdinalIgnoreCase)) },
            { "networkRowCount", rows.Count(r => string.Equals(r.Type, "Network", StringComparison.OrdinalIgnoreCase)) },
            { "usbRowCount", rows.Count(r => string.Equals(r.Type, "USB", StringComparison.OrdinalIgnoreCase)) },
            { "deviceRowCount", rows.Count(r => string.Equals(r.Type, "Devices", StringComparison.OrdinalIgnoreCase)) },
            { "nonWorkingDeviceCount", rows.Count(r => string.Equals(r.Type, "Devices", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Hardware ?? "", "Non-working devices", StringComparison.OrdinalIgnoreCase)) },
            { "printerRowCount", rows.Count(IsPrinterCommunityStatsRow) },
            { "batteryRowCount", rows.Count(r => string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase)) },
            { "gpuMemoryRowCount", rows.Count(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && CleanSensorName(r.Name).IndexOf("GPU memory", StringComparison.OrdinalIgnoreCase) >= 0) }
        };
        return payload;
    }

    private void EnsureCommunityStatsClientId()
    {
        if (!string.IsNullOrWhiteSpace(settings.CommunityStatsClientId))
        {
            return;
        }

        var bytes = new byte[32];
        using (var rng = new RNGCryptoServiceProvider())
        {
            rng.GetBytes(bytes);
        }

        settings.CommunityStatsClientId = Convert.ToBase64String(bytes);
        SaveSettings(settings);
    }

    private sealed class CommunityStatsWindowsInfo
    {
        public string Caption = "";
        public string Version = "";
        public string Build = "";
        public string Architecture = "";
    }

    private static CommunityStatsWindowsInfo GetCommunityStatsWindowsInfo()
    {
        var info = new CommunityStatsWindowsInfo();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    info.Caption = SafeCommunityStatsValue(CleanWmiText(Convert.ToString(os["Caption"])));
                    info.Version = SafeCommunityStatsValue(CleanWmiText(Convert.ToString(os["Version"])));
                    info.Build = SafeCommunityStatsValue(CleanWmiText(Convert.ToString(os["BuildNumber"])));
                    info.Architecture = SafeCommunityStatsValue(CleanWmiText(Convert.ToString(os["OSArchitecture"])));
                    return info;
                }
            }
        }
        catch
        {
        }

        info.Version = SafeCommunityStatsValue(Environment.OSVersion.Version.ToString());
        info.Architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
        return info;
    }

    private delegate bool TryGetCommunityStatsBoolean(out bool enabled);

    private static object CommunityStatsAccessibilityFlag(TryGetCommunityStatsBoolean getter)
    {
        bool enabled;
        return getter != null && getter(out enabled) ? (object)enabled : null;
    }

    private static object CommunityStatsRegistryOnOff(string subKey, string valueName)
    {
        bool enabled;
        return TryGetRegistryOnOff(subKey, valueName, out enabled) ? (object)enabled : null;
    }

    private static IEnumerable<string> SplitSourceList(string value)
    {
        return (value ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim());
    }

    private static bool IsSafeCommunityStatsToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length > 80)
        {
            return false;
        }

        return !value.Contains("\\") &&
            !value.Contains("/") &&
            !value.Contains("@") &&
            !value.Contains(":") &&
            !value.Contains("{") &&
            !value.Contains("}") &&
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"\b(?:\d{1,3}\.){3}\d{1,3}\b") &&
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string FindDisplayValue(IEnumerable<SensorRow> rows, string type, string name)
    {
        var row = (rows ?? new List<SensorRow>()).FirstOrDefault(r =>
            r != null &&
            string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CleanSensorName(r.Name), name, StringComparison.OrdinalIgnoreCase));
        return row == null ? "" : row.DisplayValue ?? "";
    }

    private static string FindDetailValue(IEnumerable<SensorRow> rows, string type, string detailName)
    {
        foreach (var row in rows ?? new List<SensorRow>())
        {
            if (row == null ||
                !string.Equals(row.Type ?? "", type, StringComparison.OrdinalIgnoreCase) ||
                row.Details == null)
            {
                continue;
            }

            string value;
            if (row.Details.TryGetValue(detailName, out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static string SafeCommunityStatsValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        value = value.Trim();
        return value.Length <= 40 && IsSafeCommunityStatsToken(value.Replace("(", "").Replace(")", "").Replace("%", "").Replace(".", "").Replace(" ", ""))
            ? value
            : "";
    }

    private static string SafeCommunityStatsSizeWithoutPercent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var index = value.IndexOf('(');
        if (index >= 0)
        {
            value = value.Substring(0, index);
        }

        return SafeCommunityStatsValue(value.Trim());
    }

    private static int SafeCommunityStatsInt(string value, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        int parsed;
        if (!int.TryParse(digits, out parsed))
        {
            return 0;
        }

        return Math.Max(min, Math.Min(max, parsed));
    }

    private static bool IsPrinterCommunityStatsRow(SensorRow row)
    {
        if (row == null || !string.Equals(row.Type ?? "", "Performance", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(row.Hardware ?? "", "Printers", StringComparison.OrdinalIgnoreCase) ||
            (row.Hardware ?? "").StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase);
    }

    private static string CpuVendorBucket(string value)
    {
        value = value ?? "";
        if (value.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Intel";
        }

        if (value.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Advanced Micro", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "AMD";
        }

        if (value.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Qualcomm", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "ARM";
        }

        return string.IsNullOrWhiteSpace(value) ? "" : "Other";
    }

    private static string CpuProcessorTypeText(string value)
    {
        value = (value ?? "").Trim();
        switch (value)
        {
            case "1": return "Other";
            case "2": return "Unknown";
            case "3": return "Central processor";
            case "4": return "Math processor";
            case "5": return "DSP processor";
            case "6": return "Video processor";
            default: return SafeCommunityStatsValue(value);
        }
    }

    private static Dictionary<string, object> GpuVendorCounts(IEnumerable<SensorRow> rows)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? new List<SensorRow>())
        {
            if (row == null || !string.Equals(row.Type ?? "", "Display", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ((row.Hardware ?? "") + " " + (row.Name ?? "") + " " + (row.DisplayValue ?? "")).Trim();
            var bucket = GpuVendorBucket(text);
            if (string.IsNullOrWhiteSpace(bucket))
            {
                continue;
            }

            int count;
            result.TryGetValue(bucket, out count);
            result[bucket] = count + 1;
        }

        return result
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => (object)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string GpuVendorBucket(string value)
    {
        value = value ?? "";
        if (value.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Quadro", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "NVIDIA";
        }

        if (value.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Advanced Micro", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "AMD";
        }

        if (value.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("UHD", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Iris", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Intel";
        }

        return string.IsNullOrWhiteSpace(value) ? "" : "Other";
    }

    private static List<string> DetectScreenReaders()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "nvda", "NVDA" },
            { "jfw", "JAWS" },
            { "narrator", "Narrator" },
            { "supernova", "SuperNova" },
            { "zoomtext", "ZoomText" },
            { "fusion", "Fusion" },
            { "systemaccess", "System Access" }
        };
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string label;
                    if (names.TryGetValue(process.ProcessName ?? "", out label) && !string.IsNullOrWhiteSpace(label))
                    {
                        found.Add(label);
                    }
                }
            }
        }
        catch
        {
        }

        return found.ToList();
    }

    private static int CountDistinctHardware(IEnumerable<SensorRow> rows, string type)
    {
        return (rows ?? new List<SensorRow>())
            .Where(r => r != null && string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Hardware))
            .Select(r => r.Hardware)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string Sha256Hex(string value)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    private static void UploadCommunityStatsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Community stats payload is empty.");
        }

        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 for older .NET Framework defaults.
        using (var client = new WebClient())
        {
            client.Encoding = Encoding.UTF8;
            client.Headers.Add("User-Agent", "Sensor Readout " + AppVersion);
            client.Headers.Add(HttpRequestHeader.ContentType, "application/json; charset=utf-8");
            client.UploadString(CommunityStatsUploadUrl, "POST", json);
        }
    }
}
