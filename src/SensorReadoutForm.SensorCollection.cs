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

    private sealed class PhysicalStorageDevice
    {
        public int Index;
        public string Model;
        public string PnpDeviceId;
        public string InterfaceType;
    }

    private sealed class CachedDetailSnapshot
    {
        public DateTime TimestampUtc;
        public Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQueryWithProtocol
    {
        public uint PropertyId;
        public uint QueryType;
        public StorageProtocolSpecificData ProtocolSpecific;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageProtocolSpecificData
    {
        public uint ProtocolType;
        public uint DataType;
        public uint ProtocolDataRequestValue;
        public uint ProtocolDataRequestSubValue;
        public uint ProtocolDataOffset;
        public uint ProtocolDataLength;
        public uint FixedProtocolReturnData;
        public uint ProtocolDataRequestSubValue2;
        public uint ProtocolDataRequestSubValue3;
        public uint ProtocolDataRequestSubValue4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThrough
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBufferOffset;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughWithBuffers
    {
        public ScsiPassThrough Spt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseBuf;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
        public byte[] DataBuf;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle StorageCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool StorageDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool StorageDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

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

        foreach (var row in GetDirectNvmeSmartRows())
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
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
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
                    AddPhysicalDiskTextRow(rows, name, "Bus type", FormatStorageBusType(GetWmiPropertyValue(disk, "BusType")), "Windows Storage");
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Operational status", DisplayValue = DecodeOperationalStatus(disk["OperationalStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Size", DisplayValue = FormatBytes(disk["Size"]), Source = "Windows Storage" });
                    AddPhysicalDiskTextRow(rows, name, "Spindle speed", FormatStorageSpindleSpeed(GetWmiPropertyValue(disk, "SpindleSpeed")), "Windows Storage");
                    AddPhysicalDiskTextRow(rows, name, "Physical sector size", FormatBytes(GetWmiPropertyValue(disk, "PhysicalSectorSize")), "Windows Storage");
                    AddPhysicalDiskTextRow(rows, name, "Logical sector size", FormatBytes(GetWmiPropertyValue(disk, "LogicalSectorSize")), "Windows Storage");
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddPhysicalDiskTextRow(List<SensorRow> rows, string hardware, string name, string value, string source)
    {
        if (rows == null || string.IsNullOrWhiteSpace(hardware) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow { Type = "SMART", Hardware = hardware, Name = name, DisplayValue = value, Source = source });
    }

    private static IEnumerable<SensorRow> GetDirectNvmeSmartRows()
    {
        var rows = new List<SensorRow>();
        foreach (var disk in GetPhysicalStorageDevices())
        {
            if (disk.Index < 0)
            {
                continue;
            }

            byte[] smartLog;
            var source = "";
            if (LooksLikeNvmeDisk(disk) && TryReadNvmeSmartLogWithStorageQuery(disk.Index, out smartLog))
            {
                source = "Windows NVMe";
            }
            else if (LooksLikeUsbStorageDisk(disk) && TryReadNvmeSmartLogWithAsmediaPassThrough(disk.Index, out smartLog))
            {
                source = "USB NVMe bridge";
            }
            else
            {
                continue;
            }

            AddNvmeSmartRows(rows, NormalizeStorageHardwareName(disk.Model), smartLog, source);
        }

        return rows;
    }

    private static IEnumerable<PhysicalStorageDevice> GetPhysicalStorageDevices()
    {
        var devices = new List<PhysicalStorageDevice>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Index, Model, PNPDeviceID, InterfaceType FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject drive in searcher.Get())
                {
                    int index;
                    if (!int.TryParse(Convert.ToString(drive["Index"]), out index))
                    {
                        continue;
                    }

                    var model = Convert.ToString(drive["Model"]);
                    if (string.IsNullOrWhiteSpace(model))
                    {
                        model = "Physical drive " + index;
                    }

                    devices.Add(new PhysicalStorageDevice
                    {
                        Index = index,
                        Model = model,
                        PnpDeviceId = Convert.ToString(drive["PNPDeviceID"]) ?? "",
                        InterfaceType = Convert.ToString(drive["InterfaceType"]) ?? ""
                    });
                }
            }
        }
        catch
        {
        }

        return devices;
    }

    private static bool LooksLikeNvmeDisk(PhysicalStorageDevice disk)
    {
        var text = ((disk == null ? "" : disk.Model) + " " + (disk == null ? "" : disk.PnpDeviceId) + " " + (disk == null ? "" : disk.InterfaceType));
        return text.IndexOf("NVME", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeUsbStorageDisk(PhysicalStorageDevice disk)
    {
        var text = ((disk == null ? "" : disk.PnpDeviceId) + " " + (disk == null ? "" : disk.InterfaceType));
        return text.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("USBSTOR", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AddNvmeSmartRows(List<SensorRow> rows, string hardware, byte[] smartLog, string source)
    {
        if (smartLog == null || smartLog.Length < 80)
        {
            return;
        }

        var dataUnitsRead = ReadUInt64LittleEndian(smartLog, 0x20);
        var dataUnitsWritten = ReadUInt64LittleEndian(smartLog, 0x30);
        AddNvmeDataUnitCounter(rows, hardware, "Data read", dataUnitsRead, source);
        AddNvmeDataUnitCounter(rows, hardware, "Data written", dataUnitsWritten, source);
    }

    private static void AddNvmeDataUnitCounter(List<SensorRow> rows, string hardware, string name, ulong dataUnits, string source)
    {
        if (dataUnits == 0)
        {
            return;
        }

        var bytes = (double)dataUnits * 512000.0;
        rows.Add(new SensorRow
        {
            Type = "SMART",
            Hardware = hardware,
            Name = name,
            Value = (float)(bytes / 1024.0 / 1024.0 / 1024.0),
            DisplayValue = FormatBytes(bytes),
            Source = source
        });
    }

    private static ulong ReadUInt64LittleEndian(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + 8 > data.Length)
        {
            return 0;
        }

        return BitConverter.ToUInt64(data, offset);
    }

    private static bool TryReadNvmeSmartLogWithStorageQuery(int physicalDriveIndex, out byte[] smartLog)
    {
        smartLog = null;
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint openExisting = 3;
        const uint ioctlStorageQueryProperty = 0x002D1400;
        const uint storageDeviceProtocolSpecificProperty = 49;
        const uint propertyStandardQuery = 0;
        const uint protocolTypeNvme = 3;
        const uint nvmeDataTypeLogPage = 2;
        const uint nvmeSmartHealthLog = 2;
        const int nvmeSmartLogLength = 512;

        try
        {
            using (var handle = StorageCreateFile(@"\\.\PhysicalDrive" + physicalDriveIndex, 0, fileShareRead | fileShareWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    return false;
                }

                var protocolDataSize = Marshal.SizeOf(typeof(StorageProtocolSpecificData));
                var querySize = Marshal.SizeOf(typeof(StoragePropertyQueryWithProtocol));
                var descriptorHeaderSize = 8;
                var outputDataOffset = descriptorHeaderSize + protocolDataSize;
                var query = new StoragePropertyQueryWithProtocol
                {
                    PropertyId = storageDeviceProtocolSpecificProperty,
                    QueryType = propertyStandardQuery,
                    ProtocolSpecific = new StorageProtocolSpecificData
                    {
                        ProtocolType = protocolTypeNvme,
                        DataType = nvmeDataTypeLogPage,
                        ProtocolDataRequestValue = nvmeSmartHealthLog,
                        ProtocolDataOffset = (uint)protocolDataSize,
                        ProtocolDataLength = nvmeSmartLogLength
                    }
                };

                var buffer = new byte[querySize + nvmeSmartLogLength];
                var pointer = Marshal.AllocHGlobal(querySize);
                try
                {
                    Marshal.StructureToPtr(query, pointer, false);
                    Marshal.Copy(pointer, buffer, 0, querySize);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }

                int returned;
                if (!StorageDeviceIoControl(handle, ioctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero))
                {
                    return false;
                }

                if (outputDataOffset + nvmeSmartLogLength > buffer.Length)
                {
                    return false;
                }

                smartLog = new byte[nvmeSmartLogLength];
                Buffer.BlockCopy(buffer, outputDataOffset, smartLog, 0, nvmeSmartLogLength);
                return SmartLogHasContent(smartLog);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadNvmeSmartLogWithAsmediaPassThrough(int physicalDriveIndex, out byte[] smartLog)
    {
        smartLog = null;
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint openExisting = 3;
        const uint ioctlScsiPassThrough = 0x0004D004;
        const byte scsiIoctlDataIn = 1;
        const int nvmeSmartLogLength = 512;

        try
        {
            using (var handle = StorageCreateFile(@"\\.\PhysicalDrive" + physicalDriveIndex, 0, fileShareRead | fileShareWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    return false;
                }

                var request = new ScsiPassThroughWithBuffers
                {
                    Spt = new ScsiPassThrough { Cdb = new byte[16] },
                    SenseBuf = new byte[32],
                    DataBuf = new byte[4096]
                };
                request.Spt.Length = (ushort)Marshal.SizeOf(typeof(ScsiPassThrough));
                request.Spt.SenseInfoLength = 24;
                request.Spt.DataTransferLength = nvmeSmartLogLength;
                request.Spt.TimeOutValue = 2;
                request.Spt.DataBufferOffset = Marshal.OffsetOf(typeof(ScsiPassThroughWithBuffers), "DataBuf");
                request.Spt.SenseInfoOffset = (uint)Marshal.OffsetOf(typeof(ScsiPassThroughWithBuffers), "SenseBuf").ToInt32();
                request.Spt.CdbLength = 16;
                request.Spt.DataIn = scsiIoctlDataIn;
                request.Spt.Cdb[0] = 0xE6;
                request.Spt.Cdb[1] = 0x02;
                request.Spt.Cdb[3] = 0x02;
                request.Spt.Cdb[7] = 0x7F;

                var dataOffset = Marshal.OffsetOf(typeof(ScsiPassThroughWithBuffers), "DataBuf").ToInt32();
                var length = dataOffset + nvmeSmartLogLength;
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ScsiPassThroughWithBuffers)));
                try
                {
                    Marshal.StructureToPtr(request, pointer, false);
                    int returned;
                    if (!StorageDeviceIoControl(handle, ioctlScsiPassThrough, pointer, length, pointer, length, out returned, IntPtr.Zero))
                    {
                        return false;
                    }

                    var response = (ScsiPassThroughWithBuffers)Marshal.PtrToStructure(pointer, typeof(ScsiPassThroughWithBuffers));
                    if (response.DataBuf == null || response.DataBuf.Length < nvmeSmartLogLength)
                    {
                        return false;
                    }

                    smartLog = new byte[nvmeSmartLogLength];
                    Buffer.BlockCopy(response.DataBuf, 0, smartLog, 0, nvmeSmartLogLength);
                    return SmartLogHasContent(smartLog);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool SmartLogHasContent(byte[] smartLog)
    {
        if (smartLog == null)
        {
            return false;
        }

        for (var i = 0; i < smartLog.Length; i++)
        {
            if (smartLog[i] != 0)
            {
                return true;
            }
        }

        return false;
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
                    AddDetail(details, label + " associativity", FormatCacheAssociativity(GetWmiPropertyValue(cache, "Associativity")));
                    AddDetail(details, label + " availability", FormatAvailability(GetWmiPropertyValue(cache, "Availability")));
                    AddDetail(details, label + " block size", GetWmiPropertyText(cache, "BlockSize"));
                    AddDetail(details, label + " cache speed", FormatMegahertz(GetWmiPropertyValue(cache, "CacheSpeed")));
                    AddDetail(details, label + " cache type", FormatCacheType(GetWmiPropertyValue(cache, "CacheType")));
                    AddDetail(details, label + " error method", FormatCacheErrorCorrectType(GetWmiPropertyValue(cache, "ErrorCorrectType")));
                    AddDetail(details, label + " SRAM type", FormatWmiDetailValue(GetWmiPropertyValue(cache, "SRAMType")));
                    AddDetail(details, label + " write policy", FormatCacheWritePolicy(GetWmiPropertyValue(cache, "WritePolicy")));
                    AddRawWmiDetails(details, label + " WMI", cache);
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

    private static Dictionary<string, string> GetWindowsHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedWindowsHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedWindowsHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddWindowsOperatingSystemDetails(details);
        AddWindowsRegistryDetails(details);
        AddWindowsLicensingDetails(details);
        AddComputerSystemProductDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedWindowsHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static List<SensorRow> AttachStorageDetailsToRows(List<SensorRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return rows ?? new List<SensorRow>();
        }

        var physicalDetails = GetPhysicalDiskDetailsByHardware();
        var logicalDetails = GetLogicalDiskDetailsByRoot();
        foreach (var row in rows)
        {
            if (row == null)
            {
                continue;
            }

            Dictionary<string, string> details;
            if (string.Equals(row.Type, "SMART", StringComparison.OrdinalIgnoreCase) &&
                physicalDetails.TryGetValue(NormalizeStorageHardwareName(row.Hardware ?? ""), out details))
            {
                row.Details = MergeDetails(row.Details, details);
            }
            else if (string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase) &&
                IsStoragePerformanceName(row.Name ?? "") &&
                logicalDetails.TryGetValue(GetLogicalDiskRootFromHardware(row.Hardware), out details))
            {
                row.Details = MergeDetails(row.Details, details);
            }
        }

        return rows;
    }

    private static Dictionary<string, Dictionary<string, string>> GetPhysicalDiskDetailsByHardware()
    {
        EnsureStorageTopologyDetails();
        return CloneDetailsMap(cachedPhysicalDiskDetailsByHardware);
    }

    private static Dictionary<string, Dictionary<string, string>> GetLogicalDiskDetailsByRoot()
    {
        EnsureStorageTopologyDetails();
        return CloneDetailsMap(cachedLogicalDiskDetailsByRoot);
    }

    private static void EnsureStorageTopologyDetails()
    {
        if (cachedPhysicalDiskDetailsByHardware != null &&
            cachedLogicalDiskDetailsByRoot != null &&
            DateTime.UtcNow - cachedStorageTopologyDetailsUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        var physical = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var logical = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    AddDiskTopologyDetails(disk, physical, logical);
                }
            }
        }
        catch
        {
        }

        AddWindowsStoragePhysicalDiskDetails(physical);

        cachedPhysicalDiskDetailsByHardware = physical;
        cachedLogicalDiskDetailsByRoot = logical;
        cachedStorageTopologyDetailsUtc = DateTime.UtcNow;
    }

    private static void AddDiskTopologyDetails(ManagementObject disk, Dictionary<string, Dictionary<string, string>> physical, Dictionary<string, Dictionary<string, string>> logical)
    {
        if (disk == null || physical == null || logical == null)
        {
            return;
        }

        var diskIndex = GetWmiPropertyText(disk, "Index");
        var model = GetWmiPropertyText(disk, "Model");
        var caption = GetWmiPropertyText(disk, "Caption");
        var hardware = NormalizeStorageHardwareName(string.IsNullOrWhiteSpace(model) ? caption : model);
        if (string.IsNullOrWhiteSpace(hardware))
        {
            hardware = "Physical disk " + diskIndex;
        }

        var diskDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(diskDetails, "Physical disk number", diskIndex);
        AddDetail(diskDetails, "Physical disk model", model);
        AddDetail(diskDetails, "Physical disk caption", caption);
        AddDetail(diskDetails, "Physical disk interface", GetWmiPropertyText(disk, "InterfaceType"));
        AddDetail(diskDetails, "Physical disk media type", GetWmiPropertyText(disk, "MediaType"));
        AddDetail(diskDetails, "Physical disk firmware", GetWmiPropertyText(disk, "FirmwareRevision"));
        AddDetail(diskDetails, "Physical disk serial number", GetWmiPropertyText(disk, "SerialNumber"));
        AddDetail(diskDetails, "Physical disk size", FormatBytes(GetWmiPropertyValue(disk, "Size")));
        AddDetail(diskDetails, "Physical disk partitions", GetWmiPropertyText(disk, "Partitions"));
        AddDetail(diskDetails, "Physical disk PNP device ID", GetWmiPropertyText(disk, "PNPDeviceID"));
        AddRawWmiDetails(diskDetails, "Physical disk WMI", disk);

        var partitionNumber = 0;
        foreach (ManagementObject partition in GetAssociatedWmiObjects("Win32_DiskDrive", "DeviceID", GetWmiPropertyText(disk, "DeviceID"), "Win32_DiskDriveToDiskPartition"))
        {
            partitionNumber++;
            AddPartitionDetails(diskDetails, logical, partitionNumber, diskDetails, partition);
        }

        AddDetail(diskDetails, "Detected partition count", partitionNumber.ToString(CultureInfo.InvariantCulture));
        physical[hardware] = diskDetails;
    }

    private static void AddWindowsStoragePhysicalDiskDetails(Dictionary<string, Dictionary<string, string>> physical)
    {
        if (physical == null)
        {
            return;
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    var friendlyName = GetWmiPropertyText(disk, "FriendlyName");
                    var model = GetWmiPropertyText(disk, "Model");
                    var hardware = NormalizeStorageHardwareName(string.IsNullOrWhiteSpace(friendlyName) ? model : friendlyName);
                    if (string.IsNullOrWhiteSpace(hardware))
                    {
                        hardware = "Physical disk " + GetWmiPropertyText(disk, "DeviceId");
                    }

                    Dictionary<string, string> details;
                    if (!physical.TryGetValue(hardware, out details) || details == null)
                    {
                        details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        physical[hardware] = details;
                    }

                    AddDetail(details, "Windows Storage device ID", GetWmiPropertyText(disk, "DeviceId"));
                    AddDetail(details, "Windows Storage friendly name", friendlyName);
                    AddDetail(details, "Windows Storage model", model);
                    AddDetail(details, "Windows Storage manufacturer", GetWmiPropertyText(disk, "Manufacturer"));
                    AddDetail(details, "Windows Storage serial number", GetWmiPropertyText(disk, "SerialNumber"));
                    AddDetail(details, "Windows Storage adapter serial number", GetWmiPropertyText(disk, "AdapterSerialNumber"));
                    AddDetail(details, "Windows Storage part number", GetWmiPropertyText(disk, "PartNumber"));
                    AddDetail(details, "Windows Storage firmware version", GetWmiPropertyText(disk, "FirmwareVersion"));
                    AddDetail(details, "Windows Storage software version", GetWmiPropertyText(disk, "SoftwareVersion"));
                    AddDetail(details, "Windows Storage bus type", FormatStorageBusType(GetWmiPropertyValue(disk, "BusType")));
                    AddDetail(details, "Windows Storage media type", DecodeMediaType(GetWmiPropertyValue(disk, "MediaType")));
                    AddDetail(details, "Windows Storage health status", DecodeHealthStatus(GetWmiPropertyValue(disk, "HealthStatus")));
                    AddDetail(details, "Windows Storage operational status", DecodeOperationalStatus(GetWmiPropertyValue(disk, "OperationalStatus")));
                    AddDetail(details, "Windows Storage operational details", FormatWmiDetailValue(GetWmiPropertyValue(disk, "OperationalDetails")));
                    AddDetail(details, "Windows Storage usage", FormatStorageUsage(GetWmiPropertyValue(disk, "Usage")));
                    AddDetail(details, "Windows Storage supported usages", FormatWmiDetailValue(GetWmiPropertyValue(disk, "SupportedUsages")));
                    AddDetail(details, "Windows Storage size", FormatBytes(GetWmiPropertyValue(disk, "Size")));
                    AddDetail(details, "Windows Storage allocated size", FormatBytes(GetWmiPropertyValue(disk, "AllocatedSize")));
                    AddDetail(details, "Windows Storage virtual disk footprint", FormatBytes(GetWmiPropertyValue(disk, "VirtualDiskFootprint")));
                    AddDetail(details, "Windows Storage logical sector size", FormatBytes(GetWmiPropertyValue(disk, "LogicalSectorSize")));
                    AddDetail(details, "Windows Storage physical sector size", FormatBytes(GetWmiPropertyValue(disk, "PhysicalSectorSize")));
                    AddDetail(details, "Windows Storage spindle speed", FormatStorageSpindleSpeed(GetWmiPropertyValue(disk, "SpindleSpeed")));
                    AddDetail(details, "Windows Storage physical location", GetWmiPropertyText(disk, "PhysicalLocation"));
                    AddDetail(details, "Windows Storage slot number", GetWmiPropertyText(disk, "SlotNumber"));
                    AddDetail(details, "Windows Storage enclosure number", GetWmiPropertyText(disk, "EnclosureNumber"));
                    AddDetail(details, "Windows Storage FRU ID", GetWmiPropertyText(disk, "FruId"));
                    AddDetail(details, "Windows Storage unique ID", GetWmiPropertyText(disk, "UniqueId"));
                    AddDetail(details, "Windows Storage unique ID format", FormatStorageUniqueIdFormat(GetWmiPropertyValue(disk, "UniqueIdFormat")));
                    AddDetail(details, "Windows Storage can pool", FormatYesNo(GetWmiPropertyValue(disk, "CanPool")));
                    AddDetail(details, "Windows Storage cannot pool reason", FormatStorageCannotPoolReason(GetWmiPropertyValue(disk, "CannotPoolReason")));
                    AddDetail(details, "Windows Storage other cannot pool reason", GetWmiPropertyText(disk, "OtherCannotPoolReasonDescription"));
                    AddRawWmiDetails(details, "Windows Storage WMI", disk);
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPartitionDetails(Dictionary<string, string> diskDetails, Dictionary<string, Dictionary<string, string>> logical, int partitionNumber, Dictionary<string, string> inheritedDiskDetails, ManagementObject partition)
    {
        if (partition == null)
        {
            return;
        }

        var label = "Partition " + partitionNumber.ToString(CultureInfo.InvariantCulture);
        AddDetail(diskDetails, label + " name", GetWmiPropertyText(partition, "Name"));
        AddDetail(diskDetails, label + " device ID", GetWmiPropertyText(partition, "DeviceID"));
        AddDetail(diskDetails, label + " type", GetWmiPropertyText(partition, "Type"));
        AddDetail(diskDetails, label + " size", FormatBytes(GetWmiPropertyValue(partition, "Size")));
        AddDetail(diskDetails, label + " starting offset", FormatBytes(GetWmiPropertyValue(partition, "StartingOffset")));
        AddDetail(diskDetails, label + " boot partition", FormatYesNo(GetWmiPropertyValue(partition, "BootPartition")));
        AddDetail(diskDetails, label + " primary partition", FormatYesNo(GetWmiPropertyValue(partition, "PrimaryPartition")));
        AddDetail(diskDetails, label + " index", GetWmiPropertyText(partition, "Index"));
        AddRawWmiDetails(diskDetails, label + " WMI", partition);

        var logicalNumber = 0;
        foreach (ManagementObject volume in GetAssociatedWmiObjects("Win32_DiskPartition", "DeviceID", GetWmiPropertyText(partition, "DeviceID"), "Win32_LogicalDiskToPartition"))
        {
            logicalNumber++;
            var root = GetWmiPropertyText(volume, "DeviceID");
            var volumeLabel = label + " volume " + logicalNumber.ToString(CultureInfo.InvariantCulture);
            AddDetail(diskDetails, volumeLabel + " drive letter", root);
            AddDetail(diskDetails, volumeLabel + " label", GetWmiPropertyText(volume, "VolumeName"));
            AddDetail(diskDetails, volumeLabel + " file system", GetWmiPropertyText(volume, "FileSystem"));
            AddDetail(diskDetails, volumeLabel + " size", FormatBytes(GetWmiPropertyValue(volume, "Size")));
            AddDetail(diskDetails, volumeLabel + " free space", FormatBytes(GetWmiPropertyValue(volume, "FreeSpace")));

            if (!string.IsNullOrWhiteSpace(root))
            {
                var volumeDetails = MergeDetails(inheritedDiskDetails, null);
                AddDetail(volumeDetails, "Containing partition", GetWmiPropertyText(partition, "DeviceID"));
                AddDetail(volumeDetails, "Partition type", GetWmiPropertyText(partition, "Type"));
                AddDetail(volumeDetails, "Partition size", FormatBytes(GetWmiPropertyValue(partition, "Size")));
                AddDetail(volumeDetails, "Partition starting offset", FormatBytes(GetWmiPropertyValue(partition, "StartingOffset")));
                AddDetail(volumeDetails, "Volume drive letter", root);
                AddDetail(volumeDetails, "Volume label", GetWmiPropertyText(volume, "VolumeName"));
                AddDetail(volumeDetails, "Volume file system", GetWmiPropertyText(volume, "FileSystem"));
                AddDetail(volumeDetails, "Volume size", FormatBytes(GetWmiPropertyValue(volume, "Size")));
                AddDetail(volumeDetails, "Volume free space", FormatBytes(GetWmiPropertyValue(volume, "FreeSpace")));
                AddRawWmiDetails(volumeDetails, "Volume WMI", volume);
                logical[root.TrimEnd('\\')] = volumeDetails;
            }
        }

        if (logicalNumber == 0)
        {
            AddDetail(diskDetails, label + " volume", "No drive letter exposed by Windows");
        }
    }

    private static IEnumerable<ManagementObject> GetAssociatedWmiObjects(string className, string keyName, string keyValue, string assocClass)
    {
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(keyValue) || string.IsNullOrWhiteSpace(assocClass))
        {
            yield break;
        }

        ManagementObjectCollection results = null;
        try
        {
            var query = "ASSOCIATORS OF {" + className + "." + keyName + "=\"" + EscapeWmiObjectPathValue(keyValue) + "\"} WHERE AssocClass=" + assocClass;
            using (var searcher = new ManagementObjectSearcher(query))
            {
                results = searcher.Get();
            }
        }
        catch
        {
        }

        if (results == null)
        {
            yield break;
        }

        foreach (ManagementObject obj in results)
        {
            yield return obj;
        }
    }

    private static string EscapeWmiObjectPathValue(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static Dictionary<string, string> MergeDetails(Dictionary<string, string> existing, Dictionary<string, string> extra)
    {
        var merged = existing == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
        if (extra != null)
        {
            foreach (var pair in extra)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !merged.ContainsKey(pair.Key))
                {
                    merged[pair.Key] = pair.Value;
                }
            }
        }

        return merged.Count == 0 ? null : merged;
    }

    private static Dictionary<string, Dictionary<string, string>> CloneDetailsMap(Dictionary<string, Dictionary<string, string>> source)
    {
        var clone = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return clone;
        }

        foreach (var pair in source)
        {
            clone[pair.Key] = CloneDetails(pair.Value) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return clone;
    }

    private static string GetLogicalDiskRootFromHardware(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "";
        }

        var trimmed = hardware.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        return (firstSpace > 0 ? trimmed.Substring(0, firstSpace) : trimmed).TrimEnd('\\');
    }

    private static void AddWindowsOperatingSystemDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    AddDetail(details, "Windows edition", GetWmiPropertyText(os, "Caption"));
                    AddDetail(details, "Windows version", GetWmiPropertyText(os, "Version"));
                    AddDetail(details, "Windows build", GetWmiPropertyText(os, "BuildNumber"));
                    AddDetail(details, "Windows build type", GetWmiPropertyText(os, "BuildType"));
                    AddDetail(details, "Windows architecture", GetWmiPropertyText(os, "OSArchitecture"));
                    AddDetail(details, "Windows install date", FormatWindowsInstallDate(GetWmiPropertyValue(os, "InstallDate")));
                    AddDetail(details, "Windows last boot time", FormatWmiDate(GetWmiPropertyValue(os, "LastBootUpTime")));
                    AddDetail(details, "Windows directory", GetWmiPropertyText(os, "WindowsDirectory"));
                    AddDetail(details, "Windows system directory", GetWmiPropertyText(os, "SystemDirectory"));
                    AddDetail(details, "Windows system drive", GetWmiPropertyText(os, "SystemDrive"));
                    AddDetail(details, "Windows boot device", GetWmiPropertyText(os, "BootDevice"));
                    AddDetail(details, "Windows system device", GetWmiPropertyText(os, "SystemDevice"));
                    AddDetail(details, "Windows locale", GetWmiPropertyText(os, "Locale"));
                    AddDetail(details, "Windows country code", GetWmiPropertyText(os, "CountryCode"));
                    AddDetail(details, "Windows code set", GetWmiPropertyText(os, "CodeSet"));
                    AddDetail(details, "Windows language", GetWmiPropertyText(os, "OSLanguage"));
                    AddDetail(details, "Windows MUI languages", FormatWmiDetailValue(GetWmiPropertyValue(os, "MUILanguages")));
                    AddDetail(details, "Windows encryption level", GetWmiPropertyText(os, "EncryptionLevel"));
                    AddDetail(details, "Windows portable OS", FormatYesNo(GetWmiPropertyValue(os, "PortableOperatingSystem")));
                    AddDetail(details, "Windows product type", FormatWindowsProductType(GetWmiPropertyValue(os, "ProductType")));
                    AddDetail(details, "Windows operating system SKU", GetWmiPropertyText(os, "OperatingSystemSKU"));
                    AddDetail(details, "Windows product suite", GetWmiPropertyText(os, "OSProductSuite"));
                    AddDetail(details, "Windows suite mask", GetWmiPropertyText(os, "SuiteMask"));
                    AddDetail(details, "Windows DEP available", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_Available")));
                    AddDetail(details, "Windows DEP for drivers", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_Drivers")));
                    AddDetail(details, "Windows DEP for 32-bit apps", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_32BitApplications")));
                    AddDetail(details, "Windows DEP support policy", GetWmiPropertyText(os, "DataExecutionPrevention_SupportPolicy"));
                    AddDetail(details, "Windows number of users", GetWmiPropertyText(os, "NumberOfUsers"));
                    AddDetail(details, "Windows number of processes", GetWmiPropertyText(os, "NumberOfProcesses"));
                    AddDetail(details, "Windows maximum process memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "MaxProcessMemorySize")));
                    AddDetail(details, "Windows total visible memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "TotalVisibleMemorySize")));
                    AddDetail(details, "Windows free physical memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreePhysicalMemory")));
                    AddDetail(details, "Windows total virtual memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "TotalVirtualMemorySize")));
                    AddDetail(details, "Windows free virtual memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreeVirtualMemory")));
                    AddDetail(details, "Windows page file size", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "SizeStoredInPagingFiles")));
                    AddDetail(details, "Windows page file free space", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreeSpaceInPagingFiles")));
                    AddDetail(details, "Windows product ID", GetWmiPropertyText(os, "SerialNumber"));
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddWindowsRegistryDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null)
                {
                    return;
                }

                AddDetail(details, "Windows registry product name", Convert.ToString(key.GetValue("ProductName")));
                AddDetail(details, "Windows registry display version", Convert.ToString(key.GetValue("DisplayVersion")));
                AddDetail(details, "Windows registry release ID", Convert.ToString(key.GetValue("ReleaseId")));
                AddDetail(details, "Windows registry edition ID", Convert.ToString(key.GetValue("EditionID")));
                AddDetail(details, "Windows registry installation type", Convert.ToString(key.GetValue("InstallationType")));
                AddDetail(details, "Windows registry current build", Convert.ToString(key.GetValue("CurrentBuild")));
                AddDetail(details, "Windows registry current build number", Convert.ToString(key.GetValue("CurrentBuildNumber")));
                AddDetail(details, "Windows registry update build revision", Convert.ToString(key.GetValue("UBR")));
                AddDetail(details, "Windows registry build branch", Convert.ToString(key.GetValue("BuildBranch")));
                AddDetail(details, "Windows registry build lab", Convert.ToString(key.GetValue("BuildLab")));
                AddDetail(details, "Windows registry build lab ex", Convert.ToString(key.GetValue("BuildLabEx")));
                AddDetail(details, "Windows registry product ID", Convert.ToString(key.GetValue("ProductId")));
                AddDetail(details, "Windows registry composition edition ID", Convert.ToString(key.GetValue("CompositionEditionID")));
                AddDetail(details, "Windows registry edition build lab", Convert.ToString(key.GetValue("EditionSubManufacturer")));
            }
        }
        catch
        {
        }
    }

    private static void AddWindowsLicensingDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Version, OA3xOriginalProductKey, ClientMachineID FROM SoftwareLicensingService"))
            {
                foreach (ManagementObject service in searcher.Get())
                {
                    AddDetail(details, "Windows licensing service version", GetWmiPropertyText(service, "Version"));
                    AddDetail(details, "Windows client machine ID", GetWmiPropertyText(service, "ClientMachineID"));
                    AddDetail(details, "Windows OEM embedded product key ending", MaskProductKey(GetWmiPropertyText(service, "OA3xOriginalProductKey")));
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Description, LicenseStatus, PartialProductKey, ProductKeyChannel, GracePeriodRemaining FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
            {
                var index = 1;
                foreach (ManagementObject product in searcher.Get())
                {
                    var label = "Windows license " + index;
                    AddDetail(details, label + " name", GetWmiPropertyText(product, "Name"));
                    AddDetail(details, label + " description", GetWmiPropertyText(product, "Description"));
                    AddDetail(details, label + " status", FormatLicenseStatus(GetWmiPropertyValue(product, "LicenseStatus")));
                    AddDetail(details, label + " product key ending", MaskPartialProductKey(GetWmiPropertyText(product, "PartialProductKey")));
                    AddDetail(details, label + " product key channel", GetWmiPropertyText(product, "ProductKeyChannel"));
                    AddDetail(details, label + " grace period remaining", FormatLicenseGracePeriod(GetWmiPropertyValue(product, "GracePeriodRemaining")));
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddComputerSystemProductDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystemProduct"))
            {
                foreach (ManagementObject product in searcher.Get())
                {
                    AddDetail(details, "System product vendor", GetWmiPropertyText(product, "Vendor"));
                    AddDetail(details, "System product name", GetWmiPropertyText(product, "Name"));
                    AddDetail(details, "System product version", GetWmiPropertyText(product, "Version"));
                    AddDetail(details, "System product SKU", GetWmiPropertyText(product, "SKUNumber"));
                    AddDetail(details, "System product UUID", GetWmiPropertyText(product, "UUID"));
                    AddDetail(details, "System product identifying number", GetWmiPropertyText(product, "IdentifyingNumber"));
                    AddRawWmiDetails(details, "System product WMI", product);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetFirmwareHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedFirmwareHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedFirmwareHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddFirmwareBiosDetails(details);
        AddTpmDetails(details);
        AddDetail(details, "BIOS mode", GetFirmwareMode());
        AddDetail(details, "Secure Boot", GetSecureBootState());

        lock (hardwareDetailsCacheLock)
        {
            cachedFirmwareHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddFirmwareBiosDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS"))
            {
                foreach (ManagementObject bios in searcher.Get())
                {
                    AddDetail(details, "BIOS vendor", GetWmiPropertyText(bios, "Manufacturer"));
                    AddDetail(details, "BIOS name", GetWmiPropertyText(bios, "Name"));
                    AddDetail(details, "BIOS caption", GetWmiPropertyText(bios, "Caption"));
                    AddDetail(details, "BIOS description", GetWmiPropertyText(bios, "Description"));
                    AddDetail(details, "BIOS version", GetWmiPropertyText(bios, "SMBIOSBIOSVersion"));
                    AddDetail(details, "BIOS release date", FormatWmiDate(GetWmiPropertyValue(bios, "ReleaseDate")));
                    AddDetail(details, "BIOS serial number", GetWmiPropertyText(bios, "SerialNumber"));
                    AddDetail(details, "BIOS status", GetWmiPropertyText(bios, "Status"));
                    AddDetail(details, "BIOS characteristics", FormatWmiDetailValue(GetWmiPropertyValue(bios, "BiosCharacteristics")));
                    AddDetail(details, "BIOS language edition", GetWmiPropertyText(bios, "LanguageEdition"));
                    AddDetail(details, "BIOS list of languages", FormatWmiDetailValue(GetWmiPropertyValue(bios, "ListOfLanguages")));
                    AddDetail(details, "BIOS current language", GetWmiPropertyText(bios, "CurrentLanguage"));
                    AddDetail(details, "SMBIOS present", FormatYesNo(GetWmiPropertyValue(bios, "SMBIOSPresent")));
                    AddDetail(details, "SMBIOS major version", GetWmiPropertyText(bios, "SMBIOSMajorVersion"));
                    AddDetail(details, "SMBIOS minor version", GetWmiPropertyText(bios, "SMBIOSMinorVersion"));
                    AddDetail(details, "SMBIOS version", FormatMajorMinor(GetWmiPropertyValue(bios, "SMBIOSMajorVersion"), GetWmiPropertyValue(bios, "SMBIOSMinorVersion"), true));
                    AddDetail(details, "System BIOS major version", GetWmiPropertyText(bios, "SystemBiosMajorVersion"));
                    AddDetail(details, "System BIOS minor version", GetWmiPropertyText(bios, "SystemBiosMinorVersion"));
                    AddDetail(details, "Embedded controller major version", GetWmiPropertyText(bios, "EmbeddedControllerMajorVersion"));
                    AddDetail(details, "Embedded controller minor version", GetWmiPropertyText(bios, "EmbeddedControllerMinorVersion"));
                    AddRawWmiDetails(details, "BIOS WMI", bios);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddTpmDetails(Dictionary<string, string> details)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
            scope.Connect();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_Tpm")))
            {
                foreach (ManagementObject tpm in searcher.Get())
                {
                    AddDetail(details, "TPM enabled", FormatYesNo(GetWmiPropertyValue(tpm, "IsEnabled_InitialValue")));
                    AddDetail(details, "TPM activated", FormatYesNo(GetWmiPropertyValue(tpm, "IsActivated_InitialValue")));
                    AddDetail(details, "TPM owned", FormatYesNo(GetWmiPropertyValue(tpm, "IsOwned_InitialValue")));
                    AddDetail(details, "TPM manufacturer ID", GetWmiPropertyText(tpm, "ManufacturerId"));
                    AddDetail(details, "TPM manufacturer version", GetWmiPropertyText(tpm, "ManufacturerVersion"));
                    AddDetail(details, "TPM spec version", GetWmiPropertyText(tpm, "SpecVersion"));
                    AddRawWmiDetails(details, "TPM WMI", tpm);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetBoardHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedBoardHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedBoardHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddBaseBoardDetails(details);
        AddSystemEnclosureDetails(details);
        AddBoardMemorySlotDetails(details);
        AddSystemSlotDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedBoardHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddBaseBoardDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject board in searcher.Get())
                {
                    AddDetail(details, "Baseboard manufacturer", GetWmiPropertyText(board, "Manufacturer"));
                    AddDetail(details, "Baseboard product", GetWmiPropertyText(board, "Product"));
                    AddDetail(details, "Baseboard version", GetWmiPropertyText(board, "Version"));
                    AddDetail(details, "Baseboard serial number", GetWmiPropertyText(board, "SerialNumber"));
                    AddDetail(details, "Baseboard part number", GetWmiPropertyText(board, "PartNumber"));
                    AddDetail(details, "Baseboard SKU", GetWmiPropertyText(board, "SKU"));
                    AddDetail(details, "Baseboard model", GetWmiPropertyText(board, "Model"));
                    AddDetail(details, "Baseboard tag", GetWmiPropertyText(board, "Tag"));
                    AddDetail(details, "Baseboard hosting board", FormatYesNo(GetWmiPropertyValue(board, "HostingBoard")));
                    AddDetail(details, "Baseboard hot swappable", FormatYesNo(GetWmiPropertyValue(board, "HotSwappable")));
                    AddDetail(details, "Baseboard removable", FormatYesNo(GetWmiPropertyValue(board, "Removable")));
                    AddDetail(details, "Baseboard replaceable", FormatYesNo(GetWmiPropertyValue(board, "Replaceable")));
                    AddDetail(details, "Baseboard requires daughter board", FormatYesNo(GetWmiPropertyValue(board, "RequiresDaughterBoard")));
                    AddDetail(details, "Baseboard slot layout", GetWmiPropertyText(board, "SlotLayout"));
                    AddDetail(details, "Baseboard configuration options", FormatWmiDetailValue(GetWmiPropertyValue(board, "ConfigOptions")));
                    AddDetail(details, "Baseboard special requirements", FormatYesNo(GetWmiPropertyValue(board, "SpecialRequirements")));
                    AddDetail(details, "Baseboard requirements description", GetWmiPropertyText(board, "RequirementsDescription"));
                    AddDetail(details, "Baseboard status", GetWmiPropertyText(board, "Status"));
                    AddRawWmiDetails(details, "Baseboard WMI", board);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddSystemEnclosureDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemEnclosure"))
            {
                foreach (ManagementObject enclosure in searcher.Get())
                {
                    AddDetail(details, "Chassis manufacturer", GetWmiPropertyText(enclosure, "Manufacturer"));
                    AddDetail(details, "Chassis version", GetWmiPropertyText(enclosure, "Version"));
                    AddDetail(details, "Chassis serial number", GetWmiPropertyText(enclosure, "SerialNumber"));
                    AddDetail(details, "Chassis asset tag", GetWmiPropertyText(enclosure, "SMBIOSAssetTag"));
                    AddDetail(details, "Chassis type", FormatChassisTypes(GetWmiPropertyValue(enclosure, "ChassisTypes")));
                    AddDetail(details, "Chassis type descriptions", FormatWmiDetailValue(GetWmiPropertyValue(enclosure, "TypeDescriptions")));
                    AddDetail(details, "Chassis lock present", FormatYesNo(GetWmiPropertyValue(enclosure, "LockPresent")));
                    AddDetail(details, "Chassis security status", FormatChassisSecurityStatus(GetWmiPropertyValue(enclosure, "SecurityStatus")));
                    AddDetail(details, "Chassis security breach", FormatWmiDetailValue(GetWmiPropertyValue(enclosure, "SecurityBreach")));
                    AddDetail(details, "Chassis number of power cords", GetWmiPropertyText(enclosure, "NumberOfPowerCords"));
                    AddDetail(details, "Chassis status", GetWmiPropertyText(enclosure, "Status"));
                    AddRawWmiDetails(details, "Chassis WMI", enclosure);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddBoardMemorySlotDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                foreach (ManagementObject array in searcher.Get())
                {
                    var label = "Board memory array " + index;
                    AddDetail(details, label + " slots", GetWmiPropertyText(array, "MemoryDevices"));
                    AddDetail(details, label + " maximum capacity", FormatMemoryArrayCapacity(array));
                    AddDetail(details, label + " use", FormatMemoryArrayUse(GetWmiPropertyValue(array, "Use")));
                    AddDetail(details, label + " location", FormatMemoryArrayLocation(GetWmiPropertyValue(array, "Location")));
                    AddDetail(details, label + " error correction", FormatMemoryErrorCorrection(GetWmiPropertyValue(array, "MemoryErrorCorrection")));
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddSystemSlotDetails(Dictionary<string, string> details)
    {
        try
        {
            var slots = new List<ManagementObject>();
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemSlot"))
            {
                foreach (ManagementObject slot in searcher.Get())
                {
                    slots.Add(slot);
                }
            }

            AddDetail(details, "Expansion slot count", slots.Count.ToString());
            for (var index = 0; index < slots.Count; index++)
            {
                using (var slot = slots[index])
                {
                    var label = "Expansion slot " + (index + 1);
                    AddDetail(details, label + " designation", GetWmiPropertyText(slot, "SlotDesignation"));
                    AddDetail(details, label + " current usage", FormatSystemSlotCurrentUsage(GetWmiPropertyValue(slot, "CurrentUsage")));
                    AddDetail(details, label + " connector type", FormatSystemSlotConnectorTypes(GetWmiPropertyValue(slot, "ConnectorType")));
                    AddDetail(details, label + " maximum data width", FormatSystemSlotDataWidth(GetWmiPropertyValue(slot, "MaxDataWidth")));
                    AddDetail(details, label + " hot plug supported", FormatYesNo(GetWmiPropertyValue(slot, "SupportsHotPlug")));
                    AddDetail(details, label + " status", GetWmiPropertyText(slot, "Status"));
                    AddRawWmiDetails(details, label + " WMI", slot);
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

    private static string FormatBytesFromKilobytes(object value)
    {
        long kb;
        return TryConvertToInt64(value, out kb) && kb > 0 ? FormatBytes(kb * 1024.0) : "";
    }

    private static string FormatWindowsProductType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Workstation";
            case 2: return "Domain controller";
            case 3: return "Server";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatLicenseStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unlicensed";
            case 1: return "Licensed";
            case 2: return "Out-of-box grace period";
            case 3: return "Out-of-tolerance grace period";
            case 4: return "Non-genuine grace period";
            case 5: return "Notification";
            case 6: return "Extended grace period";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatLicenseGracePeriod(object value)
    {
        long minutes;
        if (!TryConvertToInt64(value, out minutes) || minutes <= 0)
        {
            return "";
        }

        return FormatUptime(TimeSpan.FromMinutes(minutes));
    }

    private static string MaskPartialProductKey(string partial)
    {
        partial = (partial ?? "").Trim();
        return string.IsNullOrWhiteSpace(partial) ? "" : "ends with " + partial;
    }

    private static string MaskProductKey(string key)
    {
        key = (key ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var tail = key.Length <= 5 ? key : key.Substring(key.Length - 5);
        return "ends with " + tail;
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

    private static string FormatCacheAssociativity(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Direct mapped";
            case 4: return "2-way set associative";
            case 5: return "4-way set associative";
            case 6: return "Fully associative";
            case 7: return "8-way set associative";
            case 8: return "16-way set associative";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Instruction";
            case 4: return "Data";
            case 5: return "Unified";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheErrorCorrectType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "Parity";
            case 5: return "Single-bit ECC";
            case 6: return "Multi-bit ECC";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheWritePolicy(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Write back";
            case 4: return "Write through";
            case 5: return "Varies by memory address";
            case 6: return "Determined per I/O";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatAvailability(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Running or full power";
            case 4: return "Warning";
            case 5: return "In test";
            case 6: return "Not applicable";
            case 7: return "Power off";
            case 8: return "Off line";
            case 9: return "Off duty";
            case 10: return "Degraded";
            case 11: return "Not installed";
            case 12: return "Install error";
            case 13: return "Power save unknown";
            case 14: return "Power save low power";
            case 15: return "Power save standby";
            case 16: return "Power cycle";
            case 17: return "Power save warning";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatChassisTypes(object value)
    {
        var array = value as Array;
        if (array == null)
        {
            return FormatChassisType(value);
        }

        var parts = new List<string>();
        foreach (var item in array)
        {
            var text = FormatChassisType(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(", ", parts.ToArray());
    }

    private static string FormatChassisType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Desktop";
            case 4: return "Low-profile desktop";
            case 5: return "Pizza box";
            case 6: return "Mini tower";
            case 7: return "Tower";
            case 8: return "Portable";
            case 9: return "Laptop";
            case 10: return "Notebook";
            case 11: return "Handheld";
            case 12: return "Docking station";
            case 13: return "All-in-one";
            case 14: return "Sub-notebook";
            case 15: return "Space-saving";
            case 16: return "Lunch box";
            case 17: return "Main system chassis";
            case 18: return "Expansion chassis";
            case 19: return "Sub-chassis";
            case 20: return "Bus expansion chassis";
            case 21: return "Peripheral chassis";
            case 22: return "Storage chassis";
            case 23: return "Rack mount chassis";
            case 24: return "Sealed-case PC";
            case 30: return "Tablet";
            case 31: return "Convertible";
            case 32: return "Detachable";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatChassisSecurityStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "External interface locked out";
            case 5: return "External interface enabled";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryArrayLocation(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "System board or motherboard";
            case 4: return "ISA add-on card";
            case 5: return "EISA add-on card";
            case 6: return "PCI add-on card";
            case 7: return "MCA add-on card";
            case 8: return "PCMCIA add-on card";
            case 9: return "Proprietary add-on card";
            case 10: return "NuBus";
            case 11: return "PC-98/C20 add-on card";
            case 12: return "PC-98/C24 add-on card";
            case 13: return "PC-98/E add-on card";
            case 14: return "PC-98/local bus add-on card";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryArrayUse(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "System memory";
            case 4: return "Video memory";
            case 5: return "Flash memory";
            case 6: return "Non-volatile RAM";
            case 7: return "Cache memory";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryErrorCorrection(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "Parity";
            case 5: return "Single-bit ECC";
            case 6: return "Multi-bit ECC";
            case 7: return "CRC";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotCurrentUsage(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Available";
            case 4: return "In use";
            case 5: return "Unavailable";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotConnectorTypes(object value)
    {
        var array = value as Array;
        if (array == null)
        {
            return FormatSystemSlotConnectorType(value);
        }

        var parts = new List<string>();
        foreach (var item in array)
        {
            var text = FormatSystemSlotConnectorType(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(", ", parts.ToArray());
    }

    private static string FormatSystemSlotConnectorType(object value)
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
            case 2: return "ISA";
            case 3: return "MCA";
            case 4: return "EISA";
            case 5: return "PCI";
            case 6: return "PCMCIA";
            case 7: return "VL-VESA";
            case 8: return "Proprietary";
            case 9: return "Processor card slot";
            case 10: return "Proprietary memory card slot";
            case 11: return "I/O riser card slot";
            case 12: return "NuBus";
            case 13: return "PCI-66MHZ";
            case 14: return "AGP";
            case 15: return "AGP 2X";
            case 16: return "AGP 4X";
            case 17: return "PCI-X";
            case 18: return "AGP 8X";
            case 19: return "M.2 socket 1-DP";
            case 20: return "M.2 socket 1-SD";
            case 21: return "M.2 socket 2";
            case 22: return "M.2 socket 3";
            case 23: return "MXM type I";
            case 24: return "MXM type II";
            case 25: return "MXM type III";
            case 26: return "MXM type III-HE";
            case 27: return "MXM type IV";
            case 28: return "MXM 3.0 type A";
            case 29: return "MXM 3.0 type B";
            case 30: return "PCI Express Gen 2 SFF-8639";
            case 31: return "PCI Express Gen 3 SFF-8639";
            case 32: return "PCI Express Mini 52-pin";
            case 33: return "PCI Express Mini 52-pin with bottom-side keep-outs";
            case 34: return "PCI Express Mini 76-pin";
            case 35: return "PCI Express Gen 4 SFF-8639";
            case 36: return "PCI Express Gen 5 SFF-8639";
            case 37: return "OCP NIC 3.0 small form factor";
            case 38: return "OCP NIC 3.0 large form factor";
            case 39: return "OCP NIC prior to 3.0";
            case 40: return "CXL Flexbus 1.0";
            case 41: return "PC-98/C20";
            case 42: return "PC-98/C24";
            case 43: return "PC-98/E";
            case 44: return "PC-98/local bus";
            case 45: return "PCI Express";
            case 46: return "PCI Express x1";
            case 47: return "PCI Express x2";
            case 48: return "PCI Express x4";
            case 49: return "PCI Express x8";
            case 50: return "PCI Express x16";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotDataWidth(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "8-bit";
            case 4: return "16-bit";
            case 5: return "32-bit";
            case 6: return "64-bit";
            case 7: return "1x or x1";
            case 8: return "2x or x2";
            case 9: return "4x or x4";
            case 10: return "8x or x8";
            case 11: return "12x or x12";
            case 12: return "16x or x16";
            case 13: return "32x or x32";
            default: return Convert.ToString(value);
        }
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
        var windowsDetails = GetWindowsHardwareDetails();
        var firmwareDetails = GetFirmwareHardwareDetails();

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
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
                        AddOverviewTextRow(rows, "Windows boot time", FormatDateTime(bootTime), "Windows WMI", windowsDetails);
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
                foreach (ManagementObject bios in searcher.Get())
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

        AddPrinterOverviewRows(rows);

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

                var ipProperties = adapter.GetIPProperties();
                var networkDetails = BuildNetworkAdapterDetails(adapter, macAddress, macVendor, stats, ipProperties);
                if (!string.IsNullOrWhiteSpace(adapter.Description) && !string.Equals(adapter.Description, name, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Adapter description", DisplayValue = adapter.Description, Source = "Windows Network", Details = CloneDetails(networkDetails) });
                }
                if (!string.IsNullOrWhiteSpace(macAddress))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "MAC address", DisplayValue = macAddress, Source = "Windows Network", Details = CloneDetails(networkDetails) });
                }
                if (!string.IsNullOrWhiteSpace(macVendor))
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "MAC vendor", DisplayValue = macVendor, Source = "OUI database", Details = CloneDetails(networkDetails) });
                }
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Status", DisplayValue = adapter.OperationalStatus.ToString(), Source = "Windows Network", Details = CloneDetails(networkDetails) });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Link speed", DisplayValue = FormatBitsPerSecond(adapter.Speed), Source = "Windows Network", Details = CloneDetails(networkDetails) });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Receive rate", Value = (float)receiveRate, DisplayValue = FormatBytesPerSecond(receiveRate), Source = "Windows Network", Details = CloneDetails(networkDetails) });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Send rate", Value = (float)sendRate, DisplayValue = FormatBytesPerSecond(sendRate), Source = "Windows Network", Details = CloneDetails(networkDetails) });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data received", DisplayValue = FormatBytes(stats.BytesReceived), Source = "Windows Network", Details = CloneDetails(networkDetails) });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data sent", DisplayValue = FormatBytes(stats.BytesSent), Source = "Windows Network", Details = CloneDetails(networkDetails) });

                var addresses = ipProperties.UnicastAddresses
                    .Where(a => a.Address != null && a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct()
                    .ToList();
                if (addresses.Count > 0)
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "IP address", DisplayValue = string.Join(", ", addresses.ToArray()), Source = "Windows Network", Details = CloneDetails(networkDetails) });
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

    private Dictionary<string, string> BuildNetworkAdapterDetails(NetworkInterface adapter, string macAddress, string macVendor, IPv4InterfaceStatistics stats, IPInterfaceProperties properties)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (adapter == null)
        {
            return details;
        }

        AddDetail(details, "Adapter ID", adapter.Id);
        AddDetail(details, "Adapter name", adapter.Name);
        AddDetail(details, "Adapter description", adapter.Description);
        AddDetail(details, "Adapter type", adapter.NetworkInterfaceType.ToString());
        AddDetail(details, "Adapter status", adapter.OperationalStatus.ToString());
        AddDetail(details, "Adapter speed", FormatBitsPerSecond(adapter.Speed));
        AddDetail(details, "MAC address", macAddress);
        AddDetail(details, "MAC vendor", macVendor);
        AddDetail(details, "Supports IPv4", FormatYesNo(adapter.Supports(NetworkInterfaceComponent.IPv4)));
        AddDetail(details, "Supports IPv6", FormatYesNo(adapter.Supports(NetworkInterfaceComponent.IPv6)));
        if (stats != null)
        {
            AddDetail(details, "Bytes received", FormatBytes(stats.BytesReceived));
            AddDetail(details, "Bytes sent", FormatBytes(stats.BytesSent));
            AddDetail(details, "Incoming packets", stats.UnicastPacketsReceived.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Outgoing packets", stats.UnicastPacketsSent.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Incoming packets discarded", stats.IncomingPacketsDiscarded.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Outgoing packets discarded", stats.OutgoingPacketsDiscarded.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Incoming packet errors", stats.IncomingPacketsWithErrors.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Outgoing packet errors", stats.OutgoingPacketsWithErrors.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Incoming unknown protocol packets", stats.IncomingUnknownProtocolPackets.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Non-unicast packets received", stats.NonUnicastPacketsReceived.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "Non-unicast packets sent", stats.NonUnicastPacketsSent.ToString(CultureInfo.InvariantCulture));
        }

        if (properties != null)
        {
            AddDetail(details, "DNS suffix", properties.DnsSuffix);
            AddDetail(details, "DNS enabled", FormatYesNo(properties.IsDnsEnabled));
            AddDetail(details, "Dynamic DNS enabled", FormatYesNo(properties.IsDynamicDnsEnabled));
            AddDetail(details, "DNS servers", FormatIpAddressCollection(properties.DnsAddresses));
            AddDetail(details, "Default gateways", FormatGatewayAddressCollection(properties.GatewayAddresses));
            AddDetail(details, "DHCP servers", FormatIpAddressCollection(properties.DhcpServerAddresses));
            AddDetail(details, "WINS servers", FormatIpAddressCollection(properties.WinsServersAddresses));
            AddDetail(details, "Unicast addresses", FormatUnicastAddressCollection(properties.UnicastAddresses));
            AddDetail(details, "Multicast addresses", FormatIpAddressInformationCollection(properties.MulticastAddresses.Cast<IPAddressInformation>()));
            AddDetail(details, "Anycast addresses", FormatIpAddressInformationCollection(properties.AnycastAddresses.Cast<IPAddressInformation>()));

            try
            {
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 != null)
                {
                    AddDetail(details, "IPv4 interface index", ipv4.Index.ToString(CultureInfo.InvariantCulture));
                    AddDetail(details, "IPv4 MTU", ipv4.Mtu.ToString(CultureInfo.InvariantCulture));
                    AddDetail(details, "IPv4 DHCP enabled", FormatYesNo(ipv4.IsDhcpEnabled));
                    AddDetail(details, "IPv4 APIPA enabled", FormatYesNo(ipv4.IsAutomaticPrivateAddressingEnabled));
                    AddDetail(details, "IPv4 APIPA active", FormatYesNo(ipv4.IsAutomaticPrivateAddressingActive));
                    AddDetail(details, "IPv4 forwarding enabled", FormatYesNo(ipv4.IsForwardingEnabled));
                    AddDetail(details, "IPv4 uses WINS", FormatYesNo(ipv4.UsesWins));
                }
            }
            catch
            {
            }

            try
            {
                var ipv6 = properties.GetIPv6Properties();
                if (ipv6 != null)
                {
                    AddDetail(details, "IPv6 interface index", ipv6.Index.ToString(CultureInfo.InvariantCulture));
                    AddDetail(details, "IPv6 MTU", ipv6.Mtu.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }

        AddNetworkAdapterWmiDetails(details, adapter.Id);
        return details;
    }

    private void AddNetworkAdapterWmiDetails(Dictionary<string, string> details, string adapterId)
    {
        if (details == null || string.IsNullOrWhiteSpace(adapterId))
        {
            return;
        }

        Dictionary<string, string> cached;
        if (TryGetCachedNetworkWmiDetails(adapterId, out cached))
        {
            AddDetailsInPlace(details, cached);
            return;
        }

        var wmiDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='" + EscapeWmiQueryString(adapterId) + "'"))
            {
                foreach (ManagementObject config in searcher.Get())
                {
                    AddDetail(wmiDetails, "WMI DHCP enabled", FormatYesNo(GetWmiPropertyValue(config, "DHCPEnabled")));
                    AddDetail(wmiDetails, "WMI DHCP server", GetWmiPropertyText(config, "DHCPServer"));
                    AddDetail(wmiDetails, "WMI DNS domain", GetWmiPropertyText(config, "DNSDomain"));
                    AddDetail(wmiDetails, "WMI DNS host name", GetWmiPropertyText(config, "DNSHostName"));
                    AddDetail(wmiDetails, "WMI DNS server search order", FormatWmiDetailValue(GetWmiPropertyValue(config, "DNSServerSearchOrder")));
                    AddDetail(wmiDetails, "WMI default gateway", FormatWmiDetailValue(GetWmiPropertyValue(config, "DefaultIPGateway")));
                    AddDetail(wmiDetails, "WMI IP addresses", FormatWmiDetailValue(GetWmiPropertyValue(config, "IPAddress")));
                    AddDetail(wmiDetails, "WMI IP subnets", FormatWmiDetailValue(GetWmiPropertyValue(config, "IPSubnet")));
                    AddDetail(wmiDetails, "WMI MAC address", GetWmiPropertyText(config, "MACAddress"));
                    AddRawWmiDetails(wmiDetails, "Network configuration WMI", config);
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE GUID='" + EscapeWmiQueryString(adapterId) + "'"))
            {
                foreach (ManagementObject adapter in searcher.Get())
                {
                    AddRawWmiDetails(wmiDetails, "Network adapter WMI", adapter);
                }
            }
        }
        catch
        {
        }

        CacheNetworkWmiDetails(adapterId, wmiDetails);
        AddDetailsInPlace(details, wmiDetails);
    }

    private bool TryGetCachedNetworkWmiDetails(string adapterId, out Dictionary<string, string> details)
    {
        details = null;
        lock (networkWmiDetailsCacheLock)
        {
            CachedDetailSnapshot snapshot;
            if (networkWmiDetailsCache.TryGetValue(adapterId, out snapshot) &&
                snapshot != null &&
                (DateTime.UtcNow - snapshot.TimestampUtc).TotalMinutes < 5)
            {
                details = CloneDetails(snapshot.Details);
                return true;
            }
        }

        return false;
    }

    private void CacheNetworkWmiDetails(string adapterId, Dictionary<string, string> details)
    {
        lock (networkWmiDetailsCacheLock)
        {
            networkWmiDetailsCache[adapterId] = new CachedDetailSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                Details = CloneDetails(details)
            };
        }
    }

    private static void AddDetailsInPlace(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        if (target == null || source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item.Key) && !target.ContainsKey(item.Key))
            {
                target[item.Key] = item.Value;
            }
        }
    }

    private static string EscapeWmiQueryString(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static string FormatUnicastAddressCollection(UnicastIPAddressInformationCollection addresses)
    {
        if (addresses == null || addresses.Count == 0)
        {
            return "";
        }

        return string.Join(", ", addresses
            .Cast<UnicastIPAddressInformation>()
            .Where(a => a != null && a.Address != null)
            .Select(a =>
            {
                var suffix = "";
                try
                {
                    suffix = a.PrefixLength > 0 ? "/" + a.PrefixLength.ToString(CultureInfo.InvariantCulture) : "";
                }
                catch
                {
                }

                return a.Address + suffix;
            })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .ToArray());
    }

    private static string FormatIpAddressCollection(IPAddressCollection addresses)
    {
        if (addresses == null || addresses.Count == 0)
        {
            return "";
        }

        return string.Join(", ", addresses
            .Cast<System.Net.IPAddress>()
            .Where(address => address != null)
            .Select(address => address.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .ToArray());
    }

    private static string FormatIpAddressInformationCollection(IEnumerable<IPAddressInformation> addresses)
    {
        if (addresses == null)
        {
            return "";
        }

        return string.Join(", ", addresses
            .Where(address => address != null && address.Address != null)
            .Select(address => address.Address.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .ToArray());
    }

    private static string FormatGatewayAddressCollection(GatewayIPAddressInformationCollection gateways)
    {
        if (gateways == null || gateways.Count == 0)
        {
            return "";
        }

        return string.Join(", ", gateways
            .Cast<GatewayIPAddressInformation>()
            .Where(gateway => gateway != null && gateway.Address != null)
            .Select(gateway => gateway.Address.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .ToArray());
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
