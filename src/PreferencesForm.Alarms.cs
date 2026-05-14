using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private void PopulateAlarmReadings()
    {
        if (alarmReadingBox == null)
        {
            return;
        }

        var selectedKey = SelectedAlarmReadingKey();
        alarmReadingBox.Items.Clear();
        foreach (var row in rows.Where(r => r.Value.HasValue))
        {
            alarmReadingBox.Items.Add(new TrayItemChoice(row, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames));
        }

        SelectAlarmReading(selectedKey);
    }

    private void AddAlarm()
    {
        var firstChoice = alarmReadingBox.Items.Count > 0 ? alarmReadingBox.Items[0] as TrayItemChoice : null;
        var alarm = new AlarmSetting
        {
            Name = "New alarm",
            ReadingKey = firstChoice == null ? "" : firstChoice.Key,
            Condition = "Above",
            Threshold = 80,
            ThresholdUnit = AlarmThresholdUnits(firstChoice == null ? null : RowForKey(firstChoice.Key)).FirstOrDefault() ?? "",
            Enabled = true,
            Speak = true,
            SoundFile = "",
            CooldownSeconds = 60
        };
        alarms.Add(alarm);
        var choice = new AlarmChoice(alarm, RowForKey, FormatAlarmThresholdForList);
        alarmList.Items.Add(choice);
        alarmList.SelectedItem = choice;
        alarmNameBox.Focus();
        alarmNameBox.SelectAll();
        UpdateAlarmStatus("Created new alarm.");
        SaveLivePreferences();
    }

    private void RemoveSelectedAlarm()
    {
        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            UpdateAlarmStatus("Select an alarm first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = alarmList.SelectedIndex;
        alarms.Remove(alarm);
        var selectedItem = alarmList.SelectedItem;
        alarmList.Items.Remove(selectedItem);
        if (alarmList.Items.Count > 0)
        {
            alarmList.SelectedIndex = Math.Max(0, Math.Min(index, alarmList.Items.Count - 1));
        }
        else
        {
            LoadSelectedAlarm();
        }

        UpdateAlarmStatus("Removed alarm.");
        SaveLivePreferences();
    }

    private AlarmSetting SelectedAlarm()
    {
        var choice = alarmList == null ? null : alarmList.SelectedItem as AlarmChoice;
        return choice == null ? null : choice.Alarm;
    }

    private void LoadSelectedAlarm()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var alarm = SelectedAlarm();
            var enabled = alarm != null;
            alarmEnabledCheckBox.Enabled = enabled;
            alarmNameBox.Enabled = enabled;
            alarmReadingBox.Enabled = enabled;
            alarmConditionBox.Enabled = enabled;
            alarmThresholdBox.Enabled = enabled;
            alarmThresholdUnitBox.Enabled = enabled;
            alarmCooldownBox.Enabled = enabled;
            alarmSpeakCheckBox.Enabled = enabled;
            alarmSoundBox.Enabled = enabled;

            alarmEnabledCheckBox.Checked = alarm == null || alarm.Enabled;
            alarmNameBox.Text = alarm == null ? "" : alarm.Name ?? "";
            SelectAlarmReading(alarm == null ? "" : alarm.ReadingKey);
            RefreshAlarmThresholdUnitChoices(false);
            SelectAlarmThresholdUnit(alarm == null ? "" : alarm.ThresholdUnit);
            alarmConditionBox.SelectedIndex = AlarmConditionIndex(alarm == null ? "" : alarm.Condition);
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(alarm == null ? 80 : alarm.Threshold, SelectedAlarmThresholdUnit(), SelectedAlarmRow()), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
            alarmCooldownBox.Value = ClampDecimal(alarm == null ? 60 : alarm.CooldownSeconds, alarmCooldownBox.Minimum, alarmCooldownBox.Maximum);
            alarmSpeakCheckBox.Checked = alarm == null || alarm.Speak;
            PopulateSoundCombo(alarmSoundBox, alarm == null ? "" : alarm.SoundFile);
            lastAlarmReadingKey = SelectedAlarmReadingKey();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void SaveSelectedAlarm(bool refreshListItem = true)
    {
        if (loadingPreferences)
        {
            return;
        }

        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            return;
        }

        alarm.Enabled = alarmEnabledCheckBox.Checked;
        alarm.Name = alarmNameBox.Text.Trim();
        alarm.ReadingKey = SelectedAlarmReadingKey();
        alarm.Condition = AlarmConditionFromIndex(alarmConditionBox.SelectedIndex);
        alarm.ThresholdUnit = SelectedAlarmThresholdUnit();
        alarm.Threshold = AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), alarm.ThresholdUnit, SelectedAlarmRow());
        alarm.CooldownSeconds = Convert.ToInt32(alarmCooldownBox.Value);
        alarm.Speak = alarmSpeakCheckBox.Checked;
        alarm.SoundFile = SelectedSoundFile(alarmSoundBox);
        if (refreshListItem)
        {
            RefreshSelectedAlarmListItem();
        }
        SaveLivePreferences();
    }

    private void FocusAlarmName()
    {
        if (alarmNameBox == null || !alarmNameBox.Enabled)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        alarmNameBox.Focus();
        alarmNameBox.SelectAll();
    }

    private void FocusAlarmThreshold()
    {
        if (alarmThresholdBox == null || !alarmThresholdBox.Enabled)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        alarmThresholdBox.Focus();
        alarmThresholdBox.Select(0, alarmThresholdBox.Text.Length);
    }

    private void RefreshSelectedAlarmListItem()
    {
        if (alarmList == null)
        {
            return;
        }

        var index = alarmList.SelectedIndex;
        if (index < 0 || index >= alarmList.Items.Count)
        {
            return;
        }

        var choice = alarmList.Items[index] as AlarmChoice;
        if (choice == null || choice.Alarm == null)
        {
            alarmList.Refresh();
            return;
        }

        loadingPreferences = true;
        try
        {
            alarmList.Items[index] = new AlarmChoice(choice.Alarm, RowForKey, FormatAlarmThresholdForList);
            alarmList.SelectedIndex = index;
        }
        finally
        {
            loadingPreferences = false;
        }

        alarmList.Refresh();
        var notifyingList = alarmList as NotifyingListBox;
        if (notifyingList != null)
        {
            notifyingList.NotifyItemChanged(index);
        }
    }

    private string SelectedAlarmReadingKey()
    {
        var choice = alarmReadingBox == null ? null : alarmReadingBox.SelectedItem as TrayItemChoice;
        return choice == null ? "" : choice.Key;
    }

    private void SelectAlarmReading(string key)
    {
        if (alarmReadingBox == null || alarmReadingBox.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < alarmReadingBox.Items.Count; i++)
        {
            var choice = alarmReadingBox.Items[i] as TrayItemChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                alarmReadingBox.SelectedIndex = i;
                return;
            }
        }

        alarmReadingBox.SelectedIndex = 0;
    }

    private SensorRow SelectedAlarmRow()
    {
        var key = SelectedAlarmReadingKey();
        return RowForKey(key);
    }

    private SensorRow RowForKey(string key)
    {
        return rows.FirstOrDefault(r => string.Equals(SensorReadoutForm.RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedAlarmThresholdUnit()
    {
        return alarmThresholdUnitBox == null || alarmThresholdUnitBox.SelectedItem == null ? "" : alarmThresholdUnitBox.SelectedItem.ToString();
    }

    private void SelectAlarmThresholdUnit(string unit)
    {
        if (alarmThresholdUnitBox == null || alarmThresholdUnitBox.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < alarmThresholdUnitBox.Items.Count; i++)
        {
            if (string.Equals(alarmThresholdUnitBox.Items[i].ToString(), unit, StringComparison.OrdinalIgnoreCase))
            {
                alarmThresholdUnitBox.SelectedIndex = i;
                return;
            }
        }

        alarmThresholdUnitBox.SelectedIndex = 0;
    }

    private void RefreshAlarmThresholdUnitChoices(bool preserveBaseThreshold)
    {
        if (alarmThresholdUnitBox == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var row = SelectedAlarmRow();
            var alarm = SelectedAlarm();
            var previousUnit = alarm == null ? "" : alarm.ThresholdUnit;
            var units = AlarmThresholdUnits(row);
            var canKeepUnit = units.Any(u => string.Equals(u, previousUnit, StringComparison.OrdinalIgnoreCase));
            var readingChanged = !string.Equals(lastAlarmReadingKey, SelectedAlarmReadingKey(), StringComparison.OrdinalIgnoreCase);
            var shouldResetForNewReading = preserveBaseThreshold && alarm != null && (!canKeepUnit || readingChanged);
            var baseThreshold = preserveBaseThreshold && alarm != null && canKeepUnit && !readingChanged
                ? alarm.Threshold
                : AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), SelectedAlarmThresholdUnit(), row);
            if (shouldResetForNewReading)
            {
                baseThreshold = DefaultAlarmThresholdForRow(row);
                alarm.Threshold = baseThreshold;
            }
            alarmThresholdUnitBox.Items.Clear();
            alarmThresholdUnitBox.Items.AddRange(units.Cast<object>().ToArray());
            SelectAlarmThresholdUnit(string.IsNullOrWhiteSpace(previousUnit) ? units.FirstOrDefault() : previousUnit);
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(baseThreshold, SelectedAlarmThresholdUnit(), row), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void RefreshAlarmThresholdForSelectedUnit()
    {
        if (loadingPreferences)
        {
            return;
        }

        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(alarm.Threshold, SelectedAlarmThresholdUnit(), SelectedAlarmRow()), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private static string[] AlarmThresholdUnits(SensorRow row)
    {
        if (row == null)
        {
            return new[] { "" };
        }

        var name = (row.Name ?? "").ToLowerInvariant();
        var display = (row.DisplayValue ?? "").ToLowerInvariant();
        if (row.Type == "Temperature")
        {
            return new[] { "C", "F" };
        }

        if (row.Type == "Fan")
        {
            return new[] { "RPM" };
        }

        if (name.Contains("link speed"))
        {
            return new[] { "bits/s", "Kbit/s", "Mbit/s", "Gbit/s" };
        }

        if (name.Contains("rate") || display.Contains("/s"))
        {
            return new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        }

        if (display.Contains("%") || row.Type == "Load" || row.Type == "Performance" || row.Type == "Control")
        {
            return new[] { "%" };
        }

        if (display.Contains("gb") || display.Contains("mb") || display.Contains("kb") || display.Contains("tb") || name.Contains("space") || name.Contains("size") || name.Contains("memory"))
        {
            return new[] { "bytes", "KB", "MB", "GB", "TB", "%" };
        }

        return new[] { "value" };
    }

    private static double AlarmThresholdInputToBase(double value, string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim();
        if (string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return (value - 32.0) * 5.0 / 9.0;
        }

        return value * AlarmThresholdMultiplier(unit, row);
    }

    private static double AlarmThresholdBaseToInput(double value, string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim();
        if (string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return value * 9.0 / 5.0 + 32.0;
        }

        var multiplier = AlarmThresholdMultiplier(unit, row);
        return multiplier == 0 ? value : value / multiplier;
    }

    private string FormatAlarmThresholdForList(AlarmSetting alarm)
    {
        if (alarm == null)
        {
            return "";
        }

        var unit = alarm.ThresholdUnit ?? "";
        var row = RowForKey(alarm.ReadingKey);
        var value = AlarmThresholdBaseToInput(alarm.Threshold, unit, row);
        var suffix = string.IsNullOrWhiteSpace(unit) || unit == "value" ? "" : " " + unit;
        return Math.Round(value, 2).ToString("0.##") + suffix;
    }

    private static double AlarmThresholdMultiplier(string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim().ToUpperInvariant();
        if (unit == "BITS/S") return 1.0;
        if (unit == "KBIT/S") return 1000.0;
        if (unit == "MBIT/S") return 1000.0 * 1000.0;
        if (unit == "GBIT/S") return 1000.0 * 1000.0 * 1000.0;
        if (unit == "B/S") return 1.0;
        if (unit == "KB/S") return 1000.0;
        if (unit == "MB/S") return 1000.0 * 1000.0;
        if (unit == "GB/S") return 1000.0 * 1000.0 * 1000.0;
        if (unit == "KB") return 1024.0;
        if (unit == "MB") return 1024.0 * 1024.0;
        if (unit == "GB") return 1024.0 * 1024.0 * 1024.0;
        if (unit == "TB") return 1024.0 * 1024.0 * 1024.0 * 1024.0;
        return 1.0;
    }

    private static double DefaultAlarmThresholdForRow(SensorRow row)
    {
        if (row == null)
        {
            return 80;
        }

        if (row.Type == "Temperature")
        {
            return 80;
        }

        if (row.Type == "Fan")
        {
            return 1000;
        }

        var units = AlarmThresholdUnits(row);
        return units.Any(u => string.Equals(u, "%", StringComparison.OrdinalIgnoreCase)) ? 80 : 1;
    }

    private static int AlarmConditionIndex(string condition)
    {
        condition = SensorReadoutForm.NormalizeAlarmCondition(condition);
        if (condition == "Below") return 1;
        if (condition == "Equal") return 2;
        return 0;
    }

    private static string AlarmConditionFromIndex(int index)
    {
        if (index == 1) return "Below";
        if (index == 2) return "Equal";
        return "Above";
    }

    private void UpdateAlarmStatus(string message)
    {
        if (alarmStatusLabel != null)
        {
            alarmStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return minimum;
        }

        var decimalValue = Convert.ToDecimal(value);
        if (decimalValue < minimum) return minimum;
        if (decimalValue > maximum) return maximum;
        return decimalValue;
    }

    private List<AlarmSetting> CurrentAlarms()
    {
        return CloneAlarms(alarms)
            .Where(a => !string.IsNullOrWhiteSpace(a.ReadingKey))
            .ToList();
    }

    private static List<AlarmSetting> CloneAlarms(IEnumerable<AlarmSetting> source)
    {
        return (source ?? new List<AlarmSetting>())
            .Where(a => a != null)
            .Select(a => new AlarmSetting
            {
                Name = a.Name ?? "",
                ReadingKey = a.ReadingKey ?? "",
                Condition = SensorReadoutForm.NormalizeAlarmCondition(a.Condition),
                Threshold = a.Threshold,
                ThresholdUnit = a.ThresholdUnit ?? "",
                Enabled = a.Enabled,
                Speak = a.Speak,
                SoundFile = System.IO.Path.GetFileName(a.SoundFile ?? ""),
                CooldownSeconds = Math.Max(0, Math.Min(86400, a.CooldownSeconds))
            })
            .ToList();
    }
}
