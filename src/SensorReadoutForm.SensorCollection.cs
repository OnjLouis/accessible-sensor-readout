using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory total", DisplayValue = FormatBytes(totalKb * 1024.0), Source = "Windows WMI" });
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
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, Architecture, SocketDesignation, ProcessorId, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions, VMMonitorModeExtensions FROM Win32_Processor"))
            {
                foreach (ManagementObject cpu in searcher.Get())
                {
                    AddCpuDetailRow(rows, "CPU name", Convert.ToString(cpu["Name"]));
                    AddCpuDetailRow(rows, "CPU vendor", Convert.ToString(cpu["Manufacturer"]));
                    AddCpuDetailRow(rows, "CPU cores", Convert.ToString(cpu["NumberOfCores"]));
                    AddCpuDetailRow(rows, "CPU threads", Convert.ToString(cpu["NumberOfLogicalProcessors"]));
                    AddCpuDetailRow(rows, "CPU max clock", FormatMegahertz(cpu["MaxClockSpeed"]));
                    AddCpuDetailRow(rows, "CPU current clock", FormatMegahertz(cpu["CurrentClockSpeed"]));
                    AddCpuDetailRow(rows, "CPU socket", Convert.ToString(cpu["SocketDesignation"]));
                    AddCpuDetailRow(rows, "CPU architecture", FormatCpuArchitecture(cpu["Architecture"]));
                    AddCpuDetailRow(rows, "CPU instruction sets", GetProcessorInstructionSetSummary());
                    AddCpuDetailRow(rows, "CPU virtualization extensions", FormatWindowsReportedCpuFeature(cpu["VMMonitorModeExtensions"]));
                    AddCpuDetailRow(rows, "CPU virtualization enabled in firmware", FormatYesNo(cpu["VirtualizationFirmwareEnabled"]));
                    AddCpuDetailRow(rows, "CPU hardware VM memory translation (SLAT/EPT/NPT)", FormatWindowsReportedCpuFeature(cpu["SecondLevelAddressTranslationExtensions"]));
                    AddCpuDetailRow(rows, "CPU data execution prevention", GetProcessorFeatureYesNo(12));
                    AddCpuDetailRow(rows, "CPU processor ID", Convert.ToString(cpu["ProcessorId"]));
                    break;
                }
            }
        }
        catch
        {
        }

        return rows;
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
            Source = "Windows WMI"
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
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory total", DisplayValue = FormatBytes(totalBytes), Source = "Windows" });
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
                        rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "Uptime", DisplayValue = FormatUptime(DateTime.Now - bootTime), Source = "Windows WMI" });
                    }
                    break;
                }
            }
        }
        catch
        {
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "Uptime", DisplayValue = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount)), Source = "Windows" });
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
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }

        var strings = value as string[];
        if (strings != null)
        {
            return string.Join(", ", strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
        }

        return Convert.ToString(value);
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
