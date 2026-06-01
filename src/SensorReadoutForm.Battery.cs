using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

public sealed partial class SensorReadoutForm : Form
{
    private readonly object deviceBatteryRowsLock = new object();
    private DateTime deviceBatteryRowsLastReadUtc = DateTime.MinValue;
    private List<SensorRow> cachedDeviceBatteryRows = new List<SensorRow>();
    private readonly object wmiBatteryInfoLock = new object();
    private DateTime wmiBatteryInfoLastReadUtc = DateTime.MinValue;
    private Dictionary<int, WmiBatteryInfo> cachedWmiBatteryInfo = new Dictionary<int, WmiBatteryInfo>();

    private IEnumerable<SensorRow> GetBatteryRows()
    {
        var rows = new List<SensorRow>();
        var batteries = GetNativeBatteryInfo();
        var wmiInfo = GetWmiBatteryInfo();
        for (var i = 0; i < batteries.Count; i++)
        {
            var battery = batteries[i];
            var hardware = string.IsNullOrWhiteSpace(battery.Name) ? "Battery " + (i + 1) : battery.Name;
            WmiBatteryInfo wmi;
            if (!wmiInfo.TryGetValue(i, out wmi))
            {
                wmi = null;
            }

            var details = BuildBatteryDetails(battery, wmi, i);
            var percent = GetBatteryPercent(battery, wmi);
            if (percent.HasValue)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Charge",
                    Identifier = "battery/" + i + "/charge",
                    Value = (float)percent.Value,
                    DisplayValue = FormatNumber(Math.Round(percent.Value, 1), "0.0") + "%",
                    Source = "Windows Battery",
                    Details = CloneDetails(details)
                });
            }

            var status = BuildBatteryStatusText(battery, wmi);
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Status",
                    Identifier = "battery/" + i + "/status",
                    DisplayValue = status,
                    Source = "Windows Battery",
                    Details = CloneDetails(details)
                });
            }

            AddBatteryCapacityRows(rows, battery, hardware, i, details);
            if (battery.CycleCount > 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Cycle count",
                    Identifier = "battery/" + i + "/cycle-count",
                    Value = battery.CycleCount,
                    DisplayValue = battery.CycleCount.ToString(),
                    Source = "Windows Battery",
                    Details = CloneDetails(details)
                });
            }

            if (battery.VoltageMillivolts > 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Voltage",
                    Identifier = "battery/" + i + "/voltage",
                    Value = battery.VoltageMillivolts / 1000f,
                    DisplayValue = FormatNumber(Math.Round(battery.VoltageMillivolts / 1000.0, 2), "0.00") + " V",
                    Source = "Windows Battery",
                    Details = CloneDetails(details)
                });
            }

            if (battery.RateMilliwatts != int.MinValue && battery.RateMilliwatts != 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Power rate",
                    Identifier = "battery/" + i + "/power-rate",
                    Value = battery.RateMilliwatts / 1000f,
                    DisplayValue = FormatNumber(Math.Round(battery.RateMilliwatts / 1000.0, 2), "0.00") + " W",
                    Source = "Windows Battery",
                    Details = CloneDetails(details)
                });
            }
        }

        rows.AddRange(GetWindowsPowerMeterRows());
        rows.AddRange(GetDeviceBatteryRows());
        return rows;
    }

    private static List<SensorRow> GetWindowsPowerMeterRows()
    {
        var rows = new List<SensorRow>();
        rows.AddRange(ReadWindowsPowerMeters());
        rows.AddRange(ReadWindowsPowerSupplies());
        return rows;
    }

    private static List<SensorRow> ReadWindowsPowerMeters()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2\power", "SELECT * FROM Win32_PowerMeter"))
            {
                var index = 0;
                foreach (ManagementObject meter in searcher.Get())
                {
                    var details = ReadManagementObjectDetails(meter);
                    details["Namespace"] = @"root\cimv2\power";
                    details["WMI class"] = "Win32_PowerMeter";

                    var reading = ToNullableDouble(GetDictionaryValue(details, "CurrentReading"));
                    if (!reading.HasValue)
                    {
                        index++;
                        continue;
                    }

                    var baseUnits = ToNullableInt(GetDictionaryValue(details, "BaseUnits"));
                    var unitModifier = ToNullableInt(GetDictionaryValue(details, "UnitModifier"));
                    var watts = ApplyUnitModifier(reading.Value, unitModifier);
                    var name = FirstNonEmpty(GetDictionaryValue(details, "Name"), GetDictionaryValue(details, "ElementName"), GetDictionaryValue(details, "DeviceID"), "Power meter " + (index + 1));
                    var display = IsWattBaseUnit(baseUnits)
                        ? FormatNumber(Math.Round(watts, 2), "0.00") + " W"
                        : FormatNumber(Math.Round(watts, 2), "0.00") + " raw";

                    rows.Add(new SensorRow
                    {
                        Type = "Battery",
                        Hardware = name,
                        Name = IsWattBaseUnit(baseUnits) ? "Current power" : "Power meter reading",
                        Identifier = "power-meter/" + StableDeviceIdentifier(GetDictionaryValue(details, "DeviceID"), name) + "/current-power",
                        Value = (float)watts,
                        DisplayValue = display,
                        Source = "Windows Power Meter",
                        Details = details
                    });
                    index++;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static List<SensorRow> ReadWindowsPowerSupplies()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2\power", "SELECT * FROM Win32_PowerSupply"))
            {
                var index = 0;
                foreach (ManagementObject supply in searcher.Get())
                {
                    var details = ReadManagementObjectDetails(supply);
                    details["Namespace"] = @"root\cimv2\power";
                    details["WMI class"] = "Win32_PowerSupply";

                    var outputPower = ToNullableDouble(GetDictionaryValue(details, "TotalOutputPower"));
                    if (!outputPower.HasValue || outputPower.Value <= 0)
                    {
                        index++;
                        continue;
                    }

                    var name = FirstNonEmpty(GetDictionaryValue(details, "Name"), GetDictionaryValue(details, "ElementName"), GetDictionaryValue(details, "DeviceID"), "Power supply " + (index + 1));
                    rows.Add(new SensorRow
                    {
                        Type = "Battery",
                        Hardware = name,
                        Name = "Rated output power",
                        Identifier = "power-supply/" + StableDeviceIdentifier(GetDictionaryValue(details, "DeviceID"), name) + "/rated-output-power",
                        Value = (float)outputPower.Value,
                        DisplayValue = FormatNumber(Math.Round(outputPower.Value, 2), "0.00") + " W",
                        Source = "Windows Power Supply",
                        Details = details
                    });
                    index++;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private List<SensorRow> GetDeviceBatteryRows()
    {
        lock (deviceBatteryRowsLock)
        {
            if ((DateTime.UtcNow - deviceBatteryRowsLastReadUtc).TotalSeconds < 60)
            {
                return new List<SensorRow>(cachedDeviceBatteryRows);
            }
        }

        var rows = ReadDeviceBatteryRows();
        lock (deviceBatteryRowsLock)
        {
            cachedDeviceBatteryRows = rows;
            deviceBatteryRowsLastReadUtc = DateTime.UtcNow;
            return new List<SensorRow>(cachedDeviceBatteryRows);
        }
    }

    private static List<SensorRow> ReadDeviceBatteryRows()
    {
        var rows = new List<SensorRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID, PNPClass, Manufacturer, Service FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 0"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var name = Convert.ToString(device["Name"]) ?? "";
                    var deviceId = Convert.ToString(device["DeviceID"]) ?? "";
                    var pnpClass = Convert.ToString(device["PNPClass"]) ?? "";
                    var manufacturer = Convert.ToString(device["Manufacturer"]) ?? "";
                    var service = Convert.ToString(device["Service"]) ?? "";
                    if (!IsDeviceBatteryCandidate(name, deviceId, pnpClass))
                    {
                        continue;
                    }

                    int percent;
                    string propertyKey;
                    if (!TryGetDeviceBatteryPercent(device, out percent, out propertyKey))
                    {
                        continue;
                    }

                    var hardware = FriendlyDeviceBatteryName(name, manufacturer);
                    var unique = hardware + "|" + percent;
                    if (!seen.Add(unique))
                    {
                        continue;
                    }

                    var details = new Dictionary<string, string>();
                    details["Device ID"] = deviceId;
                    if (!string.IsNullOrWhiteSpace(pnpClass)) details["Class"] = pnpClass;
                    if (!string.IsNullOrWhiteSpace(manufacturer)) details["Manufacturer"] = manufacturer;
                    if (!string.IsNullOrWhiteSpace(service)) details["Service"] = service;
                    details["Windows property"] = propertyKey;

                    rows.Add(new SensorRow
                    {
                        Type = "Battery",
                        Hardware = hardware,
                        Name = "Charge",
                        Identifier = "device-battery/" + StableDeviceIdentifier(deviceId, hardware) + "/charge",
                        Value = percent,
                        DisplayValue = percent.ToString() + "%",
                        Source = "Windows Device Battery",
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

    private static bool IsDeviceBatteryCandidate(string name, string deviceId, string pnpClass)
    {
        var combined = ((name ?? "") + " " + (deviceId ?? "") + " " + (pnpClass ?? "")).ToLowerInvariant();
        return combined.Contains("bluetooth") ||
            combined.Contains("bth") ||
            combined.Contains("hid") ||
            combined.Contains("keyboard") ||
            combined.Contains("mouse") ||
            combined.Contains("headset") ||
            combined.Contains("headphones") ||
            combined.Contains("logitech");
    }

    private static bool TryGetDeviceBatteryPercent(ManagementObject device, out int percent, out string propertyKey)
    {
        percent = -1;
        propertyKey = "";
        try
        {
            var outParams = device.InvokeMethod("GetDeviceProperties", null, null);
            if (outParams == null)
            {
                return false;
            }

            var properties = outParams["deviceProperties"] as ManagementBaseObject[];
            if (properties == null)
            {
                return false;
            }

            foreach (var property in properties)
            {
                var key = Convert.ToString(property["KeyName"]) ?? "";
                if (!IsBatteryLifePropertyKey(key))
                {
                    continue;
                }

                var value = property["Data"];
                int parsed;
                if (TryParseDeviceBatteryPercent(value, out parsed))
                {
                    percent = parsed;
                    propertyKey = key;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsBatteryLifePropertyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.Equals("System.Devices.BatteryLife", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("{49CD1F76-5626-4B17-A4E8-18B4AA1A2213} 10", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDeviceBatteryPercent(object value, out int percent)
    {
        percent = -1;
        if (value == null)
        {
            return false;
        }

        try
        {
            percent = Convert.ToInt32(value);
        }
        catch
        {
            return false;
        }

        return percent >= 0 && percent <= 100;
    }

    private static string FriendlyDeviceBatteryName(string name, string manufacturer)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Device battery" : name.Trim();
        manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? "" : manufacturer.Trim();
        if (manufacturer.Length == 0 || name.IndexOf(manufacturer, StringComparison.OrdinalIgnoreCase) >= 0 || manufacturer.Equals("(Standard system devices)", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return name + " (" + manufacturer + ")";
    }

    private static string StableDeviceIdentifier(string deviceId, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(deviceId) ? fallback ?? "" : deviceId;
        var chars = source.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }

        var value = new string(chars).Trim('-');
        while (value.Contains("--"))
        {
            value = value.Replace("--", "-");
        }

        return string.IsNullOrWhiteSpace(value) ? "device" : value;
    }

    private static Dictionary<string, string> ReadManagementObjectDetails(ManagementObject item)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item == null)
        {
            return details;
        }

        foreach (PropertyData property in item.Properties)
        {
            if (property == null || property.Value == null)
            {
                continue;
            }

            details[property.Name] = FormatWmiObjectValue(property.Value);
        }

        return details;
    }

    private static string FormatWmiObjectValue(object value)
    {
        var array = value as Array;
        if (array != null)
        {
            var values = new List<string>();
            foreach (var item in array)
            {
                values.Add(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            }

            return string.Join(", ", values.ToArray());
        }

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static double? ToNullableDouble(string text)
    {
        double value;
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : (double?)null;
    }

    private static int? ToNullableInt(string text)
    {
        int value;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : (int?)null;
    }

    private static double ApplyUnitModifier(double value, int? unitModifier)
    {
        return unitModifier.HasValue ? value * Math.Pow(10, unitModifier.Value) : value;
    }

    private static bool IsWattBaseUnit(int? baseUnits)
    {
        // CIM_NumericSensor BaseUnits value 7 is Watts.
        return baseUnits.HasValue && baseUnits.Value == 7;
    }

    private static double? GetBatteryPercent(NativeBatteryInfo battery, WmiBatteryInfo wmi)
    {
        if (wmi != null && wmi.EstimatedChargeRemaining >= 0 && wmi.EstimatedChargeRemaining <= 100)
        {
            return wmi.EstimatedChargeRemaining;
        }

        if (battery.FullChargeCapacity > 0 && battery.CurrentCapacity >= 0)
        {
            return Math.Max(0, Math.Min(100, battery.CurrentCapacity * 100.0 / battery.FullChargeCapacity));
        }

        return null;
    }

    private static Dictionary<string, string> BuildBatteryDetails(NativeBatteryInfo battery, WmiBatteryInfo wmi, int index)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (battery != null)
        {
            AddDetail(details, "Battery index", index.ToString());
            AddDetail(details, "Battery name", battery.Name);
            AddDetail(details, "Battery manufacturer", battery.Manufacturer);
            AddDetail(details, "Battery serial number", battery.SerialNumber);
            AddDetail(details, "Battery unique ID", battery.UniqueId);
            AddDetail(details, "Battery chemistry", battery.Chemistry);
            AddDetail(details, "Designed capacity", battery.DesignedCapacity > 0 ? FormatNumber(battery.DesignedCapacity, "0") + " mWh" : "");
            AddDetail(details, "Full charge capacity", battery.FullChargeCapacity > 0 ? FormatNumber(battery.FullChargeCapacity, "0") + " mWh" : "");
            AddDetail(details, "Current capacity", battery.CurrentCapacity >= 0 ? FormatNumber(battery.CurrentCapacity, "0") + " mWh" : "");
            AddDetail(details, "Cycle count", battery.CycleCount > 0 ? battery.CycleCount.ToString() : "");
            AddDetail(details, "Voltage", battery.VoltageMillivolts > 0 ? FormatNumber(Math.Round(battery.VoltageMillivolts / 1000.0, 2), "0.00") + " V" : "");
            AddDetail(details, "Power rate", battery.RateMilliwatts != int.MinValue && battery.RateMilliwatts != 0 ? FormatNumber(Math.Round(battery.RateMilliwatts / 1000.0, 2), "0.00") + " W" : "");
            AddDetail(details, "Power state flags", battery.PowerState.ToString());
            AddDetail(details, "Native device path", battery.DevicePath);
        }

        if (wmi != null)
        {
            AddDetail(details, "WMI estimated charge remaining", wmi.EstimatedChargeRemaining >= 0 ? wmi.EstimatedChargeRemaining + "%" : "");
            AddDetail(details, "WMI battery status", DecodeWmiBatteryStatus(wmi.BatteryStatus));
            AddDetail(details, "WMI status", wmi.Status);
            if (wmi.RawDetails != null)
            {
                foreach (var pair in wmi.RawDetails)
                {
                    AddDetail(details, pair.Key, pair.Value);
                }
            }
        }

        return details;
    }

    private static void AddBatteryCapacityRows(List<SensorRow> rows, NativeBatteryInfo battery, string hardware, int index, Dictionary<string, string> details)
    {
        if (battery.CurrentCapacity >= 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Current capacity", "current-capacity", battery.CurrentCapacity, details));
        }

        if (battery.FullChargeCapacity > 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Full charge capacity", "full-charge-capacity", battery.FullChargeCapacity, details));
        }

        if (battery.DesignedCapacity > 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Design capacity", "design-capacity", battery.DesignedCapacity, details));
        }

        if (battery.FullChargeCapacity > 0 && battery.DesignedCapacity > 0)
        {
            var health = Math.Max(0, Math.Min(100, battery.FullChargeCapacity * 100.0 / battery.DesignedCapacity));
            rows.Add(new SensorRow
            {
                Type = "Battery",
                Hardware = hardware,
                Name = "Battery health",
                Identifier = "battery/" + index + "/health",
                Value = (float)health,
                DisplayValue = FormatNumber(Math.Round(health, 1), "0.0") + "%",
                Source = "Windows Battery",
                Details = CloneDetails(details)
            });
        }
    }

    private static SensorRow CreateBatteryCapacityRow(string hardware, int index, string name, string id, int milliwattHours, Dictionary<string, string> details)
    {
        return new SensorRow
        {
            Type = "Battery",
            Hardware = hardware,
            Name = name,
            Identifier = "battery/" + index + "/" + id,
            Value = milliwattHours,
            DisplayValue = FormatNumber(milliwattHours, "0") + " mWh",
            Source = "Windows Battery",
            Details = CloneDetails(details)
        };
    }

    private static string BuildBatteryStatusText(NativeBatteryInfo battery, WmiBatteryInfo wmi)
    {
        var parts = new List<string>();
        if (battery.PowerState.HasFlag(BatteryPowerState.Charging))
        {
            parts.Add("Charging");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Discharging))
        {
            parts.Add("Discharging");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Online))
        {
            parts.Add("AC connected");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Critical))
        {
            parts.Add("Critical");
        }

        if (parts.Count == 0 && wmi != null && !string.IsNullOrWhiteSpace(wmi.Status))
        {
            parts.Add(wmi.Status);
        }

        return string.Join(", ", parts.ToArray());
    }

    private static string FormatBatteryChemistry(byte[] chemistry)
    {
        if (chemistry == null || chemistry.Length == 0)
        {
            return "";
        }

        var chars = new List<char>();
        foreach (var value in chemistry)
        {
            if (value == 0)
            {
                continue;
            }

            chars.Add((char)value);
        }

        return new string(chars.ToArray()).Trim();
    }

    private static string DecodeWmiBatteryStatus(int status)
    {
        switch (status)
        {
            case 1: return "Discharging";
            case 2: return "AC connected";
            case 3: return "Fully charged";
            case 4: return "Low";
            case 5: return "Critical";
            case 6: return "Charging";
            case 7: return "Charging and high";
            case 8: return "Charging and low";
            case 9: return "Charging and critical";
            case 10: return "Undefined";
            case 11: return "Partially charged";
            default: return status > 0 ? status.ToString() : "";
        }
    }

    private static List<NativeBatteryInfo> GetNativeBatteryInfo()
    {
        var result = new List<NativeBatteryInfo>();
        var deviceInfoSet = IntPtr.Zero;
        try
        {
            var batteryGuid = BatteryDeviceInterfaceGuid;
            deviceInfoSet = BatterySetupDiGetClassDevs(ref batteryGuid, null, IntPtr.Zero, DeviceGetClassFlags.Present | DeviceGetClassFlags.DeviceInterface);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
            {
                return result;
            }

            for (uint index = 0; ; index++)
            {
                var interfaceData = new DeviceInterfaceData();
                interfaceData.Size = Marshal.SizeOf(typeof(DeviceInterfaceData));
                if (!BatterySetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref batteryGuid, index, ref interfaceData))
                {
                    break;
                }

                string devicePath;
                if (!TryGetBatteryDevicePath(deviceInfoSet, interfaceData, out devicePath))
                {
                    continue;
                }

                NativeBatteryInfo battery;
                if (TryReadNativeBattery(devicePath, (int)index, out battery))
                {
                    result.Add(battery);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (deviceInfoSet != IntPtr.Zero && deviceInfoSet.ToInt64() != -1)
            {
                BatterySetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        return result;
    }

    private static bool TryGetBatteryDevicePath(IntPtr deviceInfoSet, DeviceInterfaceData interfaceData, out string path)
    {
        path = "";
        var detailData = new DeviceInterfaceDetailData();
        detailData.Size = IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize;
        uint requiredSize;
        if (!BatterySetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, ref detailData, 1024, out requiredSize, IntPtr.Zero))
        {
            return false;
        }

        path = detailData.DevicePath;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryReadNativeBattery(string devicePath, int index, out NativeBatteryInfo info)
    {
        info = null;
        using (var handle = BatteryCreateFile(devicePath, FileAccess.Read, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero))
        {
            if (handle == null || handle.IsInvalid)
            {
                return false;
            }

            uint tag = 0;
            uint bytesReturned;
            uint zero = 0;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryTag, ref zero, 0, ref tag, (uint)Marshal.SizeOf(typeof(uint)), out bytesReturned, IntPtr.Zero) || tag == 0)
            {
                return false;
            }

            var batteryInfo = new BatteryInformationNative();
            if (!TryQueryBatteryInformation(handle, tag, BatteryQueryInformationLevel.BatteryInformation, ref batteryInfo))
            {
                return false;
            }

            var status = new BatteryStatusNative();
            TryQueryBatteryStatus(handle, tag, ref status);

            var name = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryDeviceName);
            var manufacturer = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryManufactureName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = manufacturer;
            }

            info = new NativeBatteryInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Battery " + (index + 1) : name.Trim(),
                Manufacturer = manufacturer,
                SerialNumber = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatterySerialNumber),
                UniqueId = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryUniqueId),
                Chemistry = FormatBatteryChemistry(batteryInfo.Chemistry),
                DevicePath = devicePath,
                DesignedCapacity = batteryInfo.DesignedCapacity,
                FullChargeCapacity = batteryInfo.FullChargedCapacity,
                CycleCount = batteryInfo.CycleCount,
                PowerState = status.PowerState,
                CurrentCapacity = status.Capacity,
                VoltageMillivolts = status.Voltage,
                RateMilliwatts = status.Rate
            };
            return true;
        }
    }

    private static bool TryQueryBatteryInformation<T>(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level, ref T value) where T : struct
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var querySize = Marshal.SizeOf(typeof(BatteryQueryInformation));
        var valueSize = Marshal.SizeOf(typeof(T));
        var queryPointer = Marshal.AllocHGlobal(querySize);
        var valuePointer = Marshal.AllocHGlobal(valueSize);
        try
        {
            Marshal.StructureToPtr(query, queryPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryInformation, queryPointer, (uint)querySize, valuePointer, (uint)valueSize, out bytesReturned, IntPtr.Zero))
            {
                return false;
            }

            value = (T)Marshal.PtrToStructure(valuePointer, typeof(T));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(queryPointer);
            Marshal.FreeHGlobal(valuePointer);
        }
    }

    private static string QueryBatteryString(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var querySize = Marshal.SizeOf(typeof(BatteryQueryInformation));
        var queryPointer = Marshal.AllocHGlobal(querySize);
        var valuePointer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.StructureToPtr(query, queryPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryInformation, queryPointer, (uint)querySize, valuePointer, 512, out bytesReturned, IntPtr.Zero))
            {
                return "";
            }

            return Marshal.PtrToStringUni(valuePointer) ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(queryPointer);
            Marshal.FreeHGlobal(valuePointer);
        }
    }

    private static bool TryQueryBatteryStatus(SafeFileHandle handle, uint tag, ref BatteryStatusNative status)
    {
        var waitStatus = new BatteryWaitStatus { BatteryTag = tag };
        var waitSize = Marshal.SizeOf(typeof(BatteryWaitStatus));
        var statusSize = Marshal.SizeOf(typeof(BatteryStatusNative));
        var waitPointer = Marshal.AllocHGlobal(waitSize);
        var statusPointer = Marshal.AllocHGlobal(statusSize);
        try
        {
            Marshal.StructureToPtr(waitStatus, waitPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryStatus, waitPointer, (uint)waitSize, statusPointer, (uint)statusSize, out bytesReturned, IntPtr.Zero))
            {
                return false;
            }

            status = (BatteryStatusNative)Marshal.PtrToStructure(statusPointer, typeof(BatteryStatusNative));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(waitPointer);
            Marshal.FreeHGlobal(statusPointer);
        }
    }

    private Dictionary<int, WmiBatteryInfo> GetWmiBatteryInfo()
    {
        lock (wmiBatteryInfoLock)
        {
            if ((DateTime.UtcNow - wmiBatteryInfoLastReadUtc).TotalSeconds < 60)
            {
                return CloneWmiBatteryInfo(cachedWmiBatteryInfo);
            }
        }

        var result = new Dictionary<int, WmiBatteryInfo>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery"))
            {
                var index = 0;
                foreach (ManagementObject battery in searcher.Get())
                {
                    var rawDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AddRawWmiDetails(rawDetails, "Battery WMI", battery);
                    result[index] = new WmiBatteryInfo
                    {
                        EstimatedChargeRemaining = ToInt(battery["EstimatedChargeRemaining"], -1),
                        BatteryStatus = ToInt(battery["BatteryStatus"], 0),
                        Status = Convert.ToString(battery["Status"]) ?? "",
                        RawDetails = rawDetails
                    };
                    index++;
                }
            }
        }
        catch
        {
        }

        lock (wmiBatteryInfoLock)
        {
            cachedWmiBatteryInfo = CloneWmiBatteryInfo(result);
            wmiBatteryInfoLastReadUtc = DateTime.UtcNow;
            return CloneWmiBatteryInfo(cachedWmiBatteryInfo);
        }
    }

    private static Dictionary<int, WmiBatteryInfo> CloneWmiBatteryInfo(Dictionary<int, WmiBatteryInfo> source)
    {
        var result = new Dictionary<int, WmiBatteryInfo>();
        foreach (var item in source ?? new Dictionary<int, WmiBatteryInfo>())
        {
            var value = item.Value;
            if (value == null)
            {
                continue;
            }

            result[item.Key] = new WmiBatteryInfo
            {
                EstimatedChargeRemaining = value.EstimatedChargeRemaining,
                BatteryStatus = value.BatteryStatus,
                Status = value.Status,
                RawDetails = CloneDetails(value.RawDetails)
            };
        }

        return result;
    }

    private static int ToInt(object value, int fallback)
    {
        if (value == null)
        {
            return fallback;
        }

        int parsed;
        return int.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
    }

    private static readonly Guid BatteryDeviceInterfaceGuid = new Guid(0x72631E54, 0x78A4, 0x11D0, 0xBC, 0xF7, 0x00, 0xAA, 0x00, 0xB7, 0xB3, 0x2A);
    private const uint IoctlBatteryQueryTag = (0x29u << 16) | (1u << 14) | (0x10u << 2);
    private const uint IoctlBatteryQueryInformation = (0x29u << 16) | (1u << 14) | (0x11u << 2);
    private const uint IoctlBatteryQueryStatus = (0x29u << 16) | (1u << 14) | (0x13u << 2);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BatterySetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, DeviceGetClassFlags flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    private static extern bool BatterySetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool BatterySetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref DeviceInterfaceData deviceInterfaceData, ref DeviceInterfaceDetailData deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    private static extern bool BatterySetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle BatteryCreateFile(string fileName, FileAccess desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, FileAttributes flags, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool BatteryDeviceIoControl(SafeFileHandle handle, uint controlCode, ref uint inBuffer, uint inBufferSize, ref uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool BatteryDeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [Flags]
    private enum DeviceGetClassFlags
    {
        Present = 0x00000002,
        DeviceInterface = 0x00000010
    }

    private enum BatteryQueryInformationLevel
    {
        BatteryInformation = 0,
        BatteryDeviceName = 4,
        BatteryManufactureName = 6,
        BatteryUniqueId = 7,
        BatterySerialNumber = 8
    }

    [Flags]
    private enum BatteryPowerState : uint
    {
        Online = 0x00000001,
        Discharging = 0x00000002,
        Charging = 0x00000004,
        Critical = 0x00000008
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInterfaceDetailData
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public BatteryQueryInformationLevel InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformationNative
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;
        public int DesignedCapacity;
        public int FullChargedCapacity;
        public int DefaultAlert1;
        public int DefaultAlert2;
        public int CriticalBias;
        public int CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public BatteryPowerState PowerState;
        public int LowCapacity;
        public int HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatusNative
    {
        public BatteryPowerState PowerState;
        public int Capacity;
        public int Voltage;
        public int Rate;
    }

    private sealed class NativeBatteryInfo
    {
        public string Name;
        public string Manufacturer;
        public string SerialNumber;
        public string UniqueId;
        public string Chemistry;
        public string DevicePath;
        public int DesignedCapacity;
        public int FullChargeCapacity;
        public int CycleCount;
        public BatteryPowerState PowerState;
        public int CurrentCapacity = -1;
        public int VoltageMillivolts;
        public int RateMilliwatts = int.MinValue;
    }

    private sealed class WmiBatteryInfo
    {
        public int EstimatedChargeRemaining = -1;
        public int BatteryStatus;
        public string Status;
        public Dictionary<string, string> RawDetails;
    }
}
