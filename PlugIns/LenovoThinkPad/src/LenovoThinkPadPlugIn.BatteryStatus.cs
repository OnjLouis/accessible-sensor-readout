using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed partial class LenovoThinkPadPlugIn
    {
        private static IEnumerable<SensorReading> ReadAcpiBatteryStatus(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = CreateSearcher(@"root\wmi", "SELECT * FROM BatteryStatus"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        count++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = @"root\wmi";
                        details["WMI class"] = "BatteryStatus";

                        var tag = FirstValue(details, "Tag", "InstanceName");
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            tag = count.ToString(CultureInfo.InvariantCulture);
                        }

                        var idPrefix = "acpi/battery/" + tag + "/";

                        var chargeRate = FirstNumber(details, "ChargeRate");
                        if (chargeRate.HasValue && chargeRate.Value >= 0 && chargeRate.Value < 1000000)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Charge rate",
                                Identifier = StableIdentifier(idPrefix + "charge-rate-mw"),
                                Value = (float)chargeRate.Value,
                                DisplayValue = FormatNumber(chargeRate.Value) + " mW",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var dischargeRate = FirstNumber(details, "DischargeRate");
                        if (dischargeRate.HasValue && dischargeRate.Value >= 0 && dischargeRate.Value < 1000000)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Discharge rate",
                                Identifier = StableIdentifier(idPrefix + "discharge-rate-mw"),
                                Value = (float)dischargeRate.Value,
                                DisplayValue = FormatNumber(dischargeRate.Value) + " mW",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var remaining = FirstNumber(details, "RemainingCapacity");
                        if (remaining.HasValue && remaining.Value > 0 && remaining.Value < 1000000)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Remaining capacity",
                                Identifier = StableIdentifier(idPrefix + "remaining-capacity-mwh"),
                                Value = (float)remaining.Value,
                                DisplayValue = FormatNumber(remaining.Value) + " mWh",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var voltage = FirstNumber(details, "Voltage");
                        if (voltage.HasValue && voltage.Value > 0 && voltage.Value < 30000)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Voltage",
                                Identifier = StableIdentifier(idPrefix + "voltage-mv"),
                                Value = (float)voltage.Value,
                                DisplayValue = FormatNumber(voltage.Value) + " mV",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }

                    summaryDetails["BatteryStatus instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["BatteryStatus error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\wmi\\BatteryStatus: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadAcpiBatteryRuntime(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = CreateSearcher(@"root\wmi", "SELECT * FROM BatteryRuntime"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        count++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = @"root\wmi";
                        details["WMI class"] = "BatteryRuntime";

                        var tag = FirstValue(details, "Tag", "InstanceName");
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            tag = count.ToString(CultureInfo.InvariantCulture);
                        }

                        var runtime = FirstNumber(details, "EstimatedRuntime");
                        // ACPI sentinel for "battery not discharging / runtime unknown"
                        if (!runtime.HasValue || runtime.Value <= 0 || runtime.Value >= 4294967295)
                        {
                            continue;
                        }

                        var minutes = runtime.Value / 60.0;
                        rows.Add(new SensorReading
                        {
                            Type = "Battery",
                            Hardware = "ACPI battery",
                            Name = "Estimated runtime",
                            Identifier = StableIdentifier("acpi/battery/" + tag + "/estimated-runtime-minutes"),
                            Value = (float)minutes,
                            DisplayValue = FormatRuntime(minutes),
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            {
                                { "Raw seconds", FormatNumber(runtime.Value) }
                            }
                        });
                    }

                    summaryDetails["BatteryRuntime instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["BatteryRuntime error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\wmi\\BatteryRuntime: " + ex.Message);
                }
            }

            return rows;
        }

        private static string FormatRuntime(double minutes)
        {
            if (minutes >= 60)
            {
                var hours = (int)(minutes / 60);
                var mins = (int)Math.Round(minutes - (hours * 60.0));
                return hours.ToString(CultureInfo.InvariantCulture) + " h " + mins.ToString(CultureInfo.InvariantCulture) + " min";
            }

            return ((int)Math.Round(minutes)).ToString(CultureInfo.InvariantCulture) + " min";
        }

        private static IEnumerable<SensorReading> ReadPowerState(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = CreateSearcher(@"root\wmi", "SELECT PowerOnline, Charging, Discharging, Critical, Tag FROM BatteryStatus"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        count++;
                        var details = ReadDetails(instance);
                        var tag = FirstValue(details, "Tag", "InstanceName");
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            tag = count.ToString(CultureInfo.InvariantCulture);
                        }

                        var ac = string.Equals(FirstValue(details, "PowerOnline"), "True", StringComparison.OrdinalIgnoreCase);
                        var charging = string.Equals(FirstValue(details, "Charging"), "True", StringComparison.OrdinalIgnoreCase);
                        var discharging = string.Equals(FirstValue(details, "Discharging"), "True", StringComparison.OrdinalIgnoreCase);
                        var critical = string.Equals(FirstValue(details, "Critical"), "True", StringComparison.OrdinalIgnoreCase);

                        string state;
                        if (critical)
                        {
                            state = "Battery critical";
                        }
                        else if (charging)
                        {
                            state = "AC connected, charging";
                        }
                        else if (discharging)
                        {
                            state = "On battery, discharging";
                        }
                        else if (ac)
                        {
                            state = "AC connected, not charging";
                        }
                        else
                        {
                            state = "On battery, idle";
                        }

                        var rowDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
                        rowDetails["Namespace"] = @"root\wmi";
                        rowDetails["WMI class"] = "BatteryStatus";

                        rows.Add(new SensorReading
                        {
                            Type = "Performance",
                            Hardware = "ACPI battery",
                            Name = "Power state",
                            Identifier = StableIdentifier("acpi/battery/" + tag + "/power-state"),
                            DisplayValue = state,
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = rowDetails
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["BatteryStatus power-state error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\wmi\\BatteryStatus power state: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadLenovoBatteryInformation(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = @"root\WMI";
            const string className = "Lenovo_BatteryInformation";
            const string probeKey = @"root\WMI\Lenovo_BatteryInformation";
            if (IsWmiClassUnavailable(scopePath, className, probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                using (var searcher = CreateSearcher(scopePath, "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        count++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = scopePath;
                        details["WMI class"] = className;

                        var raw = FirstValue(details, "CurrentSetting");
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            continue;
                        }

                        var match = Regex.Match(raw, @"^\s*(?<bat>BAT\d+)\s+(?<field>\w+)\s*,\s*(?<value>.+?)\s*$");
                        if (!match.Success)
                        {
                            continue;
                        }

                        var battery = match.Groups["bat"].Value;
                        var field = match.Groups["field"].Value;
                        var value = match.Groups["value"].Value;

                        string friendly;
                        string idSuffix;
                        var display = value;
                        switch (field.ToLowerInvariant())
                        {
                            case "batmaker":
                                friendly = "Battery manufacturer ID";
                                idSuffix = "manufacturer-id";
                                break;
                            case "hwid":
                                friendly = "Battery hardware ID";
                                idSuffix = "hardware-id";
                                break;
                            case "mfgdate":
                                friendly = "Battery manufacture date";
                                idSuffix = "manufacture-date";
                                display = FormatIdeapadDate(value);
                                break;
                            default:
                                friendly = "Battery " + field;
                                idSuffix = StableIdentifier(field);
                                break;
                        }

                        var rowDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
                        rowDetails["Battery tag"] = battery;
                        rowDetails["Field name"] = field;
                        rowDetails["Raw value"] = value;

                        rows.Add(new SensorReading
                        {
                            Type = "Battery",
                            Hardware = "Lenovo battery",
                            Name = friendly,
                            Identifier = StableIdentifier("lenovo/ideapad/" + battery + "/" + idSuffix),
                            DisplayValue = display,
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = rowDetails
                        });
                    }

                    summaryDetails["Lenovo_BatteryInformation instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["Lenovo_BatteryInformation error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\WMI\\Lenovo_BatteryInformation: " + ex.Message);
                }
            }

            return rows;
        }

        private static string FormatIdeapadDate(string raw)
        {
            if (!string.IsNullOrEmpty(raw) && raw.Length == 8)
            {
                int year, month, day;
                if (int.TryParse(raw.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
                    && int.TryParse(raw.Substring(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
                    && int.TryParse(raw.Substring(6, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
                    && year >= 2000 && year < 2100 && month >= 1 && month <= 12 && day >= 1 && day <= 31)
                {
                    return year.ToString("0000", CultureInfo.InvariantCulture)
                        + "-" + month.ToString("00", CultureInfo.InvariantCulture)
                        + "-" + day.ToString("00", CultureInfo.InvariantCulture);
                }
            }

            return raw == null ? "" : raw;
        }

        private static IEnumerable<SensorReading> ReadLenovoVendorPresence(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var active = new List<string>();
            foreach (var className in new[] { "LENOVO_UTILITY_DATA", "LENOVO_SR_DATA" })
            {
                var probeKey = @"root\WMI\" + className;
                if (IsWmiClassUnavailable(@"root\WMI", className, probeKey, summaryDetails))
                {
                    continue;
                }

                try
                {
                    using (var searcher = CreateSearcher(@"root\WMI", "SELECT InstanceName, Active FROM " + className))
                    using (var instances = searcher.Get())
                    {
                        foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                        {
                            var details = ReadDetails(instance);
                            var activeFlag = FirstValue(details, "Active");
                            var instanceName = FirstValue(details, "InstanceName");
                            if (string.Equals(activeFlag, "True", StringComparison.OrdinalIgnoreCase))
                            {
                                active.Add(className + " (" + instanceName + ")");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (context != null)
                    {
                        context.Log("Debug", "Lenovo vendor probe failed for root\\WMI\\" + className + ": " + ex.Message);
                    }
                }
            }

            if (active.Count == 0)
            {
                return new SensorReading[0];
            }

            summaryDetails["Lenovo vendor WMI interfaces"] = string.Join("; ", active.ToArray());
            return new[]
            {
                new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Lenovo firmware",
                    Name = "Vendor WMI surface",
                    Identifier = "lenovo-vendor-wmi-presence",
                    DisplayValue = active.Count.ToString(CultureInfo.InvariantCulture) + " active vendor classes",
                    Source = "Lenovo Laptop Support Plug-In",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Active vendor classes", string.Join("; ", active.ToArray()) },
                        { "Notes", "These classes expose data and events through Vantage / Fn and Function Keys driver, not through pollable sensor properties." }
                    }
                }
            };
        }

    }
}
