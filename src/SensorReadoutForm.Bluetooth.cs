using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private IEnumerable<SensorRow> GetBluetoothRows()
    {
        var rows = new List<SensorRow>();
        var seenDeviceAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceNamesByAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oui = MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"));

        foreach (var radio in GetBluetoothRadios())
        {
            rows.AddRange(BuildBluetoothRadioRows(radio, oui));

            foreach (var device in radio.Devices)
            {
                if (!seenDeviceAddresses.Add(device.AddressText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(device.AddressText) && !string.IsNullOrWhiteSpace(device.Name))
                {
                    deviceNamesByAddress[device.AddressText] = device.Name.Trim();
                }

                rows.AddRange(BuildBluetoothDeviceRows(device, oui));
            }
        }

        rows.AddRange(GetBluetoothPnpDeviceRows(seenDeviceAddresses, deviceNamesByAddress, oui));
        return rows;
    }

    private static IEnumerable<SensorRow> BuildBluetoothRadioRows(BluetoothRadioDetails radio, MacVendorDatabase oui)
    {
        var hardware = string.IsNullOrWhiteSpace(radio.Name) ? "Bluetooth radio" : radio.Name;
        var addressVendor = oui == null ? "" : oui.Lookup(radio.AddressText);
        var details = BuildBluetoothRadioDetails(radio, addressVendor);

        yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Status", DisplayValue = "Available", Value = 1, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".status" };
        if (!string.IsNullOrWhiteSpace(radio.AddressText))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Adapter address", DisplayValue = radio.AddressText, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".address" };
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Adapter address vendor", DisplayValue = string.IsNullOrWhiteSpace(addressVendor) ? "Unknown" : addressVendor, Source = "OUI database", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".address-vendor" };
        }
        if (!string.IsNullOrWhiteSpace(radio.DeviceClass))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Adapter type", DisplayValue = radio.DeviceClass, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".type" };
        }
        if (!string.IsNullOrWhiteSpace(radio.ServiceClasses))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Adapter services", DisplayValue = radio.ServiceClasses, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".services" };
        }
        if (!string.IsNullOrWhiteSpace(radio.Manufacturer))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Adapter manufacturer", DisplayValue = radio.Manufacturer, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".manufacturer" };
        }
        if (!string.IsNullOrWhiteSpace(radio.LmpSubversion))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "LMP subversion", DisplayValue = radio.LmpSubversion, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.radio." + radio.AddressText + ".lmp-subversion" };
        }
    }

    private static IEnumerable<SensorRow> BuildBluetoothDeviceRows(BluetoothDeviceDetails device, MacVendorDatabase oui)
    {
        var hardware = string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth device " + device.AddressText : device.Name;
        var addressVendor = oui == null ? "" : oui.Lookup(device.AddressText);
        var details = BuildBluetoothDeviceDetails(device, addressVendor);
        var connected = device.Connected ? 1 : 0;
        var paired = device.Authenticated ? 1 : 0;

        yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Connected", DisplayValue = FormatYesNo(device.Connected), Value = connected, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".connected" };
        yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Paired", DisplayValue = FormatYesNo(device.Authenticated), Value = paired, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".paired" };
        yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Remembered", DisplayValue = FormatYesNo(device.Remembered), Value = device.Remembered ? 1 : 0, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".remembered" };
        if (!string.IsNullOrWhiteSpace(device.AddressText))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Device address", DisplayValue = device.AddressText, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".address" };
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Device address vendor", DisplayValue = string.IsNullOrWhiteSpace(addressVendor) ? "Unknown" : addressVendor, Source = "OUI database", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".address-vendor" };
        }
        if (!string.IsNullOrWhiteSpace(device.DeviceClass))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Device type", DisplayValue = device.DeviceClass, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".type" };
        }
        if (!string.IsNullOrWhiteSpace(device.ServiceClasses))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Device services", DisplayValue = device.ServiceClasses, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".services" };
        }
        if (!string.IsNullOrWhiteSpace(device.LastSeen))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Last seen", DisplayValue = device.LastSeen, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".last-seen" };
        }
        if (!string.IsNullOrWhiteSpace(device.LastUsed))
        {
            yield return new SensorRow { Type = "Bluetooth", Hardware = hardware, Name = "Last used", DisplayValue = device.LastUsed, Source = "Windows Bluetooth", Details = CloneDetails(details), Identifier = "bluetooth.device." + device.AddressText + ".last-used" };
        }
    }

    private static Dictionary<string, string> BuildBluetoothRadioDetails(BluetoothRadioDetails radio, string addressVendor)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Radio name", radio.Name);
        AddDetail(details, "Radio address", radio.AddressText);
        AddDetail(details, "Radio address vendor", addressVendor);
        AddDetail(details, "Radio class", radio.DeviceClass);
        AddDetail(details, "Radio service classes", radio.ServiceClasses);
        AddDetail(details, "Radio class code", radio.ClassCode);
        AddDetail(details, "Manufacturer", radio.Manufacturer);
        AddDetail(details, "Manufacturer code", radio.ManufacturerCode);
        AddDetail(details, "LMP subversion", radio.LmpSubversion);
        AddDetail(details, "RSSI note", "Windows classic Bluetooth APIs used by Sensor Readout do not expose reliable live RSSI/dBm for paired devices. RSSI is shown only if a reliable source is added later.");
        return details;
    }

    private static Dictionary<string, string> BuildBluetoothDeviceDetails(BluetoothDeviceDetails device, string addressVendor)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Device name", device.Name);
        AddDetail(details, "Device address", device.AddressText);
        AddDetail(details, "Device address vendor", addressVendor);
        AddDetail(details, "Connected", FormatYesNo(device.Connected));
        AddDetail(details, "Paired", FormatYesNo(device.Authenticated));
        AddDetail(details, "Remembered", FormatYesNo(device.Remembered));
        AddDetail(details, "Device class", device.DeviceClass);
        AddDetail(details, "Service classes", device.ServiceClasses);
        AddDetail(details, "Class code", device.ClassCode);
        AddDetail(details, "Last seen", device.LastSeen);
        AddDetail(details, "Last used", device.LastUsed);
        AddDetail(details, "RSSI note", "Windows classic Bluetooth APIs used by Sensor Readout do not expose reliable live RSSI/dBm for paired devices. RSSI is shown only if a reliable source is added later.");
        return details;
    }

    private static List<BluetoothRadioDetails> GetBluetoothRadios()
    {
        var radios = new List<BluetoothRadioDetails>();
        var openedRadioHandles = new List<IntPtr>();
        var findParams = new BluetoothFindRadioParams { Size = Marshal.SizeOf(typeof(BluetoothFindRadioParams)) };
        IntPtr radioHandle;
        var findHandle = BluetoothFindFirstRadio(ref findParams, out radioHandle);
        if (findHandle == IntPtr.Zero)
        {
            return radios;
        }

        try
        {
            while (radioHandle != IntPtr.Zero)
            {
                openedRadioHandles.Add(radioHandle);
                var info = new BluetoothRadioInfo { Size = Marshal.SizeOf(typeof(BluetoothRadioInfo)) };
                if (BluetoothGetRadioInfo(radioHandle, ref info) == 0)
                {
                    var radio = new BluetoothRadioDetails
                    {
                        Handle = radioHandle,
                        Name = info.Name,
                        AddressText = FormatBluetoothAddress(info.Address),
                        DeviceClass = FormatBluetoothMajorClass(info.ClassOfDevice),
                        ServiceClasses = FormatBluetoothServiceClasses(info.ClassOfDevice),
                        ClassCode = "0x" + info.ClassOfDevice.ToString("X6", CultureInfo.InvariantCulture),
                        Manufacturer = FormatBluetoothManufacturer(info.Manufacturer),
                        ManufacturerCode = info.Manufacturer.ToString(CultureInfo.InvariantCulture),
                        LmpSubversion = info.LmpSubversion.ToString(CultureInfo.InvariantCulture)
                    };
                    radio.Devices.AddRange(GetBluetoothDevices(radioHandle));
                    radios.Add(radio);
                }

                IntPtr nextRadio;
                if (!BluetoothFindNextRadio(findHandle, out nextRadio))
                {
                    break;
                }

                radioHandle = nextRadio;
            }
        }
        finally
        {
            BluetoothFindRadioClose(findHandle);
            foreach (var handle in openedRadioHandles.Distinct())
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        return radios;
    }

    private static List<BluetoothDeviceDetails> GetBluetoothDevices(IntPtr radioHandle)
    {
        var devices = new List<BluetoothDeviceDetails>();
        var search = new BluetoothDeviceSearchParams
        {
            Size = Marshal.SizeOf(typeof(BluetoothDeviceSearchParams)),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = false,
            ReturnConnected = true,
            IssueInquiry = false,
            TimeoutMultiplier = 0,
            Radio = radioHandle
        };
        var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf(typeof(BluetoothDeviceInfo)) };
        var findHandle = BluetoothFindFirstDevice(ref search, ref info);
        if (findHandle == IntPtr.Zero)
        {
            return devices;
        }

        try
        {
            while (true)
            {
                var address = FormatBluetoothAddress(info.Address);
                devices.Add(new BluetoothDeviceDetails
                {
                    Name = info.Name,
                    AddressText = address,
                    Connected = info.Connected,
                    Remembered = info.Remembered,
                    Authenticated = info.Authenticated,
                    DeviceClass = FormatBluetoothMajorClass(info.ClassOfDevice),
                    ServiceClasses = FormatBluetoothServiceClasses(info.ClassOfDevice),
                    ClassCode = "0x" + info.ClassOfDevice.ToString("X6", CultureInfo.InvariantCulture),
                    LastSeen = FormatBluetoothSystemTime(info.LastSeen),
                    LastUsed = FormatBluetoothSystemTime(info.LastUsed)
                });

                info = new BluetoothDeviceInfo { Size = Marshal.SizeOf(typeof(BluetoothDeviceInfo)) };
                if (!BluetoothFindNextDevice(findHandle, ref info))
                {
                    break;
                }
            }
        }
        finally
        {
            BluetoothFindDeviceClose(findHandle);
        }

        return devices;
    }

    private static IEnumerable<SensorRow> GetBluetoothPnpDeviceRows(HashSet<string> seenDeviceAddresses, Dictionary<string, string> deviceNamesByAddress, MacVendorDatabase oui)
    {
        var rows = new List<SensorRow>();
        var emittedChildRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Present = TRUE"))
            {
                foreach (ManagementObject device in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var details = ReadManagementObjectDetails(device);
                    var name = FirstNonEmpty(GetDictionaryValue(details, "Name"), GetDictionaryValue(details, "Caption"), GetDictionaryValue(details, "Description"));
                    var deviceId = GetDictionaryValue(details, "PNPDeviceID");
                    var pnpClass = GetDictionaryValue(details, "PNPClass");
                    if (!IsBluetoothPnpDeviceCandidate(name, deviceId, pnpClass))
                    {
                        continue;
                    }

                    string address;
                    var hasAddress = TryExtractBluetoothAddressFromPnpId(deviceId, out address);
                    string parentName = "";
                    var hasParent = TryResolveBluetoothParentName(name, hasAddress ? address : "", deviceNamesByAddress, out parentName);
                    string childName = "";
                    var isChild = hasParent && TryBluetoothChildDeviceName(name, parentName, out childName);
                    if (!isChild && IsGenericBluetoothPnpName(name))
                    {
                        continue;
                    }

                    if (hasAddress && !isChild && seenDeviceAddresses != null && !seenDeviceAddresses.Add(address))
                    {
                        continue;
                    }

                    var hardware = isChild ? parentName.Trim() : (string.IsNullOrWhiteSpace(name) ? FirstNonEmpty(deviceId, "Bluetooth device") : name);
                    var rowName = isChild ? childName : "Present";
                    if (isChild && !emittedChildRows.Add(hardware + "|" + rowName))
                    {
                        continue;
                    }

                    AddDetail(details, "Bluetooth PnP note", "This row comes from Windows Plug and Play because some Bluetooth LE or HID devices are not returned by the classic Bluetooth device list.");
                    if (isChild)
                    {
                        AddDetail(details, "Parent Bluetooth device", parentName);
                        AddDetail(details, "Bluetooth child device", name);
                    }
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        AddDetail(details, "Device address", address);
                        var addressVendor = oui == null ? "" : oui.Lookup(address);
                        AddDetail(details, "Device address vendor", string.IsNullOrWhiteSpace(addressVendor) ? "Unknown" : addressVendor);
                    }

                    rows.Add(new SensorRow
                    {
                        Type = "Bluetooth",
                        Hardware = hardware,
                        Name = rowName,
                        DisplayValue = "Yes",
                        Value = 1,
                        Source = "Windows Bluetooth PnP",
                        Details = details,
                        Identifier = "bluetooth.pnp." + StableDeviceIdentifier(deviceId, hardware + "." + rowName) + ".present"
                    });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static bool TryResolveBluetoothParentName(string name, string address, Dictionary<string, string> deviceNamesByAddress, out string parentName)
    {
        parentName = "";
        if (deviceNamesByAddress == null || deviceNamesByAddress.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(address) &&
            deviceNamesByAddress.TryGetValue(address, out parentName) &&
            !string.IsNullOrWhiteSpace(parentName))
        {
            return true;
        }

        var cleanName = (name ?? "").Trim();
        foreach (var candidate in deviceNamesByAddress.Values.Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v.Length))
        {
            if (cleanName.StartsWith(candidate.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                parentName = candidate.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryBluetoothChildDeviceName(string name, string parentName, out string childName)
    {
        childName = "";
        var cleanName = (name ?? "").Trim();
        var cleanParent = (parentName ?? "").Trim();
        if (cleanName.Length <= cleanParent.Length || cleanParent.Length == 0 ||
            !cleanName.StartsWith(cleanParent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = cleanName.Substring(cleanParent.Length).Trim(new[] { ' ', '-', ':', '_' });
        if (suffix.Length == 0)
        {
            return false;
        }

        childName = suffix;
        return true;
    }

    private static bool IsBluetoothPnpDeviceCandidate(string name, string deviceId, string pnpClass)
    {
        var combined = ((name ?? "") + " " + (deviceId ?? "") + " " + (pnpClass ?? "")).ToLowerInvariant();
        if (combined.Contains("bluetooth") || combined.Contains("bthenum") || combined.Contains("bthle") || combined.Contains("bthmini"))
        {
            return true;
        }

        return string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase) &&
            (combined.Contains("bth") || combined.Contains("bluetooth"));
    }

    private static bool IsGenericBluetoothPnpName(string name)
    {
        var normalized = (name ?? "").Trim();
        if (normalized.Length == 0)
        {
            return true;
        }

        var genericNames = new[]
        {
            "AAP Client",
            "AMA SPP Server",
            "Bluetooth",
            "Bluetooth LE Generic Attribute Service",
            "Bluetooth Peripheral Device",
            "Device Information Service",
            "GATT",
            "Generic Access Profile",
            "Generic Attribute Profile",
            "Headset Audio Gateway Service",
            "IcService_New",
            "MAP MAS-iOS",
            "NearbySharing",
            "Object Push Service",
            "Personal Area Network NAP Service",
            "Personal Area Network Service",
            "Phonebook Access Pse Service",
            "Microsoft Bluetooth Enumerator",
            "Microsoft Bluetooth LE Enumerator",
            "Bluetooth Device (RFCOMM Protocol TDI)",
            "Bluetooth Device (Personal Area Network)",
            "Service Discovery Service",
            "Sim Access Service"
        };
        if (genericNames.Any(g => normalized.Equals(g, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return normalized.IndexOf("Bluetooth Radio", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Bluetooth USB Module", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("Bluetooth Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.EndsWith(" Service", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(" Profile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractBluetoothAddressFromPnpId(string deviceId, out string address)
    {
        address = "";
        var match = System.Text.RegularExpressions.Regex.Match(deviceId ?? "", @"(?:DEV_|_DEV_|BTHLEDEVICE\{?|BTHENUM\\DEV_|&0&)([0-9A-Fa-f]{12})(?:_|\\|$)");
        if (!match.Success)
        {
            return false;
        }

        var raw = match.Groups[1].Value.ToUpperInvariant();
        address = string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)).ToArray());
        return true;
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        if (address == 0)
        {
            return "";
        }

        var parts = new List<string>();
        for (var index = 5; index >= 0; index--)
        {
            parts.Add(((address >> (index * 8)) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        }

        return string.Join(":", parts.ToArray());
    }

    private static string FormatBluetoothMajorClass(uint classOfDevice)
    {
        switch ((classOfDevice >> 8) & 0x1F)
        {
            case 1: return "Computer";
            case 2: return "Phone";
            case 3: return "LAN or network access point";
            case 4: return "Audio/video";
            case 5: return "Peripheral";
            case 6: return "Imaging";
            case 7: return "Wearable";
            case 8: return "Toy";
            case 9: return "Health";
            default: return "Uncategorized";
        }
    }

    private static string FormatBluetoothServiceClasses(uint classOfDevice)
    {
        var services = new List<string>();
        if ((classOfDevice & 0x00002000) != 0) services.Add("Limited discoverable");
        if ((classOfDevice & 0x00010000) != 0) services.Add("Positioning");
        if ((classOfDevice & 0x00020000) != 0) services.Add("Networking");
        if ((classOfDevice & 0x00040000) != 0) services.Add("Rendering");
        if ((classOfDevice & 0x00080000) != 0) services.Add("Capturing");
        if ((classOfDevice & 0x00100000) != 0) services.Add("Object transfer");
        if ((classOfDevice & 0x00200000) != 0) services.Add("Audio");
        if ((classOfDevice & 0x00400000) != 0) services.Add("Telephony");
        if ((classOfDevice & 0x00800000) != 0) services.Add("Information");
        return services.Count == 0 ? "" : string.Join(", ", services.ToArray());
    }

    private static string FormatBluetoothManufacturer(ushort manufacturer)
    {
        switch (manufacturer)
        {
            case 0: return "Ericsson Technology Licensing";
            case 1: return "Nokia Mobile Phones";
            case 2: return "Intel Corp.";
            case 10: return "Cambridge Silicon Radio";
            case 13: return "Texas Instruments";
            case 15: return "Broadcom";
            case 29: return "Qualcomm";
            case 48: return "Apple";
            case 57: return "Realtek";
            case 69: return "Atheros Communications";
            case 76: return "Samsung Electronics";
            case 93: return "Microsoft";
            default: return manufacturer == 65535 ? "" : "Code " + manufacturer.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string FormatBluetoothSystemTime(SystemTime time)
    {
        if (time.Year <= 1 || time.Month < 1 || time.Day < 1)
        {
            return "";
        }

        try
        {
            return FormatDateTimeWithAge(new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Local), true);
        }
        catch
        {
            return "";
        }
    }

    private sealed class BluetoothRadioDetails
    {
        public IntPtr Handle;
        public string Name = "";
        public string AddressText = "";
        public string DeviceClass = "";
        public string ServiceClasses = "";
        public string ClassCode = "";
        public string Manufacturer = "";
        public string ManufacturerCode = "";
        public string LmpSubversion = "";
        public List<BluetoothDeviceDetails> Devices = new List<BluetoothDeviceDetails>();
    }

    private sealed class BluetoothDeviceDetails
    {
        public string Name = "";
        public string AddressText = "";
        public bool Connected;
        public bool Remembered;
        public bool Authenticated;
        public string DeviceClass = "";
        public string ServiceClasses = "";
        public string ClassCode = "";
        public string LastSeen = "";
        public string LastUsed = "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParams
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothRadioInfo
    {
        public int Size;
        public ulong Address;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;

        public uint ClassOfDevice;
        public ushort LmpSubversion;
        public ushort Manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public int Size;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnAuthenticated;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnRemembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnUnknown;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnConnected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool IssueInquiry;

        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Connected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Remembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Authenticated;

        public SystemTime LastSeen;
        public SystemTime LastUsed;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radioHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(IntPtr findHandle, out IntPtr radioHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr findHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothGetRadioInfo(IntPtr radioHandle, ref BluetoothRadioInfo radioInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams searchParams, ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr findHandle, ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);
}
