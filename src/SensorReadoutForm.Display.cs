using System;
using System.Collections.Generic;
using System.Management;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static IEnumerable<SensorRow> GetDisplayRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, CurrentBitsPerPixel, VideoModeDescription, DriverVersion, DriverDate, Status, PNPDeviceID, AdapterCompatibility, VideoProcessor FROM Win32_VideoController"))
            {
                foreach (ManagementObject gpu in searcher.Get())
                {
                    var name = FirstNonEmpty(Convert.ToString(gpu["Name"]), "Display adapter");
                    var resolution = FormatResolution(gpu["CurrentHorizontalResolution"], gpu["CurrentVerticalResolution"], gpu["CurrentRefreshRate"]);
                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Name", name);
                    AddDetail(details, "Vendor", Convert.ToString(gpu["AdapterCompatibility"]));
                    AddDetail(details, "Processor", Convert.ToString(gpu["VideoProcessor"]));
                    AddDetail(details, "Adapter RAM", FormatBytes(GetGpuAdapterMemoryBytes(name, gpu["AdapterRAM"])));
                    AddDetail(details, "Current resolution", resolution);
                    AddDetail(details, "Colour depth", FormatBitsPerPixel(gpu["CurrentBitsPerPixel"]));
                    AddDetail(details, "Video mode", Convert.ToString(gpu["VideoModeDescription"]));
                    AddDetail(details, "Driver version", Convert.ToString(gpu["DriverVersion"]));
                    AddDetail(details, "Driver date", FormatWmiDate(gpu["DriverDate"]));
                    AddDetail(details, "Status", Convert.ToString(gpu["Status"]));
                    AddDetail(details, "Device ID", Convert.ToString(gpu["PNPDeviceID"]));

                    string bios;
                    string biosDate;
                    if (TryGetGpuBiosInfo(name, Convert.ToString(gpu["PNPDeviceID"]), out bios, out biosDate))
                    {
                        AddDetail(details, "BIOS", bios);
                        AddDetail(details, "BIOS date", biosDate);
                    }

                    rows.Add(new SensorRow
                    {
                        Type = "Display",
                        Hardware = name,
                        Name = "Adapter",
                        Identifier = "display|" + Convert.ToString(gpu["PNPDeviceID"]),
                        DisplayValue = BuildDisplaySummary(gpu, resolution),
                        Source = "Windows display",
                        Details = details
                    });
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName, WeekOfManufacture, YearOfManufacture FROM WmiMonitorID"))
            {
                foreach (ManagementObject monitor in searcher.Get())
                {
                    var friendly = DecodeWmiMonitorString(monitor["UserFriendlyName"]);
                    var manufacturer = DecodeWmiMonitorString(monitor["ManufacturerName"]);
                    var product = DecodeWmiMonitorString(monitor["ProductCodeID"]);
                    var serial = DecodeWmiMonitorString(monitor["SerialNumberID"]);
                    var name = FirstNonEmpty(friendly, "Display");
                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Name", name);
                    AddDetail(details, "Vendor", manufacturer);
                    AddDetail(details, "Product code", product);
                    AddDetail(details, "Serial", serial);
                    AddDetail(details, "Manufacture week", Convert.ToString(monitor["WeekOfManufacture"]));
                    AddDetail(details, "Manufacture year", Convert.ToString(monitor["YearOfManufacture"]));
                    AddDetail(details, "Instance", Convert.ToString(monitor["InstanceName"]));

                    rows.Add(new SensorRow
                    {
                        Type = "Display",
                        Hardware = name,
                        Name = "Monitor",
                        Identifier = "display-monitor|" + Convert.ToString(monitor["InstanceName"]),
                        DisplayValue = BuildMonitorSummary(manufacturer, product),
                        Source = "Windows WMI",
                        Details = details
                    });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static string BuildDisplaySummary(ManagementObject gpu, string resolution)
    {
        var parts = new List<string>();
        var vendor = Convert.ToString(gpu["AdapterCompatibility"]);
        if (!string.IsNullOrWhiteSpace(vendor)) parts.Add(vendor.Trim());
        if (!string.IsNullOrWhiteSpace(resolution)) parts.Add(resolution);
        var ram = FormatBytes(GetGpuAdapterMemoryBytes(Convert.ToString(gpu["Name"]), gpu["AdapterRAM"]));
        if (!string.IsNullOrWhiteSpace(ram)) parts.Add(ram);
        return parts.Count == 0 ? "Display adapter" : string.Join(", ", parts.ToArray());
    }

    private static string BuildMonitorSummary(string manufacturer, string product)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manufacturer)) parts.Add(manufacturer.Trim());
        if (!string.IsNullOrWhiteSpace(product)) parts.Add("product " + product.Trim());
        return parts.Count == 0 ? "Monitor" : string.Join(", ", parts.ToArray());
    }

    private static string FormatResolution(object widthValue, object heightValue, object refreshValue)
    {
        int width;
        int height;
        if (!int.TryParse(Convert.ToString(widthValue), out width) || !int.TryParse(Convert.ToString(heightValue), out height) || width <= 0 || height <= 0)
        {
            return "";
        }

        int refresh;
        var text = width + " x " + height;
        if (int.TryParse(Convert.ToString(refreshValue), out refresh) && refresh > 0)
        {
            text += " at " + refresh + " Hz";
        }

        return text;
    }

    private static string FormatBitsPerPixel(object value)
    {
        int bits;
        return int.TryParse(Convert.ToString(value), out bits) && bits > 0 ? bits + "-bit" : "";
    }

    private static string DecodeWmiMonitorString(object value)
    {
        var array = value as ushort[];
        if (array == null || array.Length == 0)
        {
            return "";
        }

        var chars = new List<char>();
        foreach (var item in array)
        {
            if (item == 0)
            {
                break;
            }

            chars.Add((char)item);
        }

        return new string(chars.ToArray()).Trim();
    }
}
