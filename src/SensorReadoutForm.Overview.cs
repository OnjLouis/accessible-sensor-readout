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
    private static IEnumerable<SensorRow> GetOverviewRows()
    {
        var rows = new List<SensorRow>();
        var windowsDetails = GetWindowsHardwareDetails();
        var firmwareDetails = GetFirmwareHardwareDetails();

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddOverviewTextRow(rows, "Windows edition", CleanWmiText(Convert.ToString(os["Caption"])), "Windows WMI", windowsDetails);
                    AddOverviewTextRow(rows, "Windows version", CleanWmiText(Convert.ToString(os["Version"])), "Windows WMI", windowsDetails);
                    AddOverviewTextRow(rows, "Windows build", CleanWmiText(Convert.ToString(os["BuildNumber"])), "Windows WMI", windowsDetails);
                    AddOverviewTextRow(rows, "Windows architecture", CleanWmiText(Convert.ToString(os["OSArchitecture"])), "Windows WMI", windowsDetails);
                    AddOverviewTextRow(rows, "Windows install date", FormatWindowsInstallDate(os["InstallDate"]), "Windows", windowsDetails);
                    var bootTimeText = Convert.ToString(os["LastBootUpTime"]);
                    var bootTime = string.IsNullOrWhiteSpace(bootTimeText) ? DateTime.MinValue : ManagementDateTimeConverter.ToDateTime(bootTimeText);
                    if (bootTime > DateTime.MinValue)
                    {
                        AddOverviewTextRow(rows, "Windows boot time", FormatDateTimeWithAge(bootTime, true), "Windows WMI", windowsDetails);
                        rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "System uptime", DisplayValue = FormatUptime(DateTime.Now - bootTime), Source = "Windows WMI", Details = CloneDetails(windowsDetails) });
                    }
                    break;
                }
            }
        }
        catch
        {
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "System uptime", DisplayValue = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount)), Source = "Windows", Details = CloneDetails(windowsDetails) });
        }

        string baseboardManufacturer = "";
        string baseboardProduct = "";
        string baseboardVersion = "";

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject board in ExecuteWmiQuery(searcher, "WMI"))
                {
                    baseboardManufacturer = CleanWmiText(Convert.ToString(board["Manufacturer"]));
                    baseboardProduct = CleanWmiText(Convert.ToString(board["Product"]));
                    baseboardVersion = CleanWmiText(Convert.ToString(board["Version"]));
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject system in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddOverviewTextRow(rows, "System manufacturer", CleanWmiText(Convert.ToString(system["Manufacturer"])), "Windows WMI");
                    var systemModel = CleanWmiText(Convert.ToString(system["Model"]));
                    if (IsGenericSystemModel(systemModel) && !string.IsNullOrWhiteSpace(baseboardProduct))
                    {
                        systemModel = baseboardProduct;
                    }
                    AddOverviewTextRow(rows, "System model", systemModel, "Windows WMI");
                    break;
                }
            }
        }
        catch
        {
        }

        var boardDetails = GetBoardHardwareDetails();
        AddOverviewTextRow(rows, "Baseboard manufacturer", baseboardManufacturer, "Windows WMI", boardDetails);
        AddOverviewTextRow(rows, "Baseboard product", baseboardProduct, "Windows WMI", boardDetails);
        AddOverviewTextRow(rows, "Baseboard version", baseboardVersion, "Windows WMI", boardDetails);
        AddOverviewTextRow(rows, "BIOS mode", GetFirmwareMode(), "Windows registry", firmwareDetails);
        AddOverviewTextRow(rows, "Secure Boot", GetSecureBootState(), "Windows registry", firmwareDetails);

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS"))
            {
                foreach (ManagementObject bios in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddOverviewTextRow(rows, "BIOS vendor", GetWmiPropertyText(bios, "Manufacturer"), "Windows WMI", firmwareDetails);
                    var version = GetWmiPropertyText(bios, "SMBIOSBIOSVersion");
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = GetWmiPropertyText(bios, "Version");
                    }
                    AddOverviewTextRow(rows, "BIOS version", version, "Windows WMI", firmwareDetails);
                    AddOverviewTextRow(rows, "BIOS date", FormatWmiDate(GetWmiPropertyValue(bios, "ReleaseDate")), "Windows WMI", firmwareDetails);
                    AddOverviewTextRow(rows, "SMBIOS version", FormatMajorMinor(GetWmiPropertyValue(bios, "SMBIOSMajorVersion"), GetWmiPropertyValue(bios, "SMBIOSMinorVersion"), true), "Windows WMI", firmwareDetails);
                    AddOverviewTextRow(rows, "Embedded controller version", FormatMajorMinor(GetWmiPropertyValue(bios, "EmbeddedControllerMajorVersion"), GetWmiPropertyValue(bios, "EmbeddedControllerMinorVersion"), false), "Windows WMI", firmwareDetails);
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
            {
                var gpuList = new List<Dictionary<string, object>>();
                foreach (ManagementObject gpu in ExecuteWmiQuery(searcher, "WMI"))
                {
                    gpuList.Add(gpu.Properties.Cast<PropertyData>().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase));
                }

                var includeGpuNameInRows = gpuList.Count > 1;
                foreach (var gpu in gpuList)
                {
                    var name = Convert.ToString(GetGpuProperty(gpu, "Name"));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Display adapter";
                    }

                    var rowPrefix = includeGpuNameInRows ? name : "GPU";
                    var pnpDeviceId = Convert.ToString(GetGpuProperty(gpu, "PNPDeviceID"));
                    var details = BuildGpuOverviewDetails(gpu, name, pnpDeviceId);
                    var adapterRam = FormatBytes(GetGpuAdapterMemoryBytes(name, GetGpuProperty(gpu, "AdapterRAM")));
                    var adapterProblem = FormatDisplayAdapterProblem(GetGpuProperty(gpu, "ConfigManagerErrorCode"), Convert.ToString(GetGpuProperty(gpu, "Status")));
                    if (!string.IsNullOrWhiteSpace(adapterProblem))
                    {
                        adapterRam = FirstNonEmpty(adapterRam, "Unknown") + " (" + adapterProblem + "; may be unreliable)";
                    }

                    AddOverviewTextRow(rows, rowPrefix + " vendor", CleanWmiText(Convert.ToString(GetGpuProperty(gpu, "AdapterCompatibility"))), "Windows WMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " processor", CleanWmiText(Convert.ToString(GetGpuProperty(gpu, "VideoProcessor"))), "Windows WMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " adapter RAM", adapterRam, "Windows", details);
                    AddOverviewTextRow(rows, rowPrefix + " driver version", Convert.ToString(GetGpuProperty(gpu, "DriverVersion")), "Windows WMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " driver date", FormatWmiDate(GetGpuProperty(gpu, "DriverDate")), "Windows WMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " compute capability", GetDictionaryValue(details, "NVIDIA CUDA compute capability"), "NVIDIA SMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " max graphics clock", GetDictionaryValue(details, "NVIDIA max graphics clock"), "NVIDIA SMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " max memory clock", GetDictionaryValue(details, "NVIDIA max memory clock"), "NVIDIA SMI", details);
                    AddOverviewTextRow(rows, rowPrefix + " power limit", GetDictionaryValue(details, "NVIDIA power limit"), "NVIDIA SMI", details);

                    string gpuBios;
                    string gpuBiosDate;
                    if (TryGetGpuBiosInfo(name, pnpDeviceId, out gpuBios, out gpuBiosDate))
                    {
                        AddOverviewTextRow(rows, rowPrefix + " BIOS", gpuBios, "Windows registry", details);
                        AddOverviewTextRow(rows, rowPrefix + " BIOS date", gpuBiosDate, "Windows registry", details);
                    }
                }
            }
        }
        catch
        {
        }

        AddPrinterOverviewRows(rows);
        rows.AddRange(GetBatteryOverviewRows());
        AddAccessibilityOverviewRows(rows);

        return rows;
    }

    private static object GetGpuProperty(Dictionary<string, object> gpu, string name)
    {
        object value;
        return gpu != null && gpu.TryGetValue(name, out value) ? value : null;
    }

    private static Dictionary<string, string> BuildGpuOverviewDetails(Dictionary<string, object> gpu, string name, string pnpDeviceId)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (gpu == null)
        {
            return details;
        }

        AddDetail(details, "Name", name);
        AddDetail(details, "Vendor", Convert.ToString(GetGpuProperty(gpu, "AdapterCompatibility")));
        AddDetail(details, "Processor", Convert.ToString(GetGpuProperty(gpu, "VideoProcessor")));
        AddDetail(details, "Adapter RAM", FormatBytes(GetGpuAdapterMemoryBytes(name, GetGpuProperty(gpu, "AdapterRAM"))));
        AddDetail(details, "Driver version", Convert.ToString(GetGpuProperty(gpu, "DriverVersion")));
        AddDetail(details, "Driver date", FormatWmiDate(GetGpuProperty(gpu, "DriverDate")));
        AddDetail(details, "Status", Convert.ToString(GetGpuProperty(gpu, "Status")));
        AddDetail(details, "Device ID", pnpDeviceId);
        AddDetail(details, "Video architecture", Convert.ToString(GetGpuProperty(gpu, "VideoArchitecture")));
        AddDetail(details, "Video memory type", Convert.ToString(GetGpuProperty(gpu, "VideoMemoryType")));
        AddDetail(details, "Adapter DAC type", Convert.ToString(GetGpuProperty(gpu, "AdapterDACType")));
        AddDetail(details, "Current scan mode", Convert.ToString(GetGpuProperty(gpu, "CurrentScanMode")));
        AddDetail(details, "Availability", Convert.ToString(GetGpuProperty(gpu, "Availability")));
        AddDetail(details, "Config manager error code", Convert.ToString(GetGpuProperty(gpu, "ConfigManagerErrorCode")));
        AddPciDetails(details, pnpDeviceId);
        AddDeviceRegistryDetails(details, pnpDeviceId);
        AddGpuDisplayRegistryDetails(details, name);
        AddNvidiaSmiDetails(details, name, pnpDeviceId);
        AddRawWmiDetails(details, "Display adapter WMI", gpu);
        return details;
    }

    private static void AddAccessibilityOverviewRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

        var details = GetAccessibilityDetails();
        var detectedScreenReaders = ScreenReaderOutput.DetectSupportedScreenReaders();
        AddOverviewTextRow(rows, "Screen reader output", ScreenReaderOutput.IsActiveScreenReaderOutputAvailable ? T("ui.Available", "Available") : T("ui.Not available", "Not available"), "Sensor Readout", details);
        AddOverviewTextRow(rows, "Detected screen readers", detectedScreenReaders.Count == 0 ? T("ui.None detected", "None detected") : string.Join(", ", detectedScreenReaders.ToArray()), "Windows processes", details);

        bool enabled;
        if (TryGetHighContrastEnabled(out enabled))
        {
            AddOverviewTextRow(rows, "High contrast", FormatAccessibilityOnOff(enabled), "Windows accessibility", details);
        }

        if (TryGetStickyKeysEnabled(out enabled))
        {
            AddOverviewTextRow(rows, "Sticky Keys", FormatAccessibilityOnOff(enabled), "Windows accessibility", details);
        }

        if (TryGetToggleKeysEnabled(out enabled))
        {
            AddOverviewTextRow(rows, "Toggle Keys", FormatAccessibilityOnOff(enabled), "Windows accessibility", details);
        }

        if (TryGetFilterKeysEnabled(out enabled))
        {
            AddOverviewTextRow(rows, "Filter Keys", FormatAccessibilityOnOff(enabled), "Windows accessibility", details);
        }

        if (TryGetRegistryOnOff(@"Control Panel\Accessibility\ShowSounds", "On", out enabled))
        {
            AddOverviewTextRow(rows, "Show sounds", FormatAccessibilityOnOff(enabled), "Windows accessibility registry", details);
        }

        if (TryGetRegistryOnOff(@"Control Panel\Accessibility\AudioDescription", "On", out enabled))
        {
            AddOverviewTextRow(rows, "Audio descriptions", FormatAccessibilityOnOff(enabled), "Windows accessibility registry", details);
        }
    }

    private static Dictionary<string, string> GetAccessibilityDetails()
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Screen reader output available", ScreenReaderOutput.IsActiveScreenReaderOutputAvailable ? "Yes" : "No");
        AddDetail(details, "Screen reader output backend", string.IsNullOrWhiteSpace(ScreenReaderOutput.ActiveBackendName) ? "Not loaded" : ScreenReaderOutput.ActiveBackendName);
        var detectedScreenReaders = ScreenReaderOutput.DetectSupportedScreenReaders();
        AddDetail(details, "Detected screen readers", detectedScreenReaders.Count == 0 ? "None detected" : string.Join(", ", detectedScreenReaders.ToArray()));

        uint flags;
        if (TryGetHighContrastFlags(out flags))
        {
            AddDetail(details, "High contrast flags", flags.ToString(CultureInfo.InvariantCulture));
        }

        if (TryGetStickyKeysFlags(out flags))
        {
            AddDetail(details, "Sticky Keys flags", flags.ToString(CultureInfo.InvariantCulture));
        }

        if (TryGetToggleKeysFlags(out flags))
        {
            AddDetail(details, "Toggle Keys flags", flags.ToString(CultureInfo.InvariantCulture));
        }

        if (TryGetFilterKeysFlags(out flags))
        {
            AddDetail(details, "Filter Keys flags", flags.ToString(CultureInfo.InvariantCulture));
        }

        AddRegistryDetail(details, "Show sounds registry value", @"Control Panel\Accessibility\ShowSounds", "On");
        AddRegistryDetail(details, "Audio descriptions registry value", @"Control Panel\Accessibility\AudioDescription", "On");
        return details;
    }

    private static string FormatAccessibilityOnOff(bool enabled)
    {
        return enabled ? T("ui.On", "On") : T("ui.Off", "Off");
    }

    private static bool TryGetHighContrastEnabled(out bool enabled)
    {
        uint flags;
        if (TryGetHighContrastFlags(out flags))
        {
            enabled = (flags & NativeMethods.HcfHighContrastOn) != 0;
            return true;
        }

        enabled = false;
        return false;
    }

    private static bool TryGetStickyKeysEnabled(out bool enabled)
    {
        uint flags;
        if (TryGetStickyKeysFlags(out flags))
        {
            enabled = (flags & NativeMethods.SkfStickyKeysOn) != 0;
            return true;
        }

        enabled = false;
        return false;
    }

    private static bool TryGetToggleKeysEnabled(out bool enabled)
    {
        uint flags;
        if (TryGetToggleKeysFlags(out flags))
        {
            enabled = (flags & NativeMethods.TkfToggleKeysOn) != 0;
            return true;
        }

        enabled = false;
        return false;
    }

    private static bool TryGetFilterKeysEnabled(out bool enabled)
    {
        uint flags;
        if (TryGetFilterKeysFlags(out flags))
        {
            enabled = (flags & NativeMethods.FkfFilterKeysOn) != 0;
            return true;
        }

        enabled = false;
        return false;
    }

    private static bool TryGetHighContrastFlags(out uint flags)
    {
        flags = 0;
        try
        {
            var data = NativeMethods.HighContrast.Create();
            if (NativeMethods.SystemParametersInfo(NativeMethods.SpiGetHighContrast, data.cbSize, ref data, 0))
            {
                flags = data.dwFlags;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetStickyKeysFlags(out uint flags)
    {
        flags = 0;
        try
        {
            var data = NativeMethods.StickyKeys.Create();
            if (NativeMethods.SystemParametersInfo(NativeMethods.SpiGetStickyKeys, data.cbSize, ref data, 0))
            {
                flags = data.dwFlags;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetToggleKeysFlags(out uint flags)
    {
        flags = 0;
        try
        {
            var data = NativeMethods.ToggleKeys.Create();
            if (NativeMethods.SystemParametersInfo(NativeMethods.SpiGetToggleKeys, data.cbSize, ref data, 0))
            {
                flags = data.dwFlags;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetFilterKeysFlags(out uint flags)
    {
        flags = 0;
        try
        {
            var data = NativeMethods.FilterKeys.Create();
            if (NativeMethods.SystemParametersInfo(NativeMethods.SpiGetFilterKeys, data.cbSize, ref data, 0))
            {
                flags = data.dwFlags;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetRegistryOnOff(string subKey, string valueName, out bool enabled)
    {
        enabled = false;
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey))
            {
                var value = key == null ? null : key.GetValue(valueName);
                int number;
                if (TryConvertToInt32(value, out number))
                {
                    enabled = number != 0;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static void AddRegistryDetail(Dictionary<string, string> details, string label, string subKey, string valueName)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey))
            {
                var value = key == null ? null : key.GetValue(valueName);
                if (value != null)
                {
                    AddDetail(details, label, Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<SensorRow> GetSystemUptimeRows()
    {
        TimeSpan uptime;
        try
        {
            uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());
        }
        catch
        {
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
        }

        return new[]
        {
            new SensorRow
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "System uptime",
                Value = (float)Math.Max(0, uptime.TotalMinutes),
                DisplayValue = FormatUptime(uptime),
                Source = "Windows"
            }
        };
    }

    private static void AddOverviewTextRow(List<SensorRow> rows, string name, string value, string source)
    {
        AddOverviewTextRow(rows, name, value, source, null);
    }

    private static void AddOverviewTextRow(List<SensorRow> rows, string name, string value, string source, Dictionary<string, string> details)
    {
        if (rows == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "Overview",
            Name = name,
            DisplayValue = value.Trim(),
            Source = source,
            Details = CloneDetails(details)
        });
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalSeconds < 0)
        {
            uptime = TimeSpan.Zero;
        }

        var parts = new List<string>();
        if (uptime.Days > 0)
        {
            parts.Add(uptime.Days + " day" + (uptime.Days == 1 ? "" : "s"));
        }

        if (uptime.Hours > 0 || parts.Count > 0)
        {
            parts.Add(uptime.Hours + " hour" + (uptime.Hours == 1 ? "" : "s"));
        }

        if (uptime.Minutes > 0 || parts.Count > 0)
        {
            parts.Add(uptime.Minutes + " minute" + (uptime.Minutes == 1 ? "" : "s"));
        }

        parts.Add(uptime.Seconds + " second" + (uptime.Seconds == 1 ? "" : "s"));
        return string.Join(", ", parts.ToArray());
    }

    private static string FormatWindowsInstallDate(object wmiValue)
    {
        DateTime parsed;
        if (TryParseWmiDate(wmiValue, out parsed) && IsPlausibleWindowsInstallDate(parsed))
        {
            return FormatWindowsInstallDateWithAge(parsed);
        }

        if (TryGetWindowsInstallDateFromRegistry(out parsed) && IsPlausibleWindowsInstallDate(parsed))
        {
            return FormatWindowsInstallDateWithAge(parsed);
        }

        return "";
    }

    private static string FormatWindowsInstallDateWithAge(DateTime installDate)
    {
        var today = DateTime.Today;
        var date = installDate.Date;
        if (date > today)
        {
            return installDate.ToString("yyyy-MM-dd");
        }

        var totalDays = (today - date).Days;
        return installDate.ToString("yyyy-MM-dd") + " (" + FormatElapsedDateAge(date, today, totalDays) + ")";
    }

    private static string FormatElapsedDateAge(DateTime startDate, DateTime endDate, int totalDays)
    {
        if (totalDays <= 0)
        {
            return "today";
        }

        var detailed = FormatCalendarAge(startDate, endDate);
        var dayText = totalDays + " day" + (totalDays == 1 ? "" : "s") + " ago";
        if (string.IsNullOrWhiteSpace(detailed) || detailed == dayText)
        {
            return dayText;
        }

        return dayText + "; " + detailed;
    }

    private static string FormatCalendarAge(DateTime startDate, DateTime endDate)
    {
        var years = 0;
        while (startDate.AddYears(years + 1) <= endDate)
        {
            years++;
        }

        var cursor = startDate.AddYears(years);
        var months = 0;
        while (cursor.AddMonths(months + 1) <= endDate)
        {
            months++;
        }

        cursor = cursor.AddMonths(months);
        var remainingDays = (endDate - cursor).Days;
        var weeks = remainingDays / 7;
        var days = remainingDays % 7;

        var parts = new List<string>();
        AddAgePart(parts, years, "year");
        AddAgePart(parts, months, "month");
        AddAgePart(parts, weeks, "week");
        AddAgePart(parts, days, "day");

        return parts.Count == 0 ? "" : string.Join(", ", parts.ToArray()) + " ago";
    }

    private static void AddAgePart(List<string> parts, int value, string unit)
    {
        if (value <= 0)
        {
            return;
        }

        parts.Add(value + " " + unit + (value == 1 ? "" : "s"));
    }

    private static bool TryGetWindowsInstallDateFromRegistry(out DateTime installDate)
    {
        installDate = DateTime.MinValue;
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                long fileTime;
                if (TryConvertToInt64(key == null ? null : key.GetValue("InstallTime"), out fileTime) && fileTime > 0)
                {
                    installDate = DateTime.FromFileTime(fileTime);
                    return true;
                }

                long unixSeconds;
                if (TryConvertToInt64(key == null ? null : key.GetValue("InstallDate"), out unixSeconds) && unixSeconds > 0)
                {
                    installDate = new DateTime(1970, 1, 1).AddSeconds(unixSeconds).ToLocalTime();
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsPlausibleWindowsInstallDate(DateTime value)
    {
        return value.Year >= 2000 && value <= DateTime.Now.AddDays(1);
    }

    private static string FormatDateTime(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
