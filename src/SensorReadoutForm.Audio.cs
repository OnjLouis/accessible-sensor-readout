using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static IEnumerable<SensorRow> GetAudioRows()
    {
        var endpoints = GetAudioEndpointDetails();
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, ProductName, DeviceID, PNPDeviceID, Status FROM Win32_SoundDevice"))
            {
                foreach (ManagementObject device in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var name = FirstNonEmpty(Convert.ToString(device["Name"]), Convert.ToString(device["ProductName"]), "Audio device");
                    var manufacturer = Convert.ToString(device["Manufacturer"]);
                    var deviceId = FirstNonEmpty(Convert.ToString(device["PNPDeviceID"]), Convert.ToString(device["DeviceID"]));
                    var endpoint = FindAudioEndpointForDevice(endpoints, name, deviceId);
                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Name", name);
                    AddDetail(details, "Vendor", manufacturer);
                    AddDetail(details, "Status", Convert.ToString(device["Status"]));
                    AddDetail(details, "Default format", endpoint == null ? "" : endpoint.Format);
                    AddDetail(details, "Channels", endpoint == null ? "" : endpoint.Channels);
                    AddDetail(details, "Sample rate", endpoint == null ? "" : endpoint.SampleRate);
                    AddDetail(details, "Bit depth", endpoint == null ? "" : endpoint.BitsPerSample);
                    AddDetail(details, "Endpoint direction", endpoint == null ? "" : endpoint.Direction);
                    AddDetail(details, "Endpoint name", endpoint == null ? "" : endpoint.Name);
                    AddDetail(details, "Device ID", deviceId);
                    if (endpoint != null)
                    {
                        foreach (var detail in endpoint.Details)
                        {
                            AddDetail(details, detail.Key, detail.Value);
                        }
                    }
                    AddRawWmiDetails(details, "Audio device WMI", device);

                    var summary = BuildAudioSummary(manufacturer, endpoint);
                    rows.Add(new SensorRow
                    {
                        Type = "Audio",
                        Hardware = name,
                        Name = "Device",
                        Identifier = "audio|" + deviceId,
                        DisplayValue = summary,
                        Source = "Windows audio",
                        Details = details
                    });
                }
            }
        }
        catch
        {
        }

        foreach (var endpoint in endpoints.Where(e => !IsGuidLike(e.Name) && !rows.Any(r => AudioEndpointMatchesRow(e, r))))
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddDetail(details, "Name", endpoint.Name);
            AddDetail(details, "Device name", endpoint.DeviceName);
            AddDetail(details, "Default format", endpoint.Format);
            AddDetail(details, "Channels", endpoint.Channels);
            AddDetail(details, "Sample rate", endpoint.SampleRate);
            AddDetail(details, "Bit depth", endpoint.BitsPerSample);
            AddDetail(details, "Endpoint direction", endpoint.Direction);
            AddDetail(details, "Endpoint ID", endpoint.Id);
            foreach (var detail in endpoint.Details)
            {
                AddDetail(details, detail.Key, detail.Value);
            }
            rows.Add(new SensorRow
            {
                Type = "Audio",
                Hardware = endpoint.Name,
                Name = "Endpoint",
                Identifier = "audio-endpoint|" + endpoint.Id,
                DisplayValue = BuildAudioSummary("", endpoint),
                Source = "Windows audio registry",
                Details = details
            });
        }

        return rows;
    }

    private static bool AudioEndpointMatchesRow(AudioEndpointDetail endpoint, SensorRow row)
    {
        if (endpoint == null || row == null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(endpoint.Name) &&
            (ShortHardwareName(row.Hardware).IndexOf(endpoint.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
             endpoint.Name.IndexOf(ShortHardwareName(row.Hardware), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string BuildAudioSummary(string manufacturer, AudioEndpointDetail endpoint)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            parts.Add(manufacturer.Trim());
        }

        if (endpoint != null)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.Direction)) parts.Add(endpoint.Direction);
            if (!string.IsNullOrWhiteSpace(endpoint.Format)) parts.Add(endpoint.Format);
        }

        return parts.Count == 0 ? "Audio device" : string.Join(", ", parts.ToArray());
    }

    private static AudioEndpointDetail FindAudioEndpointForDevice(List<AudioEndpointDetail> endpoints, string name, string deviceId)
    {
        if (endpoints == null || endpoints.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var matches = endpoints
                .Where(e => !string.IsNullOrWhiteSpace(e.DeviceInstanceId) && DeviceIdsMatch(e.DeviceInstanceId, deviceId))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var clean = name.Trim();
            var matches = endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Name) &&
                string.Equals(e.Name.Trim(), clean, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }

    private static bool DeviceIdsMatch(string endpointDeviceId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(endpointDeviceId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        return NormalizeDeviceId(endpointDeviceId).IndexOf(NormalizeDeviceId(deviceId), StringComparison.OrdinalIgnoreCase) >= 0 ||
            NormalizeDeviceId(deviceId).IndexOf(NormalizeDeviceId(endpointDeviceId), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeDeviceId(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Replace("\\\\?\\", "").Replace("#", "\\").Trim();
    }

    private static List<AudioEndpointDetail> GetAudioEndpointDetails()
    {
        var result = new List<AudioEndpointDetail>();
        try
        {
            using (var audioKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio"))
            {
                if (audioKey == null)
                {
                    return result;
                }

                ReadAudioEndpointGroup(audioKey, "Render", "Playback", result);
                ReadAudioEndpointGroup(audioKey, "Capture", "Recording", result);
            }
        }
        catch
        {
        }

        return result;
    }

    private static void ReadAudioEndpointGroup(RegistryKey audioKey, string groupName, string direction, List<AudioEndpointDetail> result)
    {
        using (var group = audioKey.OpenSubKey(groupName))
        {
            if (group == null)
            {
                return;
            }

            foreach (var endpointId in group.GetSubKeyNames())
            {
                using (var endpointKey = group.OpenSubKey(endpointId))
                using (var properties = endpointKey == null ? null : endpointKey.OpenSubKey("Properties"))
                {
                    if (endpointKey == null || properties == null)
                    {
                        continue;
                    }

                    var state = endpointKey.GetValue("DeviceState");
                    if (Convert.ToString(state) == "4")
                    {
                        continue;
                    }

                    var detail = new AudioEndpointDetail
                    {
                        Id = endpointId,
                        Direction = direction,
                        Name = FirstNonEmpty(
                            Convert.ToString(properties.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2")),
                            Convert.ToString(properties.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14")),
                            Convert.ToString(properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")),
                            endpointId),
                        DeviceName = Convert.ToString(properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6")),
                        DeviceInstanceId = FirstNonEmpty(
                            Convert.ToString(properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},2")),
                            Convert.ToString(properties.GetValue("{233164c8-1b2c-4c7d-bc68-b671687a2567},1")),
                            Convert.ToString(properties.GetValue("{80f111c3-b103-42e1-afb6-db7a6fa8be1f},0")))
                    };
                    detail.Details["Endpoint direction"] = direction;
                    detail.Details["Endpoint ID"] = endpointId;
                    detail.Details["Endpoint state"] = DecodeAudioEndpointState(state);
                    AddAudioEndpointRegistryDetails(detail.Details, properties);

                    ApplyAudioFormat(detail, FindAudioFormatBlob(properties));
                    result.Add(detail);
                }
            }
        }
    }

    private static void AddAudioEndpointRegistryDetails(Dictionary<string, string> details, RegistryKey properties)
    {
        if (details == null || properties == null)
        {
            return;
        }

        foreach (var valueName in properties.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            AddDetail(details, "Endpoint registry raw property " + valueName, AudioRegistryValueToString(properties.GetValue(valueName)));
        }
    }

    private static string AudioRegistryValueToString(object value)
    {
        var bytes = value as byte[];
        if (bytes != null)
        {
            return bytes.Length == 0 ? "" : "Binary data (" + bytes.Length + " bytes)";
        }

        return RegistryValueToString(value);
    }

    private static string DecodeAudioEndpointState(object value)
    {
        int state;
        if (!int.TryParse(Convert.ToString(value), out state))
        {
            return "";
        }

        state &= 0xF;
        switch (state)
        {
            case 1: return "Active";
            case 2: return "Disabled";
            case 4: return "Not present";
            case 8: return "Unplugged";
            default: return state.ToString();
        }
    }

    private static byte[] FindAudioFormatBlob(RegistryKey properties)
    {
        if (properties == null)
        {
            return null;
        }

        var exact = properties.GetValue("{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0") as byte[];
        if (IsAudioFormatBlob(exact))
        {
            return exact;
        }

        foreach (var valueName in properties.GetValueNames())
        {
            var bytes = properties.GetValue(valueName) as byte[];
            if (!IsAudioFormatBlob(bytes))
            {
                continue;
            }

            return bytes;
        }

        return null;
    }

    private static bool IsAudioFormatBlob(byte[] bytes)
    {
        ushort channels;
        uint sampleRate;
        ushort bits;
        int offset;
        return TryFindAudioFormat(bytes, out channels, out sampleRate, out bits, out offset);
    }

    private static void ApplyAudioFormat(AudioEndpointDetail detail, byte[] format)
    {
        ushort channels;
        uint sampleRate;
        ushort bits;
        int offset;
        if (detail == null || format == null || !TryFindAudioFormat(format, out channels, out sampleRate, out bits, out offset))
        {
            return;
        }

        detail.Channels = channels + " channel" + (channels == 1 ? "" : "s");
        detail.SampleRate = FormatNumber(sampleRate, "0") + " Hz";
        detail.BitsPerSample = bits + "-bit";
        detail.Format = detail.Channels + ", " + detail.SampleRate + ", " + detail.BitsPerSample;
    }

    private static bool TryFindAudioFormat(byte[] bytes, out ushort channels, out uint sampleRate, out ushort bits, out int offset)
    {
        channels = 0;
        sampleRate = 0;
        bits = 0;
        offset = -1;
        if (bytes == null || bytes.Length < 16)
        {
            return false;
        }

        for (var i = 0; i <= bytes.Length - 16; i++)
        {
            var tag = BitConverter.ToUInt16(bytes, i);
            var candidateChannels = BitConverter.ToUInt16(bytes, i + 2);
            var candidateRate = BitConverter.ToUInt32(bytes, i + 4);
            var candidateBits = BitConverter.ToUInt16(bytes, i + 14);
            var displayBits = candidateBits;
            if (tag == 65534 && bytes.Length >= i + 20)
            {
                var validBits = BitConverter.ToUInt16(bytes, i + 18);
                if ((validBits == 8 || validBits == 16 || validBits == 24 || validBits == 32 || validBits == 64) &&
                    validBits <= candidateBits)
                {
                    displayBits = validBits;
                }
            }

            if ((tag == 1 || tag == 65534) &&
                candidateChannels >= 1 && candidateChannels <= 16 &&
                candidateRate >= 8000 && candidateRate <= 768000 &&
                (displayBits == 8 || displayBits == 16 || displayBits == 24 || displayBits == 32 || displayBits == 64))
            {
                channels = candidateChannels;
                sampleRate = candidateRate;
                bits = displayBits;
                offset = i;
                return true;
            }
        }

        return false;
    }

    private static bool IsGuidLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        Guid parsed;
        return Guid.TryParse(value.Trim().Trim('{', '}'), out parsed);
    }

    private sealed class AudioEndpointDetail
    {
        public string Id = "";
        public string Name = "";
        public string DeviceName = "";
        public string DeviceInstanceId = "";
        public string Direction = "";
        public string Format = "";
        public string Channels = "";
        public string SampleRate = "";
        public string BitsPerSample = "";
        public readonly Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
