using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private void AddSpokenHotKeyProfile()
    {
        var profile = new SpokenHotKeySetting
        {
            Name = "New spoken hotkey",
            HotKey = "",
            ReadingKeys = new List<string>()
        };
        spokenHotKeys.Add(profile);
        spokenHotKeyList.Items.Add(profile);
        spokenHotKeyList.SelectedItem = profile;
        spokenHotKeyNameBox.Focus();
        spokenHotKeyNameBox.SelectAll();
        UpdateSpokenSelectionStatus("Created new spoken hotkey.");
        SaveLivePreferences();
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

        var index = spokenHotKeyList.SelectedIndex;
        spokenHotKeys.Remove(profile);
        spokenHotKeyList.Items.Remove(profile);
        if (spokenHotKeyList.Items.Count > 0)
        {
            spokenHotKeyList.SelectedIndex = Math.Max(0, Math.Min(index, spokenHotKeyList.Items.Count - 1));
        }
        else
        {
            UpdateSpokenHotKeyEditor();
        }

        UpdateSpokenSelectionStatus("Removed spoken hotkey.");
        SaveLivePreferences();
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
                        ReadingKeys = resolvedKeys
                    };
                    spokenHotKeys.Add(profile);
                    spokenHotKeyList.Items.Add(profile);
                    added++;
                }

                if (added > 0)
                {
                    spokenHotKeyList.SelectedIndex = spokenHotKeyList.Items.Count - 1;
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
            spokenAvailableList.Enabled = enabled;
            spokenSelectedList.Enabled = enabled;
            spokenHotKeyNameBox.Text = profile == null ? "" : profile.Name ?? "";
            spokenHotKeyBox.Text = profile == null ? "" : SensorReadoutForm.NormalizeHotKeyText(profile.HotKey);
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
        RefreshSelectedSpokenHotKeyListItem();
        SaveLivePreferences();
    }

    private void RefreshSelectedSpokenHotKeyListItem()
    {
        if (spokenHotKeyList == null)
        {
            return;
        }

        spokenHotKeyList.Refresh();
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
