using System;
using System.Collections.Generic;
using System.Linq;

public sealed partial class SensorReadoutForm
{
    private sealed class ReadingKeyParts
    {
        public string Type = "";
        public string Hardware = "";
        public string Name = "";
        public string Identifier = "";
    }

    internal static SensorRow ResolveReadingKeyAgainstRows(string key, IEnumerable<SensorRow> availableRows)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var rows = (availableRows ?? Enumerable.Empty<SensorRow>()).Where(r => r != null).ToList();
        var exact = SingleReadingMatch(rows.Where(r => string.Equals(RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase)));
        if (exact != null)
        {
            return exact;
        }

        ReadingKeyParts source;
        if (!TryParseReadingSettingsKey(key, out source))
        {
            return null;
        }

        var sourceName = CleanSensorName(source.Name);
        var compatible = rows.Where(r => string.Equals(r.Type ?? "", source.Type, StringComparison.OrdinalIgnoreCase)).ToList();

        // Identifiers such as logicaldisk/C:/read are deliberately independent of a volume label.
        if (!string.IsNullOrWhiteSpace(source.Identifier))
        {
            var identifierMatch = SingleReadingMatch(compatible.Where(r =>
                string.Equals(r.Identifier ?? "", source.Identifier, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanSensorName(r.Name), sourceName, StringComparison.OrdinalIgnoreCase)));
            if (identifierMatch != null)
            {
                return identifierMatch;
            }
        }

        var sourceDrive = DriveLetterFromReadingParts(source.Hardware, source.Name, source.Identifier);
        if (!string.IsNullOrWhiteSpace(sourceDrive))
        {
            return SingleReadingMatch(compatible.Where(r =>
                string.Equals(DriveLetterFromRow(r), sourceDrive, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanSensorName(r.Name), sourceName, StringComparison.OrdinalIgnoreCase)));
        }

        var normalizedHardware = NormalizeHardwareName(source.Hardware);
        var sameHardware = SingleReadingMatch(compatible.Where(r =>
            string.Equals(NormalizeHardwareName(r.Hardware), normalizedHardware, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CleanSensorName(r.Name), sourceName, StringComparison.OrdinalIgnoreCase)));
        if (sameHardware != null)
        {
            return sameHardware;
        }

        var sourceClass = PortableReadingClass(source.Type, source.Hardware, sourceName, source.Identifier);
        if (string.IsNullOrWhiteSpace(sourceClass))
        {
            return null;
        }

