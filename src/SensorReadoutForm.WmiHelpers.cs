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
    private static string CleanWmiText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static bool IsGenericSystemModel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        return string.Equals(text, "System Product Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "System Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By OEM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Default string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Not Available", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static object GetWmiPropertyValue(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            var property = obj == null ? null : obj.Properties[propertyName];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWmiPropertyText(ManagementBaseObject obj, string propertyName)
    {
        return CleanWmiText(Convert.ToString(GetWmiPropertyValue(obj, propertyName)));
    }

    private static string FormatMajorMinor(object majorValue, object minorValue, bool allow255)
    {
        int major;
        int minor;
        if (!TryConvertToInt32(majorValue, out major) || !TryConvertToInt32(minorValue, out minor))
        {
            return "";
        }

        if (!allow255 && major == 255 && minor == 255)
        {
            return "";
        }

        if (major < 0 || minor < 0)
        {
            return "";
        }

        return major + "." + minor;
    }

    private static bool TryConvertToInt32(object value, out int result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        try
        {
            result = Convert.ToInt32(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertToInt64(object value, out long result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        try
        {
            result = Convert.ToInt64(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetFirmwareMode()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
            {
                int firmwareType;
                if (!TryConvertToInt32(key == null ? null : key.GetValue("PEFirmwareType"), out firmwareType))
                {
                    return "";
                }

                if (firmwareType == 1) return "Legacy BIOS";
                if (firmwareType == 2) return "UEFI";
            }
        }
        catch
        {
        }

        return "";
    }

    private static string GetSecureBootState()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            {
                int enabled;
                if (!TryConvertToInt32(key == null ? null : key.GetValue("UEFISecureBootEnabled"), out enabled))
                {
                    return "";
                }

                return enabled == 0 ? "Off" : "On";
            }
        }
        catch
        {
            return "";
        }
    }

    private static string FormatWmiDate(object value)
    {
        DateTime parsed;
        return TryParseWmiDate(value, out parsed) ? parsed.ToString("yyyy-MM-dd") : "";
    }

    private static bool TryParseWmiDate(object value, out DateTime parsed)
    {
        parsed = DateTime.MinValue;
        if (value is DateTime)
        {
            parsed = (DateTime)value;
            return true;
        }

        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            text = text.Trim();
            var looksLikeDmtf = text.Length >= 14 && text.Take(14).All(char.IsDigit);
            if (looksLikeDmtf)
            {
                parsed = ManagementDateTimeConverter.ToDateTime(text);
                return true;
            }

            return DateTime.TryParse(text, out parsed);
        }
        catch
        {
            parsed = DateTime.MinValue;
            return false;
        }
    }
}
