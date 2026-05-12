using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class LogicalDiskPerformanceCounters : IDisposable
    {
        public readonly PerformanceCounter ReadBytes;
        public readonly PerformanceCounter WriteBytes;
        public readonly PerformanceCounter ReadActivity;
        public readonly PerformanceCounter WriteActivity;
        public readonly PerformanceCounter TotalActivity;

        public LogicalDiskPerformanceCounters(string instance)
        {
            ReadBytes = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instance, true);
            WriteBytes = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instance, true);
            ReadActivity = new PerformanceCounter("LogicalDisk", "% Disk Read Time", instance, true);
            WriteActivity = new PerformanceCounter("LogicalDisk", "% Disk Write Time", instance, true);
            TotalActivity = new PerformanceCounter("LogicalDisk", "% Disk Time", instance, true);
        }

        public void Dispose()
        {
            ReadBytes.Dispose();
            WriteBytes.Dispose();
            ReadActivity.Dispose();
            WriteActivity.Dispose();
            TotalActivity.Dispose();
        }
    }

    private IEnumerable<SensorRow> GetLibreHardwareMonitorSensors(bool includeSlowHardware)
    {
        try
        {
            lock (lhmLock)
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
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private IEnumerable<SensorRow> GetCoreTempRows()
    {
        try
        {
            using (var mappedFile = MemoryMappedFile.OpenExisting("CoreTempMappingObjectEx"))
            using (var stream = mappedFile.CreateViewStream(0, Marshal.SizeOf(typeof(CoreTempSharedDataEx)), MemoryMappedFileAccess.Read))
            {
                var size = Marshal.SizeOf(typeof(CoreTempSharedDataEx));
                var buffer = new byte[size];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset < size)
                {
                    return Enumerable.Empty<SensorRow>();
                }

                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var data = (CoreTempSharedDataEx)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(CoreTempSharedDataEx));
                    return BuildCoreTempRows(data);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private static IEnumerable<SensorRow> BuildCoreTempRows(CoreTempSharedDataEx data)
    {
        var rows = new List<SensorRow>();
        var coreCount = Math.Max(0, Math.Min(256, (int)data.UiCoreCnt));
        if (coreCount == 0 || data.FTemp == null)
        {
            return rows;
        }

        var cpuName = string.IsNullOrWhiteSpace(data.CpuName) ? "CPU" : data.CpuName.Trim();
        var temps = new List<float>();
        for (var i = 0; i < coreCount; i++)
        {
            var temp = data.FTemp[i];
            if (float.IsNaN(temp) || float.IsInfinity(temp) || temp <= 0)
            {
                continue;
            }

            if (data.UcFahrenheit != 0)
            {
                temp = (temp - 32.0f) * 5.0f / 9.0f;
            }

            if (data.UcDeltaToTjMax != 0 && data.UiTjMax != null && i < data.UiTjMax.Length && data.UiTjMax[i] > 0)
            {
                temp = data.UiTjMax[i] - temp;
            }

            temps.Add(temp);
            rows.Add(new SensorRow
            {
                Type = "Temperature",
                Hardware = cpuName,
                Name = "Core #" + (i + 1),
                Identifier = "coretemp/core/" + i,
                Value = temp,
                Source = "Core Temp shared memory"
            });
        }

        if (temps.Count > 0)
        {
            rows.Add(new SensorRow
            {
                Type = "Temperature",
                Hardware = cpuName,
                Name = "CPU package",
                Identifier = "coretemp/package",
                Value = temps.Max(),
                Source = "Core Temp shared memory"
            });
        }

        if (data.UiLoad != null)
        {
            var loads = data.UiLoad.Take(coreCount).Where(v => v <= 100).Select(v => (float)v).ToList();
            if (loads.Count > 0)
            {
                var load = loads.Average();
                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = "CPU",
                    Name = "CPU usage",
                    Value = load,
                    DisplayValue = FormatNumber(Math.Round(load, 1), "0.0") + "%",
                    Source = "Core Temp shared memory"
                });
            }
        }

        return rows;
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
            hardwareName.IndexOf("Nuvoton NCT6795D", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Motherboard";
        }

        return NormalizeHardwareName(hardwareName);
    }

    private static IEnumerable<SensorRow> GetWindowsSmartRows()
    {
        foreach (var row in GetPhysicalDiskRows())
        {
            yield return row;
        }

        foreach (var row in GetStorageReliabilityRows())
        {
            yield return row;
        }
    }

    private static IEnumerable<SensorRow> GetPhysicalDiskRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, HealthStatus, MediaType, OperationalStatus, Size FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    var name = Convert.ToString(disk["FriendlyName"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Physical disk";
                    }

                    name = NormalizeStorageHardwareName(name);
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Health", DisplayValue = DecodeHealthStatus(disk["HealthStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Media type", DisplayValue = DecodeMediaType(disk["MediaType"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Operational status", DisplayValue = DecodeOperationalStatus(disk["OperationalStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Size", DisplayValue = FormatBytes(disk["Size"]), Source = "Windows Storage" });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetStorageReliabilityRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
            {
                foreach (ManagementObject counter in searcher.Get())
                {
                    var name = Convert.ToString(counter["DeviceId"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Storage device";
                    }

                    name = NormalizeStorageHardwareName(name);
                    AddSmartCounter(rows, name, counter, "Temperature", "Temperature", true);
                    AddSmartCounter(rows, name, counter, "TemperatureMax", "Maximum temperature", true);
                    AddSmartCounter(rows, name, counter, "PowerOnHours", "Power on hours", false);
                    AddSmartCounter(rows, name, counter, "ReadErrorsTotal", "Read errors total", false);
                    AddSmartCounter(rows, name, counter, "WriteErrorsTotal", "Write errors total", false);
                    AddSmartCounter(rows, name, counter, "Wear", "Wear", false);
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddSmartCounter(List<SensorRow> rows, string hardware, ManagementBaseObject counter, string property, string name, bool temperature)
    {
        try
        {
            var raw = counter[property];
            if (raw == null)
            {
                return;
            }

            double value;
            if (!double.TryParse(Convert.ToString(raw), out value))
            {
                return;
            }

            if (value <= 0 && temperature)
            {
                return;
            }

            rows.Add(new SensorRow
            {
                Type = temperature ? "Temperature" : "SMART",
                Hardware = hardware,
                Name = name,
                Value = (float)value,
                DisplayValue = temperature ? null : FormatNumber(Math.Round(value, 0), "0"),
                Source = "Windows Storage"
            });
        }
        catch
        {
        }
    }

    private static IEnumerable<SensorRow> GetWindowsLogicalDiskRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (drive.DriveType != System.IO.DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var freeBytes = Math.Max(0, drive.AvailableFreeSpace);
                var usedBytes = Math.Max(0, drive.TotalSize - freeBytes);
                var usedPercent = usedBytes / (double)drive.TotalSize * 100.0;
                var freePercent = freeBytes / (double)drive.TotalSize * 100.0;
                var hardware = GetLogicalDiskHardwareName(drive);

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Total space",
                    DisplayValue = FormatBytes(drive.TotalSize),
                    Source = "Windows Logical Disk"
                });

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Used space",
                    Value = (float)usedPercent,
                    DisplayValue = FormatBytes(usedBytes) + " (" + FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%)",
                    Source = "Windows Logical Disk"
                });

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Free space",
                    Value = (float)freePercent,
                    DisplayValue = FormatBytes(freeBytes) + " (" + FormatNumber(Math.Round(freePercent, 1), "0.0") + "%)",
                    Source = "Windows Logical Disk"
                });
            }
        }
        catch
        {
        }

        return rows;
    }

    private static string GetLogicalDiskHardwareName(System.IO.DriveInfo drive)
    {
        var root = drive.Name == null ? "" : drive.Name.TrimEnd('\\');
        var label = "";
        try
        {
            label = drive.VolumeLabel;
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(label) ? root : root + " " + label;
    }

    private IEnumerable<SensorRow> GetLogicalDiskPerformanceRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (drive.DriveType != System.IO.DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var instance = drive.Name == null ? "" : drive.Name.TrimEnd('\\');
                if (string.IsNullOrWhiteSpace(instance))
                {
                    continue;
                }

                LogicalDiskPerformanceCounters counters;
                if (!TryGetLogicalDiskPerformanceCounters(instance, out counters))
                {
                    continue;
                }

                var hardware = GetLogicalDiskHardwareName(drive);
                float readBytes;
                float writeBytes;
                float readActivity;
                float writeActivity;
                float totalActivity;
                if (TryReadLogicalDiskPerformanceCounters(instance, counters, out readBytes, out writeBytes, out readActivity, out writeActivity, out totalActivity))
                {
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Read rate", Identifier = "logicaldisk/" + instance + "/read", Value = readBytes, DisplayValue = FormatBytesPerSecond(readBytes), Source = "Windows Logical Disk" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Write rate", Identifier = "logicaldisk/" + instance + "/write", Value = writeBytes, DisplayValue = FormatBytesPerSecond(writeBytes), Source = "Windows Logical Disk" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Read activity", Identifier = "logicaldisk/" + instance + "/read-activity", Value = readActivity, DisplayValue = FormatNumber(Math.Round(readActivity, 1), "0.0") + "%", Source = "Windows Logical Disk" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Write activity", Identifier = "logicaldisk/" + instance + "/write-activity", Value = writeActivity, DisplayValue = FormatNumber(Math.Round(writeActivity, 1), "0.0") + "%", Source = "Windows Logical Disk" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Total activity", Identifier = "logicaldisk/" + instance + "/total-activity", Value = totalActivity, DisplayValue = FormatNumber(Math.Round(totalActivity, 1), "0.0") + "%", Source = "Windows Logical Disk" });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private bool TryGetLogicalDiskPerformanceCounters(string instance, out LogicalDiskPerformanceCounters counters)
    {
        counters = null;
        lock (logicalDiskCountersLock)
        {
            if (logicalDiskCounters.TryGetValue(instance, out counters))
            {
                return true;
            }

            try
            {
                counters = new LogicalDiskPerformanceCounters(instance);
                logicalDiskCounters[instance] = counters;
                return true;
            }
            catch
            {
                counters = null;
                return false;
            }
        }
    }

    private bool TryReadLogicalDiskPerformanceCounters(string instance, LogicalDiskPerformanceCounters counters, out float readBytes, out float writeBytes, out float readActivity, out float writeActivity, out float totalActivity)
    {
        readBytes = 0;
        writeBytes = 0;
        readActivity = 0;
        writeActivity = 0;
        totalActivity = 0;
        try
        {
            readBytes = Math.Max(0, counters.ReadBytes.NextValue());
            writeBytes = Math.Max(0, counters.WriteBytes.NextValue());
            readActivity = ClampPercent(counters.ReadActivity.NextValue());
            writeActivity = ClampPercent(counters.WriteActivity.NextValue());
            totalActivity = ClampPercent(counters.TotalActivity.NextValue());
            return true;
        }
        catch
        {
            RemoveLogicalDiskPerformanceCounters(instance);
            return false;
        }
    }

    private void RemoveLogicalDiskPerformanceCounters(string instance)
    {
        lock (logicalDiskCountersLock)
        {
            LogicalDiskPerformanceCounters counters;
            if (!logicalDiskCounters.TryGetValue(instance, out counters))
            {
                return;
            }

            logicalDiskCounters.Remove(instance);
            counters.Dispose();
        }
    }

    private static float ClampPercent(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0;
        }

        if (value < 0)
        {
            return 0;
        }

        return value > 100 ? 100 : value;
    }

    private static readonly object hardwareDetailsCacheLock = new object();
    private static Dictionary<string, string> cachedCpuHardwareDetails;
    private static Dictionary<string, string> cachedMemoryHardwareDetails;

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
            Details = name.Equals("CPU name", StringComparison.OrdinalIgnoreCase) ? CloneDetails(details) : null
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

    private static Dictionary<string, string> GetCpuHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedCpuHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedCpuHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
            {
                foreach (ManagementObject cpu in searcher.Get())
                {
                    AddDetail(details, "CPU name", GetWmiPropertyText(cpu, "Name"));
                    AddDetail(details, "CPU manufacturer", GetWmiPropertyText(cpu, "Manufacturer"));
                    AddDetail(details, "CPU description", GetWmiPropertyText(cpu, "Description"));
                    AddDetail(details, "CPU caption", GetWmiPropertyText(cpu, "Caption"));
                    AddDetail(details, "CPU device ID", GetWmiPropertyText(cpu, "DeviceID"));
                    AddDetail(details, "CPU processor ID", GetWmiPropertyText(cpu, "ProcessorId"));
                    AddDetail(details, "CPU socket", GetWmiPropertyText(cpu, "SocketDesignation"));
                    AddDetail(details, "CPU architecture", FormatCpuArchitecture(GetWmiPropertyValue(cpu, "Architecture")));
                    AddDetail(details, "CPU address width", FormatBits(GetWmiPropertyValue(cpu, "AddressWidth")));
                    AddDetail(details, "CPU data width", FormatBits(GetWmiPropertyValue(cpu, "DataWidth")));
                    AddDetail(details, "CPU family", GetWmiPropertyText(cpu, "Family"));
                    AddDetail(details, "CPU stepping", GetWmiPropertyText(cpu, "Stepping"));
                    AddDetail(details, "CPU revision", GetWmiPropertyText(cpu, "Revision"));
                    AddDetail(details, "CPU processor type", GetWmiPropertyText(cpu, "ProcessorType"));
                    AddDetail(details, "CPU upgrade method", GetWmiPropertyText(cpu, "UpgradeMethod"));
                    AddDetail(details, "CPU current voltage", FormatTenthsOfVolt(GetWmiPropertyValue(cpu, "CurrentVoltage")));
                    AddDetail(details, "CPU voltage caps", GetWmiPropertyText(cpu, "VoltageCaps"));
                    AddDetail(details, "CPU external clock", FormatMegahertz(GetWmiPropertyValue(cpu, "ExtClock")));
                    AddDetail(details, "CPU max clock", FormatMegahertz(GetWmiPropertyValue(cpu, "MaxClockSpeed")));
                    AddDetail(details, "CPU current clock", FormatMegahertz(GetWmiPropertyValue(cpu, "CurrentClockSpeed")));
                    AddDetail(details, "CPU cores", GetWmiPropertyText(cpu, "NumberOfCores"));
                    AddDetail(details, "CPU enabled cores", GetWmiPropertyText(cpu, "NumberOfEnabledCore"));
                    AddDetail(details, "CPU logical processors", GetWmiPropertyText(cpu, "NumberOfLogicalProcessors"));
                    AddDetail(details, "CPU thread count", GetWmiPropertyText(cpu, "ThreadCount"));
                    AddDetail(details, "CPU L2 cache size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cpu, "L2CacheSize")));
                    AddDetail(details, "CPU L2 cache speed", FormatMegahertz(GetWmiPropertyValue(cpu, "L2CacheSpeed")));
                    AddDetail(details, "CPU L3 cache size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cpu, "L3CacheSize")));
                    AddDetail(details, "CPU L3 cache speed", FormatMegahertz(GetWmiPropertyValue(cpu, "L3CacheSpeed")));
                    AddDetail(details, "CPU instruction sets", GetProcessorInstructionSetSummary());
                    AddDetail(details, "CPU virtualization extensions", FormatWindowsReportedCpuFeature(GetWmiPropertyValue(cpu, "VMMonitorModeExtensions")));
                    AddDetail(details, "CPU virtualization enabled in firmware", FormatYesNo(GetWmiPropertyValue(cpu, "VirtualizationFirmwareEnabled")));
                    AddDetail(details, "CPU hardware VM memory translation (SLAT/EPT/NPT)", FormatWindowsReportedCpuFeature(GetWmiPropertyValue(cpu, "SecondLevelAddressTranslationExtensions")));
                    AddDetail(details, "CPU data execution prevention", GetProcessorFeatureYesNo(12));
                    AddDetail(details, "CPU power management supported", FormatYesNo(GetWmiPropertyValue(cpu, "PowerManagementSupported")));
                    AddDetail(details, "CPU power management capabilities", FormatWmiDetailValue(GetWmiPropertyValue(cpu, "PowerManagementCapabilities")));
                    AddDetail(details, "CPU status", GetWmiPropertyText(cpu, "Status"));
                    AddDetail(details, "CPU status code", GetWmiPropertyText(cpu, "CpuStatus"));
                    AddDetail(details, "CPU availability", GetWmiPropertyText(cpu, "Availability"));
                    AddDetail(details, "CPU role", GetWmiPropertyText(cpu, "Role"));
                    AddDetail(details, "CPU system name", GetWmiPropertyText(cpu, "SystemName"));
                    AddRawWmiDetails(details, "CPU WMI", cpu);
                    break;
                }
            }
        }
        catch
        {
        }

        AddCpuCacheDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedCpuHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddCpuCacheDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_CacheMemory"))
            {
                foreach (ManagementObject cache in searcher.Get())
                {
                    var label = "CPU cache " + index;
                    var level = CpuCacheLevelName(GetWmiPropertyValue(cache, "Level"), GetWmiPropertyValue(cache, "Purpose"));
                    AddDetail(details, label + " level", level);
                    AddDetail(details, label + " purpose", GetWmiPropertyText(cache, "Purpose"));
                    AddDetail(details, label + " installed size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cache, "InstalledSize")));
                    AddDetail(details, label + " maximum size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cache, "MaxCacheSize")));
                    AddDetail(details, label + " associativity", GetWmiPropertyText(cache, "Associativity"));
                    AddDetail(details, label + " availability", GetWmiPropertyText(cache, "Availability"));
                    AddDetail(details, label + " block size", GetWmiPropertyText(cache, "BlockSize"));
                    AddDetail(details, label + " cache speed", FormatMegahertz(GetWmiPropertyValue(cache, "CacheSpeed")));
                    AddDetail(details, label + " cache type", GetWmiPropertyText(cache, "CacheType"));
                    AddDetail(details, label + " error method", GetWmiPropertyText(cache, "ErrorCorrectType"));
                    AddDetail(details, label + " SRAM type", GetWmiPropertyText(cache, "SRAMType"));
                    AddDetail(details, label + " write policy", GetWmiPropertyText(cache, "WritePolicy"));
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetMemoryHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedMemoryHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedMemoryHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMemoryArrayDetails(details);
        AddPhysicalMemoryDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedMemoryHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddMemoryArrayDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                foreach (ManagementObject array in searcher.Get())
                {
                    var label = "Memory array " + index;
                    AddDetail(details, label + " location", GetWmiPropertyText(array, "Location"));
                    AddDetail(details, label + " use", GetWmiPropertyText(array, "Use"));
                    AddDetail(details, label + " memory error correction", GetWmiPropertyText(array, "MemoryErrorCorrection"));
                    AddDetail(details, label + " maximum capacity", FormatMemoryArrayCapacity(array));
                    AddDetail(details, label + " memory devices", GetWmiPropertyText(array, "MemoryDevices"));
                    AddDetail(details, label + " status", GetWmiPropertyText(array, "Status"));
                    AddRawWmiDetails(details, label + " WMI", array);
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPhysicalMemoryDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject module in searcher.Get())
                {
                    var locator = FirstNonEmpty(GetWmiPropertyText(module, "DeviceLocator"), GetWmiPropertyText(module, "BankLabel"), "Module " + index);
                    var label = "Memory " + locator;
                    AddDetail(details, label + " capacity", FormatBytes(GetWmiPropertyValue(module, "Capacity")));
                    AddDetail(details, label + " manufacturer", GetWmiPropertyText(module, "Manufacturer"));
                    AddDetail(details, label + " part number", GetWmiPropertyText(module, "PartNumber"));
                    AddDetail(details, label + " serial number", GetWmiPropertyText(module, "SerialNumber"));
                    AddDetail(details, label + " bank", GetWmiPropertyText(module, "BankLabel"));
                    AddDetail(details, label + " device locator", GetWmiPropertyText(module, "DeviceLocator"));
                    AddDetail(details, label + " form factor", FormatMemoryFormFactor(GetWmiPropertyValue(module, "FormFactor")));
                    AddDetail(details, label + " memory type", FormatMemoryType(GetWmiPropertyValue(module, "MemoryType")));
                    AddDetail(details, label + " SMBIOS memory type", FormatSmbiosMemoryType(GetWmiPropertyValue(module, "SMBIOSMemoryType")));
                    AddDetail(details, label + " type detail", FormatMemoryTypeDetail(GetWmiPropertyValue(module, "TypeDetail")));
                    AddDetail(details, label + " speed", FormatMegahertz(GetWmiPropertyValue(module, "Speed")));
                    AddDetail(details, label + " configured speed", FormatMegahertz(GetWmiPropertyValue(module, "ConfiguredClockSpeed")));
                    AddDetail(details, label + " data width", FormatBits(GetWmiPropertyValue(module, "DataWidth")));
                    AddDetail(details, label + " total width", FormatBits(GetWmiPropertyValue(module, "TotalWidth")));
                    AddDetail(details, label + " configured voltage", FormatMillivolts(GetWmiPropertyValue(module, "ConfiguredVoltage")));
                    AddDetail(details, label + " minimum voltage", FormatMillivolts(GetWmiPropertyValue(module, "MinVoltage")));
                    AddDetail(details, label + " maximum voltage", FormatMillivolts(GetWmiPropertyValue(module, "MaxVoltage")));
                    AddDetail(details, label + " interleave data depth", GetWmiPropertyText(module, "InterleaveDataDepth"));
                    AddDetail(details, label + " interleave position", GetWmiPropertyText(module, "InterleavePosition"));
                    AddDetail(details, label + " position in row", GetWmiPropertyText(module, "PositionInRow"));
                    AddDetail(details, label + " tag", GetWmiPropertyText(module, "Tag"));
                    AddDetail(details, label + " status", GetWmiPropertyText(module, "Status"));
                    AddRawWmiDetails(details, label + " WMI", module);
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddRawWmiDetails(Dictionary<string, string> details, string prefix, ManagementBaseObject obj)
    {
        if (details == null || obj == null || string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        try
        {
            foreach (PropertyData property in obj.Properties)
            {
                if (property == null || string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                AddDetail(details, prefix + " " + SplitPascalCase(property.Name), FormatWmiDetailValue(property.Value));
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> CloneDetails(Dictionary<string, string> details)
    {
        return details == null || details.Count == 0
            ? null
            : new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatWmiDetailValue(object value)
    {
        if (value == null)
        {
            return "";
        }

        var array = value as Array;
        if (array != null && !(value is byte[]))
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                var text = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text.Trim());
                }
            }

            return string.Join(", ", parts.ToArray());
        }

        var bytes = value as byte[];
        if (bytes != null)
        {
            if (bytes.Length == 0)
            {
                return "";
            }

            return BitConverter.ToString(bytes);
        }

        if (value is DateTime)
        {
            return FormatDateTime((DateTime)value);
        }

        var textValue = Convert.ToString(value);
        var looksLikeDmtf = !string.IsNullOrWhiteSpace(textValue) && textValue.Trim().Length >= 14 && textValue.Trim().Take(14).All(char.IsDigit);
        DateTime date;
        if (looksLikeDmtf && TryParseWmiDate(textValue, out date))
        {
            return FormatDateTime(date);
        }

        return CleanWmiText(textValue);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return Regex.Replace(value.Trim(), "([a-z0-9])([A-Z])", "$1 $2");
    }

    private static string FormatBits(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text.Trim() + "-bit";
    }

    private static string FormatMillivolts(object value)
    {
        long millivolts;
        if (!TryConvertToInt64(value, out millivolts) || millivolts <= 0)
        {
            return "";
        }

        return FormatNumber(Math.Round(millivolts / 1000.0, 3), "0.###") + " V";
    }

    private static string FormatTenthsOfVolt(object value)
    {
        long raw;
        if (!TryConvertToInt64(value, out raw) || raw <= 0)
        {
            return "";
        }

        return FormatNumber(Math.Round(raw / 10.0, 1), "0.0") + " V";
    }

    private static string FormatCacheWmiKilobytes(object value)
    {
        long sizeKb;
        return TryConvertToInt64(value, out sizeKb) && sizeKb > 0 ? FormatCacheKilobytes(sizeKb) : "";
    }

    private static string FormatMemoryArrayCapacity(ManagementObject array)
    {
        if (array == null)
        {
            return "";
        }

        long kbEx;
        if (TryConvertToInt64(GetWmiPropertyValue(array, "MaxCapacityEx"), out kbEx) && kbEx > 0)
        {
            return FormatBytes(kbEx * 1024.0);
        }

        long kb;
        return TryConvertToInt64(GetWmiPropertyValue(array, "MaxCapacity"), out kb) && kb > 0 ? FormatBytes(kb * 1024.0) : "";
    }

    private static string FormatMemoryFormFactor(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "SIP";
            case 3: return "DIP";
            case 4: return "ZIP";
            case 5: return "SOJ";
            case 6: return "Proprietary";
            case 7: return "SIMM";
            case 8: return "DIMM";
            case 9: return "TSOP";
            case 10: return "PGA";
            case 11: return "RIMM";
            case 12: return "SODIMM";
            case 13: return "SRIMM";
            case 14: return "SMD";
            case 15: return "SSMP";
            case 16: return "QFP";
            case 17: return "TQFP";
            case 18: return "SOIC";
            case 19: return "LCC";
            case 20: return "PLCC";
            case 21: return "BGA";
            case 22: return "FPBGA";
            case 23: return "LGA";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "DRAM";
            case 3: return "Synchronous DRAM";
            case 4: return "Cache DRAM";
            case 5: return "EDO";
            case 6: return "EDRAM";
            case 7: return "VRAM";
            case 8: return "SRAM";
            case 9: return "RAM";
            case 10: return "ROM";
            case 11: return "Flash";
            case 12: return "EEPROM";
            case 13: return "FEPROM";
            case 14: return "EPROM";
            case 15: return "CDRAM";
            case 16: return "3DRAM";
            case 17: return "SDRAM";
            case 18: return "SGRAM";
            case 19: return "RDRAM";
            case 20: return "DDR";
            case 21: return "DDR2";
            case 22: return "DDR2 FB-DIMM";
            case 24: return "DDR3";
            case 25: return "FBD2";
            case 26: return "DDR4";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSmbiosMemoryType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "DRAM";
            case 3: return "Synchronous DRAM";
            case 4: return "Cache DRAM";
            case 5: return "EDO";
            case 6: return "EDRAM";
            case 7: return "VRAM";
            case 8: return "SRAM";
            case 9: return "RAM";
            case 10: return "ROM";
            case 11: return "Flash";
            case 12: return "EEPROM";
            case 13: return "FEPROM";
            case 14: return "EPROM";
            case 15: return "CDRAM";
            case 16: return "3DRAM";
            case 17: return "SDRAM";
            case 18: return "SGRAM";
            case 19: return "RDRAM";
            case 20: return "DDR";
            case 21: return "DDR2";
            case 22: return "DDR2 FB-DIMM";
            case 24: return "DDR3";
            case 25: return "FBD2";
            case 26: return "DDR4";
            case 27: return "LPDDR";
            case 28: return "LPDDR2";
            case 29: return "LPDDR3";
            case 30: return "LPDDR4";
            case 31: return "Logical non-volatile device";
            case 32: return "HBM";
            case 33: return "HBM2";
            case 34: return "DDR5";
            case 35: return "LPDDR5";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryTypeDetail(object value)
    {
        int flags;
        if (!TryConvertToInt32(value, out flags) || flags == 0)
        {
            return Convert.ToString(value);
        }

        var parts = new List<string>();
        AddMemoryTypeDetailFlag(parts, flags, 1, "Reserved");
        AddMemoryTypeDetailFlag(parts, flags, 2, "Other");
        AddMemoryTypeDetailFlag(parts, flags, 4, "Unknown");
        AddMemoryTypeDetailFlag(parts, flags, 8, "Fast-paged");
        AddMemoryTypeDetailFlag(parts, flags, 16, "Static column");
        AddMemoryTypeDetailFlag(parts, flags, 32, "Pseudo-static");
        AddMemoryTypeDetailFlag(parts, flags, 64, "RAMBUS");
        AddMemoryTypeDetailFlag(parts, flags, 128, "Synchronous");
        AddMemoryTypeDetailFlag(parts, flags, 256, "CMOS");
        AddMemoryTypeDetailFlag(parts, flags, 512, "EDO");
        AddMemoryTypeDetailFlag(parts, flags, 1024, "Window DRAM");
        AddMemoryTypeDetailFlag(parts, flags, 2048, "Cache DRAM");
        AddMemoryTypeDetailFlag(parts, flags, 4096, "Non-volatile");
        return parts.Count == 0 ? Convert.ToString(value) : string.Join(", ", parts.ToArray());
    }

    private static void AddMemoryTypeDetailFlag(List<string> parts, int flags, int flag, string text)
    {
        if ((flags & flag) != 0)
        {
            parts.Add(text);
        }
    }

    private static IEnumerable<SensorRow> GetOverviewRows()
    {
        var rows = new List<SensorRow>();

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    AddOverviewTextRow(rows, "Windows edition", CleanWmiText(Convert.ToString(os["Caption"])), "Windows WMI");
                    AddOverviewTextRow(rows, "Windows version", CleanWmiText(Convert.ToString(os["Version"])), "Windows WMI");
                    AddOverviewTextRow(rows, "Windows build", CleanWmiText(Convert.ToString(os["BuildNumber"])), "Windows WMI");
                    AddOverviewTextRow(rows, "Windows architecture", CleanWmiText(Convert.ToString(os["OSArchitecture"])), "Windows WMI");
                    AddOverviewTextRow(rows, "Windows install date", FormatWindowsInstallDate(os["InstallDate"]), "Windows");
                    var bootTimeText = Convert.ToString(os["LastBootUpTime"]);
                    var bootTime = string.IsNullOrWhiteSpace(bootTimeText) ? DateTime.MinValue : ManagementDateTimeConverter.ToDateTime(bootTimeText);
                    if (bootTime > DateTime.MinValue)
                    {
                        AddOverviewTextRow(rows, "Windows boot time", FormatDateTime(bootTime), "Windows WMI");
                        rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "System uptime", DisplayValue = FormatUptime(DateTime.Now - bootTime), Source = "Windows WMI" });
                    }
                    break;
                }
            }
        }
        catch
        {
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "System uptime", DisplayValue = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount)), Source = "Windows" });
        }

        string baseboardManufacturer = "";
        string baseboardProduct = "";
        string baseboardVersion = "";

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject board in searcher.Get())
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
                foreach (ManagementObject system in searcher.Get())
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

        AddOverviewTextRow(rows, "Baseboard manufacturer", baseboardManufacturer, "Windows WMI");
        AddOverviewTextRow(rows, "Baseboard product", baseboardProduct, "Windows WMI");
        AddOverviewTextRow(rows, "Baseboard version", baseboardVersion, "Windows WMI");
        AddOverviewTextRow(rows, "BIOS mode", GetFirmwareMode(), "Windows registry");
        AddOverviewTextRow(rows, "Secure Boot", GetSecureBootState(), "Windows registry");

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS"))
            {
                foreach (ManagementObject bios in searcher.Get())
                {
                    AddOverviewTextRow(rows, "BIOS vendor", GetWmiPropertyText(bios, "Manufacturer"), "Windows WMI");
                    var version = GetWmiPropertyText(bios, "SMBIOSBIOSVersion");
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = GetWmiPropertyText(bios, "Version");
                    }
                    AddOverviewTextRow(rows, "BIOS version", version, "Windows WMI");
                    AddOverviewTextRow(rows, "BIOS date", FormatWmiDate(GetWmiPropertyValue(bios, "ReleaseDate")), "Windows WMI");
                    AddOverviewTextRow(rows, "SMBIOS version", FormatMajorMinor(GetWmiPropertyValue(bios, "SMBIOSMajorVersion"), GetWmiPropertyValue(bios, "SMBIOSMinorVersion"), true), "Windows WMI");
                    AddOverviewTextRow(rows, "Embedded controller version", FormatMajorMinor(GetWmiPropertyValue(bios, "EmbeddedControllerMajorVersion"), GetWmiPropertyValue(bios, "EmbeddedControllerMinorVersion"), false), "Windows WMI");
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            {
                foreach (ManagementObject gpu in searcher.Get())
                {
                    var name = Convert.ToString(gpu["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Display adapter";
                    }

                    AddOverviewTextRow(rows, name + " adapter RAM", FormatBytes(GetGpuAdapterMemoryBytes(name, gpu["AdapterRAM"])), "Windows");
                    AddOverviewTextRow(rows, name + " driver version", Convert.ToString(gpu["DriverVersion"]), "Windows WMI");
                    AddOverviewTextRow(rows, name + " driver date", FormatWmiDate(gpu["DriverDate"]), "Windows WMI");

                    string gpuBios;
                    string gpuBiosDate;
                    if (TryGetGpuBiosInfo(name, Convert.ToString(gpu["PNPDeviceID"]), out gpuBios, out gpuBiosDate))
                    {
                        AddOverviewTextRow(rows, name + " BIOS", gpuBios, "Windows registry");
                        AddOverviewTextRow(rows, name + " BIOS date", gpuBiosDate, "Windows registry");
                    }
                }
            }
        }
        catch
        {
        }

        return rows;
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

    private static void AddOverviewTextRow(List<SensorRow> rows, string name, string value, string source)
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
            Source = source
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

        parts.Add(uptime.Minutes + " minute" + (uptime.Minutes == 1 ? "" : "s"));
        return string.Join(", ", parts.ToArray());
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

    private static bool TryGetGpuBiosInfo(string gpuName, string pnpDeviceId, out string bios, out string biosDate)
    {
        bios = "";
        biosDate = "";
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return false;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                bios = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosString"));
                                biosDate = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosDate"));
                                return !string.IsNullOrWhiteSpace(bios) || !string.IsNullOrWhiteSpace(biosDate);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static object GetGpuAdapterMemoryBytes(string gpuName, object fallback)
    {
        var registryBytes = GetGpuAdapterMemoryBytesFromRegistry(gpuName);
        return registryBytes > 0 ? (object)registryBytes : fallback;
    }

    private static ulong GetGpuAdapterMemoryBytesFromRegistry(string gpuName)
    {
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return 0;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                var qwMemory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.qwMemorySize"));
                                if (qwMemory > 0)
                                {
                                    return qwMemory;
                                }

                                var memory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.MemorySize"));
                                if (memory > 0)
                                {
                                    return memory;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static bool GpuRegistryEntryMatches(string gpuName, string adapterString, string driverDesc)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(adapterString) && gpuName.IndexOf(adapterString, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && gpuName.IndexOf(driverDesc, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(adapterString) && adapterString.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && driverDesc.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

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

    private IEnumerable<SensorRow> GetNetworkRows()
    {
        var rows = new List<SensorRow>();
        var wifiInterfaces = GetWifiInterfaceInfos();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "Network adapter";
                }

                var macAddress = FormatMacAddress(adapter.GetPhysicalAddress());
                var macVendor = string.IsNullOrWhiteSpace(macAddress)
                    ? ""
                    : MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")).Lookup(macAddress);
                var stats = adapter.GetIPv4Statistics();
                var now = DateTime.UtcNow;
                var id = string.IsNullOrWhiteSpace(adapter.Id) ? name : adapter.Id;
                NetworkSnapshot previous;
                var receiveRate = 0.0;
                var sendRate = 0.0;
                if (networkSnapshots.TryGetValue(id, out previous))
                {
                    var seconds = Math.Max(0.001, (now - previous.TimestampUtc).TotalSeconds);
                    receiveRate = Math.Max(0, stats.BytesReceived - previous.BytesReceived) / seconds;
                    sendRate = Math.Max(0, stats.BytesSent - previous.BytesSent) / seconds;
                }

                networkSnapshots[id] = new NetworkSnapshot
                {
                    BytesReceived = stats.BytesReceived,
                    BytesSent = stats.BytesSent,
                    TimestampUtc = now
                };

                if (!string.IsNullOrWhiteSpace(adapter.Description) && !string.Equals(adapter.Description, name, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Adapter description", DisplayValue = adapter.Description, Source = "Windows Network" });
                }
                if (!string.IsNullOrWhiteSpace(macAddress))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "MAC address", DisplayValue = macAddress, Source = "Windows Network" });
                }
                if (!string.IsNullOrWhiteSpace(macVendor))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "MAC vendor", DisplayValue = macVendor, Source = "OUI database" });
                }
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Status", DisplayValue = adapter.OperationalStatus.ToString(), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Link speed", DisplayValue = FormatBitsPerSecond(adapter.Speed), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Receive rate", Value = (float)receiveRate, DisplayValue = FormatBytesPerSecond(receiveRate), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Send rate", Value = (float)sendRate, DisplayValue = FormatBytesPerSecond(sendRate), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data received", DisplayValue = FormatBytes(stats.BytesReceived), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data sent", DisplayValue = FormatBytes(stats.BytesSent), Source = "Windows Network" });

                var addresses = adapter.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address != null && a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct()
                    .ToList();
                if (addresses.Count > 0)
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "IP address", DisplayValue = string.Join(", ", addresses.ToArray()), Source = "Windows Network" });
                }

                Guid adapterGuid;
                WifiInterfaceInfo wifi;
                if (Guid.TryParse(id, out adapterGuid) && wifiInterfaces.TryGetValue(adapterGuid, out wifi))
                {
                    rows.AddRange(BuildWifiRows(name, wifi));
                }
            }
            catch
            {
            }
        }

        return rows;
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        if (address == null)
        {
            return "";
        }

        var bytes = address.GetAddressBytes();
        return bytes == null || bytes.Length == 0
            ? ""
            : string.Join(":", bytes.Select(b => b.ToString("X2")).ToArray());
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
            (sensorName.Equals("Data Read", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Data Written", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Read Activity", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Write Activity", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Equals("Total Activity", StringComparison.OrdinalIgnoreCase));
    }
}