        var sameMeaning = compatible.Where(r =>
            string.Equals(PortableReadingClass(r.Type, r.Hardware, CleanSensorName(r.Name), r.Identifier), sourceClass, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CleanSensorName(r.Name), sourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        var semanticMatch = SingleReadingMatch(sameMeaning);
        if (semanticMatch != null)
        {
            return semanticMatch;
        }

        if (!string.Equals(sourceClass, "cpu-primary-temperature", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var primaryTemperatures = compatible
            .Where(r => string.Equals(PortableReadingClass(r.Type, r.Hardware, CleanSensorName(r.Name), r.Identifier), sourceClass, StringComparison.OrdinalIgnoreCase))
            .Select(r => new { Row = r, Rank = PrimaryCpuTemperatureRank(CleanSensorName(r.Name)) })
            .Where(item => item.Rank > 0)
            .OrderByDescending(item => item.Rank)
            .ToList();
        if (primaryTemperatures.Count == 0 || primaryTemperatures.Count(item => item.Rank == primaryTemperatures[0].Rank) != 1)
        {
            return null;
        }

        return primaryTemperatures[0].Row;
    }

    private static SensorRow SingleReadingMatch(IEnumerable<SensorRow> matches)
    {
        var result = (matches ?? Enumerable.Empty<SensorRow>()).Take(2).ToList();
        return result.Count == 1 ? result[0] : null;
    }

    private static bool TryParseReadingSettingsKey(string key, out ReadingKeyParts parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var first = key.IndexOf('|');
        var second = first < 0 ? -1 : key.IndexOf('|', first + 1);
        var third = second < 0 ? -1 : key.IndexOf('|', second + 1);
        if (first < 0 || second < 0)
        {
            return false;
        }

        parts = new ReadingKeyParts
        {
            Type = key.Substring(0, first),
            Hardware = key.Substring(first + 1, second - first - 1),
            Name = third < 0 ? key.Substring(second + 1) : key.Substring(second + 1, third - second - 1),
            Identifier = third < 0 ? "" : key.Substring(third + 1)
        };
        return !string.IsNullOrWhiteSpace(parts.Type) && !string.IsNullOrWhiteSpace(parts.Name);
    }

    private static string PortableReadingClass(string type, string hardware, string name, string identifier)
    {
        var cleanType = (type ?? "").Trim();
        var cleanHardware = (hardware ?? "").Trim();
        var cleanName = CleanSensorName(name);
        var drive = DriveLetterFromReadingParts(cleanHardware, cleanName, identifier);
        if (!string.IsNullOrWhiteSpace(drive))
        {
            return "drive:" + drive;
        }

        if (string.Equals(cleanType, "Temperature", StringComparison.OrdinalIgnoreCase) && IsPrimaryCpuTemperatureName(cleanName))
        {
            return "cpu-primary-temperature";
        }

        if (string.Equals(cleanType, "Performance", StringComparison.OrdinalIgnoreCase) && cleanName.Equals("CPU usage", StringComparison.OrdinalIgnoreCase))
        {
            return "cpu";
        }

        if (string.Equals(cleanType, "Tasks", StringComparison.OrdinalIgnoreCase))
        {
            return "tasks";
        }

        if (string.Equals(cleanType, "Battery", StringComparison.OrdinalIgnoreCase))
        {
            if ((identifier ?? "").StartsWith("device-battery/", StringComparison.OrdinalIgnoreCase))
            {
                return "battery:device";
            }

            if ((identifier ?? "").StartsWith("battery/", StringComparison.OrdinalIgnoreCase) ||
                (identifier ?? "").StartsWith("acpi-battery", StringComparison.OrdinalIgnoreCase))
            {
                return "battery:system";
            }

            return "battery:other";
        }

        if (string.Equals(cleanHardware, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            return "memory";
        }

        if (string.Equals(cleanHardware, "Overview", StringComparison.OrdinalIgnoreCase))
        {
            return "overview";
        }

        if (IsGpuReadingHardware(cleanHardware, cleanName))
        {
            return "gpu";
        }

        if (string.Equals(cleanType, "Network", StringComparison.OrdinalIgnoreCase))
        {
            if (cleanName.StartsWith("Wi-Fi ", StringComparison.OrdinalIgnoreCase) ||
                cleanHardware.IndexOf("wi-fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanHardware.IndexOf("wifi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanHardware.IndexOf("wlan", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "network:wifi";
            }
            if (cleanHardware.IndexOf("ethernet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "network:ethernet";
            }

            if (IsPortableNetworkAdapterReading(cleanName))
            {
                return "network:adapter";
            }
        }

        return "";
    }

    private static bool IsPortableNetworkAdapterReading(string name)
    {
        var clean = CleanSensorName(name);
        return clean.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Link speed", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Receive rate", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Send rate", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Data received", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Data sent", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("IP address", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGpuReadingHardware(string hardware, string name)
    {
        return hardware.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
            hardware.Equals("GPU memory", StringComparison.OrdinalIgnoreCase) ||
            hardware.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("geforce", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("graphics", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrimaryCpuTemperatureName(string name)
    {
        var clean = CleanSensorName(name);
        return clean.Equals("CPU package", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("CPU (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("CPU temperature", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Tctl", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Tdie", StringComparison.OrdinalIgnoreCase);
    }

    private static int PrimaryCpuTemperatureRank(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("CPU package", StringComparison.OrdinalIgnoreCase)) return 60;
        if (clean.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase)) return 55;
        if (clean.Equals("CPU (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase)) return 50;
        if (clean.Equals("CPU temperature", StringComparison.OrdinalIgnoreCase)) return 45;
        if (clean.Equals("Tctl", StringComparison.OrdinalIgnoreCase)) return 40;
        if (clean.Equals("Tdie", StringComparison.OrdinalIgnoreCase)) return 35;
        return 0;
    }

    private static string DriveLetterFromRow(SensorRow row)
    {
        return row == null ? "" : DriveLetterFromReadingParts(row.Hardware, row.Name, row.Identifier);
    }

    private static string DriveLetterFromReadingParts(string hardware, string name, string identifier)
    {
        var logicalDisk = (identifier ?? "").IndexOf("logicaldisk/", StringComparison.OrdinalIgnoreCase);
        if (logicalDisk >= 0)
        {
            var position = logicalDisk + "logicaldisk/".Length;
            if (position + 1 < identifier.Length && char.IsLetter(identifier[position]) && identifier[position + 1] == ':')
            {
                return char.ToUpperInvariant(identifier[position]).ToString();
            }
        }

        foreach (var value in new[] { hardware ?? "", name ?? "" })
        {
            var trimmed = value.TrimStart();
            if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            {
                return char.ToUpperInvariant(trimmed[0]).ToString();
            }
        }

        return "";
    }
}
