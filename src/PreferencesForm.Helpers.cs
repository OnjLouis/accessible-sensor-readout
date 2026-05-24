using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private List<SpokenHotKeySetting> CurrentSpokenHotKeys()
    {
        return CloneSpokenHotKeys(spokenHotKeys)
            .Where(p => !string.IsNullOrWhiteSpace(p.HotKey) || (p.ReadingKeys != null && p.ReadingKeys.Count > 0) || !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }

    private static List<SpokenHotKeySetting> CloneSpokenHotKeys(IEnumerable<SpokenHotKeySetting> source)
    {
        return (source ?? new List<SpokenHotKeySetting>())
            .Where(p => p != null)
            .Select(p => new SpokenHotKeySetting
            {
                Name = p.Name ?? "",
                HotKey = SensorReadoutForm.NormalizeHotKeyText(p.HotKey),
                ReadingKeys = (p.ReadingKeys ?? new List<string>())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();
    }

    private List<FanProfileSetting> CurrentFanProfiles()
    {
        return CloneFanProfiles(fanProfiles)
            .Where(p => !string.IsNullOrWhiteSpace(p.HotKey) || !string.IsNullOrWhiteSpace(p.SoundFile) || p.ToggleAutomatic || !p.Speak || !string.IsNullOrWhiteSpace(p.SpeechMessage) || (p.Actions != null && p.Actions.Count > 0) || !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }

    private static FanProfileSetting CloneFanProfile(FanProfileSetting profile)
    {
        if (profile == null)
        {
            return null;
        }

        return new FanProfileSetting
        {
            Name = profile.Name ?? "",
            HotKey = SensorReadoutForm.NormalizeHotKeyText(profile.HotKey),
            SoundFile = System.IO.Path.GetFileName(profile.SoundFile ?? ""),
            ToggleAutomatic = profile.ToggleAutomatic,
            Speak = profile.Speak,
            SpeechMessage = profile.SpeechMessage ?? "",
            Actions = (profile.Actions ?? new List<FanProfileActionSetting>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.FanControlKey))
                .Select(CloneFanProfileAction)
                .ToList()
        };
    }

    private static List<FanProfileSetting> CloneFanProfiles(IEnumerable<FanProfileSetting> source)
    {
        return (source ?? new List<FanProfileSetting>())
            .Where(p => p != null)
            .Select(CloneFanProfile)
            .ToList();
    }

    private static FanProfileActionSetting CloneFanProfileAction(FanProfileActionSetting action)
    {
        if (action == null)
        {
            return new FanProfileActionSetting();
        }

        return new FanProfileActionSetting
        {
            FanControlKey = SensorReadoutForm.IdentifierFromSettingsKey(action.FanControlKey),
            Manual = action.Manual,
            Percent = Math.Max(0, Math.Min(100, action.Percent))
        };
    }

    private List<string> CurrentHiddenReadingKeys()
    {
        return hiddenItemsList.CheckedItems
            .Cast<object>()
            .Select(i => Convert.ToString(i))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
    }

    private Dictionary<string, string> CurrentReadingSpeechLabels()
    {
        return readingSpeechLabels
            .Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Value))
            .ToDictionary(i => i.Key, i => i.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string SelectedFanControlChoiceKey(ListBox list)
    {
        var choice = list == null ? null : list.SelectedItem as FanControlChoice;
        return choice == null ? "" : choice.Key;
    }

    private static bool ContainsFanControlChoice(ListBox list, string key)
    {
        if (list == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return list.Items.Cast<object>()
            .OfType<FanControlChoice>()
            .Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static void SelectFanControlChoiceByKey(ListBox list, string key)
    {
        if (list == null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            var choice = list.Items[i] as FanControlChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }
    }

    private static void AddStarterFanProfiles(List<FanProfileSetting> target)
    {
        if (target == null)
        {
            return;
        }

        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Everyday", "Everyday") });
        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Gaming/rendering", "Gaming/rendering") });
        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Reset to automatic", "Reset to automatic") });
    }

    private List<SensorRow> BuildFanProfileFanControlRows(IEnumerable<SensorRow> latestRows)
    {
        var source = (latestRows ?? Enumerable.Empty<SensorRow>()).ToList();
        var showStopped = liveSettings != null && liveSettings.ShowStoppedFans;
        var hiddenFanControlKeys = HiddenFanControlKeys(source);
        return source
            .Where(r => r.Type == "Fan Control")
            .Where(r => showStopped || ShouldShowFanProfileFanControl(r, source))
            .Where(r => showStopped || !hiddenFanControlKeys.Contains(r.Identifier ?? ""))
            .OrderBy(r => SensorReadoutForm.ControlSortKey(r.Identifier))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();
    }

    private HashSet<string> HiddenFanControlKeys(List<SensorRow> source)
    {
        var hidden = new HashSet<string>(hiddenReadingKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in (source ?? new List<SensorRow>()).Where(r => r.Type == "Fan"))
        {
            if (!hidden.Contains("row|" + SensorReadoutForm.RowSettingsKey(row)))
            {
                continue;
            }

            var controlIdentifier = SensorReadoutForm.GuessControlIdentifier(row.Identifier);
            if (!string.IsNullOrWhiteSpace(controlIdentifier))
            {
                keys.Add(controlIdentifier);
            }
        }

        foreach (var row in (source ?? new List<SensorRow>()).Where(r => r.Type == "Fan Control"))
        {
            if (hidden.Contains("row|" + SensorReadoutForm.RowSettingsKey(row)) && !string.IsNullOrWhiteSpace(row.Identifier))
            {
                keys.Add(row.Identifier);
            }
        }

        return keys;
    }

    private bool ShouldShowFanProfileFanControl(SensorRow control, List<SensorRow> source)
    {
        if (SensorReadoutForm.IsGpuControl(control == null ? "" : control.Identifier))
        {
            return true;
        }

        var rpm = GetFanRpmForControl(control == null ? "" : control.Identifier, source);
        return rpm.HasValue && rpm.Value > 0;
    }

    private float? GetFanRpmForControl(string controlIdentifier, List<SensorRow> source)
    {
        var sourceRows = source ?? new List<SensorRow>();
        var fanIdentifier = SensorReadoutForm.GuessFanIdentifier(controlIdentifier);
        var row = sourceRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(r.Identifier, fanIdentifier, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            return row.Value;
        }

        var baseName = SensorReadoutForm.BaseFanControlName(controlIdentifier);
        row = sourceRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(SensorReadoutForm.BaseFanControlName(r.Name), baseName, StringComparison.OrdinalIgnoreCase));
        return row == null ? (float?)null : row.Value;
    }

    private string FanProfileFanControlDisplayName(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        var baseName = SensorReadoutForm.BaseFanControlName(row.Name);
        string label;
        if (!fanLabels.TryGetValue(row.Identifier ?? "", out label) || string.IsNullOrWhiteSpace(label))
        {
            fanLabels.TryGetValue(SensorReadoutForm.GuessFanIdentifier(row.Identifier), out label);
        }
        label = string.IsNullOrWhiteSpace(label) ? baseName : label.Trim();

        var name = string.Equals(label, baseName, StringComparison.OrdinalIgnoreCase) ? baseName : label + ", " + baseName;
        var rpm = GetFanRpmForControl(row.Identifier, rows);
        if (rpm.HasValue)
        {
            name += ", " + Math.Round(rpm.Value, 0).ToString("0") + " RPM";
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            name += ", " + row.DisplayValue;
        }

        return name;
    }

    private sealed class FanControlChoice
    {
        public readonly string Key;
        public readonly string Hardware;
        public readonly string Name;
        public FanProfileActionSetting Action;
        private readonly bool unresolved;

        public FanControlChoice(SensorRow row, string displayName)
        {
            Key = row == null ? "" : row.Identifier ?? "";
            Hardware = row == null ? "" : row.Hardware ?? "";
            Name = string.IsNullOrWhiteSpace(displayName) ? row == null ? "" : row.Name ?? "" : displayName;
            Action = new FanProfileActionSetting { FanControlKey = Key, Manual = true, Percent = 50 };
        }

        private FanControlChoice(string key, FanProfileActionSetting action)
        {
            Key = key ?? "";
            Hardware = "";
            Name = "Missing fan control: " + Key;
            Action = CloneFanProfileAction(action);
            unresolved = true;
        }

        public static FanControlChoice Unresolved(FanProfileActionSetting action)
        {
            action = CloneFanProfileAction(action);
            return new FanControlChoice(action.FanControlKey, action);
        }

        public override string ToString()
        {
            if (unresolved)
            {
                return Name;
            }

            var actionText = Action == null || !Action.Manual ? "Auto" : Math.Max(0, Math.Min(100, Action.Percent)) + "%";
            return Name + " [" + actionText + "]";
        }

        public static int Compare(FanControlChoice left, FanControlChoice right)
        {
            var hardware = string.Compare(left == null ? "" : left.Hardware, right == null ? "" : right.Hardware, StringComparison.OrdinalIgnoreCase);
            if (hardware != 0) return hardware;
            return string.Compare(left == null ? "" : left.Name, right == null ? "" : right.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TrayItemChoice
    {
        public readonly string Key;
        public readonly string Type;
        public readonly string Hardware;
        public readonly string Name;
        private readonly Dictionary<string, string> speechLabels;
        private readonly Func<bool> includeDeviceNames;
        private readonly string label;
        private readonly bool unresolved;
        public bool ShowSpeechPreview;

        private TrayItemChoice(string key, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            Key = key ?? "";
            this.speechLabels = speechLabels ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.includeDeviceNames = includeDeviceNames ?? (delegate { return true; });
            var parts = Key.Split('|');
            Type = parts.Length > 0 ? parts[0] : "";
            Hardware = parts.Length > 1 ? parts[1] : "";
            Name = parts.Length > 2 ? parts[2] : "";
            unresolved = true;
            label = string.IsNullOrWhiteSpace(Hardware) ? Key : Hardware + " - " + Name;
        }

        public TrayItemChoice(SensorRow row, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            Key = SensorReadoutForm.RowSettingsKey(row);
            this.speechLabels = speechLabels ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.includeDeviceNames = includeDeviceNames ?? (delegate { return true; });
            Type = row.Type;
            Hardware = SensorReadoutForm.ShortHardwareName(row.Hardware);
            Name = SensorReadoutForm.CleanSensorName(row.Name);
            label = "";
        }

        public static TrayItemChoice Unresolved(string key, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            return new TrayItemChoice(key, speechLabels, includeDeviceNames);
        }

        public override string ToString()
        {
            var display = unresolved
                ? SensorReadoutForm.L("ui.Missing reading -", "Missing reading -") + " " + label
                : SensorReadoutForm.TrayChoiceLabel(Hardware, Name, Type);
            return ShowSpeechPreview
                ? display + " (" + SensorReadoutForm.L("ui.speaks:", "speaks:") + " " + SensorReadoutForm.SpeechPreviewLabel(Type, Hardware, Name, Key, speechLabels, includeDeviceNames()) + ")"
                : display;
        }

        public static int Compare(TrayItemChoice left, TrayItemChoice right)
        {
            var result = string.Compare(left.Hardware, right.Hardware, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            result = SensorReadoutForm.ReadingSortIndex(left.Name).CompareTo(SensorReadoutForm.ReadingSortIndex(right.Name));
            if (result != 0) return result;
            result = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            return string.Compare(left.Type, right.Type, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class AlarmChoice
    {
        private readonly Func<string, SensorRow> rowForKey;
        private readonly Func<AlarmSetting, string> thresholdText;
        public readonly AlarmSetting Alarm;

        public AlarmChoice(AlarmSetting alarm, Func<string, SensorRow> rowForKey, Func<AlarmSetting, string> thresholdText)
        {
            Alarm = alarm;
            this.rowForKey = rowForKey;
            this.thresholdText = thresholdText;
        }

        public override string ToString()
        {
            var alarm = Alarm;
            var name = alarm == null || string.IsNullOrWhiteSpace(alarm.Name) ? SensorReadoutForm.L("ui.Alarm", "Alarm") : alarm.Name.Trim();
            var condition = alarm == null || string.IsNullOrWhiteSpace(alarm.Condition) ? "Above" : SensorReadoutForm.NormalizeAlarmCondition(alarm.Condition);
            var threshold = thresholdText == null ? "" : thresholdText(alarm);
            var cooldown = alarm == null || alarm.CooldownSeconds <= 0 ? 0 : alarm.CooldownSeconds;
            return name + " (" + LocalizedAlarmCondition(condition) + " " + threshold + ", " + cooldown + SensorReadoutForm.L("ui.seconds suffix", "s") + " " + SensorReadoutForm.L("ui.cooldown", "cooldown") + ")";
        }
    }

    private static string LocalizedAlarmCondition(string condition)
    {
        condition = SensorReadoutForm.NormalizeAlarmCondition(condition);
        if (string.Equals(condition, "Below", StringComparison.OrdinalIgnoreCase))
        {
            return SensorReadoutForm.L("ui.Below", "Below");
        }

        if (string.Equals(condition, "Equal", StringComparison.OrdinalIgnoreCase))
        {
            return SensorReadoutForm.L("ui.Equal", "Equal");
        }

        return SensorReadoutForm.L("ui.Above", "Above");
    }

    private sealed class NotifyingListBox : ListBox
    {
        public void NotifyItemChanged(int index)
        {
            var childId = index + 1;
            AccessibilityNotifyClients(AccessibleEvents.NameChange, childId);
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, childId);
        }
    }
}
