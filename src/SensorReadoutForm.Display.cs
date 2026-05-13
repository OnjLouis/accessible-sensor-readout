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
            var drivers = GetSignedDriverInfoByDeviceId();
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
            {
                foreach (ManagementObject gpu in searcher.Get())
                {
                    var name = FirstNonEmpty(Convert.ToString(gpu["Name"]), "Display adapter");
                    var pnpDeviceId = Convert.ToString(gpu["PNPDeviceID"]);
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
                    AddDetail(details, "Device ID", pnpDeviceId);
                    AddDetail(details, "Video architecture", Convert.ToString(gpu["VideoArchitecture"]));
                    AddDetail(details, "Video memory type", Convert.ToString(gpu["VideoMemoryType"]));
                    AddDetail(details, "Adapter DAC type", Convert.ToString(gpu["AdapterDACType"]));
                    AddDetail(details, "Current scan mode", Convert.ToString(gpu["CurrentScanMode"]));
                    AddDetail(details, "Availability", Convert.ToString(gpu["Availability"]));
                    AddDetail(details, "Config manager error code", Convert.ToString(gpu["ConfigManagerErrorCode"]));
                    AddPciDetails(details, pnpDeviceId);
                    AddDeviceRegistryDetails(details, pnpDeviceId);
                    DeviceDriverInfo driver;
                    if (!string.IsNullOrWhiteSpace(pnpDeviceId) && drivers.TryGetValue(pnpDeviceId, out driver))
                    {
                        AddDetail(details, "Signed driver provider", driver.DriverProviderName);
                        AddDetail(details, "Signed driver version", driver.DriverVersion);
                        AddDetail(details, "Signed driver date", driver.DriverDate);
                        AddDetail(details, "Signed driver INF", driver.InfName);
                        AddDetail(details, "Signed driver signer", driver.Signer);
                        AddDetail(details, "Signed driver location", driver.Location);
                    }
                    AddRawWmiDetails(details, "Display adapter WMI", gpu);

                    string bios;
                    string biosDate;
                    if (TryGetGpuBiosInfo(name, pnpDeviceId, out bios, out biosDate))
                    {
                        AddDetail(details, "BIOS", bios);
                        AddDetail(details, "BIOS date", biosDate);
                    }

                    rows.Add(new SensorRow
                    {
                        Type = "Display",
                        Hardware = name,
                        Name = "Adapter",
                        Identifier = "display|" + pnpDeviceId,
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
            var monitorBasicParams = GetMonitorBasicDisplayParameters();
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
                    Dictionary<string, string> basicParams;
                    if (monitorBasicParams.TryGetValue(Convert.ToString(monitor["InstanceName"]) ?? "", out basicParams))
                    {
                        foreach (var detail in basicParams)
                        {
                            AddDetail(details, detail.Key, detail.Value);
                        }
                    }
                    AddRawWmiDetails(details, "Monitor ID WMI", monitor);

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

    private static Dictionary<string, Dictionary<string, string>> GetMonitorBasicDisplayParameters()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBasicDisplayParams"))
            {
                foreach (ManagementObject monitor in searcher.Get())
                {
                    var instance = Convert.ToString(monitor["InstanceName"]) ?? "";
                    if (string.IsNullOrWhiteSpace(instance))
                    {
                        continue;
                    }

                    var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddDetail(details, "Display active", FormatYesNo(GetWmiPropertyValue(monitor, "Active")));
                    AddDetail(details, "Display input type", FormatVideoInputType(GetWmiPropertyValue(monitor, "VideoInputType")));
                    AddDetail(details, "Display max horizontal size", FormatCentimeters(GetWmiPropertyValue(monitor, "MaxHorizontalImageSize")));
                    AddDetail(details, "Display max vertical size", FormatCentimeters(GetWmiPropertyValue(monitor, "MaxVerticalImageSize")));
                    AddDetail(details, "Display transfer characteristic", Convert.ToString(GetWmiPropertyValue(monitor, "DisplayTransferCharacteristic")));
                    AddRawWmiDetails(details, "Monitor basic parameters WMI", monitor);
                    result[instance] = details;
                }
            }
        }
        catch
        {
        }

        return result;
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

    private static string FormatCentimeters(object value)
    {
        int centimeters;
        return int.TryParse(Convert.ToString(value), out centimeters) && centimeters > 0
            ? centimeters + " cm"
            : "";
    }

    private static string FormatVideoInputType(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text == "0")
        {
            return "Analog";
        }

        if (text == "1")
        {
            return "Digital";
        }

        return text.Trim();
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
