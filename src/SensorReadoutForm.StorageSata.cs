using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class SataLinkSummary
    {
        public string DisplayValue;
        public Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtaPassThrough
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public IntPtr DataBufferOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PreviousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] CurrentTaskFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtaPassThroughWithBuffers
    {
        public AtaPassThrough Apt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] DataBuf;
    }

    private static void AddSataConnectionRow(List<SensorRow> rows, string hardware, ManagementObject disk, Dictionary<string, SataLinkSummary> sataLinks)
    {
        if (rows == null || disk == null || string.IsNullOrWhiteSpace(hardware) || !IsWindowsStorageSataDisk(disk))
        {
            return;
        }

        SataLinkSummary link;
        if (sataLinks == null || !sataLinks.TryGetValue(NormalizeStorageHardwareName(hardware), out link))
        {
            link = null;
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (link != null && link.Details != null)
        {
            foreach (var pair in link.Details)
            {
                details[pair.Key] = pair.Value;
            }
        }

        var location = GetWmiPropertyText(disk, "PhysicalLocation");
        AddDetail(details, "Windows Storage physical location", location);
        AddDetail(details, "Windows Storage bus type", FormatStorageBusType(GetWmiPropertyValue(disk, "BusType")));

        var display = link == null ? "" : link.DisplayValue;
        if (string.IsNullOrWhiteSpace(display))
        {
            display = FormatSataPhysicalLocation(location);
        }

        if (string.IsNullOrWhiteSpace(display))
        {
            display = "SATA";
        }

        rows.Add(new SensorRow
        {
            Type = "SMART",
            Hardware = hardware,
            Name = "SATA connection",
            DisplayValue = display,
            Source = link == null ? "Windows Storage" : "Windows ATA identify",
            Details = details
        });
    }

    private static Dictionary<string, SataLinkSummary> GetSataLinkSummariesByHardware()
    {
        var links = new Dictionary<string, SataLinkSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in GetPhysicalStorageDevices())
        {
            if (disk == null || disk.Index < 0 || LooksLikeNvmeDisk(disk) || LooksLikeUsbStorageDisk(disk))
            {
                continue;
            }

            byte[] identifyData;
            if (!TryReadAtaIdentifyData(disk.Index, out identifyData))
            {
                continue;
            }

            var summary = BuildSataLinkSummary(identifyData);
            if (summary == null || string.IsNullOrWhiteSpace(summary.DisplayValue))
            {
                continue;
            }

            links[NormalizeStorageHardwareName(disk.Model)] = summary;
        }

        return links;
    }

    private static SataLinkSummary BuildSataLinkSummary(byte[] identifyData)
    {
        if (identifyData == null || identifyData.Length < 512)
        {
            return null;
        }

        var word76 = ReadUInt16LittleEndian(identifyData, 76 * 2);
        var word77 = ReadUInt16LittleEndian(identifyData, 77 * 2);
        var supportedGeneration = GetHighestSupportedSataGeneration(word76);
        var currentGeneration = (word77 >> 1) & 0x7;
        if (supportedGeneration <= 0 && (currentGeneration < 1 || currentGeneration > 3))
        {
            return null;
        }

        var currentText = FormatSataGeneration(currentGeneration);
        var supportedText = FormatSataGeneration(supportedGeneration);
        var display = "";
        if (!string.IsNullOrWhiteSpace(currentText) && !string.IsNullOrWhiteSpace(supportedText))
        {
            display = currentText + "; maximum " + supportedText;
        }
        else if (!string.IsNullOrWhiteSpace(currentText))
        {
            display = currentText;
        }
        else
        {
            display = "maximum " + supportedText;
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "SATA current generation", FormatSataGenerationName(currentGeneration));
        AddDetail(details, "SATA current link speed", FormatSataGenerationSpeed(currentGeneration));
        AddDetail(details, "SATA maximum generation", FormatSataGenerationName(supportedGeneration));
        AddDetail(details, "SATA maximum link speed", FormatSataGenerationSpeed(supportedGeneration));
        AddDetail(details, "SATA identify word 76", "0x" + word76.ToString("X4", CultureInfo.InvariantCulture));
        AddDetail(details, "SATA identify word 77", "0x" + word77.ToString("X4", CultureInfo.InvariantCulture));
        AddDetail(details, "SATA link source", "Windows ATA IDENTIFY DEVICE data");

        return new SataLinkSummary
        {
            DisplayValue = display,
            Details = details
        };
    }

    private static bool IsWindowsStorageSataDisk(ManagementObject disk)
    {
        var busType = GetWmiPropertyValue(disk, "BusType");
        int value;
        return int.TryParse(Convert.ToString(busType, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value == 11;
    }

    private static string FormatSataPhysicalLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "";
        }

        var port = "";
        var match = Regex.Match(location, @"\bPort\s+(\d+)\b", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            port = "port " + match.Groups[1].Value;
        }

        var prefix = location.Split(':').Select(part => part.Trim()).FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));
        if (!string.IsNullOrWhiteSpace(port) && !string.IsNullOrWhiteSpace(prefix))
        {
            return prefix + ", " + port;
        }

        if (!string.IsNullOrWhiteSpace(port))
        {
            return port;
        }

        return location.Trim();
    }

    private static bool TryReadAtaIdentifyData(int physicalDriveIndex, out byte[] identifyData)
    {
        if (TryReadAtaIdentifyDataWithStorageQuery(physicalDriveIndex, out identifyData))
        {
            return true;
        }

        return TryReadAtaIdentifyDataWithPassThrough(physicalDriveIndex, out identifyData);
    }

    private static bool TryReadAtaIdentifyDataWithStorageQuery(int physicalDriveIndex, out byte[] identifyData)
    {
        identifyData = null;
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint openExisting = 3;
        const uint ioctlStorageQueryProperty = 0x002D1400;
        const uint storageDeviceProtocolSpecificProperty = 49;
        const uint propertyStandardQuery = 0;
        const uint protocolTypeAta = 2;
        const uint ataDataTypeIdentify = 1;
        const int identifyLength = 512;

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
                        ProtocolType = protocolTypeAta,
                        DataType = ataDataTypeIdentify,
                        ProtocolDataOffset = (uint)protocolDataSize,
                        ProtocolDataLength = identifyLength
                    }
                };

                var buffer = new byte[querySize + identifyLength];
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

                if (outputDataOffset + identifyLength > buffer.Length)
                {
                    return false;
                }

                identifyData = new byte[identifyLength];
                Buffer.BlockCopy(buffer, outputDataOffset, identifyData, 0, identifyLength);
                return AtaIdentifyDataHasContent(identifyData);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadAtaIdentifyDataWithPassThrough(int physicalDriveIndex, out byte[] identifyData)
    {
        identifyData = null;
        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint openExisting = 3;
        const uint ioctlAtaPassThrough = 0x0004D02C;
        const ushort ataFlagsDataIn = 0x02;
        const int identifyLength = 512;
        const byte ataIdentifyDevice = 0xEC;

        try
        {
            using (var handle = StorageCreateFile(@"\\.\PhysicalDrive" + physicalDriveIndex, 0, fileShareRead | fileShareWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    return false;
                }

                var request = new AtaPassThroughWithBuffers
                {
                    Apt = new AtaPassThrough
                    {
                        PreviousTaskFile = new byte[8],
                        CurrentTaskFile = new byte[8]
                    },
                    DataBuf = new byte[identifyLength]
                };
                request.Apt.Length = (ushort)Marshal.SizeOf(typeof(AtaPassThrough));
                request.Apt.AtaFlags = ataFlagsDataIn;
                request.Apt.DataTransferLength = identifyLength;
                request.Apt.TimeOutValue = 3;
                request.Apt.DataBufferOffset = Marshal.OffsetOf(typeof(AtaPassThroughWithBuffers), "DataBuf");
                request.Apt.CurrentTaskFile[5] = 0xA0;
                request.Apt.CurrentTaskFile[6] = ataIdentifyDevice;

                var dataOffset = Marshal.OffsetOf(typeof(AtaPassThroughWithBuffers), "DataBuf").ToInt32();
                var length = dataOffset + identifyLength;
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AtaPassThroughWithBuffers)));
                try
                {
                    Marshal.StructureToPtr(request, pointer, false);
                    int returned;
                    if (!StorageDeviceIoControl(handle, ioctlAtaPassThrough, pointer, length, pointer, length, out returned, IntPtr.Zero))
                    {
                        return false;
                    }

                    var response = (AtaPassThroughWithBuffers)Marshal.PtrToStructure(pointer, typeof(AtaPassThroughWithBuffers));
                    if (response.DataBuf == null || response.DataBuf.Length < identifyLength || !AtaIdentifyDataHasContent(response.DataBuf))
                    {
                        return false;
                    }

                    identifyData = new byte[identifyLength];
                    Buffer.BlockCopy(response.DataBuf, 0, identifyData, 0, identifyLength);
                    return true;
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

    private static bool AtaIdentifyDataHasContent(byte[] identifyData)
    {
        if (identifyData == null || identifyData.Length < 512)
        {
            return false;
        }

        var modelWordStart = 27;
        var modelWordCount = 20;
        for (var i = modelWordStart * 2; i < (modelWordStart + modelWordCount) * 2 && i < identifyData.Length; i++)
        {
            if (identifyData[i] != 0 && identifyData[i] != 0x20)
            {
                return true;
            }
        }

        return GetHighestSupportedSataGeneration(ReadUInt16LittleEndian(identifyData, 76 * 2)) > 0;
    }

    private static int GetHighestSupportedSataGeneration(int word76)
    {
        if ((word76 & (1 << 3)) != 0)
        {
            return 3;
        }

        if ((word76 & (1 << 2)) != 0)
        {
            return 2;
        }

        if ((word76 & (1 << 1)) != 0)
        {
            return 1;
        }

        return 0;
    }

    private static string FormatSataGeneration(int generation)
    {
        var name = FormatSataGenerationName(generation);
        var speed = FormatSataGenerationSpeed(generation);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(speed) ? name : name + " / " + speed;
    }

    private static string FormatSataGenerationName(int generation)
    {
        switch (generation)
        {
            case 1:
                return "SATA I";
            case 2:
                return "SATA II";
            case 3:
                return "SATA III";
            default:
                return "";
        }
    }

    private static string FormatSataGenerationSpeed(int generation)
    {
        switch (generation)
        {
            case 1:
                return "1.5 Gb/s";
            case 2:
                return "3.0 Gb/s";
            case 3:
                return "6.0 Gb/s";
            default:
                return "";
        }
    }

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        if (data == null || offset < 0 || offset + 2 > data.Length)
        {
            return 0;
        }

        return BitConverter.ToUInt16(data, offset);
    }
}
