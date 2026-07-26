using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

public sealed partial class SensorReadoutForm
{
    private const string ApfsGptPartitionType = "7C3457EF-0000-11AA-AA11-00306543ECAC";
    private const string LinuxFileSystemGptPartitionType = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";
    private static readonly object mappedNetworkDriveCacheLock = new object();
    private static DateTime mappedNetworkDriveCacheUtc = DateTime.MinValue;
    private static Dictionary<string, Dictionary<string, string>> mappedNetworkDriveCache;

    private sealed class StorageVolumeInventory
    {
        public string DriveLetter = "";
        public string Path = "";
        public string FileSystem = "";
        public string Label = "";
        public string FileSystemType = "";
        public string DriveType = "";
        public string Health = "";
        public string Size = "";
        public string Free = "";
        public string AllocationUnitSize = "";
        public string DedupMode = "";
        public Dictionary<string, string> RawDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FileSystemProbeResult
    {
        public string FileSystem = "";
        public string Label = "";
        public string Uuid = "";
        public string BlockSize = "";
        public string Capacity = "";
        public string FreeSpace = "";
        public string State = "";
        public string Created = "";
        public string LastMounted = "";
        public string LastWritten = "";
        public string LastChecked = "";
        public string MountCount = "";
        public string MaximumMountCount = "";
        public string Creator = "";
        public string CompatibleFeatures = "";
        public string IncompatibleFeatures = "";
        public string ReadOnlyCompatibleFeatures = "";
        public string Detection = "";
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFilePointerEx", SetLastError = true)]
    private static extern bool StorageSetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint moveMethod);

