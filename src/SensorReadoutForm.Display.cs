using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static readonly object nvidiaSmiCacheLock = new object();
    private static DateTime cachedNvidiaSmiOutputUtc = DateTime.MinValue;
    private static string cachedNvidiaSmiOutput = "";

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
                    AddGpuDisplayRegistryDetails(details, name);
                    AddNvidiaSmiDetails(details, name, pnpDeviceId);
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
        var problem = FormatDisplayAdapterProblem(gpu["ConfigManagerErrorCode"], Convert.ToString(gpu["Status"]));
        if (!string.IsNullOrWhiteSpace(problem)) parts.Add(problem);
        return parts.Count == 0 ? "Display adapter" : string.Join(", ", parts.ToArray());
    }

    private static string FormatDisplayAdapterProblem(object configManagerErrorCode, string status)
    {
        int errorCode;
        if (TryConvertToInt32(configManagerErrorCode, out errorCode) && errorCode != 0)
        {
            return "device problem " + errorCode;
        }

        if (!string.IsNullOrWhiteSpace(status)
            && !status.Equals("OK", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "status " + status.Trim();
        }

        return "";
    }

    private static string BuildMonitorSummary(string manufacturer, string product)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manufacturer)) parts.Add(manufacturer.Trim());
        if (!string.IsNullOrWhiteSpace(product)) parts.Add("product " + product.Trim());
        return parts.Count == 0 ? "Monitor" : string.Join(", ", parts.ToArray());
    }

    private static void AddGpuDisplayRegistryDetails(Dictionary<string, string> details, string gpuName)
    {
        if (details == null || string.IsNullOrWhiteSpace(gpuName))
        {
            return;
        }

        try
        {
            using (var videoKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                AddDetail(details, "Display registry adapter string", adapterString);
                                AddDetail(details, "Display registry chip type", RegistryValueToString(instanceKey.GetValue("HardwareInformation.ChipType")));
                                AddDetail(details, "Display registry DAC type", RegistryValueToString(instanceKey.GetValue("HardwareInformation.DacType")));
                                AddDetail(details, "Display registry BIOS", RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosString")));
                                AddDetail(details, "Display registry BIOS date", RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosDate")));
                                AddGpuRegistryMemoryDetail(details, "Display registry memory size", instanceKey.GetValue("HardwareInformation.MemorySize"));
                                AddGpuRegistryMemoryDetail(details, "Display registry memory size 64-bit", instanceKey.GetValue("HardwareInformation.qwMemorySize"));
                                AddDetail(details, "Display registry driver description", driverDesc);
                                AddDetail(details, "Display registry driver version", RegistryValueToString(instanceKey.GetValue("DriverVersion")));
                                AddDetail(details, "Display registry provider", RegistryValueToString(instanceKey.GetValue("ProviderName")));
                                AddDetail(details, "Display registry driver date", RegistryValueToString(instanceKey.GetValue("DriverDate")));
                                AddDetail(details, "Display registry matching device ID", RegistryValueToString(instanceKey.GetValue("MatchingDeviceId")));
                                AddDetail(details, "Display registry installed display drivers", RegistryValueToString(instanceKey.GetValue("InstalledDisplayDrivers")));
                                AddDetail(details, "Display registry OpenGL driver", RegistryValueToString(instanceKey.GetValue("OpenGLDriverName")));
                                AddDetail(details, "Display registry Vulkan driver", RegistryValueToString(instanceKey.GetValue("VulkanDriverName")));
                                return;
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

    private static void AddGpuRegistryMemoryDetail(Dictionary<string, string> details, string name, object value)
    {
        if (value is int && (int)value < 0)
        {
            return;
        }

        if (value is long && (long)value < 0)
        {
            return;
        }

        var bytes = RegistryValueToUInt64(value);
        AddDetail(details, name, bytes > 0 ? FormatBytes(bytes) : RegistryValueToString(value));
    }

    private static void AddNvidiaSmiDetails(Dictionary<string, string> details, string gpuName, string pnpDeviceId)
    {
        if (details == null || string.IsNullOrWhiteSpace(gpuName) ||
            gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        var output = RunNvidiaSmiQuery();
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var expectedDeviceId = RegexGroup(pnpDeviceId, "DEV_([0-9A-Fa-f]{4})");
        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',').Select(f => f.Trim()).ToArray();
            if (fields.Length < 15)
            {
                continue;
            }

            var smiName = fields[0];
            var smiDeviceId = NormalizeNvidiaSmiDeviceId(fields[5]);
            if (!GpuNamesLikelyMatch(gpuName, smiName) &&
                (string.IsNullOrWhiteSpace(expectedDeviceId) || !string.Equals(expectedDeviceId, smiDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            AddDetail(details, "NVIDIA SMI name", smiName);
            AddDetail(details, "NVIDIA SMI bus ID", fields[1]);
            AddDetail(details, "NVIDIA SMI driver version", fields[2]);
            AddDetail(details, "NVIDIA SMI VBIOS version", fields[3]);
            AddDetail(details, "NVIDIA CUDA compute capability", fields[4]);
            AddDetail(details, "NVIDIA CUDA cores", "Not exposed by Windows or nvidia-smi");
            AddDetail(details, "NVIDIA PCI device ID", fields[5]);
            AddDetail(details, "NVIDIA PCI subdevice ID", fields[6]);
            AddDetail(details, "NVIDIA PCI bus ID", fields[7]);
            AddMibDetail(details, "NVIDIA SMI memory total", fields[8]);
            AddMibDetail(details, "NVIDIA SMI memory used", fields[9]);
            AddMibDetail(details, "NVIDIA SMI memory free", fields[10]);
            AddDetail(details, "NVIDIA SMI memory note", "These are NVIDIA driver-reported memory figures and can differ from Performance GPU memory, which uses Windows GPU performance counters.");
            AddUnitDetail(details, "NVIDIA temperature", fields[11], " C");
            AddUnitDetail(details, "NVIDIA power limit", fields[12], " W");
            AddUnitDetail(details, "NVIDIA max graphics clock", fields[13], " MHz");
            AddUnitDetail(details, "NVIDIA max memory clock", fields[14], " MHz");
            return;
        }
    }

    private static string RunNvidiaSmiQuery()
    {
        lock (nvidiaSmiCacheLock)
        {
            if (cachedNvidiaSmiOutputUtc != DateTime.MinValue && (DateTime.UtcNow - cachedNvidiaSmiOutputUtc).TotalSeconds < 5)
            {
                return cachedNvidiaSmiOutput;
            }
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=name,gpu_bus_id,driver_version,vbios_version,compute_cap,pci.device_id,pci.sub_device_id,pci.bus_id,memory.total,memory.used,memory.free,temperature.gpu,power.limit,clocks.max.graphics,clocks.max.memory --format=csv,noheader,nounits",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null || !process.WaitForExit(2500))
                {
                    try
                    {
                        if (process != null)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                    }
                    return "";
                }

                var output = process.StandardOutput.ReadToEnd();
                lock (nvidiaSmiCacheLock)
                {
                    cachedNvidiaSmiOutput = output ?? "";
                    cachedNvidiaSmiOutputUtc = DateTime.UtcNow;
                }
                return output;
            }
        }
        catch
        {
            lock (nvidiaSmiCacheLock)
            {
                cachedNvidiaSmiOutput = "";
                cachedNvidiaSmiOutputUtc = DateTime.UtcNow;
            }
            return "";
        }
    }

    private static bool GpuNamesLikelyMatch(string left, string right)
    {
        left = NormalizeGpuNameForMatch(left);
        right = NormalizeGpuNameForMatch(right);
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            (left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0 ||
             right.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeGpuNameForMatch(string value)
    {
        value = value ?? "";
        foreach (var token in new[] { "nvidia", "amd", "radeon", "intel", "graphics", "gpu", "(r)", "(tm)" })
        {
            value = ReplaceOrdinalIgnoreCase(value, token, "");
        }

        return value.Trim();
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue))
        {
            return value ?? "";
        }

        var result = value;
        var index = result.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            result = result.Substring(0, index) + (newValue ?? "") + result.Substring(index + oldValue.Length);
            index = result.IndexOf(oldValue, index + (newValue ?? "").Length, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string NormalizeNvidiaSmiDeviceId(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(2);
        }

        return value.Length >= 4 ? value.Substring(0, 4).ToUpperInvariant() : value.ToUpperInvariant();
    }

    private static void AddMibDetail(Dictionary<string, string> details, string name, string value)
    {
        double mib;
        AddDetail(details, name, double.TryParse(value, out mib) ? FormatBytes(mib * 1024 * 1024) : value);
    }

    private static void AddUnitDetail(Dictionary<string, string> details, string name, string value, string suffix)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddDetail(details, name, value.Trim() + suffix);
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
