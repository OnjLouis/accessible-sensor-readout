using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private IEnumerable<SensorRow> GetLibreHardwareMonitorSensors(bool includeSlowHardware)
    {
        try
        {
            if (!System.Threading.Monitor.TryEnter(lhmLock))
            {
                LogMessage("Debug", "LibreHardwareMonitor refresh skipped because a previous LibreHardwareMonitor refresh is still running.");
                return GetCachedLibreHardwareMonitorRowsSnapshot();
            }

            try
            {
                EnsureLibreHardwareMonitorComputerOpen();

                foreach (var hardware in lhmComputer.Hardware)
                {
                    UpdateHardware(hardware, includeSlowHardware);
                }

                var freshRows = lhmComputer.Hardware
                    .SelectMany(hardware => ReadLibreHardwareMonitorSensors(hardware, includeSlowHardware))
                    .ToList();
                UpdateCachedLhmRows(freshRows, includeSlowHardware);
                return BuildLibreHardwareMonitorRowsFromCache(freshRows, includeSlowHardware);
            }
            finally
            {
                System.Threading.Monitor.Exit(lhmLock);
            }
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private List<SensorRow> GetCachedLibreHardwareMonitorRowsSnapshot()
    {
        lock (lhmRowsLock)
        {
            return cachedLhmRows.Select(CloneSensorRow).ToList();
        }
    }

    private void EnsureLibreHardwareMonitorComputerOpen()
    {
        if (lhmComputer != null)
        {
            return;
        }

        lhmComputer = CreateLibreHardwareMonitorComputer();
        lhmComputer.Open();
    }

    private static void UpdateHardware(IHardware hardware, bool includeSlowHardware)
    {
        if (!includeSlowHardware && IsSlowLibreHardwareMonitorHardware(hardware))
        {
            return;
        }

        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateHardware(subHardware, includeSlowHardware);
        }
    }

    private static void UpdateHardware(IHardware hardware)
    {
        UpdateHardware(hardware, true);
    }

    private static bool IsSlowLibreHardwareMonitorHardware(IHardware hardware)
    {
        if (hardware == null)
        {
            return true;
        }

        return hardware.HardwareType == HardwareType.Storage ||
            hardware.HardwareType == HardwareType.Network ||
            hardware.HardwareType == HardwareType.Memory;
    }

    private IEnumerable<SensorRow> BuildLibreHardwareMonitorRowsFromCache(List<SensorRow> freshRows, bool includeSlowHardware)
    {
        if (includeSlowHardware)
        {
            return freshRows;
        }

        lock (lhmRowsLock)
        {
            if (cachedLhmRows.Count == 0)
            {
                return freshRows;
            }

            var freshKeys = new HashSet<string>(freshRows.Select(SensorDeduplicationKey), StringComparer.OrdinalIgnoreCase);
            return freshRows
                .Concat(cachedLhmRows.Where(row => row != null && !freshKeys.Contains(SensorDeduplicationKey(row))).Select(CloneSensorRow))
                .ToList();
        }
    }

    private void UpdateCachedLhmRows(List<SensorRow> freshRows, bool includeSlowHardware)
    {
        lock (lhmRowsLock)
        {
            if (includeSlowHardware || cachedLhmRows.Count == 0)
            {
                cachedLhmRows = freshRows.Select(CloneSensorRow).ToList();
                cachedLhmRowsUtc = DateTime.UtcNow;
                return;
            }

            var freshKeys = new HashSet<string>(freshRows.Select(SensorDeduplicationKey), StringComparer.OrdinalIgnoreCase);
            cachedLhmRows = freshRows
                .Concat(cachedLhmRows.Where(row => row != null && !freshKeys.Contains(SensorDeduplicationKey(row))).Select(CloneSensorRow))
                .ToList();
            cachedLhmRowsUtc = DateTime.UtcNow;
        }
    }

    private static IEnumerable<SensorRow> ReadLibreHardwareMonitorSensors(IHardware hardware, bool includeSlowHardware)
    {
        if (!includeSlowHardware && IsSlowLibreHardwareMonitorHardware(hardware))
        {
            yield break;
        }

        foreach (var sensor in hardware.Sensors)
        {
            var sensorType = sensor.SensorType.ToString();
            var hardwareType = hardware.HardwareType.ToString();
            var isStorage = hardware.HardwareType == HardwareType.Storage;
            var type = sensorType == "Fan" ? "Fan" : sensorType == "Control" && sensor.Control != null ? "Fan Control" : sensorType == "Temperature" ? "Temperature" : isStorage ? "SMART" : "";
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (!sensor.Value.HasValue && type != "Fan Control")
            {
                continue;
            }

            var value = sensor.Value;
            var numericValue = value.GetValueOrDefault();
            if (type == "Temperature" && numericValue <= 0)
            {
                continue;
            }

            if (isStorage && IsLibreHardwareMonitorFileSystemSpaceSensor(sensor.Name))
            {
                continue;
            }

            if (isStorage && IsLibreHardwareMonitorStorageVolatileCounter(sensor.Name))
            {
                continue;
            }

            yield return new SensorRow
            {
                Type = type,
                Hardware = GetLibreHardwareMonitorRowHardware(hardware, type, isStorage),
                Name = isStorage ? CleanStorageSensorName(sensor.Name, sensorType) : sensor.Name,
                Identifier = sensor.Identifier == null ? "" : sensor.Identifier.ToString(),
                Value = value,
                DisplayValue = type == "Temperature" ? null : type == "Fan Control" ? FormatLibreHardwareMonitorControlValue(sensor) : isStorage ? FormatLibreHardwareMonitorStorageValue(hardware.Name, sensorType, sensor.Name, numericValue) : null,
                Source = "LibreHardwareMonitor"
            };
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var row in ReadLibreHardwareMonitorSensors(subHardware, includeSlowHardware))
            {
                yield return row;
            }
        }
    }

    private static string GetLibreHardwareMonitorRowHardware(IHardware hardware, string type, bool isStorage)
    {
        if (type == "Fan Control")
        {
            return "Fan controls";
        }

        if (isStorage)
        {
            return NormalizeStorageHardwareName(hardware.Name ?? "");
        }

        var hardwareName = hardware.Name ?? "";
        if (hardware.HardwareType.ToString().Equals("SuperIO", StringComparison.OrdinalIgnoreCase) &&
            IsMotherboardSuperIoName(hardwareName))
        {
            return "Motherboard";
        }

        return NormalizeHardwareName(hardwareName);
    }

    private static bool IsMotherboardSuperIoName(string hardwareName)
    {
        if (string.IsNullOrWhiteSpace(hardwareName))
        {
            return false;
        }

        return hardwareName.IndexOf("Nuvoton", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("NCT", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("ITE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("IT86", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("Winbond", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("Fintek", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("Super I/O", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("SuperIO", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardwareName.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SensorDeduplicationKey(SensorRow row)
    {
        var type = row.Type ?? "";
        var hardware = NormalizeHardwareName(row.Hardware);
        var name = CleanSensorName(row.Name);

        if (type == "Temperature" && IsCpuHardwareName(hardware))
        {
            return (type + "|cpu|" + name).ToLowerInvariant();
        }

        if (type == "Performance" && IsStoragePerformanceName(name))
        {
            return (type + "|" + hardware + "|" + name).ToLowerInvariant();
        }

        if (type == "SMART" && (name.Equals("Data read", StringComparison.OrdinalIgnoreCase) || name.Equals("Data written", StringComparison.OrdinalIgnoreCase)))
        {
            return (type + "|" + hardware + "|" + name).ToLowerInvariant();
        }

        return (type + "|" + hardware + "|" + name + "|" + (row.Identifier ?? "")).ToLowerInvariant();
    }

    private static bool IsCpuHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return false;
        }

        return hardware.Equals("CPU", StringComparison.OrdinalIgnoreCase)
            || hardware.IndexOf("processor", StringComparison.OrdinalIgnoreCase) >= 0
            || hardware.IndexOf("intel", StringComparison.OrdinalIgnoreCase) >= 0
            || hardware.IndexOf("amd", StringComparison.OrdinalIgnoreCase) >= 0
            || hardware.IndexOf("ryzen", StringComparison.OrdinalIgnoreCase) >= 0
            || hardware.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<SensorRow> ConsolidateRelatedRows(List<SensorRow> rows)
    {
        var output = new List<SensorRow>();
        foreach (var group in rows.GroupBy(r => (r.Type ?? "") + "|" + NormalizeHardwareName(r.Hardware ?? "")))
        {
            var groupRows = group.ToList();
            var usedPercent = TakeRow(groupRows, "Memory used");
            var usedSize = TakeRow(groupRows, "Memory used size");
            if (usedPercent != null && usedSize != null)
            {
                output.Add(new SensorRow
                {
                    Type = usedPercent.Type,
                    Hardware = usedPercent.Hardware,
                    Name = "Memory used",
                    Value = usedPercent.Value,
                    DisplayValue = usedSize.DisplayValue + " (" + usedPercent.DisplayValue + ")",
                    Source = MergeSources(usedPercent.Source, usedSize.Source)
                });
            }
            else
            {
                if (usedPercent != null) output.Add(usedPercent);
                if (usedSize != null) output.Add(usedSize);
            }

            var freeSpace = TakeRow(groupRows, "Free space");
            var usedSpace = TakeRow(groupRows, "Used space");
            if (freeSpace != null && usedSpace != null)
            {
                var display = usedSpace.DisplayValue;
                double spaceUsedPercent;
                double freeBytes;
                if (TryParsePercent(usedSpace.DisplayValue, out spaceUsedPercent) && TryParseFormattedBytes(freeSpace.DisplayValue, out freeBytes) && spaceUsedPercent > 0 && spaceUsedPercent < 100)
                {
                    var totalBytes = freeBytes / (1.0 - (spaceUsedPercent / 100.0));
                    var usedBytes = Math.Max(0, totalBytes - freeBytes);
                    display = FormatBytes(usedBytes) + " (" + usedSpace.DisplayValue + ")";
                }

                output.Add(new SensorRow
                {
                    Type = usedSpace.Type,
                    Hardware = usedSpace.Hardware,
                    Name = "Used space",
                    Value = usedSpace.Value,
                    DisplayValue = display,
                    Source = MergeSources(usedSpace.Source, freeSpace.Source)
                });
                output.Add(freeSpace);
            }
            else
            {
                if (freeSpace != null) output.Add(freeSpace);
                if (usedSpace != null) output.Add(usedSpace);
            }

            output.AddRange(groupRows);
        }

        return output;
    }

    private static SensorRow TakeRow(List<SensorRow> rows, string name)
    {
        var index = rows.FindIndex(r => CleanSensorName(r.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var row = rows[index];
        rows.RemoveAt(index);
        return row;
    }

    private static string MergeSources(string first, string second)
    {
        return string.Join(", ", new[] { first, second }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToArray());
    }

    private static bool TryParsePercent(string value, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return double.TryParse(value.Replace("%", "").Trim(), out percent);
    }

    private static bool TryParseFormattedBytes(string value, out double bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        double number;
        if (!double.TryParse(parts[0], out number))
        {
            return false;
        }

        var unit = parts[1].ToUpperInvariant();
        var multiplier = 1.0;
        if (unit == "KB") multiplier = 1024.0;
        else if (unit == "MB") multiplier = 1024.0 * 1024.0;
        else if (unit == "GB") multiplier = 1024.0 * 1024.0 * 1024.0;
        else if (unit == "TB") multiplier = 1024.0 * 1024.0 * 1024.0 * 1024.0;
        bytes = number * multiplier;
        return true;
    }

    private static bool IsStoragePerformanceName(string name)
    {
        return name.Equals("Data read", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Data written", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Total activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Total space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Free space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Used space", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatLibreHardwareMonitorStorageValue(string hardwareName, string sensorType, string sensorName, float value)
    {
        if (sensorType == "Temperature")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + " C";
        }

        if (sensorType == "Load")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + "%";
        }

        if (sensorType == "Throughput")
        {
            return FormatBytesPerSecond(value);
        }

        if (sensorType == "Data")
        {
            if (IsLibreHardwareMonitorGigabyteCounter(sensorName))
            {
                return FormatStorageDataCounterGigabytes(hardwareName, sensorName, value);
            }

            return FormatNumber(Math.Round(value, 1), "0.0");
        }

        if (sensorType == "Level")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + "%";
        }

        if (sensorType == "Factor")
        {
            return FormatNumber(Math.Round(value, 0), "0");
        }

        return FormatNumber(Math.Round(value, 1), "0.0");
    }

    private static string FormatLibreHardwareMonitorControlValue(ISensor sensor)
    {
        var value = sensor.Value.HasValue ? FormatNumber(Math.Round(sensor.Value.Value, 0), "0") + "%" : T("value.unknown", "unknown");
        var mode = sensor.Control == null ? T("value.No direct control", "No direct control") : TranslateControlMode(sensor.Control.ControlMode.ToString());
        return mode + " " + value;
    }

    private static string TranslateControlMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "";
        }

        return T("value." + mode, mode);
    }

    private static string CleanStorageSensorName(string sensorName, string sensorType)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return sensorType == "Temperature" ? "Temperature" : "Storage reading";
        }

        if (sensorType == "Level" && sensorName.Equals("Life", StringComparison.OrdinalIgnoreCase))
        {
            return "Life remaining";
        }

        if (sensorName.Equals("Data Read", StringComparison.OrdinalIgnoreCase))
        {
            return "Data read";
        }

        if (sensorName.Equals("Data Written", StringComparison.OrdinalIgnoreCase))
        {
            return "Data written";
        }

        if (sensorName.Equals("Free Space", StringComparison.OrdinalIgnoreCase))
        {
            return "Free space";
        }

        if (sensorName.Equals("Total Space", StringComparison.OrdinalIgnoreCase))
        {
            return "Total space";
        }

        if (sensorName.Equals("Power On Count", StringComparison.OrdinalIgnoreCase))
        {
            return "Power on count";
        }

        if (sensorName.Equals("Power On Hours", StringComparison.OrdinalIgnoreCase))
        {
            return "Power on hours";
        }

        return sensorName;
    }

    private static bool IsLibreHardwareMonitorGigabyteCounter(string sensorName)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return false;
        }

        return sensorName.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0
            || sensorName.Equals("Data Read", StringComparison.OrdinalIgnoreCase)
            || sensorName.Equals("Data Written", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLibreHardwareMonitorFileSystemSpaceSensor(string sensorName)
    {
        return !string.IsNullOrWhiteSpace(sensorName) &&
            (sensorName.Equals("Free Space", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Used Space", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Total Space", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLibreHardwareMonitorStorageVolatileCounter(string sensorName)
    {
        return !string.IsNullOrWhiteSpace(sensorName) &&
            (sensorName.Equals("Read Activity", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Write Activity", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Total Activity", StringComparison.OrdinalIgnoreCase));
    }
}
