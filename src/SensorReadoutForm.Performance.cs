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
    private static readonly object hardwareDetailsCacheLock = new object();
    private static Dictionary<string, string> cachedCpuHardwareDetails;
    private static Dictionary<string, string> cachedMemoryHardwareDetails;
    private static Dictionary<string, string> cachedBoardHardwareDetails;
    private static Dictionary<string, string> cachedWindowsHardwareDetails;
    private static Dictionary<string, string> cachedFirmwareHardwareDetails;
    private static Dictionary<string, Dictionary<string, string>> cachedPhysicalDiskDetailsByHardware;
    private static Dictionary<string, Dictionary<string, string>> cachedLogicalDiskDetailsByRoot;
    private static DateTime cachedStorageTopologyDetailsUtc = DateTime.MinValue;
    private static readonly object gpuTotalMemoryCacheLock = new object();
    private static DateTime cachedGpuTotalMemoryUtc = DateTime.MinValue;
    private static double cachedGpuTotalMemoryBytes = -1;

    private IEnumerable<SensorRow> GetSystemPerformanceRows()
    {
        var rows = new List<SensorRow>();
        var fastRows = new List<SensorRow>();
        if (TryAddFastCpuUsageRow(fastRows) & TryAddFastMemoryRows(fastRows))
        {
            return fastRows;
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
            {
                var values = searcher.Get().Cast<ManagementObject>()
                    .Select(cpu => Convert.ToDouble(cpu["LoadPercentage"] ?? 0))
                    .ToList();
                if (values.Count > 0)
                {
                    rows.Add(new SensorRow
                    {
                        Type = "Performance",
                        Hardware = "CPU",
                        Name = "CPU usage",
                        Value = (float)values.Average(),
                        DisplayValue = FormatNumber(Math.Round(values.Average(), 1), "0.0") + "%",
                        Source = "Windows WMI"
                    });
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    var totalKb = Convert.ToDouble(os["TotalVisibleMemorySize"] ?? 0);
                    var freeKb = Convert.ToDouble(os["FreePhysicalMemory"] ?? 0);
                    if (totalKb <= 0)
                    {
                        continue;
                    }

                    var usedKb = Math.Max(0, totalKb - freeKb);
                    var usedPercent = usedKb / totalKb * 100.0;
                    var availablePercent = freeKb / totalKb * 100.0;
                    var memoryDetails = GetMemoryHardwareDetails();
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory total", DisplayValue = FormatBytes(totalKb * 1024.0), Source = "Windows WMI", Details = CloneDetails(memoryDetails) });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used", Value = (float)usedPercent, DisplayValue = FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%", Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used size", DisplayValue = FormatBytes(usedKb * 1024.0), Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory available", DisplayValue = FormatBytes(freeKb * 1024.0) + " (" + FormatNumber(Math.Round(availablePercent, 1), "0.0") + "%)", Source = "Windows WMI" });
                    break;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetGpuPerformanceRows()
    {
        var rows = new List<SensorRow>();
        AddGpuMemoryRows(rows);
        AddGpuEngineRows(rows);
        return rows;
    }

    private static void AddGpuMemoryRows(List<SensorRow> rows)
    {
        var dedicatedTotal = GetTotalGpuAdapterMemoryBytes();
        var dedicatedUsed = ReadGpuMemoryCounterTotal("GPU Local Adapter Memory", "Local Usage");
        var sharedUsed = ReadGpuMemoryCounterTotal("GPU Non Local Adapter Memory", "Non Local Usage");
        if (dedicatedTotal > 0)
        {
            AddGpuMemoryRow(rows, "Dedicated GPU memory total", dedicatedTotal, "gpu|memory|dedicated-total", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Source", "Win32_VideoController and display registry fallback" }
            });
        }

        if (dedicatedUsed > 0)
        {
            AddGpuMemoryRow(rows, "Dedicated GPU memory used", dedicatedUsed, "gpu|memory|dedicated-used", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Source", "GPU Local Adapter Memory performance counter" }
            });
        }

        if (dedicatedTotal > 0 && dedicatedUsed >= 0)
        {
            var free = Math.Max(0, dedicatedTotal - dedicatedUsed);
            AddGpuMemoryRow(rows, "Dedicated GPU memory free", free, "gpu|memory|dedicated-free", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Calculated from", "Dedicated GPU memory total minus dedicated GPU memory used" },
                { "Total source", "Win32_VideoController and display registry fallback" },
                { "Used source", "GPU Local Adapter Memory performance counter" }
            });
        }

        if (sharedUsed > 0)
        {
            AddGpuMemoryRow(rows, "Shared GPU memory used", sharedUsed, "gpu|memory|shared-used", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Source", "GPU Non Local Adapter Memory performance counter" }
            });
        }
    }

    private static void AddGpuMemoryRow(List<SensorRow> rows, string name, double bytes, string identifier, Dictionary<string, string> details)
    {
        if (rows == null || bytes < 0)
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "GPU memory",
            Name = name,
            Identifier = identifier,
            Value = (float)bytes,
            DisplayValue = FormatBytes(bytes),
            Source = "Windows GPU performance counters",
            Details = CloneDetails(details)
        });
    }

    private static double GetTotalGpuAdapterMemoryBytes()
    {
        lock (gpuTotalMemoryCacheLock)
        {
            if (cachedGpuTotalMemoryBytes >= 0 && (DateTime.UtcNow - cachedGpuTotalMemoryUtc).TotalMinutes < 10)
            {
                return cachedGpuTotalMemoryBytes;
            }
        }

        double total = 0;
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
            {
                foreach (ManagementObject gpu in searcher.Get())
                {
                    double bytes;
                    if (TryConvertToDouble(GetGpuAdapterMemoryBytes(Convert.ToString(gpu["Name"]), gpu["AdapterRAM"]), out bytes) && bytes > 0)
                    {
                        total += bytes;
                    }
                }
            }
        }
        catch
        {
        }

        lock (gpuTotalMemoryCacheLock)
        {
            cachedGpuTotalMemoryBytes = total;
            cachedGpuTotalMemoryUtc = DateTime.UtcNow;
        }

        return total;
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        try
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }

    private static double ReadGpuMemoryCounterTotal(string categoryName, string counterName)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return 0;
            }

            var category = new PerformanceCounterCategory(categoryName);
            double total = 0;
            foreach (var instance in category.GetInstanceNames().Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                double value;
                if (!TryReadPerformanceCounter(categoryName, counterName, instance, out value) || value <= 0)
                {
                    continue;
                }

                total += value;
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static void AddGpuEngineRows(List<SensorRow> rows)
    {
        const string categoryName = "GPU Engine";
        const string counterName = "Utilization Percentage";
        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return;
            }

            var category = new PerformanceCounterCategory(categoryName);
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in category.GetInstanceNames().Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var engineType = ExtractGpuEngineType(instance);
                if (string.IsNullOrWhiteSpace(engineType))
                {
                    continue;
                }

                double value;
                if (!TryReadPerformanceCounter(categoryName, counterName, instance, out value) || value <= 0)
                {
                    continue;
                }

                double existing;
                values.TryGetValue(engineType, out existing);
                if (value > existing)
                {
                    values[engineType] = value;
                }
            }

            foreach (var pair in values.OrderBy(p => GpuEngineSortIndex(p.Key)).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = "GPU",
                    Name = "GPU " + FormatGpuEngineType(pair.Key) + " usage",
                    Identifier = "gpu|engine|" + MakeIdentifierPart(pair.Key),
                    Value = (float)pair.Value,
                    DisplayValue = FormatNumber(Math.Round(pair.Value, 1), "0.0") + "%",
                    Source = "Windows GPU performance counters",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Counter set", categoryName },
                        { "Counter", counterName },
                        { "Aggregation", "Highest active engine of this type" }
                    }
                });
            }
        }
        catch
        {
        }
    }

    private static bool TryReadPerformanceCounter(string categoryName, string counterName, string instanceName, out double value)
    {
        value = 0;
        try
        {
            using (var counter = new PerformanceCounter(categoryName, counterName, instanceName, true))
            {
                value = counter.NextValue();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractGpuEngineType(string instance)
    {
        var marker = "_engtype_";
        var index = string.IsNullOrWhiteSpace(instance) ? -1 : instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "" : instance.Substring(index + marker.Length).Trim();
    }

    private static string FormatGpuEngineType(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace("_", " ");
    }

    private static int GpuEngineSortIndex(string value)
    {
        if (string.Equals(value, "3D", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(value, "Copy", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(value, "VideoDecode", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(value, "VideoEncode", StringComparison.OrdinalIgnoreCase)) return 3;
        return 10;
    }

    private static string MakeIdentifierPart(string value)
    {
        return Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    private bool TryAddFastCpuUsageRow(List<SensorRow> rows)
    {
        try
        {
            NativeMethods.FileTime idleTime;
            NativeMethods.FileTime kernelTime;
            NativeMethods.FileTime userTime;
            if (!NativeMethods.GetSystemTimes(out idleTime, out kernelTime, out userTime))
            {
                return false;
            }

            var idle = idleTime.ToUInt64();
            var kernel = kernelTime.ToUInt64();
            var user = userTime.ToUInt64();
            var load = 0.0;
            if (hasLastCpuTimes)
            {
                var idleDelta = idle - lastCpuIdleTime;
                var kernelDelta = kernel - lastCpuKernelTime;
                var userDelta = user - lastCpuUserTime;
                var totalDelta = kernelDelta + userDelta;
                if (totalDelta > 0)
                {
                    load = Math.Max(0, Math.Min(100, (1.0 - (idleDelta / (double)totalDelta)) * 100.0));
                }
            }

            lastCpuIdleTime = idle;
            lastCpuKernelTime = kernel;
            lastCpuUserTime = user;
            hasLastCpuTimes = true;
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = "CPU",
                Name = "CPU usage",
                Value = (float)load,
                DisplayValue = FormatNumber(Math.Round(load, 1), "0.0") + "%",
                Source = "Windows"
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<SensorRow> GetCpuDetailRows()
    {
        var rows = new List<SensorRow>();
        var cpuDetails = GetCpuHardwareDetails();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
            {
                foreach (ManagementObject cpu in searcher.Get())
                {
                    AddCpuDetailRow(rows, "CPU name", Convert.ToString(cpu["Name"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU vendor", Convert.ToString(cpu["Manufacturer"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU cores", Convert.ToString(cpu["NumberOfCores"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU threads", Convert.ToString(cpu["NumberOfLogicalProcessors"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU max clock", FormatMegahertz(cpu["MaxClockSpeed"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU current clock", FormatMegahertz(cpu["CurrentClockSpeed"]), cpuDetails);
                    AddCpuCacheRows(rows, cpuDetails);
                    AddCpuDetailRow(rows, "CPU socket", Convert.ToString(cpu["SocketDesignation"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU architecture", FormatCpuArchitecture(cpu["Architecture"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU instruction sets", GetProcessorInstructionSetSummary(), cpuDetails);
                    AddCpuDetailRow(rows, "CPU virtualization extensions", FormatWindowsReportedCpuFeature(cpu["VMMonitorModeExtensions"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU virtualization enabled in firmware", FormatYesNo(cpu["VirtualizationFirmwareEnabled"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU hardware VM memory translation (SLAT/EPT/NPT)", FormatWindowsReportedCpuFeature(cpu["SecondLevelAddressTranslationExtensions"]), cpuDetails);
                    AddCpuDetailRow(rows, "CPU data execution prevention", GetProcessorFeatureYesNo(12), cpuDetails);
                    AddCpuDetailRow(rows, "CPU processor ID", Convert.ToString(cpu["ProcessorId"]), cpuDetails);
                    break;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddCpuCacheRows(List<SensorRow> rows)
    {
        AddCpuCacheRows(rows, GetCpuHardwareDetails());
    }

    private static void AddCpuCacheRows(List<SensorRow> rows, Dictionary<string, string> cpuDetails)
    {
        if (rows == null)
        {
            return;
        }

        try
        {
            var cacheSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("SELECT Level, Purpose, InstalledSize, MaxCacheSize FROM Win32_CacheMemory"))
            {
                foreach (ManagementObject cache in searcher.Get())
                {
                    var level = CpuCacheLevelName(cache["Level"], cache["Purpose"]);
                    if (string.IsNullOrWhiteSpace(level))
                    {
                        continue;
                    }

                    var sizeKb = ReadPositiveLong(cache["InstalledSize"]);
                    if (sizeKb <= 0)
                    {
                        sizeKb = ReadPositiveLong(cache["MaxCacheSize"]);
                    }

                    if (sizeKb <= 0)
                    {
                        continue;
                    }

                    long existing;
                    cacheSizes.TryGetValue(level, out existing);
                    cacheSizes[level] = existing + sizeKb;
                }
            }

            foreach (var level in new[] { "L1", "L2", "L3", "L4" })
            {
                long sizeKb;
                if (cacheSizes.TryGetValue(level, out sizeKb))
                {
                    AddCpuDetailRow(rows, "CPU " + level + " cache", FormatCacheKilobytes(sizeKb), cpuDetails);
                }
            }
        }
        catch
        {
        }
    }

    private static string CpuCacheLevelName(object levelValue, object purposeValue)
    {
        var purpose = Convert.ToString(purposeValue) ?? "";
        foreach (var level in new[] { "L1", "L2", "L3", "L4" })
        {
            if (purpose.IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return level;
            }
        }

        try
        {
            switch (Convert.ToInt32(levelValue))
            {
                case 3:
                    return "L1";
                case 4:
                    return "L2";
                case 5:
                    return "L3";
                case 6:
                    return "L4";
            }
        }
        catch
        {
        }

        return "";
    }

    private static long ReadPositiveLong(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            var result = Convert.ToInt64(value);
            return result > 0 ? result : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatCacheKilobytes(long sizeKb)
    {
        if (sizeKb <= 0)
        {
            return "";
        }

        if (sizeKb >= 1024 && sizeKb % 1024 == 0)
        {
            return FormatNumber(sizeKb / 1024.0, "0") + " MB";
        }

        if (sizeKb >= 1024)
        {
            return FormatNumber(Math.Round(sizeKb / 1024.0, 1), "0.0") + " MB";
        }

        return FormatNumber(sizeKb, "0") + " KB";
    }

    private static string GetProcessorInstructionSetSummary()
    {
        var features = new List<string>();
        AddProcessorFeature(features, "MMX", 3);
        AddProcessorFeature(features, "3DNow!", 7);
        AddProcessorFeature(features, "SSE", 6);
        AddProcessorFeature(features, "SSE2", 10);
        AddProcessorFeature(features, "SSE3", 13);
        AddProcessorFeature(features, "SSSE3", 36);
        AddProcessorFeature(features, "SSE4.1", 37);
        AddProcessorFeature(features, "SSE4.2", 38);
        AddProcessorFeature(features, "AVX", 39);
        AddProcessorFeature(features, "AVX2", 40);
        AddProcessorFeature(features, "AVX-512F", 41);
        return features.Count == 0 ? "" : string.Join(", ", features.ToArray());
    }

    private static void AddProcessorFeature(List<string> features, string name, uint featureId)
    {
        try
        {
            if (NativeMethods.IsProcessorFeaturePresent(featureId))
            {
                features.Add(name);
            }
        }
        catch
        {
        }
    }

    private static string FormatWindowsReportedCpuFeature(object value)
    {
        if (value == null)
        {
            return "";
        }

        try
        {
            return Convert.ToBoolean(value) ? "Yes" : "Not reported by Windows";
        }
        catch
        {
            return "";
        }
    }

    private static string GetProcessorFeatureYesNo(uint featureId)
    {
        try
        {
            return NativeMethods.IsProcessorFeaturePresent(featureId) ? "Yes" : "No";
        }
        catch
        {
            return "";
        }
    }

    private static void AddCpuDetailRow(List<SensorRow> rows, string name, string value)
    {
        AddCpuDetailRow(rows, name, value, GetCpuHardwareDetails());
    }

    private static void AddCpuDetailRow(List<SensorRow> rows, string name, string value, Dictionary<string, string> details)
    {
        if (rows == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "CPU",
            Name = name,
            DisplayValue = value.Trim(),
            Source = "Windows WMI",
            Details = CloneDetails(details)
        });
    }

    private static string FormatMegahertz(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        double mhz;
        return double.TryParse(text, out mhz) && mhz > 0
            ? FormatNumber(Math.Round(mhz, 0), "0") + " MHz"
            : "";
    }

    private static string FormatYesNo(object value)
    {
        if (value == null)
        {
            return "";
        }

        bool flag;
        if (bool.TryParse(Convert.ToString(value), out flag))
        {
            return flag ? "Yes" : "No";
        }

        return Convert.ToString(value);
    }

    private static string FormatCpuArchitecture(object value)
    {
        var text = Convert.ToString(value);
        int code;
        if (!int.TryParse(text, out code))
        {
            return text;
        }

        switch (code)
        {
            case 0: return "x86";
            case 1: return "MIPS";
            case 2: return "Alpha";
            case 3: return "PowerPC";
            case 5: return "ARM";
            case 6: return "Itanium";
            case 9: return "x64";
            case 12: return "ARM64";
            default: return text;
        }
    }

    private static bool TryAddFastMemoryRows(List<SensorRow> rows)
    {
        try
        {
            var status = NativeMethods.MemoryStatusEx.Create();
            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                return false;
            }

            var totalBytes = (double)status.ullTotalPhys;
            var freeBytes = (double)status.ullAvailPhys;
            var usedBytes = Math.Max(0, totalBytes - freeBytes);
            var usedPercent = usedBytes / totalBytes * 100.0;
            var availablePercent = freeBytes / totalBytes * 100.0;
            var memoryDetails = GetMemoryHardwareDetails();
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory total", DisplayValue = FormatBytes(totalBytes), Source = "Windows", Details = CloneDetails(memoryDetails) });
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used", Value = (float)usedPercent, DisplayValue = FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%", Source = "Windows" });
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used size", DisplayValue = FormatBytes(usedBytes), Source = "Windows" });
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory available", DisplayValue = FormatBytes(freeBytes) + " (" + FormatNumber(Math.Round(availablePercent, 1), "0.0") + "%)", Source = "Windows" });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
