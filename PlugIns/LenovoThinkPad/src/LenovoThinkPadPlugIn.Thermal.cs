using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed partial class LenovoThinkPadPlugIn
    {
        private static void AddLenovoThermalDriverDetails(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Lenovo%Thermal%' OR Description LIKE '%Lenovo%Thermal%' OR Caption LIKE '%Lenovo%Thermal%'"))
                using (var instances = searcher.Get())
                {
                    var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ManagementObject instance in instances)
                    {
                        using (instance)
                        {
                            var details = ReadDetails(instance);
                            var name = FirstValue(details, "Name", "Description", "Caption", "DeviceID");
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                names.Add(name.Trim());
                            }
                        }
                    }

                    summaryDetails["Lenovo thermal driver devices"] = names.Count.ToString(CultureInfo.InvariantCulture);
                    if (names.Count > 0)
                    {
                        summaryDetails["Lenovo thermal driver device names"] = string.Join(", ", names.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["Lenovo thermal driver device error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for Lenovo thermal driver devices: " + ex.Message);
                }
            }
        }

        private static IEnumerable<SensorReading> ReadAcpiThermalZones(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject zone in instances)
                    {
                        count++;
                        var details = ReadDetails(zone);
                        details["Namespace"] = @"root\wmi";
                        details["WMI class"] = "MSAcpi_ThermalZoneTemperature";

                        var raw = FirstNumber(details, "CurrentTemperature");
                        if (!raw.HasValue)
                        {
                            continue;
                        }

                        var celsius = ConvertAcpiTemperature(raw.Value);
                        if (!IsPlausibleTemperature(celsius))
                        {
                            details["Ignored reason"] = "ACPI thermal value was outside a plausible Celsius range.";
                            continue;
                        }

                        var name = FirstValue(details, "InstanceName", "Name");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = "ACPI thermal zone " + count.ToString(CultureInfo.InvariantCulture);
                        }

                        rows.Add(new SensorReading
                        {
                            Type = "Temperature",
                            Hardware = "Lenovo ACPI",
                            Name = name,
                            Identifier = StableIdentifier("lenovo/acpi/thermal/" + name),
                            Value = (float)celsius,
                            DisplayValue = FormatNumber(celsius) + " C",
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = details
                        });
                    }

                    summaryDetails["MSAcpi_ThermalZoneTemperature instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["MSAcpi_ThermalZoneTemperature error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\wmi\\MSAcpi_ThermalZoneTemperature: " + ex.Message);
                }
            }

            return rows;
        }


        private static IEnumerable<SensorReading> ReadThermalThrottling(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, PercentPassiveLimit, ThrottleReasons, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
                    {
                        count++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = @"root\cimv2";
                        details["WMI class"] = "Win32_PerfFormattedData_Counters_ThermalZoneInformation";

                        var zone = FirstValue(details, "Name");
                        if (string.IsNullOrWhiteSpace(zone))
                        {
                            zone = "zone-" + count.ToString(CultureInfo.InvariantCulture);
                        }

                        var passive = FirstNumber(details, "PercentPassiveLimit");
                        var reasons = FirstNumber(details, "ThrottleReasons");

                        if (passive.HasValue)
                        {
                            // Percent of max passive throttle headroom. 100 = no passive throttling; lower values = thermally limited.
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Thermal",
                                Name = "Passive throttle headroom",
                                Identifier = StableIdentifier("thermal/" + zone + "/passive-limit-percent"),
                                Value = (float)passive.Value,
                                DisplayValue = FormatNumber(passive.Value) + "%",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        if (reasons.HasValue)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Thermal",
                                Name = "Throttle reasons",
                                Identifier = StableIdentifier("thermal/" + zone + "/throttle-reasons"),
                                Value = (float)reasons.Value,
                                DisplayValue = DescribeThrottleReasons((int)reasons.Value),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }

                    summaryDetails["ThermalZoneInformation counter instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["ThermalZoneInformation counter error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Thermal counter probe failed: " + ex.Message);
                }
            }

            return rows;
        }

        private static string DescribeThrottleReasons(int bitmap)
        {
            if (bitmap == 0)
            {
                return "None";
            }

            // ACPI / Windows thermal throttle reason bits. Names below come from documented Windows thermal docs.
            var bits = new List<string>();
            if ((bitmap & 0x1) != 0) { bits.Add("Thermal"); }
            if ((bitmap & 0x2) != 0) { bits.Add("Power budget"); }
            if ((bitmap & 0x4) != 0) { bits.Add("Display reduction"); }
            if ((bitmap & 0x8) != 0) { bits.Add("Processor"); }
            if ((bitmap & 0x10) != 0) { bits.Add("Memory"); }
            if ((bitmap & 0x20) != 0) { bits.Add("Storage"); }
            if (bits.Count == 0)
            {
                bits.Add("0x" + bitmap.ToString("X", CultureInfo.InvariantCulture));
            }

            return string.Join(", ", bits.ToArray());
        }

        private static IEnumerable<SensorReading> ReadSystemThermalState(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT ThermalState FROM Win32_ComputerSystem"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        var details = ReadDetails(instance);
                        var state = FirstNumber(details, "ThermalState");
                        if (!state.HasValue)
                        {
                            continue;
                        }

                        rows.Add(new SensorReading
                        {
                            Type = "Performance",
                            Hardware = "Thermal",
                            Name = "System thermal state",
                            Identifier = "thermal-system-state",
                            Value = (float)state.Value,
                            DisplayValue = DescribeThermalState((int)state.Value),
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            {
                                { "Namespace", @"root\cimv2" },
                                { "WMI class", "Win32_ComputerSystem" }
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["Win32_ComputerSystem ThermalState error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "ThermalState probe failed: " + ex.Message);
                }
            }

            return rows;
        }

        private static string DescribeThermalState(int code)
        {
            // SMBIOS / CIM thermal state codes.
            switch (code)
            {
                case 1: return "Other";
                case 2: return "Unknown";
                case 3: return "Safe";
                case 4: return "Warning";
                case 5: return "Critical";
                case 6: return "Non-recoverable";
                default: return "Code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

    }
}
