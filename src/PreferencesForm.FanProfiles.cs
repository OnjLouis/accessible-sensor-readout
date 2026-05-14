using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private void AddFanProfile()
    {
        var profile = new FanProfileSetting
        {
            Name = "New fan profile",
            HotKey = "",
            Actions = new List<FanProfileActionSetting>()
        };
        fanProfiles.Add(profile);
        fanProfileList.Items.Add(profile);
        fanProfileList.SelectedItem = profile;
        fanProfileNameBox.Focus();
        fanProfileNameBox.SelectAll();
        UpdateFanProfileStatus("Created new fan profile.");
        SaveLivePreferences();
    }

    private void RemoveSelectedFanProfile()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("Select a fan profile first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileList.SelectedIndex;
        fanProfiles.Remove(profile);
        fanProfileList.Items.Remove(profile);
        if (fanProfileList.Items.Count > 0)
        {
            fanProfileList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileList.Items.Count - 1));
        }
        else
        {
            UpdateFanProfileEditor();
        }

        UpdateFanProfileStatus("Removed fan profile.");
        SaveLivePreferences();
    }

    private FanProfileSetting SelectedFanProfile()
    {
        return fanProfileList == null ? null : fanProfileList.SelectedItem as FanProfileSetting;
    }

    private void LoadSelectedFanProfile()
    {
        UpdateFanProfileEditor();
    }

    private void UpdateFanProfileEditor()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var profile = SelectedFanProfile();
            var enabled = profile != null;
            fanProfileNameBox.Enabled = enabled;
            fanProfileHotKeyBox.Enabled = enabled;
            fanProfileAvailableList.Enabled = enabled;
            fanProfileSelectedList.Enabled = enabled;
            fanProfileActionBox.Enabled = enabled;
            fanProfilePercentBox.Enabled = enabled;
            fanProfileToggleBox.Enabled = enabled;
            fanProfileSpeakBox.Enabled = enabled;
            fanProfileSpeechMessageBox.Enabled = enabled;
            fanProfileSoundBox.Enabled = enabled;
            fanProfileNameBox.Text = profile == null ? "" : profile.Name ?? "";
            fanProfileHotKeyBox.Text = profile == null ? "" : SensorReadoutForm.NormalizeHotKeyText(profile.HotKey);
            fanProfileToggleBox.Checked = profile != null && profile.ToggleAutomatic;
            fanProfileSpeakBox.Checked = profile == null || profile.Speak;
            fanProfileSpeechMessageBox.Text = profile == null ? "" : profile.SpeechMessage ?? "";
            PopulateSoundCombo(fanProfileSoundBox, profile == null ? "" : profile.SoundFile ?? "");
            PopulateFanProfileLists(profile);
            LoadSelectedFanProfileAction();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdateFanProfileStatus();
    }

    private void PopulateFanProfileLists(FanProfileSetting profile)
    {
        var selectedAvailableKey = SelectedFanControlChoiceKey(fanProfileAvailableList);
        var selectedProfileKey = SelectedFanControlChoiceKey(fanProfileSelectedList);
        fanProfileAvailableList.Items.Clear();
        fanProfileSelectedList.Items.Clear();
        var actions = profile == null || profile.Actions == null ? new List<FanProfileActionSetting>() : profile.Actions;
        var choices = fanControlRows
            .Select(r => new FanControlChoice(r, FanProfileFanControlDisplayName(r)))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => i.Name)
            .ToList();

        foreach (var action in actions)
        {
            action.FanControlKey = SensorReadoutForm.IdentifierFromSettingsKey(action.FanControlKey);
            var selectedChoice = choices.FirstOrDefault(i => string.Equals(i.Key, action.FanControlKey, StringComparison.OrdinalIgnoreCase));
            if (selectedChoice != null && !ContainsFanControlChoice(fanProfileSelectedList, selectedChoice.Key))
            {
                selectedChoice.Action = CloneFanProfileAction(action);
                fanProfileSelectedList.Items.Add(selectedChoice);
            }
            else if (selectedChoice == null && !ContainsFanControlChoice(fanProfileSelectedList, action.FanControlKey))
            {
                fanProfileSelectedList.Items.Add(FanControlChoice.Unresolved(action));
            }
        }

        foreach (var item in choices)
        {
            if (!ContainsFanControlChoice(fanProfileSelectedList, item.Key))
            {
                fanProfileAvailableList.Items.Add(item);
            }
        }

        if (fanProfileAvailableList.Items.Count > 0)
        {
            SelectFanControlChoiceByKey(fanProfileAvailableList, selectedAvailableKey);
        }
        if (fanProfileSelectedList.Items.Count > 0)
        {
            SelectFanControlChoiceByKey(fanProfileSelectedList, selectedProfileKey);
        }
    }

    private void SaveSelectedFanProfileHeader()
    {
        if (loadingPreferences)
        {
            return;
        }

        var profile = SelectedFanProfile();
        if (profile == null)
        {
            return;
        }

        profile.Name = fanProfileNameBox.Text.Trim();
        profile.HotKey = SensorReadoutForm.NormalizeHotKeyText(fanProfileHotKeyBox.Text);
        profile.ToggleAutomatic = fanProfileToggleBox.Checked;
        profile.Speak = fanProfileSpeakBox.Checked;
        profile.SpeechMessage = fanProfileSpeechMessageBox.Text.Trim();
        profile.SoundFile = SelectedSoundFile(fanProfileSoundBox);
        RefreshSelectedFanProfileListItem();
        SaveLivePreferences();
    }

    private void RefreshSelectedFanProfileListItem()
    {
        if (fanProfileList != null)
        {
            fanProfileList.Refresh();
        }
    }

    private void AddSelectedFanProfileChoice()
    {
        var profile = SelectedFanProfile();
        var item = fanProfileAvailableList.SelectedItem as FanControlChoice;
        if (profile == null || item == null)
        {
            UpdateFanProfileStatus("Select a fan profile and an available fan control first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileAvailableList.SelectedIndex;
        fanProfileAvailableList.Items.Remove(item);
        item.Action = new FanProfileActionSetting { FanControlKey = item.Key, Manual = fanProfileActionBox.SelectedIndex != 1, Percent = (int)fanProfilePercentBox.Value };
        fanProfileSelectedList.Items.Add(item);
        fanProfileSelectedList.SelectedItem = item;
        if (fanProfileAvailableList.Items.Count > 0)
        {
            fanProfileAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileAvailableList.Items.Count - 1));
        }
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void RemoveSelectedFanProfileChoice()
    {
        var item = fanProfileSelectedList.SelectedItem as FanControlChoice;
        if (item == null)
        {
            UpdateFanProfileStatus("Select a fan action first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileSelectedList.SelectedIndex;
        fanProfileSelectedList.Items.Remove(item);
        AddAvailableFanControlChoiceSorted(item);
        if (fanProfileSelectedList.Items.Count > 0)
        {
            fanProfileSelectedList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileSelectedList.Items.Count - 1));
        }
        fanProfileAvailableList.SelectedItem = item;
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void MoveSelectedFanProfileChoice(int direction)
    {
        var index = fanProfileSelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= fanProfileSelectedList.Items.Count)
        {
            UpdateFanProfileStatus("Cannot move the selected fan action further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = fanProfileSelectedList.Items[index];
        fanProfileSelectedList.Items.RemoveAt(index);
        fanProfileSelectedList.Items.Insert(target, item);
        fanProfileSelectedList.SelectedIndex = target;
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void LoadSelectedFanProfileAction()
    {
        if (loadingPreferences)
        {
            return;
        }

        var item = fanProfileSelectedList == null ? null : fanProfileSelectedList.SelectedItem as FanControlChoice;
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var action = item == null ? null : item.Action;
            fanProfileActionBox.SelectedIndex = action != null && !action.Manual ? 1 : 0;
            fanProfilePercentBox.Value = Math.Max(fanProfilePercentBox.Minimum, Math.Min(fanProfilePercentBox.Maximum, action == null ? 50 : action.Percent));
            fanProfilePercentBox.Enabled = item != null && fanProfileActionBox.SelectedIndex == 0;
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void SaveSelectedFanProfileAction()
    {
        if (loadingPreferences)
        {
            return;
        }

        var item = fanProfileSelectedList == null ? null : fanProfileSelectedList.SelectedItem as FanControlChoice;
        if (item == null)
        {
            fanProfilePercentBox.Enabled = fanProfileActionBox.SelectedIndex == 0;
            return;
        }

        item.Action = new FanProfileActionSetting
        {
            FanControlKey = item.Key,
            Manual = fanProfileActionBox.SelectedIndex != 1,
            Percent = (int)fanProfilePercentBox.Value
        };
        fanProfilePercentBox.Enabled = item.Action.Manual;
        fanProfileSelectedList.Refresh();
        SaveSelectedFanProfileActions();
    }

    private void SaveSelectedFanProfileActions()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            return;
        }

        profile.Actions = fanProfileSelectedList.Items
            .Cast<FanControlChoice>()
            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key))
            .Select(i => CloneFanProfileAction(i.Action ?? new FanProfileActionSetting { FanControlKey = i.Key, Manual = true, Percent = 50 }))
            .ToList();
        RefreshSelectedFanProfileListItem();
        SaveLivePreferences();
        UpdateFanProfileStatus();
    }

    private void AddAvailableFanControlChoiceSorted(FanControlChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < fanProfileAvailableList.Items.Count &&
            FanControlChoice.Compare((FanControlChoice)fanProfileAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        fanProfileAvailableList.Items.Insert(insertIndex, choice);
    }

    private void ApplySelectedFanProfileFromPreferences()
    {
        CommitPreferences();
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("Select a fan profile first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var handler = ApplyFanProfileRequested;
        if (handler != null)
        {
            handler(CloneFanProfile(profile));
        }
        UpdateFanProfileStatus("Applied fan profile.");
    }

    public event Action<FanProfileSetting> ApplyFanProfileRequested;
    public event Action InstallToLocalAppDataRequested;
    public event Action UninstallLocalAppDataRequested;

    private void FanProfileAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(fanProfileAvailableList, SensorReadoutForm.L("ui.Find fan control", "Find fan control"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedFanProfileChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void FanProfileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            fanProfileNameBox.Focus();
            fanProfileNameBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            fanProfileHotKeyBox.Focus();
            fanProfileHotKeyBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedFanProfile();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void FanProfileSelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete || e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedFanProfileChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedFanProfileChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedFanProfileChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateFanProfileStatus()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("No fan profile selected.");
            return;
        }

        var count = fanProfileSelectedList == null ? 0 : fanProfileSelectedList.Items.Count;
        var key = count == 1
            ? "ui.{0} fan action selected for this fan profile."
            : "ui.{0} fan actions selected for this fan profile.";
        var fallback = count == 1
            ? "{0} fan action selected for this fan profile."
            : "{0} fan actions selected for this fan profile.";
        UpdateFanProfileStatus(string.Format(SensorReadoutForm.L(key, fallback), count));
    }

    private void UpdateFanProfileStatus(string message)
    {
        if (fanProfileStatusLabel != null)
        {
            fanProfileStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }
}
