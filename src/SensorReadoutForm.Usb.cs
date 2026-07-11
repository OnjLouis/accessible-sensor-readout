using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed partial class SensorReadoutForm : Form
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int InvalidHandleValue = -1;
    private static readonly Guid GuidDevInterfaceUsbHub = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    private IEnumerable<SensorRow> GetUsbRowsWithDiagnostics()
    {
        var diagnostics = new UsbDiagnosticSnapshot();
        var rows = GetUsbRows(diagnostics).ToList();
        lastUsbDiagnosticSnapshot = diagnostics;
        if (diagnostics.Lines.Count > 0)
        {
            LogMessage("Debug", "USB diagnostics: hubs=" + diagnostics.HubCount + ", ports=" + diagnostics.PortCount + ", matched rows=" + diagnostics.PortMatchCount + ".");
            foreach (var line in diagnostics.Lines.Take(80))
            {
                LogMessage("Debug", "USB " + line);
            }
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetUsbRows(UsbDiagnosticSnapshot diagnostics)
    {
        var rows = new List<SensorRow>();
        try
        {
            var portDetails = GetUsbPortDetailsByDriverKey(diagnostics);
            var portDetailList = portDetails.Values.Distinct().ToList();
            var physicalUsbDevices = GetPhysicalUsbDeviceIndex();
            var driveLetters = GetUsbDriveLettersByPnpDeviceId();
            var networkAdapters = GetUsbNetworkAdapters();
            var storageIdentities = GetUsbStorageIdentitiesByPnpDeviceId();
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Description, Manufacturer, PNPClass, Status, Service, DeviceID, PNPDeviceID FROM Win32_PnPEntity"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = Convert.ToString(device["PNPDeviceID"]);
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        deviceId = Convert.ToString(device["DeviceID"]);
                    }

                    if (!IsUsbDeviceId(deviceId))
                    {
                        continue;
                    }

                    var name = FirstNonEmpty(Convert.ToString(device["Name"]), Convert.ToString(device["Description"]), "USB device");
                    var manufacturer = CleanUsbManufacturer(Convert.ToString(device["Manufacturer"]));
                    var deviceType = FriendlyUsbType(Convert.ToString(device["PNPClass"]), Convert.ToString(device["Service"]), name);
                    var status = Convert.ToString(device["Status"]);
                    var service = Convert.ToString(device["Service"]);
                    var registry = ReadUsbRegistryDetails(deviceId);
                    var driverKey = GetDictionaryValue(registry, "Driver");
                    var vidPid = ExtractUsbVidPid(deviceId);
                    var serial = ExtractUsbSerial(deviceId);
                    var physical = FindPhysicalUsbDevice(physicalUsbDevices, deviceId, vidPid, serial, GetDictionaryValue(registry, "ContainerID"));
                    var physicalRegistry = physical == null ? null : physical.Registry;
                    var physicalDriverKey = physical == null ? "" : GetDictionaryValue(physicalRegistry, "Driver");
                    var physicalVidPid = physical == null ? "" : physical.VidPid;
                    var physicalSerial = physical == null ? "" : physical.Serial;
                    var effectiveVidPid = FirstNonEmpty(vidPid, physicalVidPid);
                    var usbId = UsbIdDatabase.Lookup(effectiveVidPid);
                    var port = FindUsbPortDetails(
                        portDetails,
                        portDetailList,
                        FirstNonEmpty(driverKey, physicalDriverKey),
                        effectiveVidPid,
                        FirstNonEmpty(serial, physicalSerial));
                    if (port != null && diagnostics != null)
                    {
                        diagnostics.PortMatchCount++;
                        diagnostics.Lines.Add("match row=" + name + "; deviceId=" + deviceId + "; driver=" + FirstNonEmpty(driverKey, physicalDriverKey) + "; port=" + port.Port + "; speed=" + port.Speed + "; power=" + port.Power + "; driverKey=" + port.DriverKey + "; vidpid=" + port.VidPid);
                    }
                    var speed = FormatUsbDisplaySpeed(port, GuessUsbSpeed(name, deviceId, registry));
                    var power = FirstNonEmpty(port == null ? "" : port.Power, GetDictionaryValue(registry, "Power"), GetDictionaryValue(registry, "PowerData"));
                    var safeToUnplug = DecodeUsbSafeRemoval(GetDictionaryValue(registry, "Capabilities"), deviceType, service);
                    var location = FirstNonEmpty(port == null ? "" : port.Location, GetDictionaryValue(registry, "LocationInformation"), GetDictionaryValue(registry, "LocationPaths"));
                    var containerId = GetDictionaryValue(registry, "ContainerID");
                    var letters = FindUsbDriveLetters(deviceId, driveLetters);
                    var networkAdapter = FindUsbNetworkAdapter(networkAdapters, deviceId, effectiveVidPid, FirstNonEmpty(serial, physicalSerial));
                    var storageIdentity = FindUsbStorageIdentity(deviceId, storageIdentities);

                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Name", name);
                    AddDetail(details, "Type", deviceType);
                    AddDetail(details, "Vendor", manufacturer);
                    AddDetail(details, "Connected", IsUsbConnectedStatus(status) ? "Yes" : status);
                    AddDetail(details, "Speed", speed);
                    AddDetail(details, "Capable speed", port == null ? "" : port.CapableSpeed);
                    AddDetail(details, "Speed source", port == null ? "" : "Windows USB hub connection data");
                    AddDetail(details, "Speed note", BuildUsbSpeedNote(port));
                    AddDetail(details, "Power", power);
                    AddDetail(details, "Safe to unplug", safeToUnplug);
                    AddDetail(details, "Drive letters", letters);
                    AddDetail(details, "Network adapter", networkAdapter == null ? "" : networkAdapter.Name);
                    AddDetail(details, "MAC address", networkAdapter == null ? "" : networkAdapter.MacAddress);
                    AddDetail(details, "MAC vendor", networkAdapter == null ? "" : networkAdapter.MacVendor);
                    AddDetail(details, "Storage hardware ID", storageIdentity == null ? "" : storageIdentity.UniqueId);
                    AddDetail(details, "Storage hardware ID format", storageIdentity == null ? "" : storageIdentity.UniqueIdFormat);
                    AddDetail(details, "Storage OUI vendor", storageIdentity == null ? "" : storageIdentity.OuiVendor);
                    AddDetail(details, "VID/PID", effectiveVidPid);
                    AddDetail(details, "USB ID vendor", usbId == null ? "" : usbId.VendorName);
                    AddDetail(details, "USB ID product", usbId == null ? "" : usbId.ProductName);
                    AddDetail(details, "Port", port == null ? "" : port.Port);
                    AddDetail(details, "Device address", port == null ? "" : port.DeviceAddress);
                    AddDetail(details, "USB version", port == null ? "" : port.UsbVersion);
                    AddDetail(details, "Supported USB protocols", port == null ? "" : port.SupportedUsbProtocols);
                    AddDetail(details, "USB speed flags", port == null ? "" : port.V2Flags);
                    AddDetail(details, "SuperSpeedPlus lanes", port == null ? "" : port.SuperSpeedPlusLanes);
                    AddDetail(details, "Connection status", port == null ? "" : port.ConnectionStatus);
                    AddDetail(details, "Service", service);
                    AddDetail(details, "Location", location);
                    AddDetail(details, "Container ID", containerId);
                    AddDetail(details, "Driver key", driverKey);
                    AddDetail(details, "Network PNP device", networkAdapter == null ? "" : networkAdapter.PnpDeviceId);
                    AddDetail(details, "Physical USB device", physical == null ? "" : physical.DeviceId);
                    AddDetail(details, "Device ID", deviceId);

                    var summaryVendor = FirstNonEmpty(NonGenericUsbVendor(manufacturer), usbId == null ? "" : usbId.VendorName);
                    var summary = BuildUsbSummary(
                        name,
                        deviceType,
                        summaryVendor,
                        speed,
                        power,
                        safeToUnplug,
                        letters,
                        status,
                        networkAdapter == null ? "" : networkAdapter.MacAddress,
                        networkAdapter == null ? "" : networkAdapter.MacVendor);
                    rows.Add(new SensorRow
                    {
                        Type = "USB",
                        Hardware = name,
                        Name = "Device",
                        Identifier = deviceId,
                        DisplayValue = summary,
                        Source = "Windows USB",
                        Details = details
                    });
                }
            }
        }
        catch
        {
        }

        return rows
            .GroupBy(r => r.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => IsUsbHubOrControllerName(r.Hardware, r.DisplayValue) ? 1 : 0)
            .ThenBy(r => r.Hardware)
            .ToList();
    }

    private sealed class UsbPortDetails
    {
        public string DriverKey;
        public string HubPath;
        public int PortIndex;
        public string Port;
        public string Speed;
        public string Power;
        public string Location;
        public string DeviceAddress;
        public string UsbVersion;
        public string CapableSpeed;
        public string SupportedUsbProtocols;
        public string V2Flags;
        public string SuperSpeedPlusLanes;
        public string ConnectionStatus;
        public string VidPid;
        public string VendorProductId;
    }

    private sealed class PhysicalUsbDevice
    {
        public string DeviceId;
        public string VidPid;
        public string Serial;
        public string ContainerId;
        public Dictionary<string, string> Registry;
    }

    private sealed class UsbNetworkAdapterInfo
    {
        public string Name;
        public string PnpDeviceId;
        public string VidPid;
        public string Serial;
        public string MacAddress;
        public string MacVendor;
    }

    private sealed class UsbStorageIdentity
    {
        public string PnpDeviceId;
        public string UniqueId;
        public string UniqueIdFormat;
        public string OuiVendor;
    }

    private static List<PhysicalUsbDevice> GetPhysicalUsbDeviceIndex()
    {
        var devices = new List<PhysicalUsbDevice>();
        AddPhysicalUsbDevices(devices, "USB");
        AddPhysicalUsbDevices(devices, "USB_ASMEDIA");
        return devices;
    }

    private static void AddPhysicalUsbDevices(List<PhysicalUsbDevice> devices, string enumRoot)
    {
        try
        {
            using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + enumRoot))
            {
                if (root == null)
                {
                    return;
                }

                foreach (var hardwareId in root.GetSubKeyNames())
                {
                    if (hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0 ||
                        hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    using (var hardwareKey = root.OpenSubKey(hardwareId))
                    {
                        if (hardwareKey == null)
                        {
                            continue;
                        }

                        foreach (var instance in hardwareKey.GetSubKeyNames())
                        {
                            var deviceId = enumRoot + "\\" + hardwareId + "\\" + instance;
                            var registry = ReadUsbRegistryDetails(deviceId);
                            devices.Add(new PhysicalUsbDevice
                            {
                                DeviceId = deviceId,
                                VidPid = ExtractUsbVidPid(deviceId),
                                Serial = ExtractUsbSerial(deviceId),
                                ContainerId = GetDictionaryValue(registry, "ContainerID"),
                                Registry = registry
                            });
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static PhysicalUsbDevice FindPhysicalUsbDevice(List<PhysicalUsbDevice> devices, string deviceId, string vidPid, string serial, string containerId)
    {
        if (devices == null || devices.Count == 0)
        {
            return null;
        }

        var exact = devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        if (!string.IsNullOrWhiteSpace(containerId))
        {
            var containerMatches = devices.Where(d => string.Equals(d.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (containerMatches.Count == 1)
            {
                return containerMatches[0];
            }

            if (!string.IsNullOrWhiteSpace(vidPid))
            {
                var containerVidPidMatches = containerMatches.Where(d => string.Equals(d.VidPid, vidPid, StringComparison.OrdinalIgnoreCase)).ToList();
                if (containerVidPidMatches.Count == 1)
                {
                    return containerVidPidMatches[0];
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var serialMatches = devices.Where(d =>
                string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(d.Serial) &&
                (d.Serial.IndexOf(serial, StringComparison.OrdinalIgnoreCase) >= 0 ||
                serial.IndexOf(d.Serial, StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
            if (serialMatches.Count == 1)
            {
                return serialMatches[0];
            }

            if (!string.IsNullOrWhiteSpace(vidPid))
            {
                var serialVidPidMatches = serialMatches.Where(d => string.Equals(d.VidPid, vidPid, StringComparison.OrdinalIgnoreCase)).ToList();
                if (serialVidPidMatches.Count == 1)
                {
                    return serialVidPidMatches[0];
                }
            }
        }

        return null;
    }

    private static UsbPortDetails FindUsbPortDetails(Dictionary<string, UsbPortDetails> byDriverKey, List<UsbPortDetails> allPorts, string driverKey, string vidPid, string serial)
    {
        UsbPortDetails port;
        if (!string.IsNullOrWhiteSpace(driverKey) && byDriverKey.TryGetValue(driverKey, out port))
        {
            return port;
        }

        if (string.IsNullOrWhiteSpace(vidPid) || allPorts == null)
        {
            return null;
        }

        var matches = allPorts
            .Where(p => string.Equals(p.VidPid, vidPid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var serialMatches = matches
                .Where(p => !string.IsNullOrWhiteSpace(p.DriverKey) && p.DriverKey.IndexOf(serial, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (serialMatches.Count == 1)
            {
                return serialMatches[0];
            }
        }

        return null;
    }

    private static Dictionary<string, UsbPortDetails> GetUsbPortDetailsByDriverKey(UsbDiagnosticSnapshot diagnostics)
    {
        var details = new Dictionary<string, UsbPortDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var hubPath in EnumerateUsbHubPaths())
        {
            if (diagnostics != null)
            {
                diagnostics.HubCount++;
                diagnostics.Lines.Add("hub " + hubPath);
            }

            var handle = CreateFile(hubPath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == new IntPtr(InvalidHandleValue))
            {
                handle = CreateFile(hubPath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            }

            if (handle == new IntPtr(InvalidHandleValue))
            {
                continue;
            }

            try
            {
                var portCount = GetUsbHubPortCount(handle);
                for (var port = 1; port <= portCount; port++)
                {
                    var connection = GetUsbConnectionInfo(handle, port);
                    if (diagnostics != null)
                    {
                        diagnostics.PortCount++;
                    }
                    if (!connection.HasValue || connection.Value.ConnectionStatus == 0)
                    {
                        if (diagnostics != null)
                        {
                            diagnostics.Lines.Add("port hub=" + ShortUsbHubPath(hubPath) + "; port=" + port + "; no connected device/status=" + (connection.HasValue ? connection.Value.ConnectionStatus.ToString() : "no data"));
                        }
                        continue;
                    }

                    var driverKey = GetUsbConnectionDriverKey(handle, port);
                    if (string.IsNullOrWhiteSpace(driverKey))
                    {
                        continue;
                    }

                    var connectionV2 = GetUsbConnectionV2Info(handle, port);
                    var sspInfo = GetUsbSuperSpeedPlusInfo(handle, port);
                    var power = GetUsbConnectionPower(handle, port, connection.Value.DeviceDescriptor.BcdUSB);
                    var vidPid = "VID " + connection.Value.DeviceDescriptor.IdVendor.ToString("X4") + ", PID " + connection.Value.DeviceDescriptor.IdProduct.ToString("X4");
                    var connectionV2Flags = connectionV2.HasValue ? connectionV2.Value.Flags : 0;
                    var supportedProtocols = connectionV2.HasValue ? connectionV2.Value.SupportedUsbProtocols : 0;
                    var sspLaneText = FormatSuperSpeedPlusLanes(sspInfo);
                    var speedText = FormatUsbSpeed(connection.Value.Speed, connectionV2Flags, sspInfo);
                    var capableSpeed = FormatUsbCapableSpeed(connectionV2Flags, sspInfo);
                    if (diagnostics != null)
                    {
                        diagnostics.Lines.Add("port hub=" + ShortUsbHubPath(hubPath) + "; port=" + port + "; status=" + UsbConnectionStatusName(connection.Value.ConnectionStatus) + "; speedCode=" + connection.Value.Speed + "; protocols=" + FormatUsbProtocols(supportedProtocols) + "; v2Flags=" + FormatUsbV2Flags(connectionV2Flags) + "; ssp=" + sspLaneText + "; speed=" + speedText + "; capable=" + capableSpeed + "; power=" + power + "; usb=" + FormatUsbBcd(connection.Value.DeviceDescriptor.BcdUSB) + "; driver=" + driverKey + "; vidpid=" + vidPid);
                    }
                    details[driverKey] = new UsbPortDetails
                    {
                        DriverKey = driverKey,
                        HubPath = hubPath,
                        PortIndex = port,
                        Port = "Port " + port,
                        Speed = speedText,
                        Power = power,
                        Location = "Hub " + ShortUsbHubPath(hubPath) + ", port " + port,
                        DeviceAddress = connection.Value.DeviceAddress == 0 ? "" : connection.Value.DeviceAddress.ToString(),
                        UsbVersion = FormatUsbBcd(connection.Value.DeviceDescriptor.BcdUSB),
                        CapableSpeed = capableSpeed,
                        SupportedUsbProtocols = FormatUsbProtocols(supportedProtocols),
                        V2Flags = FormatUsbV2Flags(connectionV2Flags),
                        SuperSpeedPlusLanes = sspLaneText,
                        ConnectionStatus = UsbConnectionStatusName(connection.Value.ConnectionStatus),
                        VidPid = vidPid,
                        VendorProductId = connection.Value.DeviceDescriptor.IdVendor.ToString("X4") + ":" + connection.Value.DeviceDescriptor.IdProduct.ToString("X4")
                    };
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return details;
    }

    private static IEnumerable<string> EnumerateUsbHubPaths()
    {
        var paths = new List<string>();
        var hubGuid = GuidDevInterfaceUsbHub;
        var deviceInfo = SetupDiGetClassDevs(ref hubGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (deviceInfo == new IntPtr(InvalidHandleValue))
        {
            return paths;
        }

        try
        {
            var index = 0u;
            while (true)
            {
                var interfaceData = new SpDeviceInterfaceData();
                interfaceData.CbSize = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
                if (!SetupDiEnumDeviceInterfaces(deviceInfo, IntPtr.Zero, ref hubGuid, index, ref interfaceData))
                {
                    break;
                }

                int requiredSize;
                SetupDiGetDeviceInterfaceDetail(deviceInfo, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                if (requiredSize <= 0)
                {
                    index++;
                    continue;
                }

                var buffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(deviceInfo, ref interfaceData, buffer, requiredSize, out requiredSize, IntPtr.Zero))
                    {
                        var pathPointer = new IntPtr(buffer.ToInt64() + 4);
                        paths.Add(Marshal.PtrToStringAuto(pathPointer));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                index++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfo);
        }

        return paths;
    }

    private static int GetUsbHubPortCount(IntPtr hubHandle)
    {
        var buffer = new byte[80];
        uint returned;
        if (!DeviceIoControl(hubHandle, UsbIoctl(258), buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero) || returned < 7)
        {
            return 0;
        }

        return buffer[6];
    }

    private static UsbNodeConnectionInformationEx? GetUsbConnectionInfo(IntPtr hubHandle, int port)
    {
        var info = new UsbNodeConnectionInformationEx();
        info.ConnectionIndex = (uint)port;
        uint returned;
        return DeviceIoControl(hubHandle, UsbIoctl(274), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionInformationEx)), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionInformationEx)), out returned, IntPtr.Zero)
            ? (UsbNodeConnectionInformationEx?)info
            : null;
    }

    private static UsbNodeConnectionInformationExV2? GetUsbConnectionV2Info(IntPtr hubHandle, int port)
    {
        var info = new UsbNodeConnectionInformationExV2
        {
            ConnectionIndex = (uint)port,
            Length = (uint)Marshal.SizeOf(typeof(UsbNodeConnectionInformationExV2)),
            SupportedUsbProtocols = 7
        };
        uint returned;
        return DeviceIoControl(hubHandle, UsbIoctl(279), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionInformationExV2)), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionInformationExV2)), out returned, IntPtr.Zero)
            ? (UsbNodeConnectionInformationExV2?)info
            : null;
    }

    private static UsbNodeConnectionSuperSpeedPlusInformation? GetUsbSuperSpeedPlusInfo(IntPtr hubHandle, int port)
    {
        var info = new UsbNodeConnectionSuperSpeedPlusInformation
        {
            ConnectionIndex = (uint)port,
            Length = (uint)Marshal.SizeOf(typeof(UsbNodeConnectionSuperSpeedPlusInformation))
        };
        uint returned;
        return DeviceIoControl(hubHandle, UsbIoctl(289), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionSuperSpeedPlusInformation)), ref info, Marshal.SizeOf(typeof(UsbNodeConnectionSuperSpeedPlusInformation)), out returned, IntPtr.Zero)
            ? (UsbNodeConnectionSuperSpeedPlusInformation?)info
            : null;
    }

    private static string GetUsbConnectionDriverKey(IntPtr hubHandle, int port)
    {
        var buffer = new byte[4096];
        BitConverter.GetBytes((uint)port).CopyTo(buffer, 0);
        uint returned;
        if (!DeviceIoControl(hubHandle, UsbIoctl(264), buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero) || returned <= 8)
        {
            return "";
        }

        var actualLength = BitConverter.ToInt32(buffer, 4);
        var stringBytes = Math.Max(0, Math.Min(actualLength - 8, buffer.Length - 8));
        return stringBytes <= 0 ? "" : System.Text.Encoding.Unicode.GetString(buffer, 8, stringBytes).TrimEnd('\0').Trim();
    }

    private static string GetUsbConnectionPower(IntPtr hubHandle, int port, ushort bcdUsb)
    {
        var config = GetUsbConfigurationDescriptor(hubHandle, port, 9);
        if (config == null || config.Length < 9)
        {
            return "";
        }

        var totalLength = config[2] | (config[3] << 8);
        if (totalLength > config.Length)
        {
            config = GetUsbConfigurationDescriptor(hubHandle, port, totalLength);
        }

        if (config == null || config.Length < 9)
        {
            return "";
        }

        var maxPower = config[8];
        if (maxPower <= 0)
        {
            return "";
        }

        var multiplier = bcdUsb >= 0x0300 ? 8 : 2;
        return (maxPower * multiplier) + " mA";
    }

    private static byte[] GetUsbConfigurationDescriptor(IntPtr hubHandle, int port, int descriptorLength)
    {
        if (descriptorLength < 9)
        {
            descriptorLength = 9;
        }
        if (descriptorLength > 4096)
        {
            descriptorLength = 4096;
        }

        var buffer = new byte[12 + descriptorLength];
        BitConverter.GetBytes((uint)port).CopyTo(buffer, 0);
        buffer[4] = 0x80;
        buffer[5] = 0x06;
        BitConverter.GetBytes((ushort)0x0200).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)descriptorLength).CopyTo(buffer, 10);
        uint returned;
        if (!DeviceIoControl(hubHandle, UsbIoctl(260), buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero) || returned <= 12)
        {
            return null;
        }

        var actual = Math.Min(descriptorLength, (int)returned - 12);
        var descriptor = new byte[actual];
        Buffer.BlockCopy(buffer, 12, descriptor, 0, actual);
        return descriptor;
    }

    private static uint UsbIoctl(uint function)
    {
        return (0x22u << 16) | (function << 2);
    }

    private static bool IsUsbDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        return deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith("USB_ASMEDIA\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatUsbSpeed(byte speed, uint v2Flags, UsbNodeConnectionSuperSpeedPlusInformation? sspInfo)
    {
        var laneText = FormatSuperSpeedPlusLanes(sspInfo);
        if (!string.IsNullOrWhiteSpace(laneText))
        {
            return "USB 3.2 " + laneText;
        }

        if ((v2Flags & 4) != 0)
        {
            return "USB 3.2 Gen 2 / 10 Gbps";
        }

        if ((v2Flags & 1) != 0)
        {
            return "USB 3.2 Gen 1 / 5 Gbps";
        }

        if (speed == 3)
        {
            return "USB 2.0 High Speed / 480 Mbps";
        }

        if (speed == 2)
        {
            return "USB 1.1 Full Speed / 12 Mbps";
        }

        if (speed == 1)
        {
            return "USB 1.1 Low Speed / 1.5 Mbps";
        }

        return "";
    }

    private static string FormatUsbCapableSpeed(uint v2Flags, UsbNodeConnectionSuperSpeedPlusInformation? sspInfo)
    {
        if ((v2Flags & 8) != 0)
        {
            var laneText = FormatSuperSpeedPlusLanes(sspInfo);
            return string.IsNullOrWhiteSpace(laneText)
                ? "USB 3.2 Gen 2 / 10 Gbps"
                : "USB 3.2 " + laneText;
        }

        if ((v2Flags & 2) != 0)
        {
            return "USB 3.2 Gen 1 / 5 Gbps";
        }

        return "";
    }

    private static string FormatUsbDisplaySpeed(UsbPortDetails port, string fallback)
    {
        if (port == null)
        {
            return fallback;
        }

        var connected = FirstNonEmpty(port.Speed, fallback);
        var display = FormatUsbConnectedSpeedForDisplay(connected, port.UsbVersion);
        if (!string.IsNullOrWhiteSpace(port.CapableSpeed) &&
            !string.Equals(connected, port.CapableSpeed, StringComparison.OrdinalIgnoreCase))
        {
            display += " (capable of " + port.CapableSpeed + ")";
        }

        return display;
    }

    private static string BuildUsbSpeedNote(UsbPortDetails port)
    {
        if (port == null ||
            string.IsNullOrWhiteSpace(port.Speed) ||
            string.IsNullOrWhiteSpace(port.CapableSpeed) ||
            string.Equals(port.Speed, port.CapableSpeed, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return "Windows reports the active USB link speed separately from the capable speed. Sensor Readout does not override the active speed unless Windows reports SuperSpeedPlus operating mode or lane data.";
    }

    private static string FormatUsbConnectedSpeedForDisplay(string connected, string usbVersion)
    {
        if (string.IsNullOrWhiteSpace(connected))
        {
            return "";
        }

        if (connected.StartsWith("USB 3.", StringComparison.OrdinalIgnoreCase))
        {
            return connected;
        }

        var plainSpeed = connected;
        if (plainSpeed.StartsWith("USB 2.0 ", StringComparison.OrdinalIgnoreCase))
        {
            plainSpeed = plainSpeed.Substring("USB 2.0 ".Length);
        }
        else if (plainSpeed.StartsWith("USB 1.1 ", StringComparison.OrdinalIgnoreCase))
        {
            plainSpeed = plainSpeed.Substring("USB 1.1 ".Length);
        }

        if (!string.IsNullOrWhiteSpace(usbVersion) && !usbVersion.Equals("1.1", StringComparison.OrdinalIgnoreCase))
        {
            return "USB " + usbVersion + " device, connected at " + plainSpeed;
        }

        return connected;
    }

    private static string FormatSuperSpeedPlusLanes(UsbNodeConnectionSuperSpeedPlusInformation? info)
    {
        if (!info.HasValue)
        {
            return "";
        }

        var rxRate = DecodeSuperSpeedPlusBitsPerSecond(info.Value.RxSuperSpeedPlus.Speed);
        var txRate = DecodeSuperSpeedPlusBitsPerSecond(info.Value.TxSuperSpeedPlus.Speed);
        var rate = Math.Max(rxRate, txRate);
        if (rate <= 0)
        {
            return "";
        }

        var lanes = Math.Max(info.Value.RxLaneCount, info.Value.TxLaneCount) + 1;
        var totalRate = rate * Math.Max(1, lanes);
        if (totalRate >= 19000000000.0)
        {
            return "Gen 2x2 / 20 Gbps";
        }
        if (totalRate >= 9000000000.0)
        {
            return "Gen 2 / 10 Gbps";
        }
        if (totalRate >= 4500000000.0)
        {
            return "Gen 1 / 5 Gbps";
        }

        return FormatBitsPerSecondText(totalRate);
    }

    private static double DecodeSuperSpeedPlusBitsPerSecond(uint raw)
    {
        var exponent = (raw >> 4) & 0x3;
        var mantissa = (raw >> 16) & 0xffff;
        if (mantissa == 0)
        {
            return 0;
        }

        var multiplier = 1.0;
        for (var i = 0; i < exponent; i++)
        {
            multiplier *= 1000.0;
        }

        return mantissa * multiplier;
    }

    private static string FormatBitsPerSecondText(double bitsPerSecond)
    {
        if (bitsPerSecond >= 1000000000.0)
        {
            return FormatNumber(Math.Round(bitsPerSecond / 1000000000.0, 1), "0.#") + " Gbps";
        }
        if (bitsPerSecond >= 1000000.0)
        {
            return FormatNumber(Math.Round(bitsPerSecond / 1000000.0, 1), "0.#") + " Mbps";
        }

        return FormatNumber(Math.Round(bitsPerSecond, 0), "0") + " bps";
    }

    private static string FormatUsbProtocols(uint protocols)
    {
        var parts = new List<string>();
        if ((protocols & 1) != 0) parts.Add("USB 1.1");
        if ((protocols & 2) != 0) parts.Add("USB 2.0");
        if ((protocols & 4) != 0) parts.Add("USB 3.x");
        return parts.Count == 0 ? "" : string.Join(", ", parts.ToArray());
    }

    private static string FormatUsbV2Flags(uint flags)
    {
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add("operating SuperSpeed or higher");
        if ((flags & 2) != 0) parts.Add("SuperSpeed capable");
        if ((flags & 4) != 0) parts.Add("operating SuperSpeedPlus or higher");
        if ((flags & 8) != 0) parts.Add("SuperSpeedPlus capable");
        return parts.Count == 0 ? "" : string.Join(", ", parts.ToArray());
    }

    private static string UsbConnectionStatusName(uint status)
    {
        switch (status)
        {
            case 0: return "No device connected";
            case 1: return "Connected";
            case 2: return "Failed enumeration";
            case 3: return "General failure";
            case 4: return "Overcurrent";
            case 5: return "Not enough power";
            case 6: return "Not enough bandwidth";
            case 7: return "Hub nested too deeply";
            case 8: return "In legacy hub";
            case 9: return "Enumerating";
            case 10: return "Reset";
            default: return "Status " + status;
        }
    }

    private static string FormatUsbBcd(ushort bcd)
    {
        if (bcd == 0)
        {
            return "";
        }

        var major = (bcd >> 8) & 0xff;
        var minor = (bcd >> 4) & 0x0f;
        var sub = bcd & 0x0f;
        return sub == 0 ? major + "." + minor : major + "." + minor + sub;
    }

    private static string ShortUsbHubPath(string hubPath)
    {
        if (string.IsNullOrWhiteSpace(hubPath))
        {
            return "USB hub";
        }

        var text = hubPath.Replace("\\\\?\\", "").Replace("#", "\\");
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string CleanUsbManufacturer(string manufacturer)
    {
        manufacturer = FirstNonEmpty(manufacturer);
        if (manufacturer.StartsWith("(", StringComparison.Ordinal) && manufacturer.EndsWith(")", StringComparison.Ordinal))
        {
            manufacturer = manufacturer.Substring(1, manufacturer.Length - 2).Trim();
        }

        return manufacturer;
    }

    private static string FriendlyUsbType(string pnpClass, string service, string name)
    {
        var value = FirstNonEmpty(pnpClass, service, name);
        if (value.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (value.Equals("HIDClass", StringComparison.OrdinalIgnoreCase)) return "Input";
        if (value.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)) return "Storage";
        if (value.Equals("USB", StringComparison.OrdinalIgnoreCase) && name.IndexOf("hub", StringComparison.OrdinalIgnoreCase) >= 0) return "Hub";
        if (value.Equals("USB", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (value.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        return string.IsNullOrWhiteSpace(value) ? "USB" : value;
    }

    private static Dictionary<string, string> ReadUsbRegistryDetails(string deviceId)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return details;
        }

        try
        {
            var path = @"SYSTEM\CurrentControlSet\Enum\" + deviceId.Replace('/', '\\');
            using (var key = Registry.LocalMachine.OpenSubKey(path))
            {
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        AddDetail(details, name, RegistryValueToString(key.GetValue(name)));
                    }
                }

                using (var parameters = key == null ? null : key.OpenSubKey("Device Parameters"))
                {
                    if (parameters != null)
                    {
                        foreach (var name in parameters.GetValueNames())
                        {
                            AddDetail(details, name, RegistryValueToString(parameters.GetValue(name)));
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return details;
    }

    private static void AddDetail(Dictionary<string, string> details, string name, string value)
    {
        if (details == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        details[name.Trim()] = value.Trim();
    }

    private static string GetDictionaryValue(Dictionary<string, string> details, string key)
    {
        string value;
        return details != null && details.TryGetValue(key, out value) ? value : "";
    }

    private static string DecodeUsbSafeRemoval(string capabilitiesText, string deviceType, string service)
    {
        int capabilities;
        if (int.TryParse(capabilitiesText, out capabilities))
        {
            if ((capabilities & 4) != 0)
            {
                return "Yes";
            }
        }

        if (deviceType.Equals("Storage", StringComparison.OrdinalIgnoreCase) || service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase))
        {
            return "Yes";
        }

        if (deviceType.Equals("Hub", StringComparison.OrdinalIgnoreCase))
        {
            return "No";
        }

        return "";
    }

    private static bool IsUsbConnectedStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) || status.Equals("OK", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractUsbVidPid(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "";
        }

        var match = Regex.Match(deviceId, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return match.Success ? "VID " + match.Groups[1].Value.ToUpperInvariant() + ", PID " + match.Groups[2].Value.ToUpperInvariant() : "";
    }

    private static string ExtractUsbSerial(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "";
        }

        var parts = deviceId.Split('\\');
        if (parts.Length < 3)
        {
            return "";
        }

        var serial = parts[parts.Length - 1];
        var amp = serial.IndexOf('&');
        return amp > 0 ? serial.Substring(0, amp) : serial;
    }

    private static string GuessUsbSpeed(string name, string deviceId, Dictionary<string, string> registry)
    {
        var text = (name + " " + deviceId + " " + GetDictionaryValue(registry, "LocationInformation") + " " + GetDictionaryValue(registry, "DeviceDesc")).ToLowerInvariant();
        if (text.IndexOf("gen 2x2") >= 0 || text.IndexOf("20gbps") >= 0 || text.IndexOf("20 gbps") >= 0)
        {
            return "USB 3.2 Gen 2x2 / 20 Gbps";
        }
        if (text.IndexOf("superspeedplus") >= 0 || text.IndexOf("gen 2") >= 0 || text.IndexOf("10gbps") >= 0 || text.IndexOf("10 gbps") >= 0)
        {
            return "USB 3.2 Gen 2 / 10 Gbps";
        }
        if (text.IndexOf("superspeed") >= 0 || text.IndexOf("usb 3") >= 0 || text.IndexOf("3.1") >= 0 || text.IndexOf("3.2") >= 0)
        {
            return "USB 3.2 Gen 1 / 5 Gbps";
        }
        if (text.IndexOf("high speed") >= 0 || text.IndexOf("usb 2") >= 0)
        {
            return "USB 2.0 High Speed / 480 Mbps";
        }
        if (text.IndexOf("full speed") >= 0)
        {
            return "USB 1.1 Full Speed / 12 Mbps";
        }
        if (text.IndexOf("low speed") >= 0)
        {
            return "USB 1.1 Low Speed / 1.5 Mbps";
        }

        return "";
    }

    private static string BuildUsbSummary(string name, string deviceType, string manufacturer, string speed, string power, string safeToUnplug, string driveLetters, string status, string macAddress, string macVendor)
    {
        var parts = new List<string>();
        if (!IsGenericUsbType(deviceType))
        {
            AddSummaryPart(parts, deviceType);
        }
        if (!IsRedundantUsbManufacturer(name, manufacturer))
        {
            AddSummaryPart(parts, manufacturer);
        }
        AddSummaryPart(parts, speed);
        AddSummaryPart(parts, power);
        if (!string.IsNullOrWhiteSpace(macAddress))
        {
            AddSummaryPart(parts, "MAC " + macAddress);
        }
        if (!IsRedundantUsbManufacturer(name, macVendor) && !ContainsWords(manufacturer, macVendor))
        {
            AddSummaryPart(parts, "MAC vendor " + macVendor);
        }
        if (!string.IsNullOrWhiteSpace(driveLetters))
        {
            AddSummaryPart(parts, "drives " + driveLetters);
        }
        if (!string.IsNullOrWhiteSpace(safeToUnplug))
        {
            AddSummaryPart(parts, "safe to unplug " + safeToUnplug.ToLowerInvariant());
        }
        if (!IsUsbConnectedStatus(status))
        {
            AddSummaryPart(parts, "status " + status);
        }

        return parts.Count == 0 ? "USB device" : string.Join(", ", parts.ToArray());
    }

    private static bool IsGenericUsbType(string deviceType)
    {
        return string.IsNullOrWhiteSpace(deviceType) ||
            deviceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            deviceType.Equals("USBDevice", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedundantUsbManufacturer(string name, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return true;
        }

        var value = manufacturer.Trim();
        if (value.Equals("Generic USB Hub", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard USB HUBs", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard USB Host Controller", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard disk drives", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Compatible USB storage device", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard system devices", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Generic USB Audio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsWords(name, value) || ContainsWords(value, name);
    }

    private static string NonGenericUsbVendor(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return "";
        }

        var value = manufacturer.Trim();
        if (value.Equals("Generic USB Hub", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard USB HUBs", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard USB Host Controller", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard disk drives", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Compatible USB storage device", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Standard system devices", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Generic USB Audio", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return value;
    }

    private static bool ContainsWords(string container, string value)
    {
        if (string.IsNullOrWhiteSpace(container) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedContainer = Regex.Replace(container, @"\s+", " ").Trim();
        var normalizedValue = Regex.Replace(value, @"\s+", " ").Trim();
        return normalizedContainer.IndexOf(normalizedValue, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AddSummaryPart(List<string> parts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !parts.Any(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(value.Trim());
        }
    }

    private static bool IsUsbHubOrControllerName(string name, string summary)
    {
        var text = (name + " " + summary).ToLowerInvariant();
        return text.IndexOf("hub") >= 0 || text.IndexOf("controller") >= 0 || text.IndexOf("host") >= 0;
    }

    private static Dictionary<string, string> GetUsbDriveLettersByPnpDeviceId()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var diskSearcher = new ManagementObjectSearcher("SELECT DeviceID, PNPDeviceID, InterfaceType FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    var pnpId = Convert.ToString(disk["PNPDeviceID"]);
                    var interfaceType = Convert.ToString(disk["InterfaceType"]);
                    if (string.IsNullOrWhiteSpace(pnpId) || !interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var letters = GetDriveLettersForDisk(disk);
                    if (!string.IsNullOrWhiteSpace(letters))
                    {
                        result[pnpId] = letters;
                    }
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static string GetDriveLettersForDisk(ManagementObject disk)
    {
        var letters = new List<string>();
        try
        {
            foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
            {
                foreach (ManagementObject logicalDisk in partition.GetRelated("Win32_LogicalDisk"))
                {
                    var deviceId = Convert.ToString(logicalDisk["DeviceID"]);
                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        letters.Add(deviceId);
                    }
                }
            }
        }
        catch
        {
        }

        return string.Join(", ", letters.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToArray());
    }

    private static List<UsbNetworkAdapterInfo> GetUsbNetworkAdapters()
    {
        var adapters = new List<UsbNetworkAdapterInfo>();
        try
        {
            var oui = MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"));
            using (var searcher = new ManagementObjectSearcher("SELECT Name, NetConnectionID, MACAddress, PNPDeviceID FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL"))
            {
                foreach (ManagementObject adapter in searcher.Get())
                {
                    var pnpDeviceId = Convert.ToString(adapter["PNPDeviceID"]);
                    if (!IsUsbDeviceId(pnpDeviceId))
                    {
                        continue;
                    }

                    var macAddress = NormalizeUsbMacAddress(Convert.ToString(adapter["MACAddress"]));
                    if (string.IsNullOrWhiteSpace(macAddress))
                    {
                        continue;
                    }

                    adapters.Add(new UsbNetworkAdapterInfo
                    {
                        Name = FirstNonEmpty(Convert.ToString(adapter["NetConnectionID"]), Convert.ToString(adapter["Name"]), "USB network adapter"),
                        PnpDeviceId = pnpDeviceId,
                        VidPid = ExtractUsbVidPid(pnpDeviceId),
                        Serial = ExtractUsbSerial(pnpDeviceId),
                        MacAddress = macAddress,
                        MacVendor = oui.Lookup(macAddress)
                    });
                }
            }
        }
        catch
        {
        }

        return adapters;
    }

    private static UsbNetworkAdapterInfo FindUsbNetworkAdapter(List<UsbNetworkAdapterInfo> adapters, string deviceId, string vidPid, string serial)
    {
        if (adapters == null || adapters.Count == 0 || string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var exact = adapters.FirstOrDefault(a => string.Equals(NormalizeDeviceId(a.PnpDeviceId), normalizedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        if (string.IsNullOrWhiteSpace(vidPid))
        {
            return null;
        }

        var matches = adapters.Where(a => string.Equals(a.VidPid, vidPid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var serialMatch = matches.FirstOrDefault(a => ShareUsbSerial(a.PnpDeviceId, deviceId) || ShareUsbSerial(a.Serial, serial));
            if (serialMatch != null)
            {
                return serialMatch;
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static Dictionary<string, UsbStorageIdentity> GetUsbStorageIdentitiesByPnpDeviceId()
    {
        var result = new Dictionary<string, UsbStorageIdentity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var disks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var diskSearcher = new ManagementObjectSearcher("SELECT Index, PNPDeviceID, InterfaceType FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    var pnpDeviceId = Convert.ToString(disk["PNPDeviceID"]);
                    var interfaceType = Convert.ToString(disk["InterfaceType"]);
                    var index = Convert.ToString(disk["Index"]);
                    if (!string.IsNullOrWhiteSpace(pnpDeviceId) &&
                        !string.IsNullOrWhiteSpace(index) &&
                        interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase))
                    {
                        disks[index] = pnpDeviceId;
                    }
                }
            }

            if (disks.Count == 0)
            {
                return result;
            }

            var oui = MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"));
            using (var physicalSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, UniqueId, UniqueIdFormat FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in physicalSearcher.Get())
                {
                    var deviceId = Convert.ToString(disk["DeviceId"]);
                    string pnpDeviceId;
                    if (string.IsNullOrWhiteSpace(deviceId) || !disks.TryGetValue(deviceId, out pnpDeviceId))
                    {
                        continue;
                    }

                    var uniqueId = Convert.ToString(disk["UniqueId"]);
                    if (string.IsNullOrWhiteSpace(uniqueId))
                    {
                        continue;
                    }

                    result[pnpDeviceId] = new UsbStorageIdentity
                    {
                        PnpDeviceId = pnpDeviceId,
                        UniqueId = uniqueId,
                        UniqueIdFormat = FriendlyStorageUniqueIdFormat(Convert.ToString(disk["UniqueIdFormat"]), uniqueId),
                        OuiVendor = LookupOuiFromStorageUniqueId(oui, uniqueId)
                    };
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static UsbStorageIdentity FindUsbStorageIdentity(string deviceId, Dictionary<string, UsbStorageIdentity> identities)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || identities == null || identities.Count == 0)
        {
            return null;
        }

        UsbStorageIdentity exact;
        if (identities.TryGetValue(deviceId, out exact))
        {
            return exact;
        }

        foreach (var pair in identities)
        {
            if (pair.Key.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                deviceId.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                ShareUsbSerial(deviceId, pair.Key))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string FriendlyStorageUniqueIdFormat(string format, string uniqueId)
    {
        if (!string.IsNullOrWhiteSpace(uniqueId) && uniqueId.Trim().StartsWith("eui.", StringComparison.OrdinalIgnoreCase))
        {
            return "EUI";
        }

        if (!string.IsNullOrWhiteSpace(uniqueId) && Regex.IsMatch(uniqueId.Trim(), @"^[0-9A-Fa-f]{16,}$"))
        {
            return "WWN/EUI";
        }

        if (format == "0")
        {
            return "Vendor specific";
        }

        return string.IsNullOrWhiteSpace(format) ? "" : "Format " + format;
    }

    private static string LookupOuiFromStorageUniqueId(MacVendorDatabase oui, string uniqueId)
    {
        if (oui == null || string.IsNullOrWhiteSpace(uniqueId))
        {
            return "";
        }

        var value = uniqueId.Trim();
        if (value.StartsWith("eui.", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4);
        }

        var hex = Regex.Replace(value, "[^0-9A-Fa-f]", "").ToUpperInvariant();
        if (hex.Length < 12)
        {
            return "";
        }

        var pseudoMac = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)).ToArray());
        return oui.Lookup(pseudoMac);
    }

    private static string NormalizeUsbMacAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var hex = Regex.Replace(value, "[^0-9A-Fa-f]", "").ToUpperInvariant();
        if (hex.Length != 12)
        {
            return "";
        }

        var parts = new List<string>();
        for (var i = 0; i < 12; i += 2)
        {
            parts.Add(hex.Substring(i, 2));
        }

        return string.Join(":", parts.ToArray());
    }

    private static string FindUsbDriveLetters(string deviceId, Dictionary<string, string> driveLetters)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || driveLetters == null || driveLetters.Count == 0)
        {
            return "";
        }

        string exact;
        if (driveLetters.TryGetValue(deviceId, out exact))
        {
            return exact;
        }

        foreach (var pair in driveLetters)
        {
            if (pair.Key.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                deviceId.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                ShareUsbSerial(deviceId, pair.Key))
            {
                return pair.Value;
            }
        }

        return "";
    }

    private static bool ShareUsbSerial(string first, string second)
    {
        var firstParts = (first ?? "").Split('\\');
        var secondParts = (second ?? "").Split('\\');
        if (firstParts.Length == 0 || secondParts.Length == 0)
        {
            return false;
        }

        var firstSerial = firstParts[firstParts.Length - 1];
        var secondSerial = secondParts[secondParts.Length - 1];
        return !string.IsNullOrWhiteSpace(firstSerial) &&
            !string.IsNullOrWhiteSpace(secondSerial) &&
            (firstSerial.IndexOf(secondSerial, StringComparison.OrdinalIgnoreCase) >= 0 ||
            secondSerial.IndexOf(firstSerial, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbDeviceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public ushort BcdUSB;
        public byte DeviceClass;
        public byte DeviceSubClass;
        public byte DeviceProtocol;
        public byte MaxPacketSize0;
        public ushort IdVendor;
        public ushort IdProduct;
        public ushort BcdDevice;
        public byte Manufacturer;
        public byte Product;
        public byte SerialNumber;
        public byte NumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbNodeConnectionInformationEx
    {
        public uint ConnectionIndex;
        public UsbDeviceDescriptor DeviceDescriptor;
        public byte CurrentConfigurationValue;
        public byte Speed;
        public byte DeviceIsHub;
        public ushort DeviceAddress;
        public uint NumberOfOpenPipes;
        public uint ConnectionStatus;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbNodeConnectionInformationExV2
    {
        public uint ConnectionIndex;
        public uint Length;
        public uint SupportedUsbProtocols;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbDeviceCapabilitySuperSpeedPlusSpeed
    {
        public uint Speed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbNodeConnectionSuperSpeedPlusInformation
    {
        public uint ConnectionIndex;
        public uint Length;
        public UsbDeviceCapabilitySuperSpeedPlusSpeed RxSuperSpeedPlus;
        public uint RxLaneCount;
        public UsbDeviceCapabilitySuperSpeedPlusSpeed TxSuperSpeedPlus;
        public uint TxLaneCount;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr deviceHandle, uint ioControlCode, byte[] inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr deviceHandle, uint ioControlCode, ref UsbNodeConnectionInformationEx inBuffer, int inBufferSize, ref UsbNodeConnectionInformationEx outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr deviceHandle, uint ioControlCode, ref UsbNodeConnectionInformationExV2 inBuffer, int inBufferSize, ref UsbNodeConnectionInformationExV2 outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr deviceHandle, uint ioControlCode, ref UsbNodeConnectionSuperSpeedPlusInformation inBuffer, int inBufferSize, ref UsbNodeConnectionSuperSpeedPlusInformation outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

}
