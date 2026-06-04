using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json.Linq;

public sealed partial class SensorReadoutForm : Form
{
    private const string PublicIpLookupSource = "Online IP lookup";
    private const string PublicIpLookupProvider = "https://api.techniknews.net/ipgeo/";

    private sealed class InternetIpInfo
    {
        public bool Success;
        public bool Loading;
        public string Error = "";
        public string IpAddress = "";
        public string Country = "";
        public string Region = "";
        public string City = "";
        public string PostalCode = "";
        public string Coordinates = "";
        public string TimeZone = "";
        public string Isp = "";
        public string Organization = "";
        public string AutonomousSystem = "";
        public string ReverseDns = "";
        public string ConnectionType = "";
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

        rows.AddRange(BuildInternetIpRows());
        rows.AddRange(BuildListeningPortRows());
        return rows;
    }

    private IEnumerable<SensorRow> BuildInternetIpRows()
    {
        var rows = new List<SensorRow>();
        var info = GetCachedInternetIpInfo();
        if (info == null || info.Loading)
        {
            AddInternetIpStatusRow(rows, "Loading public IP lookup...");
            return rows;
        }

        if (!info.Success)
        {
            AddInternetIpStatusRow(rows, "Unavailable" + (string.IsNullOrWhiteSpace(info.Error) ? "" : ": " + info.Error));
            return rows;
        }

        var details = BuildInternetIpDetails(info);
        AddInternetIpRow(rows, "Public IP summary", FormatInternetIpSummary(info), details);
        AddInternetIpRow(rows, "Public IP address", info.IpAddress, details);
        AddInternetIpRow(rows, "IP country", info.Country, details);
        AddInternetIpRow(rows, "IP region", info.Region, details);
        AddInternetIpRow(rows, "IP city", info.City, details);
        AddInternetIpRow(rows, "IP postal code", info.PostalCode, details);
        AddInternetIpRow(rows, "IP coordinates", info.Coordinates, details);
        AddInternetIpRow(rows, "Internet provider", info.Isp, details);
        AddInternetIpRow(rows, "IP organization", info.Organization, details);
        AddInternetIpRow(rows, "Autonomous system", info.AutonomousSystem, details);
        AddInternetIpRow(rows, "Connection type", info.ConnectionType, details);
        return rows;
    }

    private static string FormatInternetIpSummary(InternetIpInfo info)
    {
        if (info == null)
        {
            return "";
        }

        var parts = new List<string>();
        AddSummaryPart(parts, "IP", info.IpAddress);

        var location = new List<string>();
        AddSummaryValue(location, info.City);
        AddSummaryValue(location, info.Region);
        AddSummaryValue(location, info.Country);
        AddSummaryValue(location, info.PostalCode);
        if (location.Count > 0)
        {
            parts.Add("Location: " + string.Join(", ", location.ToArray()));
        }

        AddSummaryPart(parts, "Coordinates", info.Coordinates);
        AddSummaryPart(parts, "Provider", info.Isp);
        AddSummaryPart(parts, "Organization", info.Organization);
        AddSummaryPart(parts, "Autonomous system", info.AutonomousSystem);
        AddSummaryPart(parts, "Connection type", info.ConnectionType);
        return string.Join("; ", parts.ToArray());
    }

    private static void AddSummaryPart(List<string> parts, string label, string value)
    {
        if (parts == null || string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parts.Add(label + ": " + value.Trim());
    }

    private static void AddSummaryValue(List<string> parts, string value)
    {
        if (parts == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parts.Add(value.Trim());
    }

    private InternetIpInfo GetCachedInternetIpInfo()
    {
        lock (internetIpInfoLock)
        {
            if (cachedInternetIpInfo != null)
            {
                var age = DateTime.UtcNow - cachedInternetIpInfoUtc;
                if ((cachedInternetIpInfo.Loading && internetIpInfoRefreshInProgress) ||
                    (cachedInternetIpInfo.Success && age.TotalHours < 6) ||
                    (!cachedInternetIpInfo.Success && !cachedInternetIpInfo.Loading && age.TotalMinutes < 10))
                {
                    return cachedInternetIpInfo;
                }
            }

            if (!internetIpInfoRefreshInProgress)
            {
                internetIpInfoRefreshInProgress = true;
                cachedInternetIpInfo = new InternetIpInfo { Loading = true };
                cachedInternetIpInfoUtc = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var refreshed = FetchInternetIpInfo();
                    lock (internetIpInfoLock)
                    {
                        cachedInternetIpInfo = refreshed;
                        cachedInternetIpInfoUtc = DateTime.UtcNow;
                        internetIpInfoRefreshInProgress = false;
                    }
                });
            }

            return cachedInternetIpInfo;
        }
    }

