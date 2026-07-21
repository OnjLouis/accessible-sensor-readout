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
        private static IEnumerable<SensorReading> ReadAcpiBatteryHealth(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            var cycles = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var fullCharge = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var designByTag = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ReadSimpleBatteryClass(context, summaryDetails, "BatteryCycleCount", new[] { "CycleCount" }, cycles);
            ReadSimpleBatteryClass(context, summaryDetails, "BatteryFullChargedCapacity", new[] { "FullChargedCapacity" }, fullCharge);
            // Prefer BatteryStaticData (ACPI _BIX) and emit its richer rows; fall back to Win32_PortableBattery.
            rows.AddRange(ReadAcpiBatteryStaticData(context, summaryDetails, designByTag));
            if (designByTag.Count == 0)
            {
                foreach (var pair in ReadDesignCapacities(context, summaryDetails))
                {
                    designByTag[pair.Key] = pair.Value;
                }
            }

            if (fullCharge.Count == 1 && designByTag.Count == 1)
            {
                var full = fullCharge.First();
                if (!designByTag.ContainsKey(full.Key))
                {
                    designByTag[full.Key] = designByTag.First().Value;
                }
            }

            foreach (var pair in cycles)
            {
                if (pair.Value < 0 || pair.Value > 100000)
                {
                    continue;
                }

                rows.Add(new SensorReading
                {
                    Type = "Battery",
                    Hardware = "ACPI battery",
                    Name = "Cycle count",
                    Identifier = StableIdentifier("acpi/battery/" + pair.Key + "/cycles"),
                    Value = (float)pair.Value,
                    DisplayValue = FormatNumber(pair.Value),
                    Source = "Lenovo Laptop Support Plug-In",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Namespace", @"root\wmi" },
                        { "WMI class", "BatteryCycleCount" },
                        { "Battery tag", pair.Key }
                    }
                });
            }

            foreach (var pair in fullCharge)
            {
                if (pair.Value <= 0 || pair.Value > 1000000)
                {
                    continue;
                }

                rows.Add(new SensorReading
                {
                    Type = "Battery",
                    Hardware = "ACPI battery",
                    Name = "Full charge capacity",
                    Identifier = StableIdentifier("acpi/battery/" + pair.Key + "/full-charge-mwh"),
                    Value = (float)pair.Value,
                    DisplayValue = FormatNumber(pair.Value) + " mWh",
                    Source = "Lenovo Laptop Support Plug-In",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Namespace", @"root\wmi" },
                        { "WMI class", "BatteryFullChargedCapacity" },
                        { "Battery tag", pair.Key }
                    }
                });

                double design;
                if (designByTag.TryGetValue(pair.Key, out design) && design > 0 && design >= pair.Value)
                {
                    var wear = (1.0 - (pair.Value / design)) * 100.0;
                    if (wear < 0)
                    {
                        wear = 0;
                    }
                    if (wear > 100)
                    {
                        wear = 100;
                    }

                    rows.Add(new SensorReading
                    {
                        Type = "Battery",
                        Hardware = "ACPI battery",
                        Name = "Wear",
                        Identifier = StableIdentifier("acpi/battery/" + pair.Key + "/wear-percent"),
                        Value = (float)wear,
                        DisplayValue = FormatNumber(wear) + "%",
                        Source = "Lenovo Laptop Support Plug-In",
                        Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Design capacity", FormatNumber(design) + " mWh" },
                            { "Full charge capacity", FormatNumber(pair.Value) + " mWh" },
                            { "Battery tag", pair.Key },
                            { "Notes", "Wear = 1 - (full / design). Some Lenovo machines do not expose a real design capacity, in which case this row is omitted." }
                        }
                    });
                }
            }

            return rows;
        }

        private static void ReadSimpleBatteryClass(IPluginContext context, Dictionary<string, string> summaryDetails, string className, string[] valueKeys, Dictionary<string, double> resultByTag)
        {
            try
            {
                using (var searcher = CreateSearcher(@"root\wmi", "SELECT * FROM " + className))
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

                        var value = FirstNumber(details, valueKeys);
                        if (value.HasValue)
                        {
                            resultByTag[tag] = value.Value;
                        }
                    }

                    summaryDetails[className + " instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails[className + " error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\wmi\\" + className + ": " + ex.Message);
                }
            }
        }

        private static Dictionary<string, double> ReadDesignCapacities(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = CreateSearcher(@"root\cimv2", "SELECT Tag, DesignCapacity FROM Win32_PortableBattery"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        var details = ReadDetails(instance);
                        var tag = FirstValue(details, "Tag");
                        var capacity = FirstNumber(details, "DesignCapacity");
                        if (!string.IsNullOrWhiteSpace(tag) && capacity.HasValue && capacity.Value > 0)
                        {
                            result[tag] = capacity.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["Win32_PortableBattery error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\cimv2\\Win32_PortableBattery: " + ex.Message);
                }
            }

            return result;
        }

        private static IEnumerable<SensorReading> ReadAcpiBatteryStaticData(IPluginContext context, Dictionary<string, string> summaryDetails, Dictionary<string, double> designByTag)
        {
            var rows = new List<SensorReading>();
            const string probeKey = @"root\wmi\BatteryStaticData";
            if (ShouldSkipWmiProbe(probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                using (var searcher = CreateSearcher(@"root\wmi", "SELECT * FROM BatteryStaticData"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in EnumerateWmiObjects(instances))
                    {
                        count++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = @"root\wmi";
                        details["WMI class"] = "BatteryStaticData";

                        var tag = FirstValue(details, "Tag", "InstanceName");
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            tag = count.ToString(CultureInfo.InvariantCulture);
                        }

                        var idPrefix = "acpi/battery/" + tag + "/";

                        var design = FirstNumber(details, "DesignedCapacity");
                        if (design.HasValue && design.Value > 0 && design.Value < 1000000)
                        {
                            designByTag[tag] = design.Value;
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Design capacity",
                                Identifier = StableIdentifier(idPrefix + "design-capacity-mwh"),
                                Value = (float)design.Value,
                                DisplayValue = FormatNumber(design.Value) + " mWh",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var maker = FirstValue(details, "ManufactureName");
                        if (!string.IsNullOrWhiteSpace(maker))
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Battery manufacturer",
                                Identifier = StableIdentifier(idPrefix + "manufacturer"),
                                DisplayValue = maker,
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var deviceName = FirstValue(details, "DeviceName");
                        if (!string.IsNullOrWhiteSpace(deviceName))
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Battery device name",
                                Identifier = StableIdentifier(idPrefix + "device-name"),
                                DisplayValue = deviceName,
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var chemistry = FirstNumber(details, "Chemistry");
                        if (chemistry.HasValue && chemistry.Value > 0)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Battery",
                                Hardware = "ACPI battery",
                                Name = "Battery chemistry",
                                Identifier = StableIdentifier(idPrefix + "chemistry"),
                                DisplayValue = DescribeBatteryChemistry((long)chemistry.Value),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }

                    summaryDetails["BatteryStaticData instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["BatteryStaticData error"] = ex.Message;
                BackOffFailedWmiProbe(probeKey, ex, summaryDetails);
                if (context != null)
                {
                    context.Log("Debug", "Battery WMI probe failed for root\\wmi\\BatteryStaticData: " + ex.Message);
                }
            }

            return rows;
        }

        private static string DescribeBatteryChemistry(long code)
        {
            // BatteryStaticData.Chemistry on Windows comes back two different ways depending on firmware:
            // small integers map to the SMBIOS enum, larger integers are a packed little-endian ASCII string
            // (e.g. 0x504C69 = "LiP" = lithium polymer, 0x4E4F494C = "LION" = lithium ion).
            if (code >= 1 && code <= 8)
            {
                switch ((int)code)
                {
                    case 1: return "Other";
                    case 2: return "Unknown";
                    case 3: return "Lead Acid";
                    case 4: return "Nickel Cadmium";
                    case 5: return "Nickel Metal Hydride";
                    case 6: return "Lithium-ion";
                    case 7: return "Zinc air";
                    case 8: return "Lithium Polymer";
                }
            }

            var ascii = TryDecodePackedAscii((uint)(code & 0xFFFFFFFF));
            if (!string.IsNullOrEmpty(ascii))
            {
                switch (ascii.ToUpperInvariant())
                {
                    case "LION": return "Lithium-ion";
                    case "LIP":
                    case "LIPO": return "Lithium Polymer";
                    case "LIFE":
                    case "LFP":  return "Lithium iron phosphate";
                    case "NIMH": return "Nickel Metal Hydride";
                    case "PBAC": return "Lead Acid";
                    case "NICD": return "Nickel Cadmium";
                    default: return ascii;
                }
            }

            return "Chemistry code " + code.ToString(CultureInfo.InvariantCulture);
        }

        private static string TryDecodePackedAscii(uint value)
        {
            var chars = new char[4];
            var length = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = (byte)((value >> (i * 8)) & 0xFF);
                if (b == 0)
                {
                    break;
                }
                if (b < 0x20 || b > 0x7E)
                {
                    return null;
                }
                chars[length++] = (char)b;
            }
            return length == 0 ? null : new string(chars, 0, length);
        }

    }
}
