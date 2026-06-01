using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private sealed class SpokenHotKeyPresetChoice
    {
        public string Name;
        public string Description;
        public List<string> ReadingKeys = new List<string>();

        public override string ToString()
        {
            return Name + ": " + Description + " (" + ReadingKeys.Count + " readings)";
        }
    }

    private void AddSpokenHotKeyProfile()
    {
        var profile = new SpokenHotKeySetting
        {
            Name = "New spoken hotkey",
            HotKey = "",
            ReadingKeys = new List<string>()
        };
        spokenHotKeys.Add(profile);
        RebuildSpokenHotKeyProfileList(profile);
        spokenHotKeyNameBox.Focus();
        spokenHotKeyNameBox.SelectAll();
        UpdateSpokenSelectionStatus("Created new spoken hotkey.");
        SaveLivePreferences();
    }

    private void ShowSpokenHotKeyPresetsDialog()
    {
        var presets = BuildAvailableSpokenHotKeyPresets();
        if (presets.Count == 0)
        {
            UpdateSpokenSelectionStatus(SensorReadoutForm.L("status.No spoken hotkey presets are available for the current readings.", "No spoken hotkey presets are available for the current readings."));
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        using (var dialog = new Form())
        {
            dialog.Text = SensorReadoutForm.L("ui.Spoken hotkey presets", "Spoken hotkey presets");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 430);
            dialog.MinimumSize = new Size(520, 300);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = SensorReadoutForm.L("ui.Choose spoken hotkey presets to add:", "Choose spoken hotkey presets to add:"),
                AutoSize = true,
                Dock = DockStyle.Fill
            }, 0, 0);

            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                AccessibleName = SensorReadoutForm.L("a11y.Spoken hotkey presets", "Spoken hotkey presets"),
                AccessibleDescription = SensorReadoutForm.L("a11y.Check the spoken hotkey presets to create. Presets are created without key assignments.", "Check the spoken hotkey presets to create. Presets are created without key assignments.")
            };
            foreach (var preset in presets)
            {
                list.Items.Add(preset, false);
            }
            layout.Controls.Add(list, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var okButton = new Button { Text = SensorReadoutForm.L("ui.&Add selected", "&Add selected"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = SensorReadoutForm.L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 2);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                }
            };
            dialog.Shown += delegate
            {
                if (list.Items.Count > 0)
                {
                    list.SelectedIndex = 0;
                }
                list.Focus();
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var checkedPresets = list.CheckedItems.Cast<object>().OfType<SpokenHotKeyPresetChoice>().ToList();
            var added = 0;
            foreach (var preset in checkedPresets)
            {
                if (AddSpokenHotKeyPreset(preset))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                RebuildSpokenHotKeyProfileList(spokenHotKeys.LastOrDefault());
                SaveLivePreferences();
            }

            UpdateSpokenSelectionStatus(string.Format(SensorReadoutForm.L("status.Added spoken hotkey presets:", "Added spoken hotkey presets: {0}."), added));
        }
    }

    private bool AddSpokenHotKeyPreset(SpokenHotKeyPresetChoice preset)
    {
        if (preset == null || preset.ReadingKeys == null || preset.ReadingKeys.Count == 0)
        {
            return false;
        }

        var profile = new SpokenHotKeySetting
        {
            Name = UniqueSpokenHotKeyName(preset.Name),
            HotKey = "",
            ReadingKeys = preset.ReadingKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        spokenHotKeys.Add(profile);
        RebuildSpokenHotKeyProfileList(profile);
        return true;
    }

    private void RebuildSpokenHotKeyProfileList(SpokenHotKeySetting selectedProfile)
    {
        if (spokenHotKeyList == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            spokenHotKeyList.BeginUpdate();
            try
            {
                spokenHotKeyList.Items.Clear();
                foreach (var profile in SortedSpokenHotKeyProfiles())
                {
                    spokenHotKeyList.Items.Add(profile);
                }

                if (selectedProfile != null && spokenHotKeyList.Items.Contains(selectedProfile))
                {
                    spokenHotKeyList.SelectedItem = selectedProfile;
                }
                else if (spokenHotKeyList.Items.Count > 0)
                {
                    spokenHotKeyList.SelectedIndex = 0;
                }
            }
            finally
            {
                spokenHotKeyList.EndUpdate();
            }
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdateSpokenHotKeyEditor();
    }

    private IEnumerable<SpokenHotKeySetting> SortedSpokenHotKeyProfiles()
    {
        return spokenHotKeys
            .Where(p => p != null)
            .OrderBy(p => string.IsNullOrWhiteSpace(p.HotKey) ? 1 : 0)
            .ThenBy(p => HotKeySortKey(p.HotKey), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static string HotKeySortKey(string hotKey)
    {
        var normalized = SensorReadoutForm.NormalizeHotKeyText(hotKey);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "9|";
        }

        var parts = normalized.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();
        var key = parts.Count == 0 ? normalized : parts[parts.Count - 1];
        var modifierRank =
            (parts.Contains("Ctrl", StringComparer.OrdinalIgnoreCase) ? "1" : "0") +
            (parts.Contains("Shift", StringComparer.OrdinalIgnoreCase) ? "1" : "0") +
            (parts.Contains("Alt", StringComparer.OrdinalIgnoreCase) ? "1" : "0");

        return modifierRank + "|" + BaseHotKeySortKey(key) + "|" + normalized;
    }

    private static string BaseHotKeySortKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "9999|";
        }

        if (key.Length > 1 && (key[0] == 'F' || key[0] == 'f'))
        {
            int number;
            if (int.TryParse(key.Substring(1), out number))
            {
                return "0100|" + number.ToString("000");
            }
        }

        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            return "0200|" + char.ToUpperInvariant(key[0]);
        }

        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            return "0300|" + key;
        }

        return "0400|" + key;
    }

    private List<SpokenHotKeyPresetChoice> BuildAvailableSpokenHotKeyPresets()
    {
        var presets = new List<SpokenHotKeyPresetChoice>();
        AddSpokenPresetIfAny(presets, "System status", "CPU, memory, uptime, and core temperature where available.",
            FindPresetRow("Performance", "CPU usage", null),
            FindPresetRow("Performance", "Memory used", "Memory"),
            FindPresetRow("Performance", "Memory available", "Memory"),
            FindPresetRow("Performance", "System uptime", null),
            FindPresetTemperatureRow("cpu"));
        AddSpokenPresetIfAny(presets, "Network status", "Current adapter rates and Wi-Fi strength where available.",
            FindPresetRow("Network", "Receive rate", null),
            FindPresetRow("Network", "Send rate", null),
            FindPresetRow("Network", "Wi-Fi signal strength", null),
            FindPresetRow("Network", "Wi-Fi RSSI", null),
            FindPresetRow("Network", "Wi-Fi channel", null));
        AddSpokenPresetIfAny(presets, "Disk activity", "Read/write rates and disk activity for the first fixed drive found.",
            FindPresetRow("Performance", "Read rate", null),
            FindPresetRow("Performance", "Write rate", null),
            FindPresetRow("Performance", "Read activity", null),
            FindPresetRow("Performance", "Write activity", null),
            FindPresetRow("Performance", "Free space", null));
        AddSpokenPresetIfAny(presets, "GPU status", "GPU usage, temperature, and memory where available.",
            FindPresetRow("Performance", "GPU usage", "GPU"),
            FindPresetTemperatureRow("gpu"),
            FindPresetRow("Performance", "Dedicated GPU memory used", "GPU memory"),
            FindPresetRow("Performance", "Dedicated GPU memory free", "GPU memory"),
            FindPresetRow("Performance", "Shared GPU memory used", "GPU memory"));
        AddSpokenPresetIfAny(presets, "Battery status", "Battery charge, rate, health, and cycle count where available.",
            FindPresetRow("Battery", "Charge", null),
            FindPresetRow("Battery", "Power rate", null),
            FindPresetRow("Battery", "Health", null),
            FindPresetRow("Battery", "Cycle count", null));
        AddSpokenPresetIfAny(presets, "Fan and temperature", "Useful cooling readings where available.",
            FindPresetTemperatureRow("cpu"),
            FindPresetTemperatureRow("gpu"),
            FindFirstPresetRowByType("Fan"),
            FindPresetRow("Performance", "CPU usage", null));
        return presets;
    }

    private void AddSpokenPresetIfAny(List<SpokenHotKeyPresetChoice> presets, string name, string description, params SensorRow[] presetRows)
    {
        var keys = (presetRows ?? new SensorRow[0])
            .Where(r => r != null)
            .Select(SensorReadoutForm.RowSettingsKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0)
        {
            return;
        }

        presets.Add(new SpokenHotKeyPresetChoice
        {
            Name = SensorReadoutForm.L("ui.Spoken hotkey preset " + name, name),
            Description = SensorReadoutForm.L("ui.Spoken hotkey preset description " + name, description),
            ReadingKeys = keys
        });
    }

    private SensorRow FindPresetRow(string type, string name, string hardwareContains)
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            (string.IsNullOrWhiteSpace(type) || string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(name) || string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(hardwareContains) || (r.Hardware ?? "").IndexOf(hardwareContains, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private SensorRow FindPresetTemperatureRow(string hardwareOrName)
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            string.Equals(r.Type, "Temperature", StringComparison.OrdinalIgnoreCase) &&
            (((r.Hardware ?? "").IndexOf(hardwareOrName, StringComparison.OrdinalIgnoreCase) >= 0) ||
             ((r.Name ?? "").IndexOf(hardwareOrName, StringComparison.OrdinalIgnoreCase) >= 0)));
    }

    private SensorRow FindFirstPresetRowByType(string type)
    {
        return rows.FirstOrDefault(r => r != null && string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveShowStoppedFansPreference()
    {
        if (loadingPreferences)
        {
            return;
        }

        liveSettings.ShowStoppedFans = fanProfileShowStoppedBox.Checked;
        RebuildFanProfileFanControlRows();
        SaveLivePreferences();
    }

    private void RebuildFanProfileFanControlRows()
    {
        var newFanControlRows = BuildFanProfileFanControlRows(latestSensorRows);
        fanControlRows.Clear();
        fanControlRows.AddRange(newFanControlRows);
        fanControlRowsSignature = BuildRowsSignature(newFanControlRows);
        PopulateFanProfileLists(SelectedFanProfile());
        UpdateFanProfileStatus();
    }

    private void RemoveSelectedSpokenHotKeyProfile()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken hotkey first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (!ConfirmRemoveSpokenHotKeyProfile(profile))
        {
            return;
        }

        var ordered = SortedSpokenHotKeyProfiles().ToList();
        var index = ordered.IndexOf(profile);
        spokenHotKeys.Remove(profile);
        var replacement = SortedSpokenHotKeyProfiles()
            .ElementAtOrDefault(Math.Max(0, Math.Min(index, spokenHotKeys.Count - 1)));
        RebuildSpokenHotKeyProfileList(replacement);
        if (spokenHotKeyList.Items.Count == 0)
        {
            UpdateSpokenHotKeyEditor();
        }

        UpdateSpokenSelectionStatus("Removed spoken hotkey.");
        SaveLivePreferences();
    }

    private bool ConfirmRemoveSpokenHotKeyProfile(SpokenHotKeySetting profile)
    {
        if (profile == null || liveSettings == null || !liveSettings.ConfirmSpokenHotKeyProfileRemoval)
        {
            return true;
        }

        using (var dialog = new Form())
        {
            dialog.Text = SensorReadoutForm.L("ui.Remove spoken hotkey profile", "Remove spoken hotkey profile");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(520, 210);
            dialog.MinimumSize = new Size(420, 190);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var name = string.IsNullOrWhiteSpace(profile.Name) ? SensorReadoutForm.L("ui.Spoken hotkey", "Spoken hotkey") : profile.Name.Trim();
            var message = string.Format(
                SensorReadoutForm.L("message.Remove spoken hotkey profile?", "Remove the spoken hotkey profile \"{0}\"?"),
                name);
            layout.Controls.Add(new Label
            {
                Text = message,
                AutoSize = true,
                Dock = DockStyle.Fill
            }, 0, 0);

            var dontShowAgainBox = new CheckBox
            {
                Text = SensorReadoutForm.L("ui.Do not show this &message again", "Do not show this &message again"),
                AutoSize = true,
                AccessibleName = SensorReadoutForm.L("a11y.Do not show this message again", "Do not show this message again")
            };
            layout.Controls.Add(dontShowAgainBox, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var removeButton = new Button { Text = SensorReadoutForm.L("ui.&Remove", "&Remove"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = SensorReadoutForm.L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(removeButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 2);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = removeButton;
            dialog.CancelButton = cancelButton;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                }
            };

            var confirmed = dialog.ShowDialog(this) == DialogResult.OK;
            if (confirmed && dontShowAgainBox.Checked)
            {
                liveSettings.ConfirmSpokenHotKeyProfileRemoval = false;
                if (confirmSpokenHotKeyProfileRemovalCheckBox != null)
                {
                    confirmSpokenHotKeyProfileRemovalCheckBox.Checked = false;
                }
                SaveLivePreferences();
            }

            return confirmed;
        }
    }

    private void ImportSpokenHotKeysFromConfig()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Import spoken hotkeys";
            dialog.Filter = "Sensor Readout config (*.json)|*.json|All files (*.*)|*.*";
            dialog.InitialDirectory = SensorReadoutForm.GetConfigFolderPath();
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var importedSettings = JsonConvert.DeserializeObject<MachineAppSettings>(File.ReadAllText(dialog.FileName));
                var importedProfiles = importedSettings == null ? new List<SpokenHotKeySetting>() : importedSettings.SpokenHotKeys ?? new List<SpokenHotKeySetting>();
                var added = 0;
                var skippedProfiles = 0;
                var skippedReadings = 0;
                foreach (var imported in importedProfiles.Where(p => p != null))
                {
                    var resolvedKeys = new List<string>();
                    foreach (var key in imported.ReadingKeys ?? new List<string>())
                    {
                        var resolved = ResolveImportedSpokenReadingKey(key);
                        if (string.IsNullOrWhiteSpace(resolved))
                        {
                            skippedReadings++;
                            continue;
                        }

                        if (!resolvedKeys.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                        {
                            resolvedKeys.Add(resolved);
                        }
                    }

                    if (resolvedKeys.Count == 0)
                    {
                        skippedProfiles++;
                        continue;
                    }

        var profile = new SpokenHotKeySetting
        {
            Name = UniqueSpokenHotKeyName(imported.Name),
            HotKey = "",
            SkipUnavailableReadings = imported.SkipUnavailableReadings,
            ReadingKeys = resolvedKeys
        };
                    spokenHotKeys.Add(profile);
                    added++;
                }

                if (added > 0)
                {
                    RebuildSpokenHotKeyProfileList(spokenHotKeys.LastOrDefault());
                    SaveLivePreferences();
                }

                UpdateSpokenSelectionStatus("Imported " + added + " spoken hotkey" + (added == 1 ? "" : "s") + ". " + skippedProfiles + " profile" + (skippedProfiles == 1 ? "" : "s") + " and " + skippedReadings + " reading" + (skippedReadings == 1 ? "" : "s") + " skipped.");
            }
            catch (Exception ex)
            {
                UpdateSpokenSelectionStatus("Could not import spoken hotkeys: " + ex.Message);
                System.Media.SystemSounds.Beep.Play();
            }
        }
    }

    private string ResolveImportedSpokenReadingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var exact = rows.FirstOrDefault(r => string.Equals(SensorReadoutForm.RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return SensorReadoutForm.RowSettingsKey(exact);
        }

        var parts = key.Split('|');
        if (parts.Length < 3)
        {
            return "";
        }

        var type = parts[0];
        var hardware = parts[1];
        var name = SensorReadoutForm.CleanSensorName(parts[2]);
        var portableHardware = IsPortableImportedHardware(hardware);
        var matches = rows
            .Where(r =>
                string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(SensorReadoutForm.CleanSensorName(r.Name), name, StringComparison.OrdinalIgnoreCase) &&
                (portableHardware || string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count == 1 ? SensorReadoutForm.RowSettingsKey(matches[0]) : "";
    }

    private static bool IsPortableImportedHardware(string hardware)
    {
        return string.Equals(hardware ?? "", "CPU", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Memory", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Battery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Overview", StringComparison.OrdinalIgnoreCase);
    }

    private string UniqueSpokenHotKeyName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Imported spoken hotkey" : name.Trim();
        var candidate = baseName;
        var suffix = 2;
        while (spokenHotKeys.Any(p => p != null && string.Equals(p.Name ?? "", candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + " " + suffix;
            suffix++;
        }

        return candidate;
    }

    private SpokenHotKeySetting SelectedSpokenHotKey()
    {
        return spokenHotKeyList == null ? null : spokenHotKeyList.SelectedItem as SpokenHotKeySetting;
    }

    private void LoadSelectedSpokenHotKey()
    {
        UpdateSpokenHotKeyEditor();
    }

    private void UpdateSpokenHotKeyEditor()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var profile = SelectedSpokenHotKey();
            var enabled = profile != null;
            spokenHotKeyNameBox.Enabled = enabled;
            spokenHotKeyBox.Enabled = enabled;
            spokenHotKeySkipUnavailableCheckBox.Enabled = enabled;
            spokenAvailableList.Enabled = enabled;
            spokenSelectedList.Enabled = enabled;
            spokenHotKeyNameBox.Text = profile == null ? "" : profile.Name ?? "";
            spokenHotKeyBox.Text = profile == null ? "" : SensorReadoutForm.NormalizeHotKeyText(profile.HotKey);
            spokenHotKeySkipUnavailableCheckBox.Checked = profile != null && profile.SkipUnavailableReadings;
            PopulateSpokenReadingLists(profile);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdateSpokenSelectionStatus();
    }

    private void PopulateSpokenReadingLists(SpokenHotKeySetting profile)
    {
        var selectedAvailableKey = SelectedTrayChoiceKey(spokenAvailableList);
        var selectedSpokenKey = SelectedTrayChoiceKey(spokenSelectedList);
        spokenAvailableList.Items.Clear();
        spokenSelectedList.Items.Clear();
        var selectedKeys = profile == null || profile.ReadingKeys == null ? new List<string>() : profile.ReadingKeys;
        var choices = rows
            .Select(r => new TrayItemChoice(r, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => SensorReadoutForm.ReadingSortIndex(i.Name))
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Type)
            .ToList();

        foreach (var key in selectedKeys)
        {
            var selectedChoice = choices.FirstOrDefault(i => i.Key == key);
            if (selectedChoice != null && !ContainsTrayChoice(spokenSelectedList, selectedChoice.Key))
            {
                selectedChoice.ShowSpeechPreview = true;
                spokenSelectedList.Items.Add(selectedChoice);
            }
            else if (selectedChoice == null && !ContainsTrayChoice(spokenSelectedList, key))
            {
                var unresolved = TrayItemChoice.Unresolved(key, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames);
                unresolved.ShowSpeechPreview = true;
                spokenSelectedList.Items.Add(unresolved);
            }
        }

        foreach (var item in choices)
        {
            if (!ContainsTrayChoice(spokenSelectedList, item.Key))
            {
                spokenAvailableList.Items.Add(item);
            }
        }

        if (spokenAvailableList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(spokenAvailableList, selectedAvailableKey);
        }
        if (spokenSelectedList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(spokenSelectedList, selectedSpokenKey);
        }
    }

    private void SaveSelectedSpokenHotKeyHeader()
    {
        if (loadingPreferences)
        {
            return;
        }

        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            return;
        }

        profile.Name = spokenHotKeyNameBox.Text.Trim();
        profile.HotKey = SensorReadoutForm.NormalizeHotKeyText(spokenHotKeyBox.Text);
        profile.SkipUnavailableReadings = spokenHotKeySkipUnavailableCheckBox.Checked;
        RefreshSelectedSpokenHotKeyListItem();
        SaveLivePreferences();
    }

    private void RefreshSelectedSpokenHotKeyListItem()
    {
        if (spokenHotKeyList == null)
        {
            return;
        }

        RebuildSpokenHotKeyProfileList(SelectedSpokenHotKey());
    }

    private void AddSelectedSpokenChoice()
    {
        var profile = SelectedSpokenHotKey();
        var item = spokenAvailableList.SelectedItem as TrayItemChoice;
        if (profile == null || item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken hotkey and an available reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = spokenAvailableList.SelectedIndex;
        spokenAvailableList.Items.Remove(item);
        item.ShowSpeechPreview = true;
        spokenSelectedList.Items.Add(item);
        spokenSelectedList.SelectedItem = item;
        if (spokenAvailableList.Items.Count > 0)
        {
            spokenAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, spokenAvailableList.Items.Count - 1));
        }
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Added " + item + " to spoken hotkey.");
    }

    private void RemoveSelectedSpokenChoice()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = spokenSelectedList.SelectedIndex;
        spokenSelectedList.Items.Remove(item);
        item.ShowSpeechPreview = false;
        AddAvailableSpokenChoiceSorted(item);
        if (spokenSelectedList.Items.Count > 0)
        {
            spokenSelectedList.SelectedIndex = Math.Max(0, Math.Min(index, spokenSelectedList.Items.Count - 1));
        }
        spokenAvailableList.SelectedItem = item;
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Removed " + item + " from spoken hotkey.");
    }

    private void MoveSelectedSpokenChoice(int direction)
    {
        var index = spokenSelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= spokenSelectedList.Items.Count)
        {
            UpdateSpokenSelectionStatus("Cannot move the selected spoken reading further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = spokenSelectedList.Items[index];
        spokenSelectedList.Items.RemoveAt(index);
        spokenSelectedList.Items.Insert(target, item);
        spokenSelectedList.SelectedIndex = target;
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Moved " + item + (direction < 0 ? " up." : " down."));
    }

    private void RenameSelectedSpokenChoice()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        RenameSpeechLabel(item, UpdateSpokenSelectionStatus);
    }

    private void RenameSpeechLabel(TrayItemChoice item, Action<string> updateStatus)
    {
        if (item == null)
        {
            return;
        }

        var current = SpeechLabelForChoice(item);
        var value = PromptForText(this, SensorReadoutForm.L("ui.Rename...", "Rename..."), SensorReadoutForm.L("ui.Spoken label:", "Spoken label:"), current);
        if (value == null)
        {
            return;
        }

        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, DefaultSpeechLabelForChoice(item), StringComparison.OrdinalIgnoreCase))
        {
            readingSpeechLabels.Remove(item.Key);
            updateStatus(SensorReadoutForm.L("ui.Reset spoken label for", "Reset spoken label for") + " " + item + ".");
        }
        else
        {
            readingSpeechLabels[item.Key] = value;
            updateStatus(SensorReadoutForm.L("ui.Renamed spoken label to", "Renamed spoken label to") + " " + value + ".");
        }

        RefreshSpeechPreviewLists();
        SaveLivePreferences();
    }

    private void ResetSelectedSpokenChoiceLabel()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        readingSpeechLabels.Remove(item.Key);
        RefreshSpeechPreviewLists();
        SaveLivePreferences();
        UpdateSpokenSelectionStatus(SensorReadoutForm.L("ui.Reset spoken label for", "Reset spoken label for") + " " + item + ".");
    }

    private void RefreshSpeechPreviewLists()
    {
        if (traySelectedList != null) traySelectedList.Refresh();
        if (spokenSelectedList != null) spokenSelectedList.Refresh();
        if (spokenAvailableList != null) spokenAvailableList.Refresh();
        if (trayAvailableList != null) trayAvailableList.Refresh();
    }

    private string SpeechLabelForChoice(TrayItemChoice choice)
    {
        if (choice == null)
        {
            return "";
        }

        string custom;
        return readingSpeechLabels.TryGetValue(choice.Key, out custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : DefaultSpeechLabelForChoice(choice);
    }

    private string DefaultSpeechLabelForChoice(TrayItemChoice choice)
    {
        return choice == null ? "" : SensorReadoutForm.DefaultSpeechLabel(choice.Type, choice.Hardware, choice.Name, ShouldPreviewSpeechWithDeviceNames());
    }

    private void SaveSelectedSpokenReadingKeys()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            return;
        }

        profile.ReadingKeys = spokenSelectedList.Items
            .Cast<TrayItemChoice>()
            .Select(i => i.Key)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
        RefreshSelectedSpokenHotKeyListItem();
        SaveLivePreferences();
    }

    private void AddAvailableSpokenChoiceSorted(TrayItemChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < spokenAvailableList.Items.Count &&
            TrayItemChoice.Compare((TrayItemChoice)spokenAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        spokenAvailableList.Items.Insert(insertIndex, choice);
    }

    private void SpokenAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(spokenAvailableList, SensorReadoutForm.L("ui.Find reading", "Find reading"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SpokenHotKeyListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            spokenHotKeyNameBox.Focus();
            spokenHotKeyNameBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            spokenHotKeyBox.Focus();
            spokenHotKeyBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedSpokenHotKeyProfile();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SpokenSelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            RenameSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedSpokenChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedSpokenChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateSpokenSelectionStatus()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            UpdateSpokenSelectionStatus("No spoken hotkey selected.");
            return;
        }

        var count = spokenSelectedList == null ? 0 : spokenSelectedList.Items.Count;
        UpdateSpokenSelectionStatus(count + " reading" + (count == 1 ? "" : "s") + " selected for this spoken hotkey.");
    }

    private void UpdateSpokenSelectionStatus(string message)
    {
        if (spokenSelectionStatusLabel != null)
        {
            spokenSelectionStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }
}
