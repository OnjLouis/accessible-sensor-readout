using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private sealed class AlarmPresetChoice
    {
        public string Name;
        public string Description;
        public SensorRow Row;
        public string Condition;
        public double Threshold;
        public string Unit;

        public override string ToString()
        {
            return Name + ": " + Description;
        }
    }

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

    private void ShowAlarmPresetsDialog()
    {
        var presets = BuildAvailableAlarmPresets();
        if (presets.Count == 0)
        {
            UpdateAlarmStatus(SensorReadoutForm.L("status.No alarm presets are available for the current readings.", "No alarm presets are available for the current readings."));
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        using (var dialog = new Form())
        {
            dialog.Text = SensorReadoutForm.L("ui.Alarm presets", "Alarm presets");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(640, 420);
            dialog.MinimumSize = new Size(460, 280);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = SensorReadoutForm.L("ui.Choose alarm presets to add:", "Choose alarm presets to add:"),
                AutoSize = true,
                Dock = DockStyle.Fill
            }, 0, 0);

            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                AccessibleName = SensorReadoutForm.L("a11y.Alarm presets", "Alarm presets"),
                AccessibleDescription = SensorReadoutForm.L("a11y.Check the alarm presets to create.", "Check the alarm presets to create.")
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

            var checkedPresets = list.CheckedItems.Cast<object>().OfType<AlarmPresetChoice>().ToList();
            var added = 0;
            var previousLoading = loadingPreferences;
            loadingPreferences = true;
            try
            {
                foreach (var preset in checkedPresets)
                {
                    if (AddAlarmPreset(preset))
                    {
                        added++;
                    }
                }
            }
            finally
            {
                loadingPreferences = previousLoading;
            }

            if (alarmList != null && alarmList.Items.Count > 0)
            {
                alarmList.SelectedIndex = alarmList.Items.Count - 1;
                LoadSelectedAlarm();
                alarmList.Focus();
            }

            UpdateAlarmStatus(string.Format(SensorReadoutForm.L("status.Added alarm presets:", "Added alarm presets: {0}."), added));
            SaveLivePreferences();
        }
    }

    private bool AddAlarmPreset(AlarmPresetChoice preset)
    {
        if (preset == null || preset.Row == null)
        {
            return false;
        }

        var key = SensorReadoutForm.RowSettingsKey(preset.Row);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (alarms.Any(a => a != null && string.Equals(a.ReadingKey, key, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var alarm = new AlarmSetting
        {
            Name = preset.Name,
            ReadingKey = key,
            Condition = preset.Condition,
            Threshold = AlarmThresholdInputToBase(preset.Threshold, preset.Unit, preset.Row),
            ThresholdUnit = preset.Unit,
            Enabled = true,
            Speak = true,
            SoundFile = "",
            CooldownSeconds = 60
        };
        alarms.Add(alarm);
        alarmList.Items.Add(new AlarmChoice(alarm, RowForKey, FormatAlarmThresholdForList));
        alarmList.SelectedIndex = alarmList.Items.Count - 1;
        return true;
    }

    private List<AlarmPresetChoice> BuildAvailableAlarmPresets()
    {
        var result = new List<AlarmPresetChoice>();
        AddPresetIfFound(result, "Wi-Fi disconnected", "Alert when a Wi-Fi adapter reports disconnected.", FindRow("Network", "Wi-Fi connected", null), "Equal", 0, "value");
        AddPresetIfFound(result, "Low Wi-Fi signal", "Alert when Wi-Fi signal strength is 30% or lower.", FindRow("Network", "Wi-Fi signal strength", null), "Below", 30, "%");
        AddPresetIfFound(result, "Battery low", "Alert when battery charge is 20% or lower.", FindBatteryChargeRow(), "Below", 20, "%");
        AddPresetIfFound(result, "CPU usage high", "Alert when CPU usage reaches 90%.", FindRow("Performance", "CPU usage", null), "Above", 90, "%");
        AddPresetIfFound(result, "Memory usage high", "Alert when memory usage reaches 90%.", FindRow("Performance", "Memory used", "Memory"), "Above", 90, "%");
        AddPresetIfFound(result, "System uptime long", "Alert when Windows has been running for 7 days.", FindRow("Performance", "System uptime", null), "Above", 7, "days");
        AddPresetIfFound(result, "CPU temperature high", "Alert when CPU temperature reaches 85 C.", FindTemperatureRow("cpu"), "Above", 85, "C");
        AddPresetIfFound(result, "GPU temperature high", "Alert when GPU temperature reaches 85 C.", FindTemperatureRow("gpu"), "Above", 85, "C");
        AddPresetIfFound(result, "GPU memory free low", "Alert when dedicated GPU memory free drops below 1 GB.", FindRow("Performance", "Dedicated GPU memory free", "GPU memory"), "Below", 1, "GB");
        AddPresetIfFound(result, "Disk health low", "Alert when a disk health or remaining-life percentage drops below 90%.", FindDiskHealthRow(), "Below", 90, "%");
        AddPresetIfFound(result, "Disk free space low", "Alert when a drive reports 10% free space or less.", FindRow("Performance", "Free space", null), "Below", 10, "%");
        AddPresetIfFound(result, "Disk activity high", "Alert when a drive reports 90% total activity.", FindRow("Performance", "Total activity", null), "Above", 90, "%");
        AddPresetIfFound(result, "Printer issue", "Alert when a printer issue count or offline flag is reported.", FindPrinterIssueRow(), "Above", 0, "value");
        return result;
    }

    private void AddPresetIfFound(List<AlarmPresetChoice> presets, string name, string description, SensorRow row, string condition, double threshold, string unit)
    {
        if (row == null || !row.Value.HasValue)
        {
            return;
        }

        presets.Add(new AlarmPresetChoice
        {
            Name = SensorReadoutForm.L("ui.Alarm preset " + name, name),
            Description = SensorReadoutForm.L("ui.Alarm preset description " + name, description),
            Row = row,
            Condition = condition,
            Threshold = threshold,
            Unit = unit
        });
    }

    private SensorRow FindRow(string type, string name, string hardwareContains)
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            r.Value.HasValue &&
            (string.IsNullOrWhiteSpace(type) || string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(name) || string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(hardwareContains) || (r.Hardware ?? "").IndexOf(hardwareContains, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private SensorRow FindTemperatureRow(string hardwareOrName)
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            r.Value.HasValue &&
            string.Equals(r.Type, "Temperature", StringComparison.OrdinalIgnoreCase) &&
            (((r.Hardware ?? "").IndexOf(hardwareOrName, StringComparison.OrdinalIgnoreCase) >= 0) ||
             ((r.Name ?? "").IndexOf(hardwareOrName, StringComparison.OrdinalIgnoreCase) >= 0)));
    }

    private SensorRow FindBatteryChargeRow()
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            r.Value.HasValue &&
            string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase) &&
            ((r.DisplayValue ?? "").Contains("%") || (r.Name ?? "").IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private SensorRow FindDiskHealthRow()
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            r.Value.HasValue &&
            string.Equals(r.Type, "SMART", StringComparison.OrdinalIgnoreCase) &&
            ((r.DisplayValue ?? "").Contains("%") || (r.Name ?? "").IndexOf("health", StringComparison.OrdinalIgnoreCase) >= 0 || (r.Name ?? "").IndexOf("life", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private SensorRow FindPrinterIssueRow()
    {
        return rows.FirstOrDefault(r =>
            r != null &&
            r.Value.HasValue &&
            string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) &&
            ((r.Hardware ?? "").IndexOf("printer", StringComparison.OrdinalIgnoreCase) >= 0 || (r.Name ?? "").IndexOf("printer", StringComparison.OrdinalIgnoreCase) >= 0) &&
            (((r.Name ?? "").IndexOf("issue", StringComparison.OrdinalIgnoreCase) >= 0) ||
             ((r.Name ?? "").IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0) ||
             ((r.Name ?? "").IndexOf("queue", StringComparison.OrdinalIgnoreCase) >= 0)));
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
            alarmSpokenMessageBox.Enabled = enabled && (alarm == null || alarm.Speak);
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
            alarmSpokenMessageBox.Text = alarm == null ? "" : alarm.SpokenMessage ?? "";
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
        alarm.SpokenMessage = alarmSpokenMessageBox.Text.Trim();
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

        var focusedControl = FindFocusedControl(this);
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

        RestoreAlarmRefreshFocus(focusedControl);
    }

    private static Control FindFocusedControl(Control root)
    {
        if (root == null)
        {
            return null;
        }

        if (root.Focused)
        {
            return root;
        }

        foreach (Control child in root.Controls)
        {
            if (!child.ContainsFocus)
            {
                continue;
            }

            var focused = FindFocusedControl(child);
            if (focused != null)
            {
                return focused;
            }
        }

        return null;
    }

    private void RestoreAlarmRefreshFocus(Control focusedControl)
    {
        if (focusedControl == null || focusedControl.IsDisposed)
        {
            return;
        }

        var shouldRestore =
            focusedControl == alarmList ||
            focusedControl == alarmEnabledCheckBox ||
            focusedControl == alarmNameBox ||
            focusedControl == alarmReadingBox ||
            focusedControl == alarmConditionBox ||
            focusedControl == alarmThresholdBox ||
            focusedControl == alarmThresholdUnitBox ||
            focusedControl == alarmCooldownBox ||
            focusedControl == alarmSpeakCheckBox ||
            focusedControl == alarmSpokenMessageBox ||
            focusedControl == alarmSoundBox;

        if (!shouldRestore)
        {
            return;
        }

        BeginInvoke((MethodInvoker)delegate
        {
            if (focusedControl != null && !focusedControl.IsDisposed && focusedControl.CanFocus)
            {
                focusedControl.Focus();
            }
        });
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
            var baseThreshold = alarm == null
                ? AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), SelectedAlarmThresholdUnit(), row)
                : preserveBaseThreshold && (!canKeepUnit || readingChanged)
                    ? AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), SelectedAlarmThresholdUnit(), row)
                    : alarm.Threshold;
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

        if ((row.Name ?? "").Equals("System uptime", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "minutes", "hours", "days" };
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
        if (unit == "MINUTES") return 1.0;
        if (unit == "HOURS") return 60.0;
        if (unit == "DAYS") return 60.0 * 24.0;
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
                SpokenMessage = a.SpokenMessage ?? "",
                SoundFile = System.IO.Path.GetFileName(a.SoundFile ?? ""),
                CooldownSeconds = Math.Max(0, Math.Min(86400, a.CooldownSeconds))
            })
            .ToList();
    }
}
