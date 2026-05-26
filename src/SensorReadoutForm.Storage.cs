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

    private static DateTime bitLockerQueryDisabledUntilUtc = DateTime.MinValue;

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
            var logicalDetails = GetLogicalDiskDetailsByRoot();
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (!ShouldIncludeLogicalDiskDrive(drive) || drive.TotalSize <= 0)
                {
                    continue;
                }

                var freeBytes = Math.Max(0, drive.AvailableFreeSpace);
                var usedBytes = Math.Max(0, drive.TotalSize - freeBytes);
                var usedPercent = usedBytes / (double)drive.TotalSize * 100.0;
                var freePercent = freeBytes / (double)drive.TotalSize * 100.0;
                var hardware = GetLogicalDiskHardwareName(drive);
                var details = BuildDriveInfoDetails(drive);
                Dictionary<string, string> topologyDetails;
                if (logicalDetails.TryGetValue(GetLogicalDiskRootFromHardware(hardware), out topologyDetails))
                {
                    details = MergeDetails(details, topologyDetails);
                }

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Total space",
                    DisplayValue = FormatBytes(drive.TotalSize),
                    Source = "Windows Logical Disk",
                    Details = CloneDetails(details)
                });

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Used space",
                    Value = (float)usedPercent,
                    DisplayValue = FormatBytes(usedBytes) + " (" + FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%)",
                    Source = "Windows Logical Disk",
                    Details = CloneDetails(details)
                });

                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "Free space",
                    Value = (float)freePercent,
                    DisplayValue = FormatBytes(freeBytes) + " (" + FormatNumber(Math.Round(freePercent, 1), "0.0") + "%)",
                    Source = "Windows Logical Disk",
                    Details = CloneDetails(details)
                });

                var bitLockerStatus = GetDictionaryValue(details, "BitLocker status");
                if (!string.IsNullOrWhiteSpace(bitLockerStatus))
                {
                    rows.Add(new SensorRow
                    {
                        Type = "Performance",
                        Hardware = hardware,
                        Name = "BitLocker status",
                        DisplayValue = bitLockerStatus,
                        Source = "Windows BitLocker",
                        Details = CloneDetails(details)
                    });
                }
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

    private static bool ShouldIncludeLogicalDiskDrive(System.IO.DriveInfo drive)
    {
        if (drive == null || !drive.IsReady)
        {
            return false;
        }

        return drive.DriveType == System.IO.DriveType.Fixed ||
            drive.DriveType == System.IO.DriveType.Removable;
    }

    private static Dictionary<string, string> BuildDriveInfoDetails(System.IO.DriveInfo drive)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (drive == null)
        {
            return details;
        }

        AddDetail(details, "Drive root", drive.Name);
        AddDetail(details, "Drive type", FormatDriveType(drive.DriveType));
        try
        {
            AddDetail(details, "Drive label", drive.VolumeLabel);
        }
        catch
        {
        }

        try
        {
            AddDetail(details, "Drive format", drive.DriveFormat);
        }
        catch
        {
        }

        return details;
    }

    private static string FormatDriveType(System.IO.DriveType type)
    {
        switch (type)
        {
            case System.IO.DriveType.Fixed:
                return "Fixed";
            case System.IO.DriveType.Removable:
                return "Removable";
            case System.IO.DriveType.CDRom:
                return "Optical";
            case System.IO.DriveType.Network:
                return "Network";
            case System.IO.DriveType.Ram:
                return "RAM disk";
            case System.IO.DriveType.NoRootDirectory:
                return "No root directory";
            default:
                return "Unknown";
        }
    }

    private IEnumerable<SensorRow> GetLogicalDiskPerformanceRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (!ShouldIncludeLogicalDiskDrive(drive))
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
                var details = BuildDriveInfoDetails(drive);
                float readBytes;
                float writeBytes;
                float readActivity;
                float writeActivity;
                float totalActivity;
                if (TryReadLogicalDiskPerformanceCounters(instance, counters, out readBytes, out writeBytes, out readActivity, out writeActivity, out totalActivity))
                {
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Read rate", Identifier = "logicaldisk/" + instance + "/read", Value = readBytes, DisplayValue = FormatBytesPerSecond(readBytes), Source = "Windows Logical Disk", Details = CloneDetails(details) });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Write rate", Identifier = "logicaldisk/" + instance + "/write", Value = writeBytes, DisplayValue = FormatBytesPerSecond(writeBytes), Source = "Windows Logical Disk", Details = CloneDetails(details) });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Read activity", Identifier = "logicaldisk/" + instance + "/read-activity", Value = readActivity, DisplayValue = FormatNumber(Math.Round(readActivity, 1), "0.0") + "%", Source = "Windows Logical Disk", Details = CloneDetails(details) });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Write activity", Identifier = "logicaldisk/" + instance + "/write-activity", Value = writeActivity, DisplayValue = FormatNumber(Math.Round(writeActivity, 1), "0.0") + "%", Source = "Windows Logical Disk", Details = CloneDetails(details) });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Total activity", Identifier = "logicaldisk/" + instance + "/total-activity", Value = totalActivity, DisplayValue = FormatNumber(Math.Round(totalActivity, 1), "0.0") + "%", Source = "Windows Logical Disk", Details = CloneDetails(details) });
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
        AddBitLockerDetails(logical);

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

    private static void AddBitLockerDetails(Dictionary<string, Dictionary<string, string>> logical)
    {
        if (logical == null)
        {
            return;
        }

        if (DateTime.UtcNow < bitLockerQueryDisabledUntilUtc)
        {
            return;
        }

        Dictionary<string, Dictionary<string, string>> result = null;
        Exception error = null;
        var thread = new System.Threading.Thread(delegate()
        {
            try
            {
                result = QueryBitLockerDetails();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.IsBackground = true;
        thread.Name = "Sensor Readout BitLocker WMI";
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            bitLockerQueryDisabledUntilUtc = DateTime.UtcNow.AddMinutes(30);
            return;
        }

        if (error != null || result == null)
        {
            return;
        }

        foreach (var item in result)
        {
            Dictionary<string, string> details;
            if (!logical.TryGetValue(item.Key, out details) || details == null)
            {
                details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                logical[item.Key] = details;
            }

            foreach (var detail in item.Value)
            {
                AddDetail(details, detail.Key, detail.Value);
            }
        }
    }

    private static Dictionary<string, Dictionary<string, string>> QueryBitLockerDetails()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                Impersonation = ImpersonationLevel.Impersonate
            };
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption", options);
            scope.Connect();
            var enumerationOptions = new EnumerationOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                ReturnImmediately = true
            };
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"), enumerationOptions))
            {
                foreach (ManagementObject volume in searcher.Get())
                {
                    var driveLetter = GetWmiPropertyText(volume, "DriveLetter");
                    if (string.IsNullOrWhiteSpace(driveLetter))
                    {
                        continue;
                    }

                    var root = driveLetter.TrimEnd('\\');
                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    var protectionStatus = GetBitLockerMethodValue(volume, "GetProtectionStatus", "ProtectionStatus", "ProtectionStatus");
                    var conversionStatus = GetBitLockerMethodValue(volume, "GetConversionStatus", "ConversionStatus", "ConversionStatus");
                    var encryptionPercentage = GetBitLockerMethodValue(volume, "GetConversionStatus", "EncryptionPercentage", "EncryptionPercentage");
                    var encryptionMethod = GetBitLockerMethodValue(volume, "GetEncryptionMethod", "EncryptionMethod", "EncryptionMethod");
                    var lockStatus = GetBitLockerMethodValue(volume, "GetLockStatus", "LockStatus", "LockStatus");

                    var statusText = FormatBitLockerStatus(protectionStatus, conversionStatus, encryptionPercentage, lockStatus);
                    AddDetail(details, "BitLocker status", statusText);
                    AddDetail(details, "BitLocker protection status", DecodeBitLockerProtectionStatus(protectionStatus));
                    AddDetail(details, "BitLocker conversion status", DecodeBitLockerConversionStatus(conversionStatus));
                    AddDetail(details, "BitLocker encryption percentage", FormatBitLockerPercentage(encryptionPercentage));
                    AddDetail(details, "BitLocker encryption method", DecodeBitLockerEncryptionMethod(encryptionMethod));
                    AddDetail(details, "BitLocker lock status", DecodeBitLockerLockStatus(lockStatus));
                    AddDetail(details, "BitLocker drive letter", driveLetter);
                    AddDetail(details, "BitLocker device ID", GetWmiPropertyText(volume, "DeviceID"));
                    AddDetail(details, "BitLocker persistent volume ID", GetWmiPropertyText(volume, "PersistentVolumeID"));
                    AddDetail(details, "BitLocker volume type", DecodeBitLockerVolumeType(GetWmiPropertyValue(volume, "VolumeType")));
                    AddRawWmiDetails(details, "BitLocker WMI", volume);
                    result[root] = details;
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static object GetBitLockerMethodValue(ManagementObject volume, string methodName, string outputName, string propertyName)
    {
        if (volume == null)
        {
            return null;
        }

        try
        {
            var propertyValue = GetWmiPropertyValue(volume, propertyName);
            if (propertyValue != null && !string.IsNullOrWhiteSpace(Convert.ToString(propertyValue)))
            {
                return propertyValue;
            }
        }
        catch
        {
        }

        try
        {
            using (var result = volume.InvokeMethod(methodName, null, null))
            {
                if (result == null)
                {
                    return null;
                }

                return result[outputName];
            }
        }
        catch
        {
            return null;
        }
    }

    private static string FormatBitLockerStatus(object protectionStatus, object conversionStatus, object encryptionPercentage, object lockStatus)
    {
        var protection = DecodeBitLockerProtectionStatus(protectionStatus);
        var conversion = DecodeBitLockerConversionStatus(conversionStatus);
        var percent = FormatBitLockerPercentage(encryptionPercentage);
        var locked = DecodeBitLockerLockStatus(lockStatus);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(protection))
        {
            parts.Add(protection);
        }

        if (!string.IsNullOrWhiteSpace(conversion) && !conversion.Equals(protection, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(conversion);
        }

        if (!string.IsNullOrWhiteSpace(percent))
        {
            parts.Add(percent);
        }

        if (!string.IsNullOrWhiteSpace(locked) && !locked.Equals("Unlocked", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(locked);
        }

        return parts.Count == 0 ? "" : string.Join(", ", parts.ToArray());
    }

    private static string FormatBitLockerPercentage(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        double percent;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)
            ? FormatNumber(Math.Round(percent, 0), "0") + "%"
            : text;
    }

    private static string DecodeBitLockerProtectionStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Protection off";
            case 1: return "Protection on";
            case 2: return "Protection unknown";
            default: return Convert.ToString(value);
        }
    }

    private static string DecodeBitLockerConversionStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Fully decrypted";
            case 1: return "Fully encrypted";
            case 2: return "Encryption in progress";
            case 3: return "Decryption in progress";
            case 4: return "Encryption paused";
            case 5: return "Decryption paused";
            default: return Convert.ToString(value);
        }
    }

    private static string DecodeBitLockerEncryptionMethod(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "None";
            case 1: return "AES 128 with diffuser";
            case 2: return "AES 256 with diffuser";
            case 3: return "AES 128";
            case 4: return "AES 256";
            case 5: return "Hardware encryption";
            case 6: return "XTS-AES 128";
            case 7: return "XTS-AES 256";
            default: return Convert.ToString(value);
        }
    }

    private static string DecodeBitLockerLockStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unlocked";
            case 1: return "Locked";
            default: return Convert.ToString(value);
        }
    }

    private static string DecodeBitLockerVolumeType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Operating system volume";
            case 1: return "Fixed data volume";
            case 2: return "Removable data volume";
            default: return Convert.ToString(value);
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
}
