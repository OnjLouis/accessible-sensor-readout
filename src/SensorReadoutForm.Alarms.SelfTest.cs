using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private void SelfTestAlarmRepetition()
    {
        var savedAlarms = settings.Alarms;
        var savedTrayEnabled = settings.TrayStatusEnabled;
        var savedRows = latestRows;
        try
        {
            settings.TrayStatusEnabled = false;
            var row = new SensorRow { Type = "SMART", Hardware = "Test disk", Name = "Health", Identifier = "alarm-test-health", Value = 80, DisplayValue = "80%" };
            SetLatestRows(new[] { row });
            var alarm = new AlarmSetting { Name = "Health warning", ReadingKey = RowSettingsKey(row), Condition = "Below", Threshold = 90, Speak = false, RepeatWhileActive = false, CooldownSeconds = 0 };
            settings.Alarms = new List<AlarmSetting> { alarm };
            CheckAlarms(latestRows);
            Require(alarmTriggerStates.Count == 1, "Alarm runtime state was not created.");
            var initialState = alarmTriggerStates.Values.Single();
            var later = DateTime.UtcNow.AddDays(1);
            Require(!initialState.ShouldTrigger(true, false, 0, later), "One-time alarm did not latch after notification.");
            row.Value = float.NaN;
            CheckAlarms(latestRows);
            row.Value = float.PositiveInfinity;
            CheckAlarms(latestRows);
            CheckAlarms(new List<SensorRow>());
            CheckAlarms(null);
            Require(!initialState.ShouldTrigger(true, false, 0, later), "Invalid or absent data rearmed a latched alarm.");

            SaveSettings(settings);
            settings.Alarms = LoadSettings().Alarms;
            Require(!settings.Alarms[0].RepeatWhileActive, "Save/reload lost one-time mode.");
            CheckAlarms(latestRows);
            Require(object.ReferenceEquals(initialState, alarmTriggerStates.Values.Single()), "Unchanged preference reload reset an alarm.");
            Require(JsonConvert.DeserializeObject<AlarmSetting>("{}").RepeatWhileActive, "Legacy alarms lost their recurring reminders.");
            row.Value = 80;

            using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Alarms"))
            {
                SetPrivateField(preferences, "loadingPreferences", false);
                var repeat = (CheckBox)typeof(PreferencesForm).GetField("alarmRepeatCheckBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(preferences);
                Require(!repeat.Checked && repeat.Enabled && repeat.UseMnemonic, "Alarm repeat checkbox has the wrong state or lacks a mnemonic.");
                Require(repeat.AccessibilityObject.Role == AccessibleRole.CheckButton, "Alarm repeat option is not exposed as a checkbox.");
                repeat.Checked = true;
                var cloned = preferences.Alarms;
                Require(cloned != null && cloned.Count == 1 && cloned[0].RepeatWhileActive, "Preferences did not save repeat mode.");
                repeat.Checked = false;
                cloned = preferences.Alarms;
                Require(!cloned[0].RepeatWhileActive, "Preferences cloning lost one-time mode.");
                InvokePrivate(preferences, "AddAlarm");
                cloned = preferences.Alarms;
                Require(!cloned.Last().RepeatWhileActive, "New alarms should default to one-time warnings.");
            }

            settings.Alarms = new List<AlarmSetting> { alarm };
            row.Value = 80;
            ApplySettingsTransferPackage(new SettingsTransferPackage { MachineSettings = ExtractMachineSettings(settings) }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TransferAlarms });
            Require(settings.Alarms.Count == 1 && !settings.Alarms[0].RepeatWhileActive && !settings.Alarms[0].Enabled, "Imported alarm lost one-time mode or was enabled without review.");
            settings.Alarms = new List<AlarmSetting> { alarm, JsonConvert.DeserializeObject<AlarmSetting>(JsonConvert.SerializeObject(alarm)) };
            CheckAlarms(latestRows);
            Require(alarmTriggerStates.Count == 2 && alarmTriggerStates.Values.Distinct().Count() == 2, "Identical alarms share notification state.");
            settings.Alarms[1].Threshold = 85;
            CheckAlarms(latestRows);
            Require(alarmTriggerStates.Count == 2, "Editing an alarm leaked obsolete state.");
            alarm.Enabled = false;
            settings.Alarms.RemoveAt(1);
            PruneAlarmTriggerStates(ActiveAlarmsByKey());
            Require(alarmTriggerStates.Count == 0, "Disabled or removed alarms retained runtime state.");
            alarm.Enabled = true;
            CheckAlarms(latestRows);
            Require(alarmTriggerStates.Count == 1 && !object.ReferenceEquals(initialState, alarmTriggerStates.Values.Single()), "Reenabled alarm did not start a fresh episode.");
            row.Value = 95;
            CheckAlarms(latestRows);
            Require(alarmTriggerStates.Values.Single().ShouldTrigger(true, false, 0, later), "A valid healthy sample did not rearm the alarm.");
        }
        finally
        {
            alarmTriggerStates.Clear();
            settings.Alarms = savedAlarms;
            settings.TrayStatusEnabled = savedTrayEnabled;
            SetLatestRows(savedRows);
            SaveSettings(settings);
        }
    }
}
