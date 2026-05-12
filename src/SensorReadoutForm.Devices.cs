using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

public sealed partial class SensorReadoutForm
{
    private sealed class DeviceDriverInfo
    {
        public string DeviceId = "";
        public string DeviceClass = "";
        public string DriverProviderName = "";
        public string DriverVersion = "";
        public string DriverDate = "";
        public string DriverName = "";
        public string InfName = "";
        public string IsSigned = "";
        public string Signer = "";
        public string DeviceName = "";
        public string Manufacturer = "";
        public string FriendlyName = "";
        public string Description = "";
        public string HardwareId = "";
        public string CompatibleId = "";
        public string Location = "";
        public string Pdo = "";
        public string Started = "";
        public string StartMode = "";
        public string State = "";
        public string Status = "";
        public string SystemName = "";
    }

    private IEnumerable<SensorRow> GetDeviceInventoryRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            var drivers = GetSignedDriverInfoByDeviceId();
            var resources = GetAllocatedResourcesByDeviceId();
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = CleanWmiText(GetWmiPropertyText(device, "PNPDeviceID"));
                    var name = CleanWmiText(GetWmiPropertyText(device, "Name"));
                    if (ShouldSkipDeviceInventoryRow(device, deviceId, name))
                    {
                        continue;
                    }

