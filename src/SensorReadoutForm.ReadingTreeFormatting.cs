using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    public static int TypeSortIndex(string type)
    {
        if (type == "Performance")
        {
            return 0;
        }

        if (type == "Temperature")
        {
            return 1;
        }

        if (type == "Fan")
        {
            return 2;
        }

        if (type == "SMART")
        {
            return 3;
        }

        if (type == "Network")
        {
            return 5;
        }

        if (type == "Bluetooth")
        {
            return 6;
        }

        if (type == "Tasks")
        {
            return 7;
        }

        if (type == "Spoken Hotkeys")
        {
            return 8;
        }

        if (type == "USB")
        {
            return 9;
        }

        if (type == "Audio")
        {
            return 10;
        }

        if (type == "Display")
        {
            return 11;
        }

        if (type == "Devices")
        {
            return 12;
        }

        if (type == "Firmware Security")
        {
            return 13;
        }

        if (type == "Battery")
        {
            return 4;
        }

        return 14;
    }

    public static string DisplayTypeName(string type)
    {
        if (type == "Temperature")
        {
            return T("type.Temperature", "Temperatures");
        }

        if (type == "Fan")
        {
            return T("type.Fan", "Fans");
        }

        if (type == "SMART")
        {
            return T("type.SMART", "SMART");
        }

        if (type == "Performance")
        {
            return T("type.Performance", "Performance/Overview");
        }

        if (type == "Network")
        {
            return T("type.Network", "Network");
        }

        if (type == "Tasks")
        {
            return T("type.Tasks", "Tasks");
        }

        if (type == "Spoken Hotkeys")
        {
            return T("type.Spoken Hotkeys", "Spoken Hotkeys");
        }

        if (type == "Bluetooth")
        {
            return T("type.Bluetooth", "Bluetooth");
        }

        if (type == "Battery")
        {
            return T("type.Battery", "Battery");
        }

        if (type == "USB")
        {
            return T("type.USB", "USB");
        }

        if (type == "Audio")
        {
            return T("type.Audio", "Audio");
        }

        if (type == "Display")
        {
            return T("type.Display", "Display");
        }

        if (type == "Devices")
        {
            return T("type.Devices", "Devices");
        }

        if (type == "Firmware Security")
        {
            return T("type.Firmware Security", "Firmware Security");
        }

        return string.IsNullOrWhiteSpace(type) ? T("type.Readings", "Readings") : type;
    }

    public static int ReadingSortIndex(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("System uptime", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Firmware mode", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Secure Boot", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("UEFI database access", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("UEFI certificate databases", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("Earliest certificate expiry", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("Expired certificates", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Not-yet-valid certificates", StringComparison.OrdinalIgnoreCase)) return 6;
        if (clean.Equals("Possible test certificates", StringComparison.OrdinalIgnoreCase)) return 7;
        if (clean.Equals("Hash-only entries", StringComparison.OrdinalIgnoreCase)) return 8;
        if (clean.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Model", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU usage", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU name", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU vendor", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU architecture", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU socket", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU processor ID", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("CPU cores", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU threads", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU current clock", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU max clock", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("CPU instruction sets", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU virtualization extensions", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU virtualization enabled in firmware", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU hardware VM memory translation (SLAT/EPT/NPT)", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU data execution prevention", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS vendor", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("BIOS version", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS date", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Health", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Physical + virtual memory total", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Physical + virtual memory used", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Physical + virtual memory free", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("Memory total", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("Memory used", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("Memory used size", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Memory available", StringComparison.OrdinalIgnoreCase)) return 6;
        if (clean.Equals("Memory slots", StringComparison.OrdinalIgnoreCase)) return 7;
        if (clean.Equals("Memory module count", StringComparison.OrdinalIgnoreCase)) return 8;
        if (clean.Equals("Memory module layout", StringComparison.OrdinalIgnoreCase)) return 9;
        if (clean.Equals("Memory type", StringComparison.OrdinalIgnoreCase)) return 10;
        if (clean.Equals("Memory form factor", StringComparison.OrdinalIgnoreCase)) return 11;
        if (clean.Equals("Memory rated speed", StringComparison.OrdinalIgnoreCase)) return 12;
        if (clean.Equals("Memory configured speed", StringComparison.OrdinalIgnoreCase)) return 13;
        if (clean.Equals("Memory manufacturers", StringComparison.OrdinalIgnoreCase)) return 14;
        if (clean.Equals("Memory part numbers", StringComparison.OrdinalIgnoreCase)) return 15;
        if (clean.Equals("Memory populated slots", StringComparison.OrdinalIgnoreCase)) return 16;
        if (clean.Equals("Paging file total", StringComparison.OrdinalIgnoreCase)) return 17;
        if (clean.Equals("Paging file used", StringComparison.OrdinalIgnoreCase)) return 18;
        if (clean.Equals("Paging file free", StringComparison.OrdinalIgnoreCase)) return 19;
        if (clean.Equals("Expansion slots", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Data read", StringComparison.OrdinalIgnoreCase)) return 13;
        if (clean.Equals("Data written", StringComparison.OrdinalIgnoreCase)) return 14;
        if (clean.Equals("Read rate", StringComparison.OrdinalIgnoreCase)) return 15;
        if (clean.Equals("Write rate", StringComparison.OrdinalIgnoreCase)) return 16;
        if (clean.Equals("Read activity", StringComparison.OrdinalIgnoreCase)) return 17;
        if (clean.Equals("Write activity", StringComparison.OrdinalIgnoreCase)) return 18;
        if (clean.Equals("Total activity", StringComparison.OrdinalIgnoreCase)) return 19;
        if (clean.Equals("Connected disks total space", StringComparison.OrdinalIgnoreCase)) return 20;
        if (clean.Equals("Connected disks used space", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Connected disks free space", StringComparison.OrdinalIgnoreCase)) return 22;
        if (clean.Equals("Total space", StringComparison.OrdinalIgnoreCase)) return 20;
        if (clean.Equals("Used space", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Space used", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Free space", StringComparison.OrdinalIgnoreCase)) return 22;
        if (clean.Equals("Size", StringComparison.OrdinalIgnoreCase)) return 23;
        if (clean.Equals("Status", StringComparison.OrdinalIgnoreCase)) return 30;
        if (clean.Equals("IP address", StringComparison.OrdinalIgnoreCase)) return 31;
        if (clean.Equals("Link speed", StringComparison.OrdinalIgnoreCase)) return 32;
        if (clean.Equals("Receive rate", StringComparison.OrdinalIgnoreCase)) return 33;
        if (clean.Equals("Send rate", StringComparison.OrdinalIgnoreCase)) return 34;
        if (clean.Equals("Data received", StringComparison.OrdinalIgnoreCase)) return 35;
        if (clean.Equals("Data sent", StringComparison.OrdinalIgnoreCase)) return 36;
        if (clean.Equals("Wi-Fi network", StringComparison.OrdinalIgnoreCase)) return 37;
        if (clean.Equals("Wi-Fi profile", StringComparison.OrdinalIgnoreCase)) return 38;
        if (clean.Equals("Wi-Fi access point", StringComparison.OrdinalIgnoreCase)) return 39;
        if (clean.Equals("Wi-Fi access point vendor", StringComparison.OrdinalIgnoreCase)) return 40;
        if (clean.Equals("Wi-Fi signal strength", StringComparison.OrdinalIgnoreCase)) return 41;
        if (clean.Equals("Wi-Fi signal RSSI", StringComparison.OrdinalIgnoreCase)) return 42;
        if (clean.Equals("Wi-Fi channel", StringComparison.OrdinalIgnoreCase)) return 43;
        if (clean.Equals("Wi-Fi frequency", StringComparison.OrdinalIgnoreCase)) return 44;
        if (clean.Equals("Wi-Fi radio type", StringComparison.OrdinalIgnoreCase)) return 45;
        if (clean.Equals("Wi-Fi receive link speed", StringComparison.OrdinalIgnoreCase)) return 46;
        if (clean.Equals("Wi-Fi transmit link speed", StringComparison.OrdinalIgnoreCase)) return 47;
        if (clean.Equals("Wi-Fi security", StringComparison.OrdinalIgnoreCase)) return 48;
        if (clean.Equals("Wi-Fi authentication", StringComparison.OrdinalIgnoreCase)) return 49;
        if (clean.Equals("Wi-Fi cipher", StringComparison.OrdinalIgnoreCase)) return 50;
        if (clean.Equals("Connected", StringComparison.OrdinalIgnoreCase)) return 51;
        if (clean.Equals("Paired", StringComparison.OrdinalIgnoreCase)) return 52;
        if (clean.Equals("Remembered", StringComparison.OrdinalIgnoreCase)) return 53;
        if (clean.Equals("Adapter address", StringComparison.OrdinalIgnoreCase)) return 54;
        if (clean.Equals("Adapter type", StringComparison.OrdinalIgnoreCase)) return 55;
        if (clean.Equals("Adapter services", StringComparison.OrdinalIgnoreCase)) return 56;
        if (clean.Equals("Adapter manufacturer", StringComparison.OrdinalIgnoreCase)) return 57;
        if (clean.Equals("Device address", StringComparison.OrdinalIgnoreCase)) return 58;
        if (clean.Equals("Device type", StringComparison.OrdinalIgnoreCase)) return 59;
        if (clean.Equals("Device services", StringComparison.OrdinalIgnoreCase)) return 60;
        if (clean.Equals("Last seen", StringComparison.OrdinalIgnoreCase)) return 61;
        if (clean.Equals("Last used", StringComparison.OrdinalIgnoreCase)) return 62;
        if (clean.Equals("Device", StringComparison.OrdinalIgnoreCase)) return 63;
        return 100;
    }

    public static string ShortHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeHardwareName(hardware);
    }

    private static string NormalizeHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeStorageHardwareName(hardware.Trim());
    }

    private static string NormalizeStorageHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        var value = hardware.Trim();
        if (value.StartsWith("USB ", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4).Trim();
        }

        value = ReplaceIgnoreCase(value, "SCSI Disk Device", "").Trim();
        while (value.IndexOf("  ", StringComparison.Ordinal) >= 0)
        {
            value = value.Replace("  ", " ");
        }

        return value;
    }

    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue))
        {
            return value;
        }

        var index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value.Substring(0, index) + newValue + value.Substring(index + oldValue.Length);
            index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    public static string CleanSensorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed sensor";
        }

        return name
            .Replace("Uptime", "System uptime")
            .Replace("Core (Tctl/Tdie)", "CPU package")
            .Replace("CCD1 (Tdie)", "CCD1 Tdie");
    }

    private static string FormatValue(SensorRow row)
    {
        if (!row.Value.HasValue)
        {
            return row.DisplayValue ?? "";
        }

        if (row.Type == "Temperature")
        {
            return FormatTemperature(row.Value.Value);
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            return row.DisplayValue;
        }

        if (row.Type == "Fan")
        {
            return FormatNumber(Math.Round(row.Value.Value, 0), "0") + " RPM";
        }

        if (row.Type == "SMART")
        {
            return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
        }
        return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
    }

    private static string FormatTemperature(float celsius)
    {
        var unit = NormalizeTemperatureUnit(activeTemperatureUnit);
        var celsiusText = FormatNumber(Math.Round(celsius, 1), "0.0") + " C";
        var fahrenheitText = FormatNumber(Math.Round((celsius * 9.0 / 5.0) + 32.0, 1), "0.0") + " F";
        if (unit == "F")
        {
            return fahrenheitText;
        }
        if (unit == "CF")
        {
            return celsiusText + " / " + fahrenheitText;
        }
        if (unit == "FC")
        {
            return fahrenheitText + " / " + celsiusText;
        }

        return celsiusText;
    }

    private static string HtmlEncode(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

}