    private InternetIpInfo FetchInternetIpInfo()
    {
        var info = new InternetIpInfo();
        try
        {
            var json = DownloadStringWithTimeout(PublicIpLookupProvider, 8000);
            var obj = JObject.Parse(json);
            info.Success = string.Equals(ValueText(obj, "status"), "success", StringComparison.OrdinalIgnoreCase);
            if (!info.Success)
            {
                info.Error = FirstNonEmpty(ValueText(obj, "message"), "Public IP lookup did not return success.");
                return info;
            }

            info.IpAddress = ValueText(obj, "ip");
            info.Country = ValueText(obj, "country");
            info.Region = ValueText(obj, "regionName");
            info.City = ValueText(obj, "city");
            info.PostalCode = ValueText(obj, "zip");
            info.TimeZone = ValueText(obj, "timezone");
            info.Isp = ValueText(obj, "isp");
            info.Organization = ValueText(obj, "org");
            info.AutonomousSystem = ValueText(obj, "as");
            info.ReverseDns = ValueText(obj, "reverse");
            var lat = ValueText(obj, "lat");
            var lon = ValueText(obj, "lon");
            info.Coordinates = string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon) ? "" : lat + ", " + lon;
            info.ConnectionType = FormatInternetConnectionType(obj);
        }
        catch (Exception ex)
        {
            info.Success = false;
            info.Error = ex.Message;
        }

