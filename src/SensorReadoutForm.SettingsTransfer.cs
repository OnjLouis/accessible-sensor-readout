using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private const string SettingsTransferFormat = "SensorReadoutSettingsTransfer";
    private const string TransferGeneral = "General preferences";
    private const string TransferTray = "Notification area readings";
    private const string TransferSpokenHotKeys = "Spoken hotkey profiles";
    private const string TransferFanProfiles = "Fan profiles";
    private const string TransferFanCurves = "Fan curves";
    private const string TransferAlarms = "Alarms";
    private const string TransferHiddenItems = "Hidden items";
    private const string TransferPlugIns = "Plug-In choices";

    private void ExportSettingsAndProfiles()
    {
        var categories = ChooseSettingsTransferCategories(
            "Export settings and profiles",
            "Choose what to include in the settings package.",
            new[]
            {
                TransferGeneral,
                TransferTray,
                TransferSpokenHotKeys,
                TransferFanProfiles,
                TransferFanCurves,
                TransferAlarms,
                TransferHiddenItems,
                TransferPlugIns
            },
            null);
        if (categories == null || categories.Count == 0)
        {
            return;
        }

        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "Export settings and profiles";
            dialog.Filter = "Sensor Readout settings package (*.srsettings.json)|*.srsettings.json|JSON files (*.json)|*.json|All files (*.*)|*.*";
            dialog.FileName = Environment.MachineName + ".srsettings.json";
            dialog.InitialDirectory = GetConfigFolderPath();
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var package = BuildSettingsTransferPackage(categories);
                File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(package, Formatting.Indented));
                statusLabel.Text = L("message.Exported settings package.", "Exported settings package.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("message.Could not export settings package:", "Could not export settings package:") + " " + ex.Message, L("ui.Export settings and profiles", "Export settings and profiles"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void ImportSettingsAndProfiles()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Import settings and profiles";
            dialog.Filter = "Sensor Readout settings package (*.srsettings.json;*.json)|*.srsettings.json;*.json|All files (*.*)|*.*";
            dialog.InitialDirectory = GetConfigFolderPath();
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            SettingsTransferPackage package;
            try
            {
                package = JsonConvert.DeserializeObject<SettingsTransferPackage>(File.ReadAllText(dialog.FileName));
                if (package == null || !string.Equals(package.Format, SettingsTransferFormat, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(L("message.This is not a Sensor Readout settings package.", "This is not a Sensor Readout settings package."));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("message.Could not read settings package:", "Could not read settings package:") + " " + ex.Message, L("ui.Import settings and profiles", "Import settings and profiles"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var available = AvailableImportCategories(package);
            var categories = ChooseSettingsTransferCategories(
                "Import settings and profiles",
                "Choose what to import. Global hotkeys, fan controls, and fan curves are imported without active bindings so they can be assigned safely on this computer.",
                available,
                package);
            if (categories == null || categories.Count == 0)
            {
                return;
            }

            try
            {
                var summary = ApplySettingsTransferPackage(package, categories);
                SaveSettings(settings);
                RefreshAfterSettingsTransferImport();
                MessageBox.Show(this, summary, L("ui.Import settings and profiles", "Import settings and profiles"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                statusLabel.Text = L("message.Imported settings package.", "Imported settings package.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("message.Could not import settings package:", "Could not import settings package:") + " " + ex.Message, L("ui.Import settings and profiles", "Import settings and profiles"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private SettingsTransferPackage BuildSettingsTransferPackage(HashSet<string> categories)
    {
        var package = new SettingsTransferPackage
        {
            Format = SettingsTransferFormat,
            AppVersion = AppVersion,
            MachineName = Environment.MachineName,
            ExportedAtUtc = DateTime.UtcNow.ToString("o")
        };

        if (categories.Contains(TransferGeneral))
        {
            package.SharedSettings = ExtractSharedSettings(settings);
        }

        var machine = ExtractMachineSettings(settings);
        var includeMachine = false;
        if (!categories.Contains(TransferTray))
        {
            machine.TrayItemKeys = new List<string>();
            machine.ReadingSpeechLabels = new Dictionary<string, string>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferSpokenHotKeys))
        {
            machine.SpokenHotKeys = new List<SpokenHotKeySetting>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferFanProfiles))
        {
            machine.FanProfiles = new List<FanProfileSetting>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferFanCurves))
        {
            machine.FanCurves = new List<FanCurveSetting>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferAlarms))
        {
            machine.Alarms = new List<AlarmSetting>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferHiddenItems))
        {
            machine.HiddenReadingKeys = new List<string>();
        }
        else
        {
            includeMachine = true;
        }

        if (!categories.Contains(TransferPlugIns))
        {
            machine.PlugInsEnabled = new Dictionary<string, bool>();
        }
        else
        {
            includeMachine = true;
        }

        machine.RunAtStartup = false;
        machine.PrerequisitesPromptShown = false;
        machine.LoggingLevel = "";
        machine.FanLabels = new Dictionary<string, string>();
        machine.FanControlSettings = new Dictionary<string, FanControlSetting>();
        machine.CommunityStatsClientId = "";
        package.MachineSettings = includeMachine ? machine : null;
        return package;
    }

    private string ApplySettingsTransferPackage(SettingsTransferPackage package, HashSet<string> categories)
    {
        var lines = new List<string>();
        var machine = package.MachineSettings ?? new MachineAppSettings();

        if (categories.Contains(TransferGeneral) && package.SharedSettings != null)
        {
            var shared = package.SharedSettings;
            NormalizeSharedSettings(shared);
            ApplySharedSettings(settings, shared);
            settings.ShowHideHotKey = "";
            settings.SpeakTrayHotKey = "";
            lines.Add(L("message.Imported general preferences. Global hotkeys were left unassigned.", "Imported general preferences. Global hotkeys were left unassigned."));
        }

        if (categories.Contains(TransferTray))
        {
            var resolved = ResolveImportedReadingKeys(machine.TrayItemKeys).Take(MaxTrayStatusReadings).ToList();
            settings.TrayItemKeys = resolved;
            settings.ReadingSpeechLabels = ResolveImportedSpeechLabels(machine.ReadingSpeechLabels);
            lines.Add(L("message.Imported notification area readings:", "Imported notification area readings:") + " " + resolved.Count + ".");
        }

        if (categories.Contains(TransferSpokenHotKeys))
        {
            var importedProfiles = new List<SpokenHotKeySetting>();
            var skippedReadings = 0;
            foreach (var profile in machine.SpokenHotKeys ?? new List<SpokenHotKeySetting>())
            {
                if (profile == null)
                {
                    continue;
                }

                var resolved = new List<string>();
                foreach (var key in profile.ReadingKeys ?? new List<string>())
                {
                    var match = ResolveImportedReadingKey(key);
                    if (string.IsNullOrWhiteSpace(match))
                    {
                        skippedReadings++;
                        continue;
                    }

                    if (!resolved.Contains(match, StringComparer.OrdinalIgnoreCase))
                    {
                        resolved.Add(match);
                    }
                }

                importedProfiles.Add(new SpokenHotKeySetting
                {
                    Name = string.IsNullOrWhiteSpace(profile.Name) ? "Imported spoken hotkey" : profile.Name.Trim(),
                    HotKey = "",
                    SkipUnavailableReadings = profile.SkipUnavailableReadings,
                    ReadingKeys = resolved
                });
            }

            settings.SpokenHotKeys = importedProfiles;
            lines.Add(L("message.Imported spoken hotkey profiles with key assignments left blank:", "Imported spoken hotkey profiles with key assignments left blank:") + " " + importedProfiles.Count + ". " + L("message.Skipped readings:", "Skipped readings:") + " " + skippedReadings + ".");
        }

        if (categories.Contains(TransferFanProfiles))
        {
            settings.FanProfiles = (machine.FanProfiles ?? new List<FanProfileSetting>())
                .Where(p => p != null)
                .Select(p => new FanProfileSetting
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "Imported fan profile" : p.Name.Trim(),
                    HotKey = "",
                    SoundFile = Path.GetFileName(p.SoundFile ?? ""),
                    ToggleAutomatic = p.ToggleAutomatic,
                    Speak = p.Speak,
                    SpeechMessage = p.SpeechMessage ?? "",
                    Actions = new List<FanProfileActionSetting>()
                })
                .ToList();
            lines.Add(L("message.Imported fan profile shells. Fan actions must be bound on this computer:", "Imported fan profile shells. Fan actions must be bound on this computer:") + " " + settings.FanProfiles.Count + ".");
        }

        if (categories.Contains(TransferFanCurves))
        {
            settings.FanCurves = (machine.FanCurves ?? new List<FanCurveSetting>())
                .Where(c => c != null)
                .Select(c => new FanCurveSetting
                {
                    Name = string.IsNullOrWhiteSpace(c.Name) ? "Imported fan curve" : c.Name.Trim(),
                    FanControlKey = "",
                    TemperatureReadingKey = ResolveImportedReadingKey(c.TemperatureReadingKey),
                    Enabled = false,
                    SuspendedByManualControl = false,
                    LowTemperatureC = c.LowTemperatureC,
                    LowPercent = c.LowPercent,
                    HighTemperatureC = c.HighTemperatureC,
                    HighPercent = c.HighPercent,
                    EmergencyTemperatureC = c.EmergencyTemperatureC,
                    EmergencyPercent = c.EmergencyPercent,
                    MinimumChangePercent = c.MinimumChangePercent
                })
                .ToList();
            NormalizeSettings(settings);
            lines.Add(L("message.Imported fan curves disabled until fan controls are bound:", "Imported fan curves disabled until fan controls are bound:") + " " + settings.FanCurves.Count + ".");
        }

        if (categories.Contains(TransferAlarms))
        {
            var alarms = new List<AlarmSetting>();
            var skipped = 0;
            foreach (var alarm in machine.Alarms ?? new List<AlarmSetting>())
            {
                if (alarm == null)
                {
                    continue;
                }

                var resolved = ResolveImportedReadingKey(alarm.ReadingKey);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    skipped++;
                    continue;
                }

                alarms.Add(new AlarmSetting
                {
                    Name = string.IsNullOrWhiteSpace(alarm.Name) ? "Imported alarm" : alarm.Name.Trim(),
                    ReadingKey = resolved,
                    Condition = NormalizeAlarmCondition(alarm.Condition),
                    Threshold = alarm.Threshold,
                    ThresholdUnit = alarm.ThresholdUnit ?? "",
                    Enabled = false,
                    Speak = alarm.Speak,
                    SoundFile = Path.GetFileName(alarm.SoundFile ?? ""),
                    CooldownSeconds = Math.Max(0, Math.Min(86400, alarm.CooldownSeconds))
                });
            }

            settings.Alarms = alarms;
            lines.Add(L("message.Imported alarms disabled for review:", "Imported alarms disabled for review:") + " " + alarms.Count + ". " + L("message.Skipped:", "Skipped:") + " " + skipped + ".");
        }

        if (categories.Contains(TransferHiddenItems))
        {
            settings.HiddenReadingKeys = ResolveImportedHiddenKeys(machine.HiddenReadingKeys);
            lines.Add(L("message.Imported hidden item keys:", "Imported hidden item keys:") + " " + settings.HiddenReadingKeys.Count + ".");
        }

        if (categories.Contains(TransferPlugIns))
        {
            settings.PlugInsEnabled = new Dictionary<string, bool>(machine.PlugInsEnabled ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            lines.Add(L("message.Imported Plug-In enable choices. Missing Plug-Ins are ignored until installed.", "Imported Plug-In enable choices. Missing Plug-Ins are ignored until installed."));
        }

        NormalizeSettings(settings);
        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private HashSet<string> ChooseSettingsTransferCategories(string title, string prompt, IEnumerable<string> available, SettingsTransferPackage package)
    {
        using (var dialog = new Form())
        {
            dialog.Text = L("ui." + title, title);
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(560, 420);
            dialog.MinimumSize = new Size(460, 320);
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(new Label { Text = L("ui." + prompt, prompt), AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            var packageText = package == null
                ? ""
                : L("ui.Package from", "Package from") + " " + (string.IsNullOrWhiteSpace(package.MachineName) ? L("ui.unknown computer", "unknown computer") : package.MachineName) + ", " + L("ui.app", "app") + " " + (string.IsNullOrWhiteSpace(package.AppVersion) ? L("ui.unknown version", "unknown version") : package.AppVersion) + ".";
            layout.Controls.Add(new Label { Text = packageText, AutoSize = true, Dock = DockStyle.Fill }, 0, 1);

            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                AccessibleName = L("a11y.Settings transfer categories", "Settings transfer categories"),
                AccessibleDescription = L("a11y.Check the settings categories to include.", "Check the settings categories to include.")
            };
            foreach (var item in available.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                list.Items.Add(new SettingsTransferCategoryChoice(item), true);
            }
            layout.Controls.Add(list, 0, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = L("ui.&OK", "&OK"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 3);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return null;
            }

            return new HashSet<string>(list.CheckedItems.Cast<SettingsTransferCategoryChoice>().Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class SettingsTransferCategoryChoice
    {
        public readonly string Key;

        public SettingsTransferCategoryChoice(string key)
        {
            Key = key ?? "";
        }

        public override string ToString()
        {
            return L("ui." + Key, Key);
        }
    }

    private static string[] AvailableImportCategories(SettingsTransferPackage package)
    {
        var result = new List<string>();
        var machine = package == null ? null : package.MachineSettings;
        if (package != null && package.SharedSettings != null) result.Add(TransferGeneral);
        if (machine != null && ((machine.TrayItemKeys != null && machine.TrayItemKeys.Count > 0) || (machine.ReadingSpeechLabels != null && machine.ReadingSpeechLabels.Count > 0))) result.Add(TransferTray);
        if (machine != null && machine.SpokenHotKeys != null && machine.SpokenHotKeys.Count > 0) result.Add(TransferSpokenHotKeys);
        if (machine != null && machine.FanProfiles != null && machine.FanProfiles.Count > 0) result.Add(TransferFanProfiles);
        if (machine != null && machine.FanCurves != null && machine.FanCurves.Count > 0) result.Add(TransferFanCurves);
        if (machine != null && machine.Alarms != null && machine.Alarms.Count > 0) result.Add(TransferAlarms);
        if (machine != null && machine.HiddenReadingKeys != null && machine.HiddenReadingKeys.Count > 0) result.Add(TransferHiddenItems);
        if (machine != null && machine.PlugInsEnabled != null && machine.PlugInsEnabled.Count > 0) result.Add(TransferPlugIns);
        return result.ToArray();
    }

    private List<string> ResolveImportedReadingKeys(IEnumerable<string> keys)
    {
        var resolved = new List<string>();
        foreach (var key in keys ?? new List<string>())
        {
            var match = ResolveImportedReadingKey(key);
            if (!string.IsNullOrWhiteSpace(match) && !resolved.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(match);
            }
        }

        return resolved;
    }

    private Dictionary<string, string> ResolveImportedSpeechLabels(Dictionary<string, string> labels)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in labels ?? new Dictionary<string, string>())
        {
            var key = ResolveImportedReadingKey(item.Key);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(item.Value))
            {
                result[key] = item.Value.Trim();
            }
        }

        return result;
    }

    private List<string> ResolveImportedHiddenKeys(IEnumerable<string> keys)
    {
        var result = new List<string>();
        foreach (var key in keys ?? new List<string>())
        {
            var resolved = ResolveImportedReadingKey(key);
            if (string.IsNullOrWhiteSpace(resolved) && !string.IsNullOrWhiteSpace(key) && key.StartsWith("type|", StringComparison.OrdinalIgnoreCase))
            {
                resolved = key;
            }

            if (!string.IsNullOrWhiteSpace(resolved) && !result.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(resolved);
            }
        }

        return result;
    }

    private string ResolveImportedReadingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var exact = latestRows.FirstOrDefault(r => string.Equals(RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return RowSettingsKey(exact);
        }

        var parts = key.Split('|');
        if (parts.Length < 3)
        {
            return "";
        }

        var type = parts[0];
        var hardware = parts[1];
        var name = CleanSensorName(parts[2]);
        var portableHardware = IsPortableImportedHardware(hardware);
        var matches = latestRows
            .Where(r =>
                string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanSensorName(r.Name), name, StringComparison.OrdinalIgnoreCase) &&
                (portableHardware || string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count == 1 ? RowSettingsKey(matches[0]) : "";
    }

    private static bool IsPortableImportedHardware(string hardware)
    {
        return string.Equals(hardware ?? "", "CPU", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Memory", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Battery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Overview", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshAfterSettingsTransferImport()
    {
        if (settings.RunAtStartup || settings.StartMinimizedToTray)
        {
            settings.TrayStatusEnabled = true;
        }

        plugInManager = null;
        autoRefreshMenuItem.Checked = settings.AutoRefreshEnabled;
        refreshWhileFocusedMenuItem.Checked = settings.RefreshWhileFocused;
        trayStatusMenuItem.Checked = settings.TrayStatusEnabled;
        pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
        activeTemperatureUnit = settings.TemperatureUnit;
        activeDecimalSeparator = settings.DecimalSeparator;
        LoadSelectedLanguage();
        UpdateTemperatureUnitMenu();
        BuildLanguageMenu();
        ApplyLanguage();
        RegisterGlobalHotKeys();
        ApplyTimerSettings();
        StartAutomaticUpdateChecks();
        RefreshSensors(false, false, "settings import");
    }
}
