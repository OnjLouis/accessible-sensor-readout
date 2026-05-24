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
    private static string RegistryValueToString(object value)
    {
        if (value == null)
        {
            return "";
        }

        var bytes = value as byte[];
        if (bytes != null)
        {
            var unicode = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            if (IsMostlyPrintableRegistryText(unicode))
            {
                return unicode;
            }

            return BitConverter.ToString(bytes).Replace("-", " ");
        }

        var strings = value as string[];
        if (strings != null)
        {
            return string.Join(", ", strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
        }

        return Convert.ToString(value);
    }

    private static bool IsMostlyPrintableRegistryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var printable = 0;
        var total = 0;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
            {
                total++;
                continue;
            }

            total++;
            if (!char.IsSurrogate(ch) && ch != '\uFFFD')
            {
                printable++;
            }
        }

        return total > 0 && printable >= total * 8 / 10;
    }

    private static ulong RegistryValueToUInt64(object value)
    {
        if (value == null)
        {
            return 0;
        }

        if (value is ulong) return (ulong)value;
        if (value is long) return (ulong)Math.Max(0, (long)value);
        if (value is uint) return (uint)value;
        if (value is int) return (ulong)Math.Max(0, (int)value);

        ulong parsed;
        return ulong.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
    }
}