        return info;
    }

    private static string DownloadStringWithTimeout(string url, int timeoutMilliseconds)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "Sensor Readout " + AppVersion;
        request.Timeout = timeoutMilliseconds;
        request.ReadWriteTimeout = timeoutMilliseconds;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream())
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Public IP lookup did not return a response stream.");
            }

            using (var reader = new System.IO.StreamReader(stream))
            {
            return reader.ReadToEnd();
            }
        }
    }

    private static string ValueText(JObject obj, string name)
    {
        if (obj == null || string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var token = obj[name];
        return token == null ? "" : Convert.ToString(token, CultureInfo.InvariantCulture);
    }

    private static string FormatInternetConnectionType(JObject obj)
    {
        if (obj == null)
        {
            return "";
        }

        var parts = new List<string>();
        AddInternetFlag(parts, obj, "mobile", "Mobile");
        AddInternetFlag(parts, obj, "proxy", "Proxy or VPN");
        AddInternetFlag(parts, obj, "hosting", "Hosting or datacenter");
        return parts.Count == 0 ? "ISP or residential connection" : string.Join(", ", parts.ToArray());
    }

    private static void AddInternetFlag(List<string> parts, JObject obj, string key, string label)
    {
        if (parts == null || obj == null)
        {
            return;
        }

        var token = obj[key];
        bool value;
        if (token != null && bool.TryParse(Convert.ToString(token, CultureInfo.InvariantCulture), out value) && value)
        {
            parts.Add(label);
        }
    }

    private static Dictionary<string, string> BuildInternetIpDetails(InternetIpInfo info)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (info == null)
        {
            return details;
        }

        AddDetail(details, "Public IP address", info.IpAddress);
        AddDetail(details, "Country", info.Country);
        AddDetail(details, "Region", info.Region);
        AddDetail(details, "City", info.City);
        AddDetail(details, "Postal code", info.PostalCode);
        AddDetail(details, "Coordinates", info.Coordinates);
        AddDetail(details, "Time zone", info.TimeZone);
        AddDetail(details, "Internet provider", info.Isp);
        AddDetail(details, "Organization", info.Organization);
        AddDetail(details, "Autonomous system", info.AutonomousSystem);
        AddDetail(details, "Reverse DNS", info.ReverseDns);
        AddDetail(details, "Connection type", info.ConnectionType);
        AddDetail(details, "Lookup provider", PublicIpLookupProvider);
        AddDetail(details, "Lookup note", "Public IP metadata is estimated by an online lookup service and may be approximate.");
        return details;
    }

    private static void AddInternetIpRow(List<SensorRow> rows, string name, string value, Dictionary<string, string> details)
    {
        if (rows == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Network",
            Hardware = "Internet connection",
            Name = name,
            DisplayValue = value,
            Source = PublicIpLookupSource,
            Details = CloneDetails(details)
        });
    }

    private static void AddInternetIpStatusRow(List<SensorRow> rows, string status)
    {
        if (rows == null || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Lookup provider", PublicIpLookupProvider);
        AddDetail(details, "Lookup status", status);
        AddDetail(details, "Lookup note", "Public IP metadata is estimated by an online lookup service and may be approximate.");
        rows.Add(new SensorRow
        {
            Type = "Network",
            Hardware = "Internet connection",
            Name = "Public IP lookup",
            DisplayValue = status,
            Source = PublicIpLookupSource,
            Details = details
        });
    }

    private IEnumerable<SensorRow> BuildListeningPortRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var tcp = properties.GetActiveTcpListeners()
                .Where(e => e != null)
                .OrderBy(e => e.Port)
                .ThenBy(e => e.Address == null ? "" : e.Address.ToString())
                .ToList();
            var udp = properties.GetActiveUdpListeners()
                .Where(e => e != null)
                .OrderBy(e => e.Port)
                .ThenBy(e => e.Address == null ? "" : e.Address.ToString())
                .ToList();

            var details = BuildListeningPortDetails(tcp, udp);
            rows.Add(new SensorRow { Type = "Network", Hardware = "Local listening ports", Name = "TCP listening ports", Value = tcp.Count, DisplayValue = FormatPortCount(tcp.Count), Source = "Windows Network", Details = CloneDetails(details) });
            rows.Add(new SensorRow { Type = "Network", Hardware = "Local listening ports", Name = "UDP listening ports", Value = udp.Count, DisplayValue = FormatPortCount(udp.Count), Source = "Windows Network", Details = CloneDetails(details) });
        }
        catch
        {
        }

        return rows;
    }

    private static Dictionary<string, string> BuildListeningPortDetails(List<IPEndPoint> tcp, List<IPEndPoint> udp)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "TCP listening port count", tcp == null ? "" : tcp.Count.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, "UDP listening port count", udp == null ? "" : udp.Count.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, "TCP listening port numbers", FormatPortNumbers(tcp));
        AddDetail(details, "UDP listening port numbers", FormatPortNumbers(udp));
        AddEndpointDetails(details, "TCP listening endpoint", tcp);
        AddEndpointDetails(details, "UDP listening endpoint", udp);
        AddDetail(details, "Listening port note", "These are local endpoints Windows reports as listening. Sensor Readout does not scan remote hosts or test firewall exposure.");
        return details;
    }

    private static void AddEndpointDetails(Dictionary<string, string> details, string prefix, List<IPEndPoint> endpoints)
    {
        if (details == null || endpoints == null)
        {
            return;
        }

        var index = 1;
        foreach (var endpoint in endpoints.Take(200))
        {
            AddDetail(details, prefix + " " + index.ToString(CultureInfo.InvariantCulture), FormatEndpoint(endpoint));
            index++;
        }

        if (endpoints.Count > 200)
        {
            AddDetail(details, prefix + " omitted", (endpoints.Count - 200).ToString(CultureInfo.InvariantCulture) + " more");
        }
    }

    private static string FormatPortCount(int count)
    {
        return count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " port" : " ports");
    }

    private static string FormatPortNumbers(List<IPEndPoint> endpoints)
    {
        if (endpoints == null || endpoints.Count == 0)
        {
            return "";
        }

        return string.Join(", ", endpoints
            .Select(e => e == null ? 0 : e.Port)
            .Where(p => p > 0)
            .Distinct()
            .OrderBy(p => p)
            .Select(p => p.ToString(CultureInfo.InvariantCulture))
            .ToArray());
    }

    private static string FormatEndpoint(IPEndPoint endpoint)
    {
        if (endpoint == null)
        {
            return "";
        }

        var address = endpoint.Address == null ? "" : endpoint.Address.ToString();
        return address + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
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
                    var wmiMacAddress = GetWmiPropertyText(config, "MACAddress");
                    AddDetail(wmiDetails, "WMI MAC address", wmiMacAddress);
                    if (!string.IsNullOrWhiteSpace(wmiMacAddress))
                    {
                        AddDetail(wmiDetails, "WMI MAC vendor", MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")).Lookup(wmiMacAddress));
                    }
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
}