                    var pnpClass = CleanWmiText(GetWmiPropertyText(device, "PNPClass"));
                    var group = FriendlyDeviceGroup(pnpClass, deviceId, name);
                    var details = BuildDeviceDetails(device, drivers, resources, deviceId, name, group, pnpClass);
                    AddDeviceInventoryRows(rows, group, name, deviceId, details);
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage("Debug", "Device inventory failed. " + ex.Message);
        }

        return rows;
    }

    private static bool ShouldSkipDeviceInventoryRow(ManagementObject device, string deviceId, string name)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var pnpClass = CleanWmiText(GetWmiPropertyText(device, "PNPClass"));
        if (pnpClass.Equals("SoftwareDevice", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("VolumeSnapshot", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Volume", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("AudioProcessingObject", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("MidiEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (deviceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith("STORAGE\\VOLUME", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, DeviceDriverInfo> GetSignedDriverInfoByDeviceId()
    {
        var drivers = new Dictionary<string, DeviceDriverInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPSignedDriver"))
            {
                foreach (ManagementObject driver in searcher.Get())
                {
                    var deviceId = CleanWmiText(GetWmiPropertyText(driver, "DeviceID"));
                    if (string.IsNullOrWhiteSpace(deviceId) || drivers.ContainsKey(deviceId))
                    {
                        continue;
                    }

                    drivers[deviceId] = new DeviceDriverInfo
                    {
                        DeviceId = deviceId,
                        DeviceClass = CleanWmiText(GetWmiPropertyText(driver, "DeviceClass")),
                        DriverProviderName = CleanWmiText(GetWmiPropertyText(driver, "DriverProviderName")),
                        DriverVersion = CleanWmiText(GetWmiPropertyText(driver, "DriverVersion")),
                        DriverDate = FormatWmiDate(GetWmiPropertyValue(driver, "DriverDate")),
                        DriverName = CleanWmiText(GetWmiPropertyText(driver, "DriverName")),
                        InfName = CleanWmiText(GetWmiPropertyText(driver, "InfName")),
                        IsSigned = FormatYesNo(GetWmiPropertyValue(driver, "IsSigned")),
                        Signer = CleanWmiText(GetWmiPropertyText(driver, "Signer")),
                        DeviceName = CleanWmiText(GetWmiPropertyText(driver, "DeviceName")),
                        Manufacturer = CleanWmiText(GetWmiPropertyText(driver, "Manufacturer")),
                        FriendlyName = CleanWmiText(GetWmiPropertyText(driver, "FriendlyName")),
                        Description = CleanWmiText(GetWmiPropertyText(driver, "Description")),
                        HardwareId = CleanWmiText(GetWmiPropertyText(driver, "HardWareID")),
                        CompatibleId = CleanWmiText(GetWmiPropertyText(driver, "CompatID")),
                        Location = CleanWmiText(GetWmiPropertyText(driver, "Location")),
                        Pdo = CleanWmiText(GetWmiPropertyText(driver, "PDO")),
                        Started = FormatYesNo(GetWmiPropertyValue(driver, "Started")),
                        StartMode = CleanWmiText(GetWmiPropertyText(driver, "StartMode")),
                        State = CleanWmiText(GetWmiPropertyText(driver, "State")),
                        Status = CleanWmiText(GetWmiPropertyText(driver, "Status")),
                        SystemName = CleanWmiText(GetWmiPropertyText(driver, "SystemName"))
                    };
                }
            }
        }
        catch
        {
        }

        return drivers;
    }

    private static Dictionary<string, List<string>> GetAllocatedResourcesByDeviceId()
    {
        var resources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_PNPAllocatedResource"))
            {
                foreach (ManagementObject resource in searcher.Get())
                {
                    var deviceId = DeviceIdFromAllocatedResourcePath(Convert.ToString(GetWmiPropertyValue(resource, "Dependent")));
                    var antecedent = Convert.ToString(GetWmiPropertyValue(resource, "Antecedent"));
                    var resourceText = ResourceTextFromAllocatedResourcePath(antecedent);
                    if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(resourceText))
                    {
                        continue;
                    }

                    List<string> list;
                    if (!resources.TryGetValue(deviceId, out list))
                    {
                        list = new List<string>();
                        resources[deviceId] = list;
                    }

                    if (!list.Contains(resourceText))
                    {
                        list.Add(resourceText);
                    }
                }
            }
        }
        catch
        {
        }

        return resources;
    }

    private static string DeviceIdFromAllocatedResourcePath(string path)
    {
        var value = WmiPathKeyValue(path, "DeviceID");
        return value.Replace("\\\\", "\\").Trim();
    }

    private static string ResourceTextFromAllocatedResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var className = path;
        var dot = className.IndexOf('.');
        if (dot >= 0)
        {
            className = className.Substring(0, dot);
        }

        var resourceId = WmiPathKeyValue(path, "Name");
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            resourceId = WmiPathKeyValue(path, "StartingAddress");
        }

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            resourceId = WmiPathKeyValue(path, "IRQNumber");
        }

        return string.IsNullOrWhiteSpace(resourceId) ? className : className + " " + resourceId;
    }

    private static string WmiPathKeyValue(string path, string key)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var match = Regex.Match(path, Regex.Escape(key) + "=\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["value"].Value.Replace("\\\"", "\"");
        }

        match = Regex.Match(path, Regex.Escape(key) + "=(?<value>[^,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"') : "";
    }

    private static Dictionary<string, string> BuildDeviceDetails(
        ManagementObject device,
        Dictionary<string, DeviceDriverInfo> drivers,
        Dictionary<string, List<string>> resources,
        string deviceId,
        string name,
        string group,
        string pnpClass)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDeviceDetail(details, "Device name", name);
        AddDeviceDetail(details, "Device group", group);
        AddDeviceDetail(details, "Windows class", pnpClass);
        AddDeviceDetail(details, "Description", CleanWmiText(GetWmiPropertyText(device, "Description")));
        AddDeviceDetail(details, "Caption", CleanWmiText(GetWmiPropertyText(device, "Caption")));
        AddDeviceDetail(details, "Manufacturer", CleanWmiText(GetWmiPropertyText(device, "Manufacturer")));
        AddDeviceDetail(details, "Status", CleanWmiText(GetWmiPropertyText(device, "Status")));
        AddDeviceDetail(details, "Service", CleanWmiText(GetWmiPropertyText(device, "Service")));
        AddDeviceDetail(details, "Availability", CleanWmiText(Convert.ToString(GetWmiPropertyValue(device, "Availability"))));
        AddDeviceDetail(details, "Error cleared", FormatYesNo(GetWmiPropertyValue(device, "ErrorCleared")));
        AddDeviceDetail(details, "Error description", CleanWmiText(GetWmiPropertyText(device, "ErrorDescription")));
        AddDeviceDetail(details, "Install date", FormatWmiDate(GetWmiPropertyValue(device, "InstallDate")));
        AddDeviceDetail(details, "Last error code", CleanWmiText(Convert.ToString(GetWmiPropertyValue(device, "LastErrorCode"))));
        AddDeviceDetail(details, "Power management supported", FormatYesNo(GetWmiPropertyValue(device, "PowerManagementSupported")));
        AddDeviceDetail(details, "Power management capabilities", StringArrayWmiValue(GetWmiPropertyValue(device, "PowerManagementCapabilities")));
        AddDeviceDetail(details, "Present", FormatYesNo(GetWmiPropertyValue(device, "Present")));
        AddDeviceDetail(details, "System name", CleanWmiText(GetWmiPropertyText(device, "SystemName")));
        AddDeviceDetail(details, "Creation class", CleanWmiText(GetWmiPropertyText(device, "CreationClassName")));
        AddDeviceDetail(details, "System creation class", CleanWmiText(GetWmiPropertyText(device, "SystemCreationClassName")));
        AddDeviceDetail(details, "PNP device ID", deviceId);
        AddDeviceDetail(details, "Device ID", CleanWmiText(GetWmiPropertyText(device, "DeviceID")));
        AddDeviceDetail(details, "Class GUID", CleanWmiText(GetWmiPropertyText(device, "ClassGuid")));
        AddDeviceDetail(details, "Config manager error code", CleanWmiText(Convert.ToString(GetWmiPropertyValue(device, "ConfigManagerErrorCode"))));
        AddDeviceDetail(details, "Config manager user config", FormatYesNo(GetWmiPropertyValue(device, "ConfigManagerUserConfig")));
        AddDeviceDetail(details, "Hardware IDs", StringArrayWmiValue(GetWmiPropertyValue(device, "HardwareID")));
        AddDeviceDetail(details, "Compatible IDs", StringArrayWmiValue(GetWmiPropertyValue(device, "CompatibleID")));
        AddPciDetails(details, deviceId);

        DeviceDriverInfo driver;
        if (drivers != null && drivers.TryGetValue(deviceId, out driver))
        {
            AddDeviceDetail(details, "Driver class", driver.DeviceClass);
            AddDeviceDetail(details, "Driver provider", driver.DriverProviderName);
            AddDeviceDetail(details, "Driver version", driver.DriverVersion);
            AddDeviceDetail(details, "Driver date", driver.DriverDate);
            AddDeviceDetail(details, "Driver name", driver.DriverName);
            AddDeviceDetail(details, "INF name", driver.InfName);
            AddDeviceDetail(details, "Driver signed", driver.IsSigned);
            AddDeviceDetail(details, "Driver signer", driver.Signer);
            AddDeviceDetail(details, "Driver device name", driver.DeviceName);
            AddDeviceDetail(details, "Driver manufacturer", driver.Manufacturer);
            AddDeviceDetail(details, "Driver friendly name", driver.FriendlyName);
            AddDeviceDetail(details, "Driver description", driver.Description);
            AddDeviceDetail(details, "Driver hardware ID", driver.HardwareId);
            AddDeviceDetail(details, "Driver compatible ID", driver.CompatibleId);
            AddDeviceDetail(details, "Driver location", driver.Location);
            AddDeviceDetail(details, "Driver PDO", driver.Pdo);
            AddDeviceDetail(details, "Driver started", driver.Started);
            AddDeviceDetail(details, "Driver start mode", driver.StartMode);
            AddDeviceDetail(details, "Driver state", driver.State);
            AddDeviceDetail(details, "Driver status", driver.Status);
            AddDeviceDetail(details, "Driver system name", driver.SystemName);
        }

        List<string> resourceList;
        if (resources != null && resources.TryGetValue(deviceId, out resourceList) && resourceList.Count > 0)
        {
            AddDeviceDetail(details, "Resource assignments", string.Join("; ", resourceList.OrderBy(r => r).ToArray()));
        }

        AddDeviceRegistryDetails(details, deviceId);
        return details;
    }

    private static void AddDeviceRegistryDetails(Dictionary<string, string> details, string deviceId)
    {
        if (details == null || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceId))
            {
                if (key == null)
                {
                    return;
                }

                AddRegistryDeviceValue(details, key, "Registry device description", "DeviceDesc");
                AddRegistryDeviceValue(details, key, "Registry friendly name", "FriendlyName");
                AddRegistryDeviceValue(details, key, "Registry manufacturer", "Mfg");
                AddRegistryDeviceValue(details, key, "Registry service", "Service");
                AddRegistryDeviceValue(details, key, "Registry class", "Class");
                AddRegistryDeviceValue(details, key, "Registry class GUID", "ClassGUID");
                AddRegistryDeviceValue(details, key, "Registry location", "LocationInformation");
                AddRegistryDeviceValue(details, key, "Registry container ID", "ContainerID");
                AddRegistryDeviceValue(details, key, "Registry parent ID prefix", "ParentIdPrefix");
                AddRegistryDeviceValue(details, key, "Registry hardware IDs", "HardwareID");
                AddRegistryDeviceValue(details, key, "Registry compatible IDs", "CompatibleIDs");
                AddRegistryDeviceValue(details, key, "Registry capabilities", "Capabilities");
                AddRegistryDeviceValue(details, key, "Registry config flags", "ConfigFlags");
                AddRegistryDeviceValue(details, key, "Registry problem", "Problem");
                AddRegistryDeviceValue(details, key, "Registry status flags", "StatusFlags");
                AddRegistryDeviceValue(details, key, "Registry address", "Address");
                AddRegistryDeviceValue(details, key, "Registry bus number", "BusNumber");
                AddRegistryDeviceValue(details, key, "Registry enumerator", "EnumeratorName");
                AddRegistryDeviceParameters(details, key);
            }
        }
        catch
        {
        }
    }

    private static void AddRegistryDeviceValue(Dictionary<string, string> details, RegistryKey key, string label, string valueName)
    {
        try
        {
            AddDeviceDetail(details, label, RegistryValueToString(key.GetValue(valueName)));
        }
        catch
        {
        }
    }

    private static void AddRegistryDeviceParameters(Dictionary<string, string> details, RegistryKey key)
    {
        try
        {
            using (var parameters = key.OpenSubKey("Device Parameters"))
            {
                if (parameters == null)
                {
                    return;
                }

                foreach (var name in parameters.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    AddDeviceDetail(details, "Registry device parameter " + name, RegistryValueToString(parameters.GetValue(name)));
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPciDetails(Dictionary<string, string> details, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddDeviceDetail(details, "PCI vendor ID", RegexGroup(deviceId, "VEN_([0-9A-Fa-f]{4})"));
        AddDeviceDetail(details, "PCI device ID", RegexGroup(deviceId, "DEV_([0-9A-Fa-f]{4})"));
        AddDeviceDetail(details, "PCI subsystem ID", RegexGroup(deviceId, "SUBSYS_([0-9A-Fa-f]{8})"));
        AddDeviceDetail(details, "PCI revision", RegexGroup(deviceId, "REV_([0-9A-Fa-f]{2})"));
    }

    private static string RegexGroup(string text, string pattern)
    {
        var match = Regex.Match(text ?? "", pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static string StringArrayWmiValue(object value)
    {
        if (value == null)
        {
            return "";
        }

        var array = value as string[];
        if (array != null)
        {
            return string.Join("; ", array.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray());
        }

        return CleanWmiText(Convert.ToString(value));
    }

    private static void AddDeviceInventoryRows(List<SensorRow> rows, string group, string name, string deviceId, Dictionary<string, string> details)
    {
        AddDeviceInventoryRow(rows, group, name, deviceId, BuildDeviceInventorySummary(details, deviceId), details);
    }

    private static void AddDeviceInventoryRow(List<SensorRow> rows, string group, string name, string deviceId, string summary, Dictionary<string, string> details)
    {
        if (rows == null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Devices",
            Hardware = group,
            Name = name.Trim(),
            Identifier = "device/" + StableDeviceInventoryKey(deviceId),
            DisplayValue = summary,
            Source = "Windows PnP",
            Details = details == null ? null : new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
        });
    }

    private static string BuildDeviceInventorySummary(Dictionary<string, string> details, string deviceId)
    {
        var parts = new List<string>();
        AddDeviceSummaryPart(parts, DetailValue(details, "Status"));
        AddDeviceSummaryPart(parts, DetailValue(details, "Manufacturer"));
        AddDeviceSummaryPart(parts, DetailValue(details, "Windows class"));
        AddDeviceSummaryPart(parts, DeviceBusName(deviceId));

        var errorCode = DetailValue(details, "Config manager error code");
        if (!string.IsNullOrWhiteSpace(errorCode) && errorCode != "0")
        {
            AddDeviceSummaryPart(parts, "Problem code " + errorCode);
        }

        return string.Join(", ", parts.ToArray());
    }

    private static void AddDeviceSummaryPart(List<string> parts, string value)
    {
        if (parts == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        value = value.Trim();
        if (!parts.Any(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(value);
        }
    }

    private static string StableDeviceInventoryKey(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return Regex.Replace(value, "[^a-z0-9]+", "-").Trim('-');
    }

    private static string DetailValue(Dictionary<string, string> details, string key)
    {
        string value;
        return details != null && details.TryGetValue(key, out value) ? value : "";
    }

    private static void AddDeviceDetail(Dictionary<string, string> details, string key, string value)
    {
        if (details == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        details[key] = value.Trim();
    }

    private static string DeviceBusName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "";
        }

        var separator = deviceId.IndexOf('\\');
        return separator > 0 ? deviceId.Substring(0, separator).ToUpperInvariant() : "";
    }

    private static string FriendlyDeviceGroup(string pnpClass, string deviceId, string name)
    {
        pnpClass = (pnpClass ?? "").Trim();
        deviceId = deviceId ?? "";
        name = name ?? "";

        if (pnpClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("HDC", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("CDROM", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("storage", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DeviceGroupText("group.Device storage", "Storage devices and controllers");
        }

        if (pnpClass.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("PointingDevice", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device input", "Input devices");
        }

        if (pnpClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device bluetooth", "Bluetooth");
        }

        if (pnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) && name.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DeviceGroupText("group.Device imaging", "Cameras and imaging");
        }

        if (pnpClass.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Printer", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device printers", "Printers");
        }

        if (pnpClass.Equals("SecurityDevices", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device security", "Security devices");
        }

        if (pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device network", "Network devices");
        }

        if (pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DeviceGroupText("group.Device audio", "Audio devices");
        }

        if (pnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device display", "Display devices");
        }

        if (pnpClass.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("USBDevice", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device USB", "USB devices and controllers");
        }

        if (pnpClass.Equals("Processor", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("Computer", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceGroupText("group.Device PCI and system", "PCI and system devices");
        }

        if (deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            pnpClass.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DeviceGroupText("group.Device PCI and system", "PCI and system devices");
        }

        return DeviceGroupText("group.Device other", "Other devices");
    }

    private static string DeviceGroupText(string key, string fallback)
    {
        return T(key, fallback);
    }
}
