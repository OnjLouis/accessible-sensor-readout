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
    private static readonly object pagingFileCacheLock = new object();
    private static DateTime cachedPagingFileRowsUtc = DateTime.MinValue;
    private static PagingFileSummary cachedPagingFileSummary;

    private sealed class PagingFileSummary
    {
        public double TotalBytes;
        public double UsedBytes;
        public double PeakBytes;
        public readonly Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

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
                    AddPagingFileRows(rows);
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
        AddNvidiaSmiPerformanceRows(rows);
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

    private static void AddNvidiaSmiPerformanceRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

        var output = RunNvidiaSmiQuery("--query-gpu=name,pci.bus_id,driver_version,vbios_version,temperature.gpu,fan.speed,utilization.gpu,memory.total,memory.used,memory.free,power.draw,power.limit,clocks.gr,clocks.mem");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var index = 0;
        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',').Select(f => f.Trim()).ToArray();
            if (fields.Length < 14)
            {
                continue;
            }

            var gpuIndex = index++;
            var gpuName = string.IsNullOrWhiteSpace(fields[0]) ? "NVIDIA GPU" : fields[0];
            var idPrefix = "nvidia-smi|gpu|" + gpuIndex.ToString(CultureInfo.InvariantCulture) + "|";
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "NVIDIA SMI name", gpuName },
                { "NVIDIA SMI bus ID", fields[1] },
                { "NVIDIA SMI driver version", fields[2] },
                { "NVIDIA SMI VBIOS version", fields[3] },
                { "Source", "nvidia-smi.exe" }
            };

            float value;
            if (TryParseNvidiaSmiFloat(fields[4], out value))
            {
                rows.Add(new SensorRow
                {
                    Type = "Temperature",
                    Hardware = gpuName,
                    Name = "GPU temperature",
                    Identifier = idPrefix + "temperature",
                    Value = value,
                    Source = "NVIDIA SMI",
                    Details = CloneDetails(details)
                });
            }

            if (TryParseNvidiaSmiFloat(fields[5], out value))
            {
                rows.Add(new SensorRow
                {
                    Type = "Fan",
                    Hardware = gpuName,
                    Name = "GPU fan duty cycle",
                    Identifier = idPrefix + "fan-duty",
                    Value = value,
                    DisplayValue = FormatNumber(Math.Round(value, 0), "0") + "%",
                    Source = "NVIDIA SMI",
                    Details = CloneDetails(details)
                });
            }

            if (TryParseNvidiaSmiFloat(fields[6], out value))
            {
                AddNvidiaSmiPerformanceRow(rows, "GPU", "GPU usage", idPrefix + "usage", value, "%", details);
            }

            AddNvidiaSmiMemoryRow(rows, "NVIDIA GPU memory total", idPrefix + "memory-total", fields[7], details);
            AddNvidiaSmiMemoryRow(rows, "NVIDIA GPU memory used", idPrefix + "memory-used", fields[8], details);
            AddNvidiaSmiMemoryRow(rows, "NVIDIA GPU memory free", idPrefix + "memory-free", fields[9], details);

            if (TryParseNvidiaSmiFloat(fields[10], out value))
            {
                AddNvidiaSmiPerformanceRow(rows, "GPU", "GPU power draw", idPrefix + "power-draw", value, " W", details);
            }

            if (TryParseNvidiaSmiFloat(fields[11], out value))
            {
                AddNvidiaSmiPerformanceRow(rows, "GPU", "GPU power limit", idPrefix + "power-limit", value, " W", details);
            }

            if (TryParseNvidiaSmiFloat(fields[12], out value))
            {
                AddNvidiaSmiPerformanceRow(rows, "GPU", "GPU graphics clock", idPrefix + "graphics-clock", value, " MHz", details);
            }

            if (TryParseNvidiaSmiFloat(fields[13], out value))
            {
                AddNvidiaSmiPerformanceRow(rows, "GPU", "GPU memory clock", idPrefix + "memory-clock", value, " MHz", details);
            }
        }
    }

    private static void AddNvidiaSmiMemoryRow(List<SensorRow> rows, string name, string identifier, string mibText, Dictionary<string, string> details)
    {
        float mib;
        if (!TryParseNvidiaSmiFloat(mibText, out mib) || mib < 0)
        {
            return;
        }

        var bytes = mib * 1024.0 * 1024.0;
        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "GPU memory",
            Name = name,
            Identifier = identifier,
            Value = (float)bytes,
            DisplayValue = FormatBytes(bytes),
            Source = "NVIDIA SMI",
            Details = CloneDetails(details)
        });
    }

    private static void AddNvidiaSmiPerformanceRow(List<SensorRow> rows, string hardware, string name, string identifier, float value, string suffix, Dictionary<string, string> details)
    {
        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = hardware,
            Name = name,
            Identifier = identifier,
            Value = value,
            DisplayValue = FormatNumber(Math.Round(value, suffix == "%" ? 1 : 0), suffix == "%" ? "0.0" : "0") + suffix,
            Source = "NVIDIA SMI",
            Details = CloneDetails(details)
        });
    }

    private static bool TryParseNvidiaSmiFloat(string text, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var clean = text.Trim();
        if (clean.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Not Supported", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return float.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
                    var cpuName = Convert.ToString(cpu["Name"]);
                    var cpuManufacturer = Convert.ToString(cpu["Manufacturer"]);
                    AddCpuDetailRow(rows, "CPU name", cpuName, cpuDetails);
                    AddCpuDetailRow(rows, "CPU generation", FormatCpuGeneration(cpuName, cpuManufacturer), cpuDetails);
                    AddCpuDetailRow(rows, "CPU vendor", cpuManufacturer, cpuDetails);
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

    private static string FormatCpuGeneration(string cpuName, string manufacturer)
    {
        var text = (cpuName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (!ContainsAny(text, "Intel", "Core") && !ContainsAny(manufacturer, "Intel", "GenuineIntel"))
        {
            return "";
        }

        var dash = text.IndexOf('-');
        if (dash < 0 || dash + 1 >= text.Length)
        {
            return "";
        }

        var digits = "";
        for (var i = dash + 1; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i]))
            {
                break;
            }

            digits += text[i];
        }

        if (digits.Length < 4)
        {
            return "";
        }

        int generation;
        if (digits.Length >= 5)
        {
            if (!int.TryParse(digits.Substring(0, 2), out generation))
            {
                return "";
            }
        }
        else if (!int.TryParse(digits.Substring(0, 1), out generation))
        {
            return "";
        }

        if (generation <= 0)
        {
            return "";
        }

        return FormatOrdinal(generation) + " Gen Intel Core";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var value in values ?? new string[0])
        {
            if (!string.IsNullOrWhiteSpace(value) && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatOrdinal(int value)
    {
        var mod100 = value % 100;
        if (mod100 >= 11 && mod100 <= 13)
        {
            return value.ToString(CultureInfo.InvariantCulture) + "th";
        }

        switch (value % 10)
        {
            case 1:
                return value.ToString(CultureInfo.InvariantCulture) + "st";
            case 2:
                return value.ToString(CultureInfo.InvariantCulture) + "nd";
            case 3:
                return value.ToString(CultureInfo.InvariantCulture) + "rd";
            default:
                return value.ToString(CultureInfo.InvariantCulture) + "th";
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
            AddPagingFileRows(rows);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddPagingFileRows(List<SensorRow> rows)
    {
        var summary = GetPagingFileSummary();
        if (summary == null || summary.TotalBytes <= 0)
        {
            return;
        }

        var usedBytes = Math.Max(0, Math.Min(summary.UsedBytes, summary.TotalBytes));
        var freeBytes = Math.Max(0, summary.TotalBytes - usedBytes);
        var usedPercent = summary.TotalBytes <= 0 ? 0 : usedBytes / summary.TotalBytes * 100.0;
        var freePercent = summary.TotalBytes <= 0 ? 0 : freeBytes / summary.TotalBytes * 100.0;
        var details = CloneDetails(summary.Details);

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "Memory",
            Name = "Paging file total",
            Identifier = "memory|paging-file|total",
            DisplayValue = FormatBytes(summary.TotalBytes),
            Source = "Windows WMI",
            Details = details
        });
        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "Memory",
            Name = "Paging file used",
            Identifier = "memory|paging-file|used",
            Value = (float)usedPercent,
            DisplayValue = FormatBytes(usedBytes) + " (" + FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%)",
            Source = "Windows WMI",
            Details = details
        });
        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "Memory",
            Name = "Paging file free",
            Identifier = "memory|paging-file|free",
            Value = (float)freePercent,
            DisplayValue = FormatBytes(freeBytes) + " (" + FormatNumber(Math.Round(freePercent, 1), "0.0") + "%)",
            Source = "Windows WMI",
            Details = details
        });
    }

    private static PagingFileSummary GetPagingFileSummary()
    {
        lock (pagingFileCacheLock)
        {
            if (cachedPagingFileSummary != null && (DateTime.UtcNow - cachedPagingFileRowsUtc).TotalSeconds < 30)
            {
                return cachedPagingFileSummary;
            }
        }

        PagingFileSummary summary = null;
        try
        {
            var options = new EnumerationOptions
            {
                ReturnImmediately = true,
                Timeout = TimeSpan.FromSeconds(2)
            };
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
            {
                searcher.Options = options;
                var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Source", "Win32_PageFileUsage" }
                };
                double totalBytes = 0;
                double usedBytes = 0;
                double peakBytes = 0;
                int index = 0;
                foreach (ManagementObject pageFile in searcher.Get())
                {
                    index++;
                    var name = CleanWmiText(Convert.ToString(pageFile["Name"]));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Paging file " + index.ToString(CultureInfo.InvariantCulture);
                    }

                    var allocatedMb = WmiMegabytesToBytes(pageFile["AllocatedBaseSize"]);
                    var currentMb = WmiMegabytesToBytes(pageFile["CurrentUsage"]);
                    var peakMb = WmiMegabytesToBytes(pageFile["PeakUsage"]);
                    totalBytes += Math.Max(0, allocatedMb);
                    usedBytes += Math.Max(0, currentMb);
                    peakBytes += Math.Max(0, peakMb);
                    AddDetail(details, "Paging file " + index.ToString(CultureInfo.InvariantCulture) + " path", name);
                    AddDetail(details, name + " total", FormatBytes(allocatedMb));
                    AddDetail(details, name + " used", FormatBytes(currentMb));
                    AddDetail(details, name + " peak used", FormatBytes(peakMb));
                }

                if (totalBytes > 0)
                {
                    summary = new PagingFileSummary
                    {
                        TotalBytes = totalBytes,
                        UsedBytes = usedBytes,
                        PeakBytes = peakBytes
                    };
                    foreach (var pair in details)
                    {
                        summary.Details[pair.Key] = pair.Value;
                    }

                    AddDetail(summary.Details, "Paging file total", FormatBytes(totalBytes));
                    AddDetail(summary.Details, "Paging file used", FormatBytes(Math.Max(0, Math.Min(usedBytes, totalBytes))));
                    AddDetail(summary.Details, "Paging file free", FormatBytes(Math.Max(0, totalBytes - Math.Max(0, Math.Min(usedBytes, totalBytes)))));
                    AddDetail(summary.Details, "Paging file peak used", FormatBytes(peakBytes));
                }
            }
        }
        catch
        {
        }

        lock (pagingFileCacheLock)
        {
            cachedPagingFileSummary = summary;
            cachedPagingFileRowsUtc = DateTime.UtcNow;
            return cachedPagingFileSummary;
        }
    }

    private static double WmiMegabytesToBytes(object value)
    {
        double megabytes;
        if (!TryConvertToDouble(value, out megabytes) || megabytes <= 0)
        {
            return 0;
        }

        return megabytes * 1024.0 * 1024.0;
    }
}
