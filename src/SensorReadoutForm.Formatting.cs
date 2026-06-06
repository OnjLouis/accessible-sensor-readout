using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    public static string RowSettingsKey(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        return (row.Type ?? "") + "|" + (row.Hardware ?? "") + "|" + (row.Name ?? "") + "|" + (row.Identifier ?? "");
    }

    public static bool IsSelectableReadoutRow(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        var type = row.Type ?? "";
        if (type == "Temperature" || type == "Fan" || type == "SMART" || type == "Network" || type == "Battery" || type == "Tasks")
        {
            return true;
        }

        if (type != "Performance")
        {
            return false;
        }

        var name = CleanSensorName(row.Name);
        if (IsPrinterPerformanceReading(row))
        {
            return name.EndsWith(" level", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Offline", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Jobs queued", StringComparison.OrdinalIgnoreCase);
        }

        return name.Equals("CPU usage", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(row.Hardware, "GPU memory", StringComparison.OrdinalIgnoreCase) &&
                (name.Equals("Dedicated GPU memory total", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Dedicated GPU memory used", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Dedicated GPU memory free", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Shared GPU memory used", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("NVIDIA SMI memory ", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(" NVIDIA SMI ", StringComparison.OrdinalIgnoreCase) >= 0))
            || (string.Equals(row.Hardware, "GPU", StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("GPU ", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(" usage", StringComparison.OrdinalIgnoreCase))
            || name.Equals("System uptime", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory total", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory used", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory used size", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory available", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Paging file total", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Paging file used", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Paging file free", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Physical + virtual memory total", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Physical + virtual memory used", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Physical + virtual memory free", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connected disks total space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connected disks used space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connected disks free space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Total space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Used space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Space used", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Free space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Total activity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrinterPerformanceReading(SensorRow row)
    {
        return row != null &&
            string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase) &&
            ((row.Hardware ?? "").StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Hardware, "Printers", StringComparison.OrdinalIgnoreCase));
    }

    public static string IdentifierFromSettingsKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var trimmed = key.Trim();
        var lastSeparator = trimmed.LastIndexOf('|');
        return lastSeparator >= 0 ? trimmed.Substring(lastSeparator + 1).Trim() : trimmed;
    }

    public static string TrayChoiceLabel(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        return TrayChoiceLabel(ShortHardwareName(row.Hardware), CleanSensorName(row.Name), row.Type);
    }

    public static string TrayChoiceLabel(string hardware, string name, string type)
    {
        return ShortHardwareName(hardware) + " - " + DisplayReadingName(name) + ": " + DisplayTypeName(type);
    }

    public static string SpeechPreviewLabel(string type, string hardware, string name, string key, Dictionary<string, string> speechLabels, bool includeHardwareName)
    {
        string custom;
        return speechLabels != null &&
            !string.IsNullOrWhiteSpace(key) &&
            speechLabels.TryGetValue(key, out custom) &&
            !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : DefaultSpeechLabel(type, hardware, name, includeHardwareName);
    }

    public static string DefaultSpeechLabel(string type, string hardware, string name, bool includeHardwareName)
    {
        var label = ShortTrayName(name);
        if (string.Equals(type, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        if (string.Equals(type, "Network", StringComparison.OrdinalIgnoreCase))
        {
            return includeHardwareName ? ShortTrayHardware(hardware) + " " + label : label;
        }

        if (string.Equals(type, "USB", StringComparison.OrdinalIgnoreCase))
        {
            return includeHardwareName ? ShortTrayHardware(hardware) : label;
        }

        if (string.Equals(type, "Tasks", StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        if (string.Equals(type, "Performance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "SMART", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }

            return includeHardwareName ? ShortTrayHardware(hardware) + " " + label : label;
        }

        return label;
    }

    private static string ShortTrayReadingText(SensorRow row)
    {
        return ShortTrayReadingText(row, true);
    }

    private static string ShortTrayReadingText(SensorRow row, bool includeHardwareName)
    {
        if (row == null)
        {
            return "";
        }

        var hardware = ShortTrayHardware(row.Hardware);
        var name = ShortTrayName(row.Name);
        if (row.Type == "Temperature")
        {
            return name + " " + FormatValue(row);
        }

        if (row.Type == "Network")
        {
            return (includeHardwareName ? hardware + " " : "") + name + " " + FormatValue(row);
        }

        if (row.Type == "USB")
        {
            return includeHardwareName ? hardware + ", " + FormatValue(row) : FormatValue(row);
        }

        if (row.Type == "Performance" || row.Type == "SMART")
        {
            if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase))
            {
                return name + " " + FormatValue(row);
            }

            return (includeHardwareName ? hardware + " " : "") + name + " " + FormatValue(row);
        }

        return name + " " + FormatValue(row);
    }

    private static string ShortTrayHardware(string hardware)
    {
        var text = ShortHardwareName(hardware);
        if (text.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Eth";
        }

        if (text.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "WiFi";
        }

        if (text.StartsWith("VMware Network Adapter ", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("VMware Network Adapter ", "VM");
        }

        if (text.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        return text;
    }

    private static string ShortTrayName(string name)
    {
        var text = CleanSensorName(name);
        if (text.Equals("CPU package", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (text.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        if (text.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
        {
            return "T" + text.Substring("Temperature #".Length);
        }

        if (text.Equals("Receive rate", StringComparison.OrdinalIgnoreCase))
        {
            return "Rx";
        }

        if (text.Equals("Send rate", StringComparison.OrdinalIgnoreCase))
        {
            return "Tx";
        }

        if (text.Equals("Data received", StringComparison.OrdinalIgnoreCase))
        {
            return "Rx total";
        }

        if (text.Equals("Data sent", StringComparison.OrdinalIgnoreCase))
        {
            return "Tx total";
        }

        if (text.Equals("Wi-Fi network", StringComparison.OrdinalIgnoreCase))
        {
            return "SSID";
        }

        if (text.Equals("Wi-Fi signal strength", StringComparison.OrdinalIgnoreCase))
        {
            return "Signal";
        }

        if (text.Equals("Wi-Fi signal RSSI", StringComparison.OrdinalIgnoreCase))
        {
            return "RSSI";
        }

        if (text.Equals("Wi-Fi channel", StringComparison.OrdinalIgnoreCase))
        {
            return "Ch";
        }

        if (text.Equals("System uptime", StringComparison.OrdinalIgnoreCase))
        {
            return "Uptime";
        }

        if (text.Equals("CPU usage", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (text.Equals("Memory used", StringComparison.OrdinalIgnoreCase))
        {
            return "Mem";
        }

        if (text.StartsWith("Paging file ", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("Paging file", "Pagefile");
        }

        return text.Replace(" Activity", "").Replace(" activity", "");
    }

    private static string T(string key, string fallback)
    {
        return activeLanguage == null ? fallback : activeLanguage.Text(key, fallback);
    }

    public static string DefaultStartupSpeechMessage()
    {
        return T("speech.startupActive", "Sensor Readout active.");
    }

    private static string FormatNumber(double value, string format)
    {
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        var separator = !string.IsNullOrWhiteSpace(activeDecimalSeparator)
            ? activeDecimalSeparator
            : activeLanguage == null ? "" : activeLanguage.DecimalSeparator;
        if (!string.IsNullOrWhiteSpace(separator) && separator != ".")
        {
            text = text.Replace(".", separator);
        }

        return text;
    }

    private static string DecodeHealthStatus(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 0) return "Healthy";
        if (code == 1) return "Warning";
        if (code == 2) return "Unhealthy";
        return "Unknown (" + code + ")";
    }

    private static string DecodeMediaType(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 3) return "HDD";
        if (code == 4) return "SSD";
        if (code == 5) return "SCM";
        return "Unspecified";
    }

    private static string DecodeOperationalStatus(object value)
    {
        var values = value as ushort[];
        if (values == null)
        {
            var single = value as ushort?;
            values = single.HasValue ? new[] { single.Value } : new ushort[0];
        }

        if (values.Length == 0)
        {
            return "Unknown";
        }

        return string.Join(", ", values.Select(v => v == 2 ? "OK" : "Status " + v).ToArray());
    }

    private static string FormatStorageBusType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "SCSI";
            case 2: return "ATAPI";
            case 3: return "ATA";
            case 4: return "IEEE 1394";
            case 5: return "SSA";
            case 6: return "Fibre Channel";
            case 7: return "USB";
            case 8: return "RAID";
            case 9: return "iSCSI";
            case 10: return "SAS";
            case 11: return "SATA";
            case 12: return "SD";
            case 13: return "MMC";
            case 15: return "File-backed virtual disk";
            case 16: return "Storage Spaces";
            case 17: return "NVMe";
            case 18: return "SCM";
            case 19: return "UFS";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatStorageSpindleSpeed(object value)
    {
        int rpm;
        if (!TryConvertToInt32(value, out rpm) || rpm <= 0)
        {
            return "";
        }

        return rpm.ToString(CultureInfo.InvariantCulture) + " RPM";
    }

    private static string FormatStorageUsage(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Auto-select";
            case 2: return "Manual-select";
            case 3: return "Hot spare";
            case 4: return "Retired";
            case 5: return "Journal";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatStorageUniqueIdFormat(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Vendor-specific";
            case 1: return "Vendor ID";
            case 2: return "EUI-64";
            case 3: return "FCPH name";
            case 8: return "SCSI name string";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatStorageCannotPoolReason(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "";
            case 1: return "In a storage pool";
            case 2: return "Not healthy";
            case 3: return "Removable media";
            case 4: return "In use by cluster";
            case 5: return "Offline";
            case 6: return "Insufficient capacity";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatBytes(object value)
    {
        double bytes;
        if (value == null || !double.TryParse(Convert.ToString(value), out bytes) || bytes <= 0)
        {
            return "";
        }

        var units = new[] { "bytes", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return FormatNumber(Math.Round(bytes, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            return "";
        }

        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var unit = 0;
        while (bytesPerSecond >= 1024 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }

        return FormatNumber(Math.Round(bytesPerSecond, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatBitsPerSecond(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "0 bps";
        }

        var value = (double)bitsPerSecond;
        var units = new[] { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return FormatNumber(Math.Round(value, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatGigabytes(double gigabytes)
    {
        if (gigabytes <= 0)
        {
            return "";
        }

        if (gigabytes >= 1024)
        {
            var terabytes = gigabytes / 1024;
            return FormatNumber(Math.Round(terabytes, 2), "0.##") + " TB";
        }

        var rounded = Math.Round(gigabytes, 1);
        return FormatNumber(rounded, Math.Abs(rounded % 1) < 0.05 ? "0" : "0.0") + " GB";
    }

    private static string FormatStorageDataCounterGigabytes(string hardwareName, string sensorName, double gigabytes)
    {
        if (gigabytes <= 0)
        {
            return "";
        }

        if (gigabytes > 1024 * 100)
        {
            var samsungValue = FormatSamsungStorageDataCounter(hardwareName, sensorName, gigabytes);
            if (!string.IsNullOrWhiteSpace(samsungValue))
            {
                return samsungValue;
            }

            return FormatNumber(Math.Round(gigabytes, 0), "0") + " raw";
        }

        return FormatGigabytes(gigabytes);
    }

    private static string FormatSamsungStorageDataCounter(string hardwareName, string sensorName, double rawValue)
    {
        if (string.IsNullOrWhiteSpace(hardwareName) ||
            hardwareName.IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) < 0 ||
            string.IsNullOrWhiteSpace(sensorName) ||
            (sensorName.IndexOf("Data Read", StringComparison.OrdinalIgnoreCase) < 0 &&
             sensorName.IndexOf("Data Written", StringComparison.OrdinalIgnoreCase) < 0))
        {
            return "";
        }

        // Samsung NVMe drives commonly expose SMART data-unit counters where one
        // unit is 1000 blocks of 512 bytes. Older SATA-style counters are often
        // raw 512-byte LBAs, which are much larger for the same real byte count.
        var bytes = rawValue >= 1000000000.0 ? rawValue * 512.0 : rawValue * 512000.0;
        var gibibytes = bytes / 1024.0 / 1024.0 / 1024.0;
        return FormatGigabytes(gibibytes);
    }
}