    [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    private static extern bool StorageReadFile(SafeFileHandle file, byte[] buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

    private static void AddWindowsStorageInventoryDetails(
        Dictionary<string, Dictionary<string, string>> physical,
        Dictionary<string, Dictionary<string, string>> logical)
    {
        if (physical == null || logical == null)
        {
            return;
        }

        var volumes = QueryStorageVolumes();
        AddMsftDiskDetails(physical);
        AddMsftPartitionDetails(physical, logical, volumes);
        AddUnmatchedVolumeDetails(logical, volumes);
    }

    private static void AddMsftDiskDetails(Dictionary<string, Dictionary<string, string>> physical)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk"))
            {
                foreach (ManagementObject disk in ExecuteWmiQuery(searcher, "WMI"))
                {
                    using (disk)
                    {
                        var number = GetWmiPropertyText(disk, "Number");
                        var friendlyName = GetWmiPropertyText(disk, "FriendlyName");
                        var details = FindOrCreatePhysicalDiskDetails(physical, number, friendlyName);
                        AddDetail(details, "Disk number", number);
                        AddDetail(details, "Disk friendly name", friendlyName);
                        AddDetail(details, "Disk manufacturer", GetWmiPropertyText(disk, "Manufacturer"));
                        AddDetail(details, "Disk model", GetWmiPropertyText(disk, "Model"));
                        AddDetail(details, "Disk serial number", GetWmiPropertyText(disk, "SerialNumber"));
                        AddDetail(details, "Disk firmware version", GetWmiPropertyText(disk, "FirmwareVersion"));
                        AddDetail(details, "Disk bus type", FormatStorageBusType(GetWmiPropertyValue(disk, "BusType")));
                        AddDetail(details, "Disk partition style", DecodePartitionStyle(GetWmiPropertyValue(disk, "PartitionStyle")));
                        AddDetail(details, "Disk partition count", GetWmiPropertyText(disk, "NumberOfPartitions"));
                        AddDetail(details, "Disk size", FormatStorageBytes(GetWmiPropertyValue(disk, "Size")));
                        AddDetail(details, "Disk allocated size", FormatStorageBytes(GetWmiPropertyValue(disk, "AllocatedSize")));
                        AddDetail(details, "Disk unallocated size", FormatStorageDifference(GetWmiPropertyValue(disk, "Size"), GetWmiPropertyValue(disk, "AllocatedSize")));
                        AddDetail(details, "Disk largest free extent", FormatStorageBytes(GetWmiPropertyValue(disk, "LargestFreeExtent")));
                        AddDetail(details, "Disk logical sector size", FormatStorageBytes(GetWmiPropertyValue(disk, "LogicalSectorSize")));
                        AddDetail(details, "Disk physical sector size", FormatStorageBytes(GetWmiPropertyValue(disk, "PhysicalSectorSize")));
                        AddDetail(details, "Disk health status", DecodeHealthStatus(GetWmiPropertyValue(disk, "HealthStatus")));
                        AddDetail(details, "Disk operational status", DecodeOperationalStatus(GetWmiPropertyValue(disk, "OperationalStatus")));
                        AddDetail(details, "Disk provisioning type", DecodeProvisioningType(GetWmiPropertyValue(disk, "ProvisioningType")));
                        AddDetail(details, "Disk GUID", GetWmiPropertyText(disk, "Guid"));
                        AddDetail(details, "Disk MBR signature", GetWmiPropertyText(disk, "Signature"));
                        AddDetail(details, "Disk location", GetWmiPropertyText(disk, "Location"));
                        AddDetail(details, "Disk path", GetWmiPropertyText(disk, "Path"));
                        AddNullableYesNoDetail(details, "Disk boot disk", GetWmiPropertyValue(disk, "IsBoot"));
                        AddNullableYesNoDetail(details, "Disk system disk", GetWmiPropertyValue(disk, "IsSystem"));
                        AddNullableYesNoDetail(details, "Disk offline", GetWmiPropertyValue(disk, "IsOffline"));
                        AddNullableYesNoDetail(details, "Disk read only", GetWmiPropertyValue(disk, "IsReadOnly"));
                        AddNullableYesNoDetail(details, "Disk clustered", GetWmiPropertyValue(disk, "IsClustered"));
                        AddRawWmiDetails(details, "Disk WMI", disk);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void AddMsftPartitionDetails(
        Dictionary<string, Dictionary<string, string>> physical,
        Dictionary<string, Dictionary<string, string>> logical,
        List<StorageVolumeInventory> volumes)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Partition"))
            {
                foreach (ManagementObject partition in ExecuteWmiQuery(searcher, "WMI"))
                {
                    using (partition)
                    {
                        var diskNumber = GetWmiPropertyText(partition, "DiskNumber");
                        var partitionNumber = GetWmiPropertyText(partition, "PartitionNumber");
                        // Win32_DiskPartition omits some reserved partitions and therefore uses
                        // different numbering. Keep Storage API partitions in their own namespace.
                        var prefix = "Storage partition " + partitionNumber;
                        var details = FindOrCreatePhysicalDiskDetails(physical, diskNumber, "");
                        var driveLetter = CleanDriveLetter(GetWmiPropertyText(partition, "DriveLetter"));
                        var accessPaths = FormatWmiDetailValue(GetWmiPropertyValue(partition, "AccessPaths"));
                        var gptType = NormalizeGuidText(GetWmiPropertyText(partition, "GptType"));
                        var mbrType = GetWmiPropertyText(partition, "MbrType");

                        AddDetail(details, prefix + " number", partitionNumber);
                        AddDetail(details, prefix + " drive letter", driveLetter);
                        AddDetail(details, prefix + " size", FormatStorageBytes(GetWmiPropertyValue(partition, "Size")));
                        AddDetail(details, prefix + " starting offset", FormatStorageBytes(GetWmiPropertyValue(partition, "Offset")));
                        AddDetail(details, prefix + " GUID", GetWmiPropertyText(partition, "Guid"));
                        AddDetail(details, prefix + " GPT type", gptType);
                        AddDetail(details, prefix + " GPT type name", DecodeGptPartitionType(gptType));
                        AddDetail(details, prefix + " MBR type", mbrType);
                        AddDetail(details, prefix + " access paths", accessPaths);
                        AddNullableYesNoDetail(details, prefix + " boot partition", GetWmiPropertyValue(partition, "IsBoot"));
                        AddNullableYesNoDetail(details, prefix + " system partition", GetWmiPropertyValue(partition, "IsSystem"));
                        AddNullableYesNoDetail(details, prefix + " active partition", GetWmiPropertyValue(partition, "IsActive"));
                        AddNullableYesNoDetail(details, prefix + " hidden partition", GetWmiPropertyValue(partition, "IsHidden"));
                        AddNullableYesNoDetail(details, prefix + " read only", GetWmiPropertyValue(partition, "IsReadOnly"));
                        AddNullableYesNoDetail(details, prefix + " offline", GetWmiPropertyValue(partition, "IsOffline"));
                        AddNullableYesNoDetail(details, prefix + " shadow copy", GetWmiPropertyValue(partition, "IsShadowCopy"));
                        AddNullableYesNoDetail(details, prefix + " no default drive letter", GetWmiPropertyValue(partition, "NoDefaultDriveLetter"));
                        AddRawWmiDetails(details, prefix + " storage WMI", partition);

                        var volume = FindStorageVolume(volumes, driveLetter, accessPaths);
                        if (volume != null)
                        {
                            AddVolumeInventoryDetails(details, prefix + " volume", volume);
                        }

                        FileSystemProbeResult probe;
                        if (TryProbePartitionFileSystem(partition, volume, out probe))
                        {
                            AddFileSystemProbeDetails(details, prefix, probe);
                        }

                        if (!string.IsNullOrWhiteSpace(driveLetter))
                        {
                            Dictionary<string, string> logicalDetails;
                            if (!logical.TryGetValue(driveLetter, out logicalDetails) || logicalDetails == null)
                            {
                                logicalDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                logical[driveLetter] = logicalDetails;
                            }

                            AddDetail(logicalDetails, "Disk number", diskNumber);
                            AddDetail(logicalDetails, "Partition number", partitionNumber);
                            AddDetail(logicalDetails, "Partition size", FormatStorageBytes(GetWmiPropertyValue(partition, "Size")));
                            AddDetail(logicalDetails, "Partition starting offset", FormatStorageBytes(GetWmiPropertyValue(partition, "Offset")));
                            AddDetail(logicalDetails, "Partition GUID", GetWmiPropertyText(partition, "Guid"));
                            AddDetail(logicalDetails, "Partition GPT type", gptType);
                            AddDetail(logicalDetails, "Partition GPT type name", DecodeGptPartitionType(gptType));
                            AddDetail(logicalDetails, "Partition MBR type", mbrType);
                            if (volume != null)
                            {
                                AddVolumeInventoryDetails(logicalDetails, "Volume", volume);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static List<StorageVolumeInventory> QueryStorageVolumes()
    {
        var result = new List<StorageVolumeInventory>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Volume"))
            {
                foreach (ManagementObject volume in ExecuteWmiQuery(searcher, "WMI"))
                {
                    using (volume)
                    {
                        var item = new StorageVolumeInventory
                        {
                            DriveLetter = CleanDriveLetter(GetWmiPropertyText(volume, "DriveLetter")),
                            Path = GetWmiPropertyText(volume, "Path"),
                            FileSystem = GetWmiPropertyText(volume, "FileSystem"),
                            Label = GetWmiPropertyText(volume, "FileSystemLabel"),
                            FileSystemType = DecodeFileSystemType(GetWmiPropertyValue(volume, "FileSystemType")),
                            DriveType = DecodeStorageDriveType(GetWmiPropertyValue(volume, "DriveType")),
                            Health = DecodeHealthStatus(GetWmiPropertyValue(volume, "HealthStatus")),
                            Size = FormatStorageBytes(GetWmiPropertyValue(volume, "Size")),
                            Free = FormatStorageBytes(GetWmiPropertyValue(volume, "SizeRemaining")),
                            AllocationUnitSize = FormatStorageBytes(GetWmiPropertyValue(volume, "AllocationUnitSize")),
                            DedupMode = DecodeDedupMode(GetWmiPropertyValue(volume, "DedupMode"))
                        };
                        AddRawWmiDetails(item.RawDetails, "Volume WMI", volume);
                        result.Add(item);
                    }
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static void AddUnmatchedVolumeDetails(Dictionary<string, Dictionary<string, string>> logical, IEnumerable<StorageVolumeInventory> volumes)
    {
        foreach (var volume in volumes ?? Enumerable.Empty<StorageVolumeInventory>())
        {
            if (volume == null || string.IsNullOrWhiteSpace(volume.DriveLetter))
            {
                continue;
            }

            Dictionary<string, string> details;
            if (!logical.TryGetValue(volume.DriveLetter, out details) || details == null)
            {
                details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                logical[volume.DriveLetter] = details;
            }

            AddVolumeInventoryDetails(details, "Volume", volume);
        }
    }

    private static void AddVolumeInventoryDetails(Dictionary<string, string> details, string prefix, StorageVolumeInventory volume)
    {
        if (details == null || volume == null)
        {
            return;
        }

        AddDetail(details, prefix + " drive letter", volume.DriveLetter);
        AddDetail(details, prefix + " path", volume.Path);
        AddDetail(details, prefix + " file system", volume.FileSystem);
        AddDetail(details, prefix + " file system type", volume.FileSystemType);
        AddDetail(details, prefix + " label", volume.Label);
        AddDetail(details, prefix + " drive type", volume.DriveType);
        AddDetail(details, prefix + " health", volume.Health);
        AddDetail(details, prefix + " size", volume.Size);
        AddDetail(details, prefix + " free space", volume.Free);
        AddDetail(details, prefix + " allocation unit size", volume.AllocationUnitSize);
        AddDetail(details, prefix + " deduplication mode", volume.DedupMode);
        foreach (var detail in volume.RawDetails)
        {
            AddDetail(details, detail.Key, detail.Value);
        }
    }

    private static bool TryProbePartitionFileSystem(ManagementObject partition, StorageVolumeInventory volume, out FileSystemProbeResult result)
    {
        result = null;
        if (partition == null || (volume != null && !string.IsNullOrWhiteSpace(volume.FileSystem)))
        {
            return false;
        }

        int diskNumber;
        long offset;
        if (!int.TryParse(GetWmiPropertyText(partition, "DiskNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out diskNumber) ||
            !long.TryParse(GetWmiPropertyText(partition, "Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) ||
            offset < 0 ||
            IsTrue(GetWmiPropertyValue(partition, "IsOffline")))
        {
            return false;
        }

        var gptType = NormalizeGuidText(GetWmiPropertyText(partition, "GptType"));
        var mbrType = GetWmiPropertyText(partition, "MbrType");
        var mayBeApfs = string.Equals(gptType, ApfsGptPartitionType, StringComparison.OrdinalIgnoreCase);
        var mayBeExt = string.Equals(gptType, LinuxFileSystemGptPartitionType, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mbrType, "131", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mbrType, "0x83", StringComparison.OrdinalIgnoreCase);
        if (!mayBeApfs && !mayBeExt)
        {
            return false;
        }

        byte[] data;
        if (mayBeApfs && TryReadPhysicalDriveRange(diskNumber, offset, 4096, out data) && TryParseApfsContainerBlock(data, out result))
        {
            result.Detection = "APFS partition type and container superblock";
            return true;
        }

        if (mayBeExt && TryReadPhysicalDriveRange(diskNumber, offset + 1024, 1024, out data) && TryParseExtSuperblock(data, out result))
        {
            result.Detection = "Linux partition type and EXT superblock";
            return true;
        }

        if (mayBeApfs)
        {
            result = new FileSystemProbeResult { FileSystem = "APFS", Detection = "APFS GPT partition type" };
            return true;
        }

        return false;
    }

    private static bool TryReadPhysicalDriveRange(int diskNumber, long offset, int count, out byte[] data)
    {
        data = null;
        const uint genericRead = 0x80000000;
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint openExisting = 3;
        const uint fileBegin = 0;
        if (diskNumber < 0 || offset < 0 || count <= 0 || count > 65536)
        {
            return false;
        }

        try
        {
            using (var handle = StorageCreateFile(@"\\.\PhysicalDrive" + diskNumber, genericRead, fileShareRead | fileShareWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    return false;
                }

                long position;
                if (!StorageSetFilePointerEx(handle, offset, out position, fileBegin) || position != offset)
                {
                    return false;
                }

                var buffer = new byte[count];
                int bytesRead;
                if (!StorageReadFile(handle, buffer, count, out bytesRead, IntPtr.Zero) || bytesRead < count)
                {
                    return false;
                }

                data = buffer;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseExtSuperblock(byte[] data, out FileSystemProbeResult result)
    {
        result = null;
        if (data == null || data.Length < 364 || ReadUInt16(data, 0x38) != 0xEF53)
        {
            return false;
        }

        var compatible = ReadUInt32(data, 0x5C);
        var incompatible = ReadUInt32(data, 0x60);
        var readOnlyCompatible = ReadUInt32(data, 0x64);
        var ext4Indicators = 0x40U | 0x80U | 0x200U | 0x400U | 0x2000U | 0x4000U | 0x8000U | 0x10000U | 0x20000U;
        var fileSystem = (incompatible & ext4Indicators) != 0 ? "EXT4" : (compatible & 0x4U) != 0 ? "EXT3" : "EXT2";
        var logBlockSize = ReadUInt32(data, 0x18);
        if (logBlockSize > 6)
        {
            return false;
        }

        var blockSize = 1024UL << (int)logBlockSize;
        var blockCount = (ulong)ReadUInt32(data, 0x04);
        var freeBlockCount = (ulong)ReadUInt32(data, 0x0C);
        if ((incompatible & 0x80U) != 0)
        {
            blockCount |= (ulong)ReadUInt32(data, 0x150) << 32;
            freeBlockCount |= (ulong)ReadUInt32(data, 0x158) << 32;
        }

        result = new FileSystemProbeResult
        {
            FileSystem = fileSystem,
            Label = ReadNullTerminatedText(data, 0x78, 16),
            Uuid = FormatUuid(data, 0x68),
            BlockSize = FormatStorageBytes(blockSize),
            Capacity = FormatStorageBytes(SafeMultiply(blockCount, blockSize)),
            FreeSpace = FormatStorageBytes(SafeMultiply(freeBlockCount, blockSize)),
            State = DecodeExtState(ReadUInt16(data, 0x3A)),
            Created = FormatUnixTimeWithAge(ReadUInt32(data, 0x108)),
            LastMounted = FormatUnixTimeWithAge(ReadUInt32(data, 0x2C)),
            LastWritten = FormatUnixTimeWithAge(ReadUInt32(data, 0x30)),
            LastChecked = FormatUnixTimeWithAge(ReadUInt32(data, 0x40)),
            MountCount = ReadUInt16(data, 0x34).ToString(CultureInfo.InvariantCulture),
            MaximumMountCount = FormatExtMaximumMountCount(ReadUInt16(data, 0x36)),
            Creator = DecodeExtCreator(ReadUInt32(data, 0x48)),
            CompatibleFeatures = "0x" + compatible.ToString("X8", CultureInfo.InvariantCulture),
            IncompatibleFeatures = "0x" + incompatible.ToString("X8", CultureInfo.InvariantCulture),
            ReadOnlyCompatibleFeatures = "0x" + readOnlyCompatible.ToString("X8", CultureInfo.InvariantCulture)
        };
        return true;
    }

    private static bool TryParseApfsContainerBlock(byte[] data, out FileSystemProbeResult result)
    {
        result = null;
        if (data == null || data.Length < 88 ||
            data[32] != (byte)'N' || data[33] != (byte)'X' || data[34] != (byte)'S' || data[35] != (byte)'B')
        {
            return false;
        }

        var blockSize = ReadUInt32(data, 36);
        var blockCount = ReadUInt64(data, 40);
        if (blockSize < 4096 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            return false;
        }

        result = new FileSystemProbeResult
        {
            FileSystem = "APFS",
            Uuid = FormatUuid(data, 72),
            BlockSize = FormatStorageBytes(blockSize),
            Capacity = FormatStorageBytes(SafeMultiply(blockCount, blockSize)),
            CompatibleFeatures = "0x" + ReadUInt64(data, 48).ToString("X16", CultureInfo.InvariantCulture),
            ReadOnlyCompatibleFeatures = "0x" + ReadUInt64(data, 56).ToString("X16", CultureInfo.InvariantCulture),
            IncompatibleFeatures = "0x" + ReadUInt64(data, 64).ToString("X16", CultureInfo.InvariantCulture),
            State = "Container superblock detected"
        };
        return true;
    }

    private static void AddFileSystemProbeDetails(Dictionary<string, string> details, string prefix, FileSystemProbeResult probe)
    {
        if (details == null || probe == null)
        {
            return;
        }

        AddDetail(details, prefix + " detected file system", probe.FileSystem);
        AddDetail(details, prefix + " file system label", probe.Label);
        AddDetail(details, prefix + " file system UUID", probe.Uuid);
        AddDetail(details, prefix + " file system block size", probe.BlockSize);
        AddDetail(details, prefix + " file system capacity", probe.Capacity);
        AddDetail(details, prefix + " file system free space", probe.FreeSpace);
        AddDetail(details, prefix + " file system state", probe.State);
        AddDetail(details, prefix + " file system created", probe.Created);
        AddDetail(details, prefix + " file system last mounted", probe.LastMounted);
        AddDetail(details, prefix + " file system last written", probe.LastWritten);
        AddDetail(details, prefix + " file system last checked", probe.LastChecked);
        AddDetail(details, prefix + " file system mount count", probe.MountCount);
        AddDetail(details, prefix + " file system maximum mount count", probe.MaximumMountCount);
        AddDetail(details, prefix + " file system creator", probe.Creator);
        AddDetail(details, prefix + " compatible feature flags", probe.CompatibleFeatures);
        AddDetail(details, prefix + " incompatible feature flags", probe.IncompatibleFeatures);
        AddDetail(details, prefix + " read-only compatible feature flags", probe.ReadOnlyCompatibleFeatures);
        AddDetail(details, prefix + " file system detection", probe.Detection);
    }

    private static void AddMappedNetworkDriveRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

        var mappings = GetMappedNetworkDrives();
        foreach (var mapping in mappings.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var root = mapping.Key;
            var details = mapping.Value;
            var hardware = T("ui.Mapped network drive", "Mapped network drive") + " " + root;
            var status = GetDictionaryValue(details, "Status");
            rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = "Status", DisplayValue = status, Source = "Windows network drive", Details = CloneDetails(details) });
            AddNetworkDriveTextRow(rows, hardware, "Remote location", GetDictionaryValue(details, "Remote location"), details);
            AddNetworkDriveTextRow(rows, hardware, "Provider", GetDictionaryValue(details, "Provider"), details);
            AddNetworkDriveTextRow(rows, hardware, "File system", GetDictionaryValue(details, "File system"), details);
            AddNetworkDriveTextRow(rows, hardware, "Total space", GetDictionaryValue(details, "Total space"), details);
            AddNetworkDriveTextRow(rows, hardware, "Used space", GetDictionaryValue(details, "Used space"), details);
            AddNetworkDriveTextRow(rows, hardware, "Free space", GetDictionaryValue(details, "Free space"), details);
        }
    }

    private static Dictionary<string, Dictionary<string, string>> GetMappedNetworkDrives()
    {
        lock (mappedNetworkDriveCacheLock)
        {
            if (mappedNetworkDriveCache != null && DateTime.UtcNow - mappedNetworkDriveCacheUtc < TimeSpan.FromSeconds(30))
            {
                return CloneDetailsMap(mappedNetworkDriveCache);
            }
        }

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var networkKey = Registry.CurrentUser.OpenSubKey("Network"))
            {
                foreach (var letter in networkKey == null ? new string[0] : networkKey.GetSubKeyNames())
                {
                    using (var driveKey = networkKey.OpenSubKey(letter))
                    {
                        var root = CleanDriveLetter(letter);
                        if (string.IsNullOrWhiteSpace(root))
                        {
                            continue;
                        }

                        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        AddDetail(details, "Drive root", root);
                        AddDetail(details, "Remote location", Convert.ToString(driveKey == null ? null : driveKey.GetValue("RemotePath")));
                        AddDetail(details, "Provider", Convert.ToString(driveKey == null ? null : driveKey.GetValue("ProviderName")));
                        AddDetail(details, "Connection type", "Persistent mapping");
                        details["Status"] = T("value.Network drive mapped", "Mapped");
                        result[root] = details;
                    }
                }
            }
        }
        catch
        {
        }

        ApplySmbMappingDetails(result);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive == null || drive.DriveType != DriveType.Network)
            {
                continue;
            }

            var root = CleanDriveLetter(drive.Name);
            Dictionary<string, string> details;
            if (!result.TryGetValue(root, out details) || details == null)
            {
                details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[root] = details;
            }

            AddDetail(details, "Drive root", root);
            AddDetail(details, "Drive type", "Network");
            var ready = false;
            try { ready = drive.IsReady; } catch { }
            if (ready)
            {
                details["Status"] = T("value.Network drive connected", "Connected");
            }
            else if (!details.ContainsKey("SMB mapping status"))
            {
                details["Status"] = T("value.Network drive disconnected", "Disconnected");
            }
            if (!ready)
            {
                continue;
            }

            try { AddDetail(details, "Drive label", drive.VolumeLabel); } catch { }
            try { AddDetail(details, "File system", drive.DriveFormat); } catch { }
            try
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                AddDetail(details, "Total space", FormatStorageBytes(total));
                AddDetail(details, "Used space", FormatStorageBytes(Math.Max(0, total - free)));
                AddDetail(details, "Free space", FormatStorageBytes(Math.Max(0, free)));
            }
            catch
            {
            }
        }

        foreach (var details in result.Values)
        {
            if (!details.ContainsKey("Status"))
            {
                details["Status"] = T("value.Network drive mapped", "Mapped");
            }
        }

        lock (mappedNetworkDriveCacheLock)
        {
            mappedNetworkDriveCache = CloneDetailsMap(result);
            mappedNetworkDriveCacheUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static void ApplySmbMappingDetails(Dictionary<string, Dictionary<string, string>> result)
    {
        if (result == null)
        {
            return;
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\SMB", "SELECT LocalPath, RemotePath, Status FROM MSFT_SmbMapping"))
            {
                foreach (ManagementObject mapping in ExecuteWmiQuery(searcher, "MappedNetworkDrives"))
                {
                    using (mapping)
                    {
                        var root = CleanDriveLetter(GetWmiPropertyText(mapping, "LocalPath"));
                        if (string.IsNullOrWhiteSpace(root))
                        {
                            continue;
                        }

                        Dictionary<string, string> details;
                        if (!result.TryGetValue(root, out details) || details == null)
                        {
                            details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            result[root] = details;
                        }

                        AddDetail(details, "Drive root", root);
                        AddDetail(details, "Drive type", "Network");
                        AddDetail(details, "Remote location", GetWmiPropertyText(mapping, "RemotePath"));
                        var status = DecodeSmbMappingStatus(GetWmiPropertyValue(mapping, "Status"));
                        details["SMB mapping status"] = status;
                        details["Status"] = status;
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static string DecodeSmbMappingStatus(object value)
    {
        int status;
        if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out status))
        {
            return T("value.Network drive mapped", "Mapped");
        }

        if (status == 0) return T("value.Network drive connected", "Connected");
        if (status == 1) return T("value.Network drive paused", "Paused");
        if (status == 2) return T("value.Network drive disconnected", "Disconnected");
        if (status == 3) return T("value.Network drive network error", "Network error");
        if (status == 4) return T("value.Network drive connecting", "Connecting");
        if (status == 5) return T("value.Network drive reconnecting", "Reconnecting");
        if (status == 6) return T("value.Network drive unavailable", "Unavailable");
        return T("value.Network drive mapped", "Mapped");
    }

    private static void AddPhysicalDiskInventoryRows(
        List<SensorRow> rows,
        string hardware,
        Dictionary<string, Dictionary<string, string>> inventoryDetails)
    {
        if (rows == null || string.IsNullOrWhiteSpace(hardware) || inventoryDetails == null)
        {
            return;
        }

        Dictionary<string, string> details;
        if (!inventoryDetails.TryGetValue(NormalizeStorageHardwareName(hardware), out details) || details == null)
        {
            return;
        }

        AddPhysicalDiskTextRow(rows, hardware, "Partition style", GetDictionaryValue(details, "Disk partition style"), "Windows Storage");
        AddPhysicalDiskTextRow(rows, hardware, "Partition count", GetDictionaryValue(details, "Disk partition count"), "Windows Storage");
        foreach (var item in details
            .Where(item => item.Key.StartsWith("Storage partition ", StringComparison.OrdinalIgnoreCase) && item.Key.EndsWith(" detected file system", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = item.Key.Substring(0, item.Key.Length - " detected file system".Length) + " file system";
            AddPhysicalDiskTextRow(rows, hardware, name, item.Value, "Sensor Readout storage parser");
        }
    }

    private static void AddNetworkDriveTextRow(List<SensorRow> rows, string hardware, string name, string value, Dictionary<string, string> details)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow { Type = "Performance", Hardware = hardware, Name = name, DisplayValue = value, Source = "Windows network drive", Details = CloneDetails(details) });
    }

    private static Dictionary<string, string> FindOrCreatePhysicalDiskDetails(
        Dictionary<string, Dictionary<string, string>> physical,
        string diskNumber,
        string friendlyName)
    {
        foreach (var item in physical)
        {
            var details = item.Value;
            if (details != null &&
                (string.Equals(GetDictionaryValue(details, "Physical disk number"), diskNumber, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetDictionaryValue(details, "Disk number"), diskNumber, StringComparison.OrdinalIgnoreCase)))
            {
                return details;
            }
        }

        var hardware = NormalizeStorageHardwareName(friendlyName);
        Dictionary<string, string> existing;
        if (!string.IsNullOrWhiteSpace(hardware) && physical.TryGetValue(hardware, out existing) && existing != null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(hardware))
        {
            hardware = "Physical disk " + diskNumber;
        }

        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        physical[hardware] = created;
        return created;
    }

    private static StorageVolumeInventory FindStorageVolume(IEnumerable<StorageVolumeInventory> volumes, string driveLetter, string accessPaths)
    {
        foreach (var volume in volumes ?? Enumerable.Empty<StorageVolumeInventory>())
        {
            if (volume == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(driveLetter) && string.Equals(volume.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase))
            {
                return volume;
            }

            if (!string.IsNullOrWhiteSpace(volume.Path) && !string.IsNullOrWhiteSpace(accessPaths) &&
                accessPaths.IndexOf(volume.Path, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return volume;
            }
        }

        return null;
    }

    private static string DecodePartitionStyle(object value)
    {
        int code;
        if (!int.TryParse(Convert.ToString(value), out code)) return "";
        if (code == 0) return "RAW";
        if (code == 1) return "MBR";
        if (code == 2) return "GPT";
        return "Unknown (" + code.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string DecodeProvisioningType(object value)
    {
        int code;
        if (!int.TryParse(Convert.ToString(value), out code)) return "";
        if (code == 1) return "Thin";
        if (code == 2) return "Fixed";
        return code == 0 ? "Unknown" : "Other (" + code.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string DecodeGptPartitionType(string value)
    {
        value = NormalizeGuidText(value);
        if (string.Equals(value, ApfsGptPartitionType, StringComparison.OrdinalIgnoreCase)) return "Apple File System container";
        if (string.Equals(value, LinuxFileSystemGptPartitionType, StringComparison.OrdinalIgnoreCase)) return "Linux filesystem data";
        if (string.Equals(value, "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", StringComparison.OrdinalIgnoreCase)) return "Microsoft basic data";
        if (string.Equals(value, "C12A7328-F81F-11D2-BA4B-00A0C93EC93B", StringComparison.OrdinalIgnoreCase)) return "EFI system partition";
        if (string.Equals(value, "E3C9E316-0B5C-4DB8-817D-F92DF00215AE", StringComparison.OrdinalIgnoreCase)) return "Microsoft reserved partition";
        if (string.Equals(value, "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC", StringComparison.OrdinalIgnoreCase)) return "Windows recovery environment";
        return string.IsNullOrWhiteSpace(value) ? "" : "Other";
    }

    private static string DecodeFileSystemType(object value)
    {
        int code;
        if (!int.TryParse(Convert.ToString(value), out code)) return "";
        switch (code)
        {
            case 0: return "Unknown";
            case 2: return "UFS";
            case 3: return "HFS";
            case 4: return "FAT";
            case 5: return "FAT16";
            case 6: return "FAT32";
            case 7: return "NTFS4";
            case 8: return "NTFS5";
            case 9: return "XFS";
            case 10: return "AFS";
            case 11: return "EXT2";
            case 12: return "EXT3";
            case 13: return "ReiserFS";
            case 14: return "NTFS";
            case 15: return "ReFS";
            case 16: return "exFAT";
            case 0x8000: return "CSVFS_NTFS";
            case 0x8001: return "CSVFS_ReFS";
            default: return "Windows type " + code.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddNullableYesNoDetail(Dictionary<string, string> details, string name, object value)
    {
        if (value == null)
        {
            return;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AddDetail(details, name, FormatYesNo(value));
    }

    private static string DecodeStorageDriveType(object value)
    {
        int code;
        if (!int.TryParse(Convert.ToString(value), out code)) return "";
        return FormatDriveType((DriveType)code);
    }

    private static string DecodeDedupMode(object value)
    {
        int code;
        if (!int.TryParse(Convert.ToString(value), out code)) return "";
        if (code == 0) return "Not available";
        if (code == 1) return "Disabled";
        if (code == 2) return "General purpose";
        if (code == 3) return "Hyper-V";
        if (code == 4) return "Not supported";
        return "Unknown (" + code.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string DecodeExtState(ushort state)
    {
        var values = new List<string>();
        if ((state & 0x1) != 0) values.Add("Cleanly unmounted");
        if ((state & 0x2) != 0) values.Add("Errors detected");
        if ((state & 0x4) != 0) values.Add("Orphans being recovered");
        return values.Count == 0 ? "Unknown (0x" + state.ToString("X4", CultureInfo.InvariantCulture) + ")" : string.Join(", ", values);
    }

    private static string DecodeExtCreator(uint creator)
    {
        if (creator == 0) return "Linux";
        if (creator == 1) return "Hurd";
        if (creator == 2) return "Masix";
        if (creator == 3) return "FreeBSD";
        if (creator == 4) return "Lites";
        return "Unknown (" + creator.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string FormatExtMaximumMountCount(ushort value)
    {
        return value == ushort.MaxValue ? "Disabled" : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatStorageDifference(object totalValue, object allocatedValue)
    {
        ulong total;
        ulong allocated;
        if (!ulong.TryParse(Convert.ToString(totalValue), out total) || !ulong.TryParse(Convert.ToString(allocatedValue), out allocated))
        {
            return "";
        }

        return FormatStorageBytes(total > allocated ? total - allocated : 0);
    }

    private static double SafeMultiply(ulong left, ulong right)
    {
        return (double)left * (double)right;
    }

    private static string FormatUnixTimeWithAge(uint value)
    {
        if (value == 0)
        {
            return "";
        }

        try
        {
            var date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(value).ToLocalTime();
            return FormatDateTimeWithAge(date, true);
        }
        catch
        {
            return "";
        }
    }

    private static string ReadNullTerminatedText(byte[] data, int offset, int count)
    {
        if (data == null || offset < 0 || count <= 0 || offset + count > data.Length)
        {
            return "";
        }

        var length = 0;
        while (length < count && data[offset + length] != 0) length++;
        return Encoding.UTF8.GetString(data, offset, length).Trim();
    }

    private static string FormatUuid(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + 16 > data.Length)
        {
            return "";
        }

        var builder = new StringBuilder(36);
        for (var index = 0; index < 16; index++)
        {
            if (index == 4 || index == 6 || index == 8 || index == 10) builder.Append('-');
            builder.Append(data[offset + index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return data == null || offset < 0 || offset + 2 > data.Length ? (ushort)0 : BitConverter.ToUInt16(data, offset);
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return data == null || offset < 0 || offset + 4 > data.Length ? 0U : BitConverter.ToUInt32(data, offset);
    }

    private static ulong ReadUInt64(byte[] data, int offset)
    {
        return data == null || offset < 0 || offset + 8 > data.Length ? 0UL : BitConverter.ToUInt64(data, offset);
    }

    private static string CleanDriveLetter(string value)
    {
        value = (value ?? "").Replace("\0", "").Trim().TrimEnd('\\');
        if (value.Length == 1 && char.IsLetter(value[0])) value += ":";
        return value;
    }

    private static string NormalizeGuidText(string value)
    {
        return (value ?? "").Trim().Trim('{', '}').ToUpperInvariant();
    }

    private static bool IsTrue(object value)
    {
        bool parsed;
        return bool.TryParse(Convert.ToString(value), out parsed) && parsed;
    }
}
