using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static readonly Guid PciDevicePropertyGuid = new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DeviceRelationPropertyGuid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7");
    private const uint DevPropTypeUInt32 = 0x00000007;
    private const uint DevPropTypeStringList = 0x00002012;
    private const int CmCrSuccess = 0;

    private static IEnumerable<SensorRow> GetPciExpansionRows()
    {
        var rows = new List<SensorRow>();
        AddPciSlotRows(rows);
        AddPciLinkRows(rows);
        return rows;
    }

    private static void AddPciSlotRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

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

            if (slots.Count == 0)
            {
                return;
            }

            var inUse = slots.Count(s => string.Equals(FormatSystemSlotCurrentUsage(GetWmiPropertyValue(s, "CurrentUsage")), "In use", StringComparison.OrdinalIgnoreCase));
            var empty = slots.Count(s => string.Equals(FormatSystemSlotCurrentUsage(GetWmiPropertyValue(s, "CurrentUsage")), "Empty", StringComparison.OrdinalIgnoreCase));
            var unknown = Math.Max(0, slots.Count - inUse - empty);
            var summaryDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddDetail(summaryDetails, "Expansion slot count", slots.Count.ToString());
            AddDetail(summaryDetails, "Expansion slots in use", inUse.ToString());
            AddDetail(summaryDetails, "Expansion slots reported empty", empty.ToString());
            AddDetail(summaryDetails, "Expansion slots unknown usage", unknown.ToString());
            AddDetail(summaryDetails, "Expansion slot usage note", "Windows SMBIOS slot data may report some slots without a clear in-use or empty state.");
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = "PCIe and expansion slots",
                Name = "Expansion slots",
                DisplayValue = FormatExpansionSlotSummary(slots.Count, inUse, empty, unknown),
                Source = "Windows WMI",
                Details = summaryDetails
            });

            for (var index = 0; index < slots.Count; index++)
            {
                using (var slot = slots[index])
                {
                    var designation = FirstNonEmpty(GetWmiPropertyText(slot, "SlotDesignation"), "Expansion slot " + (index + 1));
                    var usage = FormatSystemSlotCurrentUsage(GetWmiPropertyValue(slot, "CurrentUsage"));
                    var connector = FormatSystemSlotConnectorTypes(GetWmiPropertyValue(slot, "ConnectorType"));
                    var displayConnector = !string.IsNullOrWhiteSpace(connector) && connector.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) >= 0 ? connector : "";
                    var width = FormatSystemSlotDataWidth(GetWmiPropertyValue(slot, "MaxDataWidth"));
                    var length = FormatSystemSlotLength(GetWmiPropertyValue(slot, "Length"));
                    var busAddress = FormatSystemSlotAddress(slot);
                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Slot designation", designation);
                    AddDetail(details, "Slot current usage", usage);
                    AddDetail(details, "Slot connector type", connector);
                    AddDetail(details, "Slot maximum data width", width);
                    AddDetail(details, "Slot maximum data width note", "This comes from Windows SMBIOS slot data and can be less reliable than per-device PCIe link properties.");
                    AddDetail(details, "Slot length", length);
                    AddDetail(details, "Slot bus address", busAddress);
                    AddDetail(details, "Slot bus address note", "This comes from Windows SMBIOS slot data and may not map directly to Device Manager bus numbers on every firmware.");
                    AddDetail(details, "Slot hot plug supported", FormatYesNo(GetWmiPropertyValue(slot, "SupportsHotPlug")));
                    AddDetail(details, "Slot status", GetWmiPropertyText(slot, "Status"));
                    AddRawWmiDetails(details, "System slot WMI", slot);

                    rows.Add(new SensorRow
                    {
                        Type = "Performance",
                        Hardware = "PCIe and expansion slots",
                        Name = "Slot " + designation,
                        Identifier = "pcie-slot|" + designation + "|" + index,
                        DisplayValue = JoinNonEmpty("; ", usage, displayConnector, length),
                        Source = "Windows WMI",
                        Details = details
                    });
                }
            }
        }
        catch
        {
        }
    }

    private static string FormatExpansionSlotSummary(int total, int inUse, int empty, int unknown)
    {
        var parts = new List<string>
        {
            total + " " + (total == 1 ? "slot" : "slots"),
            inUse + " in use"
        };

        if (empty > 0)
        {
            parts.Add(empty + " reported empty");
        }

        if (unknown > 0)
        {
            parts.Add(unknown + " unknown usage");
        }
        else if (empty == 0)
        {
            parts.Add("0 reported empty");
        }

        return string.Join("; ", parts);
    }

    private static void AddPciLinkRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

        try
        {
            var candidates = new List<PciLinkCandidate>();
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\\\%'"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    using (device)
                    {
                        var deviceId = GetWmiPropertyText(device, "DeviceID");
                        if (string.IsNullOrWhiteSpace(deviceId))
                        {
                            continue;
                        }

                        uint currentSpeed;
                        uint currentWidth;
                        uint maxSpeed;
                        uint maxWidth;
                        var hasCurrentSpeed = TryGetPciDeviceUInt32Property(deviceId, 9, out currentSpeed);
                        var hasCurrentWidth = TryGetPciDeviceUInt32Property(deviceId, 10, out currentWidth);
                        var hasMaxSpeed = TryGetPciDeviceUInt32Property(deviceId, 11, out maxSpeed);
                        var hasMaxWidth = TryGetPciDeviceUInt32Property(deviceId, 12, out maxWidth);
                        if (!hasCurrentSpeed && !hasCurrentWidth && !hasMaxSpeed && !hasMaxWidth)
                        {
                            continue;
                        }

                        var name = FirstNonEmpty(GetWmiPropertyText(device, "Name"), GetWmiPropertyText(device, "Caption"), deviceId);
                        if (IsNoisyPciLinkDeviceName(name))
                        {
                            continue;
                        }

                        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        AddDetail(details, "Device name", name);
                        AddDetail(details, "Manufacturer", GetWmiPropertyText(device, "Manufacturer"));
                        AddDetail(details, "PNP class", GetWmiPropertyText(device, "PNPClass"));
                        AddDetail(details, "Service", GetWmiPropertyText(device, "Service"));
                        AddDetail(details, "Status", GetWmiPropertyText(device, "Status"));
                        AddDetail(details, "PNP device ID", deviceId);
                        AddPciDetails(details, deviceId);
                        if (hasCurrentSpeed)
                        {
                            AddDetail(details, "PCIe current link speed", FormatPciExpressLinkSpeed(currentSpeed));
                        }

                        if (hasCurrentWidth)
                        {
                            AddDetail(details, "PCIe current link width", FormatPciExpressLinkWidth(currentWidth));
                        }

                        if (hasMaxSpeed)
                        {
                            AddDetail(details, "PCIe maximum link speed", FormatPciExpressLinkSpeed(maxSpeed));
                        }

                        if (hasMaxWidth)
                        {
                            AddDetail(details, "PCIe maximum link width", FormatPciExpressLinkWidth(maxWidth));
                        }

                        AddRawWmiDetails(details, "PCI device WMI", device);
                        var current = JoinNonEmpty(" at ",
                            hasCurrentWidth ? FormatPciExpressLinkWidth(currentWidth) : "",
                            hasCurrentSpeed ? FormatPciExpressLinkSpeed(currentSpeed) : "");
                        var maximum = JoinNonEmpty(" at ",
                            hasMaxWidth ? FormatPciExpressLinkWidth(maxWidth) : "",
                            hasMaxSpeed ? FormatPciExpressLinkSpeed(maxSpeed) : "");
                        var display = JoinNonEmpty("; ",
                            string.IsNullOrWhiteSpace(current) ? "" : "current " + current,
                            string.IsNullOrWhiteSpace(maximum) ? "" : "maximum " + maximum);

                        candidates.Add(new PciLinkCandidate
                        {
                            Name = name,
                            DeviceId = deviceId,
                            DisplayValue = display,
                            Details = details
                        });
                    }
                }
            }

            var duplicateNames = new HashSet<string>(
                candidates.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var name = duplicateNames.Contains(candidate.Name)
                    ? candidate.Name + " (" + FirstNonEmpty(RegexGroup(candidate.DeviceId, "DEV_([0-9A-Fa-f]{4})"), RegexGroup(candidate.DeviceId, "VEN_([0-9A-Fa-f]{4})"), "PCI device") + ")"
                    : candidate.Name;
                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = "PCIe and expansion slots",
                    Name = name,
                    Identifier = "pcie-link|" + candidate.DeviceId,
                    DisplayValue = candidate.DisplayValue,
                    Source = "Windows PCI properties",
                    Details = candidate.Details
                });
            }
        }
        catch
        {
        }
    }

    private sealed class PciLinkCandidate
    {
        public string Name;
        public string DeviceId;
        public string DisplayValue;
        public Dictionary<string, string> Details;
    }

    private static bool IsNoisyPciLinkDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return name.IndexOf("dummy", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryGetPciDeviceUInt32Property(string deviceId, uint propertyId, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            uint devInst;
            if (CM_Locate_DevNode(out devInst, deviceId, 0) != CmCrSuccess)
            {
                return false;
            }

            var key = new DevPropKey { FmtId = PciDevicePropertyGuid, Pid = propertyId };
            var buffer = new byte[4];
            var size = buffer.Length;
            uint propertyType;
            var result = CM_Get_DevNode_Property(devInst, ref key, out propertyType, buffer, ref size, 0);
            if (result != CmCrSuccess || propertyType != DevPropTypeUInt32 || size < 4)
            {
                return false;
            }

            value = BitConverter.ToUInt32(buffer, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPciExpressLinkWidth(uint width)
    {
        return width == 0 ? "" : "x" + width;
    }

    private static string FormatPciExpressLinkSpeed(uint speed)
    {
        switch (speed)
        {
            case 1: return "2.5 GT/s";
            case 2: return "5.0 GT/s";
            case 3: return "8.0 GT/s";
            case 4: return "16.0 GT/s";
            case 5: return "32.0 GT/s";
            case 6: return "64.0 GT/s";
            default: return speed == 0 ? "" : "raw " + speed;
        }
    }

    private static string FormatPciExpressGeneration(uint speed)
    {
        switch (speed)
        {
            case 1: return "PCIe Gen 1";
            case 2: return "PCIe Gen 2";
            case 3: return "PCIe Gen 3";
            case 4: return "PCIe Gen 4";
            case 5: return "PCIe Gen 5";
            case 6: return "PCIe Gen 6";
            default: return speed == 0 ? "" : "PCIe raw generation " + speed;
        }
    }

    private static bool TryGetDeviceChildrenProperty(string deviceId, out List<string> children)
    {
        var key = new DevPropKey { FmtId = DeviceRelationPropertyGuid, Pid = 9 };
        return TryGetDeviceStringListProperty(deviceId, key, out children);
    }

    private static bool TryGetDeviceStringListProperty(string deviceId, DevPropKey key, out List<string> values)
    {
        values = new List<string>();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            uint devInst;
            if (CM_Locate_DevNode(out devInst, deviceId, 0) != CmCrSuccess)
            {
                return false;
            }

            var buffer = new byte[4096];
            var size = buffer.Length;
            uint propertyType;
            var result = CM_Get_DevNode_Property(devInst, ref key, out propertyType, buffer, ref size, 0);
            if (result != CmCrSuccess || propertyType != DevPropTypeStringList || size <= 2)
            {
                return false;
            }

            var text = System.Text.Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            values = text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
            return values.Count > 0;
        }
        catch
        {
            values = new List<string>();
            return false;
        }
    }

    private static string FormatSystemSlotLength(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return "";
        }

        switch (code)
        {
            case 1: return "Other length";
            case 2: return "Unknown length";
            case 3: return "Short";
            case 4: return "Long";
            default: return "Length code " + code;
        }
    }

    private static string FormatSystemSlotAddress(ManagementObject slot)
    {
        if (slot == null)
        {
            return "";
        }

        var segment = FormatSlotAddressPart(GetWmiPropertyValue(slot, "SegmentGroupNumber"));
        var bus = FormatSlotAddressPart(GetWmiPropertyValue(slot, "BusNumber"));
        var device = FormatSlotAddressPart(GetWmiPropertyValue(slot, "DeviceNumber"));
        var function = FormatSlotAddressPart(GetWmiPropertyValue(slot, "FunctionNumber"));
        if (string.IsNullOrWhiteSpace(segment) && string.IsNullOrWhiteSpace(bus) && string.IsNullOrWhiteSpace(device) && string.IsNullOrWhiteSpace(function))
        {
            return "";
        }

        return "reported address " + FirstNonEmpty(segment, "?") + ":" + FirstNonEmpty(bus, "?") + ":" + FirstNonEmpty(device, "?") + ":" + FirstNonEmpty(function, "?");
    }

    private static string FormatSlotAddressPart(object value)
    {
        long number;
        return TryConvertToInt64(value, out number) && number >= 0 ? number.ToString() : "";
    }

    private static string JoinNonEmpty(string separator, params string[] parts)
    {
        return string.Join(separator, (parts ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Property(uint devInst, ref DevPropKey propertyKey, out uint propertyType, byte[] propertyBuffer, ref int propertyBufferSize, uint flags);
}
