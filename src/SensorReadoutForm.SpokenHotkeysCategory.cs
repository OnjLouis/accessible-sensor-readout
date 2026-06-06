using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private List<SensorRow> BuildSpokenHotKeyCategoryRows(List<SensorRow> sourceRows)
    {
        var rows = new List<SensorRow>();
        var source = (sourceRows ?? new List<SensorRow>()).Where(r => r != null).ToList();
        if (source.Count == 0)
        {
            return rows;
        }

        var lookup = source
            .Select(r => new { Key = RowSettingsKey(r), Row = r })
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Row, StringComparer.OrdinalIgnoreCase);

        AddSpokenHotKeyProfileRows(rows, lookup, T("ui.Notification area status", "Notification area status"), settings.SpeakTrayHotKey, settings.TrayItemKeys, settings.TraySpeechSkipsUnavailableReadings, "tray");

        var profiles = settings.SpokenHotKeys == null
            ? new List<SpokenHotKeySetting>()
            : settings.SpokenHotKeys
                .Where(p => p != null)
                .OrderBy(p => string.IsNullOrWhiteSpace(p.HotKey) ? 1 : 0)
                .ThenBy(p => MainFormHotKeySortKey(p.HotKey), StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var name = string.IsNullOrWhiteSpace(profile.Name)
                ? string.Format(T("ui.Spoken hotkey profile number", "Spoken hotkey {0}"), i + 1)
                : profile.Name.Trim();
            AddSpokenHotKeyProfileRows(rows, lookup, name, profile.HotKey, profile.ReadingKeys, profile.SkipUnavailableReadings, "profile-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return rows;
    }

    private void AddSpokenHotKeyProfileRows(List<SensorRow> output, Dictionary<string, SensorRow> lookup, string profileName, string hotKey, IEnumerable<string> readingKeys, bool skipUnavailable, string profileKey)
    {
        var keys = (readingKeys ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToList();
        if (keys.Count == 0)
        {
            output.Add(new SensorRow
            {
                Type = "Spoken Hotkeys",
                Hardware = profileName,
                Name = T("ui.Configured readings", "Configured readings"),
                Identifier = "spoken-hotkeys|" + profileKey + "|empty",
                DisplayValue = T("ui.None", "None"),
                Source = "Sensor Readout",
                Details = BuildSpokenHotKeyDetails(profileName, hotKey, skipUnavailable, 0, 0)
            });
            return;
        }

        var index = 0;
        foreach (var key in keys)
        {
            SensorRow row;
            lookup.TryGetValue(key, out row);
            index++;
            if (row == null)
            {
                output.Add(new SensorRow
                {
                    Type = "Spoken Hotkeys",
                    Hardware = profileName,
                    Name = T("ui.Missing reading", "Missing reading"),
                    Identifier = "spoken-hotkeys|" + profileKey + "|missing|" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DisplayValue = T("ui.Unavailable", "Unavailable"),
                    Source = "Sensor Readout",
                    Details = BuildSpokenHotKeyDetails(profileName, hotKey, skipUnavailable, keys.Count, index, key, null)
                });
                continue;
            }

            var rowKey = RowSettingsKey(row);
            output.Add(new SensorRow
            {
                Type = "Spoken Hotkeys",
                Hardware = profileName,
                Name = ShortSpeechReadingLabel(row, settings.SpeechIncludesDeviceNames, settings.ReadingSpeechLabels),
                Identifier = "spoken-hotkeys|" + profileKey + "|reading|" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + rowKey,
                Value = row.Value,
                DisplayValue = FormatValue(row),
                Source = "Sensor Readout",
                Details = BuildSpokenHotKeyDetails(profileName, hotKey, skipUnavailable, keys.Count, index, rowKey, row)
            });
        }
    }

    private Dictionary<string, string> BuildSpokenHotKeyDetails(string profileName, string hotKey, bool skipUnavailable, int readingCount, int readingIndex, string readingKey = "", SensorRow sourceRow = null)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Profile", profileName);
        AddDetail(details, "Hotkey", string.IsNullOrWhiteSpace(hotKey) ? T("ui.no hotkey", "no hotkey") : hotKey);
        AddDetail(details, "Configured readings", readingCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (readingIndex > 0)
        {
            AddDetail(details, "Reading order", readingIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        AddDetail(details, "Skip unavailable readings", skipUnavailable ? T("ui.On", "On") : T("ui.Off", "Off"));
        if (!string.IsNullOrWhiteSpace(readingKey))
        {
            AddDetail(details, "Reading key", readingKey);
        }

        if (sourceRow != null)
        {
            AddDetail(details, "Original category", DisplayTypeName(sourceRow.Type));
            AddDetail(details, "Original group", sourceRow.Hardware);
            AddDetail(details, "Original reading", DisplayReadingName(sourceRow.Name));
            AddDetail(details, "Original source", sourceRow.Source);
        }

        AddDetail(details, "Visibility note", T("details.Spoken Hotkeys visibility note", "Hiding this row only hides it from the main tree. It does not remove the reading from the spoken hotkey profile."));
        return details;
    }

    private static string ShortSpeechReadingLabel(SensorRow row, bool includeHardwareName, Dictionary<string, string> speechLabels)
    {
        if (row == null)
        {
            return "";
        }

        string custom;
        var key = RowSettingsKey(row);
        if (speechLabels != null && speechLabels.TryGetValue(key, out custom) && !string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        var hardware = ShortTrayHardware(row.Hardware);
        var name = ShortTrayName(row.Name);
        if (row.Type == "Temperature")
        {
            return name;
        }

        if (row.Type == "Network")
        {
            return includeHardwareName ? hardware + " " + name : name;
        }

        if (row.Type == "USB")
        {
            return includeHardwareName ? hardware : DisplayReadingName(row.Name);
        }

        if (row.Type == "Performance" || row.Type == "SMART")
        {
            if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            return includeHardwareName ? hardware + " " + name : name;
        }

        return name;
    }

    private static string MainFormHotKeySortKey(string hotKey)
    {
        var normalized = NormalizeHotKeyText(hotKey);
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

        return modifierRank + "|" + MainFormBaseHotKeySortKey(key) + "|" + normalized;
    }

    private static string MainFormBaseHotKeySortKey(string key)
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
}
