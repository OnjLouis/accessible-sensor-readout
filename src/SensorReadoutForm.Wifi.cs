using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed partial class SensorReadoutForm
{
    private sealed class WifiInterfaceInfo
    {
        public Guid InterfaceGuid;
        public string InterfaceDescription;
        public bool Connected;
        public string ProfileName;
        public string Ssid;
        public string Bssid;
        public string BssType;
        public string RadioType;
        public string Authentication;
        public string Cipher;
        public bool SecurityEnabled;
        public uint SignalQuality;
        public int? RssiDbm;
        public uint? Channel;
        public uint? FrequencyMhz;
        public uint ReceiveRateKbps;
        public uint TransmitRateKbps;
    }

    private static Dictionary<Guid, WifiInterfaceInfo> GetWifiInterfaceInfos()
    {
        var results = new Dictionary<Guid, WifiInterfaceInfo>();
        IntPtr clientHandle = IntPtr.Zero;
        IntPtr interfaceList = IntPtr.Zero;
        uint negotiatedVersion;

        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle) != 0 || clientHandle == IntPtr.Zero)
            {
                return results;
            }

            if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceList) != 0 || interfaceList == IntPtr.Zero)
            {
                return results;
            }

            var count = Marshal.ReadInt32(interfaceList, 0);
            var itemOffset = 8;
            var itemSize = Marshal.SizeOf(typeof(WlanInterfaceInfo));
            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(interfaceList, itemOffset + (i * itemSize));
                var wlanInterface = (WlanInterfaceInfo)Marshal.PtrToStructure(itemPtr, typeof(WlanInterfaceInfo));
                var info = GetWifiInterfaceInfo(clientHandle, wlanInterface);
                if (info != null)
                {
                    results[info.InterfaceGuid] = info;
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (interfaceList != IntPtr.Zero)
            {
                WlanFreeMemory(interfaceList);
            }

            if (clientHandle != IntPtr.Zero)
            {
                WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }

        return results;
    }

    private static WifiInterfaceInfo GetWifiInterfaceInfo(IntPtr clientHandle, WlanInterfaceInfo wlanInterface)
    {
        IntPtr data = IntPtr.Zero;
        int dataSize;
        try
        {
            var guid = wlanInterface.InterfaceGuid;
            if (WlanQueryInterface(clientHandle, ref guid, WlanIntfOpcode.CurrentConnection, IntPtr.Zero, out dataSize, out data, IntPtr.Zero) != 0 || data == IntPtr.Zero)
            {
                return new WifiInterfaceInfo
                {
                    InterfaceGuid = wlanInterface.InterfaceGuid,
                    InterfaceDescription = wlanInterface.Description,
                    Connected = false
                };
            }

            var connection = (WlanConnectionAttributes)Marshal.PtrToStructure(data, typeof(WlanConnectionAttributes));
            if (connection.InterfaceState != WlanInterfaceState.Connected)
            {
                return new WifiInterfaceInfo
                {
                    InterfaceGuid = wlanInterface.InterfaceGuid,
                    InterfaceDescription = wlanInterface.Description,
                    Connected = false
                };
            }

            var association = connection.AssociationAttributes;
            var info = new WifiInterfaceInfo
            {
                InterfaceGuid = wlanInterface.InterfaceGuid,
                InterfaceDescription = wlanInterface.Description,
                Connected = true,
                ProfileName = connection.ProfileName,
                Ssid = DecodeSsid(association.Ssid),
                Bssid = FormatMacAddress(association.Bssid),
                BssType = FormatBssType(association.BssType),
                RadioType = FormatPhyType(association.PhyType),
                SignalQuality = association.SignalQuality,
                ReceiveRateKbps = association.ReceiveRateKbps,
                TransmitRateKbps = association.TransmitRateKbps,
                SecurityEnabled = connection.SecurityAttributes.SecurityEnabled,
                Authentication = FormatAuthentication(connection.SecurityAttributes.AuthenticationAlgorithm),
                Cipher = FormatCipher(connection.SecurityAttributes.CipherAlgorithm)
            };

            AddWifiBssDetails(clientHandle, wlanInterface.InterfaceGuid, info);
            return info;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }
        }
    }

    private static void AddWifiBssDetails(IntPtr clientHandle, Guid interfaceGuid, WifiInterfaceInfo info)
    {
        IntPtr bssList = IntPtr.Zero;
        try
        {
            var guid = interfaceGuid;
            if (WlanGetNetworkBssList(clientHandle, ref guid, IntPtr.Zero, Dot11BssType.Any, false, IntPtr.Zero, out bssList) != 0 || bssList == IntPtr.Zero)
            {
                return;
            }

            var count = Marshal.ReadInt32(bssList, 0);
            var itemOffset = 8;
            var itemSize = Marshal.SizeOf(typeof(WlanBssEntry));
            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(bssList, itemOffset + (i * itemSize));
                var entry = (WlanBssEntry)Marshal.PtrToStructure(itemPtr, typeof(WlanBssEntry));
                var bssid = FormatMacAddress(entry.Bssid);
                if (!string.Equals(bssid, info.Bssid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                info.RssiDbm = entry.Rssi;
                if (info.SignalQuality == 0 && entry.LinkQuality <= 100)
                {
                    info.SignalQuality = entry.LinkQuality;
                }

                var frequencyMhz = entry.CenterFrequencyKhz / 1000;
                if (frequencyMhz > 0)
                {
                    info.FrequencyMhz = frequencyMhz;
                    info.Channel = WifiChannelFromFrequencyMhz(frequencyMhz);
                }

                if (string.IsNullOrWhiteSpace(info.RadioType))
                {
                    info.RadioType = FormatPhyType(entry.PhyType);
                }

                return;
            }
        }
        catch
        {
        }
        finally
        {
            if (bssList != IntPtr.Zero)
            {
                WlanFreeMemory(bssList);
            }
        }
    }

    private static IEnumerable<SensorRow> BuildWifiRows(string hardware, WifiInterfaceInfo wifi)
    {
        if (wifi == null)
        {
            yield break;
        }

        yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi connected", Value = wifi.Connected ? 1 : 0, DisplayValue = wifi.Connected ? "Connected" : "Disconnected", Source = "Windows WLAN" };

        if (!wifi.Connected)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(wifi.Ssid))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi network", DisplayValue = wifi.Ssid, Source = "Windows WLAN" };
        }

        if (!string.IsNullOrWhiteSpace(wifi.ProfileName) && !string.Equals(wifi.ProfileName, wifi.Ssid, StringComparison.OrdinalIgnoreCase))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi profile", DisplayValue = wifi.ProfileName, Source = "Windows WLAN" };
        }

        if (!string.IsNullOrWhiteSpace(wifi.Bssid))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi access point", DisplayValue = wifi.Bssid, Source = "Windows WLAN" };
        }

        yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi signal strength", Value = wifi.SignalQuality, DisplayValue = FormatNumber(wifi.SignalQuality, "0") + "%", Source = "Windows WLAN" };

        if (wifi.RssiDbm.HasValue)
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi signal RSSI", Value = wifi.RssiDbm.Value, DisplayValue = wifi.RssiDbm.Value + " dBm", Source = "Windows WLAN" };
        }

        if (wifi.Channel.HasValue)
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi channel", Value = wifi.Channel.Value, DisplayValue = wifi.Channel.Value.ToString(), Source = "Windows WLAN" };
        }

        if (wifi.FrequencyMhz.HasValue)
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi frequency", Value = wifi.FrequencyMhz.Value, DisplayValue = wifi.FrequencyMhz.Value + " MHz", Source = "Windows WLAN" };
        }

        if (!string.IsNullOrWhiteSpace(wifi.RadioType))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi radio type", DisplayValue = wifi.RadioType, Source = "Windows WLAN" };
        }

        if (!string.IsNullOrWhiteSpace(wifi.BssType))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi connection type", DisplayValue = wifi.BssType, Source = "Windows WLAN" };
        }

        if (wifi.ReceiveRateKbps > 0)
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi receive link speed", Value = wifi.ReceiveRateKbps * 1000f, DisplayValue = FormatBitsPerSecond((long)wifi.ReceiveRateKbps * 1000L), Source = "Windows WLAN" };
        }

        if (wifi.TransmitRateKbps > 0)
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi transmit link speed", Value = wifi.TransmitRateKbps * 1000f, DisplayValue = FormatBitsPerSecond((long)wifi.TransmitRateKbps * 1000L), Source = "Windows WLAN" };
        }

        yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi security", DisplayValue = wifi.SecurityEnabled ? "Enabled" : "Open", Source = "Windows WLAN" };

        if (!string.IsNullOrWhiteSpace(wifi.Authentication))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi authentication", DisplayValue = wifi.Authentication, Source = "Windows WLAN" };
        }

        if (!string.IsNullOrWhiteSpace(wifi.Cipher))
        {
            yield return new SensorRow { Type = "Network", Hardware = hardware, Name = "Wi-Fi cipher", DisplayValue = wifi.Cipher, Source = "Windows WLAN" };
        }
    }

    private static string DecodeSsid(Dot11Ssid ssid)
    {
        if (ssid.Ssid == null || ssid.SsidLength == 0)
        {
            return "";
        }

        var length = (int)Math.Min(ssid.SsidLength, (uint)ssid.Ssid.Length);
        return Encoding.UTF8.GetString(ssid.Ssid, 0, length).TrimEnd('\0').Trim();
    }

    private static string FormatMacAddress(byte[] bytes)
    {
        return bytes == null || bytes.Length == 0
            ? ""
            : string.Join(":", Array.ConvertAll(bytes, b => b.ToString("X2")));
    }

    private static uint? WifiChannelFromFrequencyMhz(uint mhz)
    {
        if (mhz == 2484)
        {
            return 14;
        }

        if (mhz >= 2412 && mhz <= 2472)
        {
            return (mhz - 2407) / 5;
        }

        if (mhz >= 5005 && mhz <= 5895)
        {
            return (mhz - 5000) / 5;
        }

        if (mhz >= 5955 && mhz <= 7115)
        {
            return (mhz - 5950) / 5;
        }

        return null;
    }

    private static string FormatBssType(Dot11BssType type)
    {
        if (type == Dot11BssType.Infrastructure) return "Infrastructure";
        if (type == Dot11BssType.Independent) return "Ad hoc";
        return "";
    }

    private static string FormatPhyType(Dot11PhyType type)
    {
        switch (type)
        {
            case Dot11PhyType.Fhss: return "FHSS";
            case Dot11PhyType.Dsss: return "DSSS";
            case Dot11PhyType.IrBaseband: return "IR baseband";
            case Dot11PhyType.Ofdm: return "802.11a/g";
            case Dot11PhyType.HrDsss: return "802.11b";
            case Dot11PhyType.Erp: return "802.11g";
            case Dot11PhyType.Ht: return "802.11n";
            case Dot11PhyType.Vht: return "802.11ac";
            case Dot11PhyType.Dmg: return "802.11ad";
            case Dot11PhyType.He: return "802.11ax";
            case Dot11PhyType.Eht: return "802.11be";
            default: return "";
        }
    }

    private static string FormatAuthentication(Dot11AuthAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case Dot11AuthAlgorithm.Open: return "Open";
            case Dot11AuthAlgorithm.SharedKey: return "Shared key";
            case Dot11AuthAlgorithm.Wpa: return "WPA";
            case Dot11AuthAlgorithm.WpaPsk: return "WPA-PSK";
            case Dot11AuthAlgorithm.WpaNone: return "WPA-None";
            case Dot11AuthAlgorithm.Rsna: return "WPA2";
            case Dot11AuthAlgorithm.RsnaPsk: return "WPA2-PSK";
            case Dot11AuthAlgorithm.Wpa3: return "WPA3";
            case Dot11AuthAlgorithm.Wpa3Sae: return "WPA3-SAE";
            case Dot11AuthAlgorithm.Owe: return "OWE";
            default: return "";
        }
    }

    private static string FormatCipher(Dot11CipherAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case Dot11CipherAlgorithm.None: return "None";
            case Dot11CipherAlgorithm.Wep40: return "WEP-40";
            case Dot11CipherAlgorithm.Tkip: return "TKIP";
            case Dot11CipherAlgorithm.Ccmp: return "CCMP";
            case Dot11CipherAlgorithm.Wep104: return "WEP-104";
            case Dot11CipherAlgorithm.Bip: return "BIP";
            case Dot11CipherAlgorithm.Gcmp: return "GCMP";
            case Dot11CipherAlgorithm.Gcmp256: return "GCMP-256";
            case Dot11CipherAlgorithm.Ccmp256: return "CCMP-256";
            case Dot11CipherAlgorithm.Wep: return "WEP";
            default: return "";
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, WlanIntfOpcode opcode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, Dot11BssType bssType, bool securityEnabled, IntPtr reserved, out IntPtr bssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    private enum WlanIntfOpcode
    {
        CurrentConnection = 7
    }

    private enum WlanInterfaceState
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7
    }

    private enum WlanConnectionMode
    {
        Profile = 0,
        TemporaryProfile = 1,
        DiscoverySecure = 2,
        DiscoveryUnsecure = 3,
        Auto = 4,
        Invalid = 5
    }

    private enum Dot11BssType
    {
        Infrastructure = 1,
        Independent = 2,
        Any = 3
    }

    private enum Dot11PhyType
    {
        Unknown = 0,
        Any = 1,
        Fhss = 2,
        Dsss = 3,
        IrBaseband = 4,
        Ofdm = 5,
        HrDsss = 6,
        Erp = 7,
        Ht = 8,
        Vht = 9,
        Dmg = 10,
        He = 11,
        Eht = 12
    }

    private enum Dot11AuthAlgorithm
    {
        Open = 1,
        SharedKey = 2,
        Wpa = 3,
        WpaPsk = 4,
        WpaNone = 5,
        Rsna = 6,
        RsnaPsk = 7,
        Wpa3 = 8,
        Wpa3Sae = 9,
        Owe = 10
    }

    private enum Dot11CipherAlgorithm
    {
        None = 0,
        Wep40 = 1,
        Tkip = 2,
        Ccmp = 4,
        Wep104 = 5,
        Bip = 6,
        Gcmp = 8,
        Gcmp256 = 9,
        Ccmp256 = 10,
        Wep = 257
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public WlanInterfaceState InterfaceState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint SsidLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public Dot11BssType BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public Dot11PhyType PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint ReceiveRateKbps;
        public uint TransmitRateKbps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool SecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool OneXEnabled;

        public Dot11AuthAlgorithm AuthenticationAlgorithm;
        public Dot11CipherAlgorithm CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public WlanInterfaceState InterfaceState;
        public WlanConnectionMode ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WlanAssociationAttributes AssociationAttributes;
        public WlanSecurityAttributes SecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanRateSet
    {
        public uint RateSetLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] Rates;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanBssEntry
    {
        public Dot11Ssid Ssid;
        public uint PhyId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public Dot11BssType BssType;
        public Dot11PhyType PhyType;
        public int Rssi;
        public uint LinkQuality;

        [MarshalAs(UnmanagedType.U1)]
        public bool InRegDomain;

        public ushort BeaconPeriod;
        public ulong Timestamp;
        public ulong HostTimestamp;
        public ushort CapabilityInformation;
        public uint CenterFrequencyKhz;
        public WlanRateSet RateSet;
        public uint IeOffset;
        public uint IeSize;
    }
}
