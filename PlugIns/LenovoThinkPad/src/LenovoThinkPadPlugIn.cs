using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed class LenovoThinkPadPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.lenovo.laptop.experimental",
            Name = "Lenovo Laptop Support (experimental)",
            Version = "0.2.0",
            Author = "Sensor Readout",
            Description = "Experimental read-only probe for Lenovo laptops. Reads ThinkPad fan WMI, ACPI thermal zones, ACPI battery health (cycle count, full-charge capacity, charge/discharge rate, voltage, estimated runtime, power state, design capacity, chemistry, manufacturer), IdeaPad Lenovo_BatteryInformation (manufacturer, hardware ID, manufacture date), thermal throttle reasons and passive limits, system thermal state, storage health/temperature/wear for NVMe and SATA drives, and reports presence of Lenovo vendor WMI interfaces."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsLenovoComputer(context))
            {
                var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddMachineIdentityDetails(context, details);
                return new[]
                {
                    new SensorReading
                    {
                        Type = "Performance",
                        Hardware = "Overview",
                        Name = "Lenovo Plug-In",
                        DisplayValue = "Enabled, but this computer was not detected as Lenovo",
                        Source = "Lenovo Laptop Support Plug-In",
                        Details = details
                    }
                };
            }

            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(30))
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = ProbeLenovo(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        private static bool IsLenovoComputer(IPluginContext context)
        {
            var machine = context == null ? null : context.Machine;
            var manufacturer = machine == null ? "" : machine.Manufacturer ?? "";
            var model = machine == null ? "" : machine.Model ?? "";
            if (ContainsAny(manufacturer, "Lenovo", "ThinkPad")
                || ContainsAny(model, "Lenovo", "ThinkPad", "ThinkBook", "IdeaPad", "Yoga", "Legion", "LOQ"))
            {
                return true;
            }

            return QueryWmiContains(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem", "Lenovo", "ThinkPad", "ThinkBook", "IdeaPad", "Yoga", "Legion", "LOQ")
                || QueryWmiContains(@"root\cimv2", "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct", "Lenovo", "ThinkPad", "ThinkBook", "IdeaPad", "Yoga", "Legion", "LOQ")
                || QueryWmiContains(@"root\cimv2", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", "Lenovo", "ThinkPad", "ThinkBook", "IdeaPad", "Yoga", "Legion", "LOQ");
        }

        private static IEnumerable<SensorReading> ProbeLenovo(IPluginContext context)
        {
            var rows = new List<SensorReading>();
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mode", "Read-only experimental probe" },
                { "Fan control", "Not attempted. Lenovo laptop fan control usually requires model-specific embedded-controller access." }
            };

            rows.AddRange(ReadLenovoFanMethod(context, details));
            rows.AddRange(ReadLenovoFanTestData(context, details));
            rows.AddRange(ReadWin32Fan(context, details));
            rows.AddRange(ReadAcpiThermalZones(context, details));
            rows.AddRange(ReadAcpiBatteryHealth(context, details));
            rows.AddRange(ReadAcpiBatteryStatus(context, details));
            rows.AddRange(ReadAcpiBatteryRuntime(context, details));
            rows.AddRange(ReadPowerState(context, details));
            rows.AddRange(ReadLenovoBatteryInformation(context, details));
            rows.AddRange(ReadLenovoVendorPresence(context, details));
            rows.AddRange(ReadThermalThrottling(context, details));
            rows.AddRange(ReadSystemThermalState(context, details));
            rows.AddRange(ReadStorageHealth(context, details));

            var candidateClasses = DiscoverCandidateClasses(context).ToList();
            details["Candidate WMI classes found"] = candidateClasses.Count == 0
                ? "None"
                : string.Join(", ", candidateClasses.Select(c => c.ScopePath + "\\" + c.ClassName).ToArray());

            foreach (var candidate in candidateClasses)
            {
                if (IsBuiltInClass(candidate.ClassName))
                {
                    continue;
                }

                rows.AddRange(ReadGenericSensorClass(context, candidate.ScopePath, candidate.ClassName));
            }

            if (rows.Count == 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Lenovo Plug-In",
                    DisplayValue = "No extra Lenovo fan or temperature values found",
                    Source = "Lenovo Laptop Support Plug-In",
                    Details = details
                });
            }
            else
            {
                foreach (var row in rows)
                {
                    if (row.Details == null)
                    {
                        row.Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    row.Details["Lenovo plug-in mode"] = "Read-only";
                    foreach (var detail in details)
                    {
                        var key = "Lenovo probe " + detail.Key;
                        if (!row.Details.ContainsKey(key))
                        {
                            row.Details[key] = detail.Value;
                        }
                    }
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadLenovoFanMethod(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = @"root\WMI";
            const string className = "LENOVO_FAN_METHOD";
            const string methodName = "Fan_GetCurrentFanSpeed";

            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                using (var managementClass = new ManagementClass(scope, new ManagementPath(className), null))
                {
                    var inParamsTemplate = managementClass.GetMethodParameters(methodName);
                    var attemptedFans = 0;
                    for (byte fanId = 0; fanId < 4; fanId++)
                    {
                        attemptedFans++;
                        ManagementBaseObject inParams = null;
                        try
                        {
                            inParams = inParamsTemplate == null ? null : (ManagementBaseObject)inParamsTemplate.Clone();
                            if (inParams != null && inParams.Properties["FanID"] != null)
                            {
                                inParams["FanID"] = fanId;
                            }

                            using (var outParams = managementClass.InvokeMethod(methodName, inParams, null))
                            {
                                var details = ReadMethodDetails(outParams);
                                details["Namespace"] = scopePath;
                                details["WMI class"] = className;
                                details["WMI method"] = methodName;
                                details["Fan ID"] = fanId.ToString(CultureInfo.InvariantCulture);

                                double speed;
                                if (TryReadMethodNumber(outParams, out speed) && speed >= 0 && speed < 30000)
                                {
                                    rows.Add(new SensorReading
                                    {
                                        Type = "Fan",
                                        Hardware = "Lenovo",
                                        Name = "Fan " + (fanId + 1).ToString(CultureInfo.InvariantCulture),
                                        Identifier = "lenovo/fan/method/" + fanId.ToString(CultureInfo.InvariantCulture),
                                        Value = (float)speed,
                                        DisplayValue = FormatNumber(speed) + " RPM",
                                        Source = "Lenovo Laptop Support Plug-In",
                                        Details = details
                                    });
                                }
                            }
                        }
                        finally
                        {
                            if (inParams != null)
                            {
                                inParams.Dispose();
                            }
                        }
                    }

                    summaryDetails["LENOVO_FAN_METHOD fans checked"] = attemptedFans.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["LENOVO_FAN_METHOD error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\WMI\\LENOVO_FAN_METHOD." + methodName + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadLenovoFanTestData(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = @"root\WMI";
            const string className = "LENOVO_FAN_TEST_DATA";

            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    var instanceCount = 0;
                    foreach (ManagementObject instance in instances)
                    {
                        instanceCount++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = scopePath;
                        details["WMI class"] = className;

                        var fanIds = ReadNumberList(details, "FanId");
                        var minimums = ReadNumberList(details, "FanMinSpeed");
                        var maximums = ReadNumberList(details, "FanMaxSpeed");
                        var fanCount = FirstNumber(details, "NumOfFans");
                        var count = Math.Max(fanIds.Count, Math.Max(minimums.Count, maximums.Count));
                        if (count == 0 && fanCount.HasValue && fanCount.Value > 0 && fanCount.Value < 32)
                        {
                            count = (int)fanCount.Value;
                        }

                        for (var index = 0; index < count; index++)
                        {
                            var fanId = index < fanIds.Count ? fanIds[index] : index + 1;
                            var min = index < minimums.Count ? minimums[index] : (double?)null;
                            var max = index < maximums.Count ? maximums[index] : (double?)null;
                            var rowDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            {
                                { "Fan ID", FormatNumber(fanId) },
                                { "Live speed", "Not exposed by this WMI class." }
                            };
                            if (min.HasValue)
                            {
                                rowDetails["Minimum fan speed"] = FormatNumber(min.Value) + " RPM";
                            }
                            if (max.HasValue)
                            {
                                rowDetails["Maximum fan speed"] = FormatNumber(max.Value) + " RPM";
                            }

                            rows.Add(new SensorReading
                            {
                                Type = "Fan",
                                Hardware = "Lenovo",
                                Name = "Fan " + FormatNumber(fanId) + " capability",
                                Identifier = StableIdentifier("lenovo/fan-test/" + fanId.ToString(CultureInfo.InvariantCulture)),
                                DisplayValue = FormatFanCapability(min, max),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = rowDetails
                            });
                        }
                    }

                    summaryDetails["LENOVO_FAN_TEST_DATA instances"] = instanceCount.ToString(CultureInfo.InvariantCulture);
                    if (rows.Count > 0)
                    {
                        summaryDetails["LENOVO_FAN_TEST_DATA fan capabilities"] = rows.Count.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["LENOVO_FAN_TEST_DATA error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\WMI\\LENOVO_FAN_TEST_DATA: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadWin32Fan(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_Fan"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject fan in instances)
                    {
                        count++;
                        var details = ReadDetails(fan);
                        details["Namespace"] = @"root\cimv2";
                        details["WMI class"] = "Win32_Fan";

                        var name = FirstValue(details, "Name", "DeviceID", "Description", "Caption");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = "Windows fan " + count.ToString(CultureInfo.InvariantCulture);
                        }

                        var value = FirstNumber(details, "CurrentReading", "CurrentSpeed", "DesiredSpeed", "Speed");
                        if (value.HasValue && value.Value > 0)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Fan",
                                Hardware = "Lenovo",
                                Name = name,
                                Identifier = StableIdentifier("lenovo/win32fan/" + name),
                                Value = (float)value.Value,
                                DisplayValue = FormatNumber(value.Value) + " RPM",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = details
                            });
                        }
                    }

                    summaryDetails["Win32_Fan instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["Win32_Fan error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\cimv2\\Win32_Fan: " + ex.Message);
                }
            }

            return rows;
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
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Tag, DesignCapacity FROM Win32_PortableBattery"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
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
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryStaticData"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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

        private static IEnumerable<SensorReading> ReadStorageHealth(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string storageNamespace = @"ROOT\Microsoft\Windows\Storage";
            try
            {
                // SELECT * is required so the disk object carries its full key path (ObjectId) — GetRelated needs it to traverse the storage reliability association.
                using (var searcher = new ManagementObjectSearcher(storageNamespace, "SELECT * FROM MSFT_PhysicalDisk"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject disk in instances)
                    {
                        count++;
                        var details = ReadDetails(disk);
                        details["Namespace"] = storageNamespace;
                        details["WMI class"] = "MSFT_PhysicalDisk";

                        var friendly = FirstValue(details, "FriendlyName");
                        if (string.IsNullOrWhiteSpace(friendly))
                        {
                            friendly = "Disk " + count.ToString(CultureInfo.InvariantCulture);
                        }

                        var diskSlug = StableIdentifier(friendly);
                        var idPrefix = "storage/" + diskSlug + "/";

                        var health = FirstNumber(details, "HealthStatus");
                        if (health.HasValue)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " health",
                                Identifier = StableIdentifier(idPrefix + "health"),
                                Value = (float)health.Value,
                                DisplayValue = DescribeDiskHealth((int)health.Value),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var opStatus = FirstValue(details, "OperationalStatus");
                        if (!string.IsNullOrWhiteSpace(opStatus))
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " operational status",
                                Identifier = StableIdentifier(idPrefix + "operational-status"),
                                DisplayValue = DescribeOperationalStatus(opStatus),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        // Reliability counter is reached through the disk's association set rather than direct query.
                        rows.AddRange(ReadDiskReliabilityCounter(context, summaryDetails, disk, friendly, idPrefix));
                    }

                    summaryDetails["MSFT_PhysicalDisk instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["MSFT_PhysicalDisk error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Storage probe failed for " + storageNamespace + " MSFT_PhysicalDisk: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadDiskReliabilityCounter(IPluginContext context, Dictionary<string, string> summaryDetails, ManagementObject disk, string friendly, string idPrefix)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var related = disk.GetRelated("MSFT_StorageReliabilityCounter"))
                {
                    foreach (ManagementObject counter in related)
                    {
                        var details = ReadDetails(counter);
                        details["Namespace"] = @"ROOT\Microsoft\Windows\Storage";
                        details["WMI class"] = "MSFT_StorageReliabilityCounter";

                        var temp = FirstNumber(details, "Temperature");
                        if (temp.HasValue && temp.Value > 0 && temp.Value < 150)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Temperature",
                                Hardware = "Storage",
                                Name = friendly + " temperature",
                                Identifier = StableIdentifier(idPrefix + "temperature-c"),
                                Value = (float)temp.Value,
                                DisplayValue = FormatNumber(temp.Value) + " C",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var maxTemp = FirstNumber(details, "TemperatureMax");
                        if (maxTemp.HasValue && maxTemp.Value > 0 && maxTemp.Value < 150)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Temperature",
                                Hardware = "Storage",
                                Name = friendly + " max-rated temperature",
                                Identifier = StableIdentifier(idPrefix + "temperature-max-c"),
                                Value = (float)maxTemp.Value,
                                DisplayValue = FormatNumber(maxTemp.Value) + " C",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var wear = FirstNumber(details, "Wear");
                        if (wear.HasValue && wear.Value >= 0 && wear.Value <= 100)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " wear",
                                Identifier = StableIdentifier(idPrefix + "wear-percent"),
                                Value = (float)wear.Value,
                                DisplayValue = FormatNumber(wear.Value) + "%",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var hours = FirstNumber(details, "PowerOnHours");
                        if (hours.HasValue && hours.Value > 0)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " power-on hours",
                                Identifier = StableIdentifier(idPrefix + "power-on-hours"),
                                Value = (float)hours.Value,
                                DisplayValue = FormatNumber(hours.Value) + " h",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Storage reliability probe failed for " + friendly + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static string DescribeDiskHealth(int code)
        {
            switch (code)
            {
                case 0: return "Healthy";
                case 1: return "Warning";
                case 2: return "Unhealthy";
                case 5: return "Unknown";
                default: return "Code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string DescribeOperationalStatus(string raw)
        {
            // CIM operational status codes from CIM_ManagedSystemElement. Comma-separated when multiple are active.
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Unknown";
            }

            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var labels = new List<string>();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                int code;
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                {
                    labels.Add(DescribeOperationalStatusCode(code));
                }
                else
                {
                    labels.Add(trimmed);
                }
            }

            return string.Join(", ", labels.ToArray());
        }

        private static string DescribeOperationalStatusCode(int code)
        {
            switch (code)
            {
                case 0: return "Unknown";
                case 1: return "Other";
                case 2: return "OK";
                case 3: return "Degraded";
                case 4: return "Stressed";
                case 5: return "Predictive Failure";
                case 8: return "Starting";
                case 9: return "Stopping";
                case 10: return "Stopped";
                case 11: return "In Service";
                case 12: return "No Contact";
                case 13: return "Lost Communication";
                case 14: return "Aborted";
                case 15: return "Dormant";
                case 16: return "Supporting Entity in Error";
                case 17: return "Completed";
                default: return "Code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static IEnumerable<SensorReading> ReadAcpiBatteryStatus(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryStatus"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryRuntime"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT PowerOnline, Charging, Discharging, Critical, Tag FROM BatteryStatus"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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

            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject instance in instances)
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
                try
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, Active FROM " + className))
                    using (var instances = searcher.Get())
                    {
                        foreach (ManagementObject instance in instances)
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

        private static IEnumerable<CandidateClass> DiscoverCandidateClasses(IPluginContext context)
        {
            var results = new List<CandidateClass>();
            foreach (var scopePath in new[] { @"root\wmi", @"root\cimv2" })
            {
                foreach (var pattern in new[] { "Lenovo%", "LENOVO%", "%Fan%", "%Thermal%", "%Temperature%" })
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM meta_class WHERE __CLASS LIKE '" + pattern + "'"))
                        using (var classes = searcher.Get())
                        {
                            foreach (ManagementObject managementClass in classes)
                            {
                                var className = Convert.ToString(managementClass["__CLASS"], CultureInfo.InvariantCulture);
                                if (string.IsNullOrWhiteSpace(className))
                                {
                                    continue;
                                }

                                if (!IsCandidateClassName(className))
                                {
                                    continue;
                                }

                                if (!results.Any(c => string.Equals(c.ScopePath, scopePath, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(c.ClassName, className, StringComparison.OrdinalIgnoreCase)))
                                {
                                    results.Add(new CandidateClass(scopePath, className));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (context != null)
                        {
                            context.Log("Debug", "Lenovo WMI class discovery failed for " + scopePath + " pattern " + pattern + ": " + ex.Message);
                        }
                    }
                }
            }

            return results.OrderBy(c => c.ScopePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<SensorReading> ReadGenericSensorClass(IPluginContext context, string scopePath, string className)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    var index = 0;
                    foreach (ManagementObject instance in instances)
                    {
                        index++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = scopePath;
                        details["WMI class"] = className;

                        var name = FirstValue(details, "ElementName", "Name", "Description", "DeviceID", "InstanceName");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = className + " " + index.ToString(CultureInfo.InvariantCulture);
                        }

                        var isFan = ContainsAny(className, "Fan") || ContainsAny(name, "Fan");
                        var isTemperature = ContainsAny(className, "Thermal", "Temperature", "Temp")
                            || ContainsAny(name, "Thermal", "Temperature", "Temp");

                        if (isFan)
                        {
                            var value = FirstNumber(details, "CurrentReading", "CurrentSpeed", "CurrentValue", "Speed", "DesiredSpeed", "Value");
                            if (value.HasValue && value.Value > 0)
                            {
                                rows.Add(new SensorReading
                                {
                                    Type = "Fan",
                                    Hardware = "Lenovo",
                                    Name = name,
                                    Identifier = StableIdentifier("lenovo/fan/" + className + "/" + name),
                                    Value = (float)value.Value,
                                    DisplayValue = FormatNumber(value.Value) + " RPM",
                                    Source = "Lenovo Laptop Support Plug-In",
                                    Details = details
                                });
                            }
                        }

                        if (isTemperature)
                        {
                            var value = FirstNumber(details, "CurrentTemperature", "Temperature", "CurrentReading", "CurrentValue", "Value");
                            if (value.HasValue)
                            {
                                var celsius = LooksLikeAcpiTemperature(value.Value) ? ConvertAcpiTemperature(value.Value) : value.Value;
                                if (IsPlausibleTemperature(celsius))
                                {
                                    rows.Add(new SensorReading
                                    {
                                        Type = "Temperature",
                                        Hardware = "Lenovo",
                                        Name = name,
                                        Identifier = StableIdentifier("lenovo/temperature/" + className + "/" + name),
                                        Value = (float)celsius,
                                        DisplayValue = FormatNumber(celsius) + " C",
                                        Source = "Lenovo Laptop Support Plug-In",
                                        Details = details
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for " + scopePath + "\\" + className + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static bool IsBuiltInClass(string className)
        {
            return string.Equals(className, "Win32_Fan", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "MSAcpi_ThermalZoneTemperature", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Lenovo_BatteryInformation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Lenovo_SystemElement", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "LENOVO_UTILITY_DATA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "LENOVO_SR_DATA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "LENOVO_UTILITY_EVENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "LENOVO_SR_EVENT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCandidateClassName(string className)
        {
            return ContainsAny(className, "Lenovo", "Thermal", "Temperature", "Temp")
                || Regex.IsMatch(className ?? "", @"(^|[_\W])Fan($|[_\W])", RegexOptions.IgnoreCase);
        }

        private static bool QueryWmiContains(string scopePath, string query, params string[] needles)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, query))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        foreach (PropertyData property in instance.Properties)
                        {
                            if (property == null || property.Value == null)
                            {
                                continue;
                            }

                            if (ContainsAny(FormatWmiValue(property.Value), needles))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void AddMachineIdentityDetails(IPluginContext context, Dictionary<string, string> details)
        {
            var machine = context == null ? null : context.Machine;
            if (machine != null)
            {
                details["Context manufacturer"] = machine.Manufacturer ?? "";
                details["Context model"] = machine.Model ?? "";
            }

            AddFirstWmiIdentity(details, @"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem", "Computer system");
            AddFirstWmiIdentity(details, @"root\cimv2", "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct", "Computer system product");
            AddFirstWmiIdentity(details, @"root\cimv2", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", "Baseboard");
        }

        private static void AddFirstWmiIdentity(Dictionary<string, string> details, string scopePath, string query, string label)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, query))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        foreach (PropertyData property in instance.Properties)
                        {
                            if (property == null || property.Value == null)
                            {
                                continue;
                            }

                            details[label + " " + property.Name] = FormatWmiValue(property.Value);
                        }

                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                details[label + " identity error"] = ex.Message;
            }
        }

        private static Dictionary<string, string> ReadDetails(ManagementObject instance)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyData property in instance.Properties)
            {
                if (property == null || property.Value == null)
                {
                    continue;
                }

                details[property.Name] = FormatWmiValue(property.Value);
            }

            return details;
        }

        private static List<double> ReadNumberList(Dictionary<string, string> details, string key)
        {
            var values = new List<double>();
            string text;
            if (!details.TryGetValue(key, out text) || string.IsNullOrWhiteSpace(text))
            {
                return values;
            }

            foreach (Match match in Regex.Matches(text, @"-?\d+(?:\.\d+)?"))
            {
                double value;
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static string FormatFanCapability(double? min, double? max)
        {
            if (min.HasValue && max.HasValue)
            {
                return "No live speed exposed; range " + FormatNumber(min.Value) + "-" + FormatNumber(max.Value) + " RPM";
            }

            if (max.HasValue)
            {
                return "No live speed exposed; maximum " + FormatNumber(max.Value) + " RPM";
            }

            if (min.HasValue)
            {
                return "No live speed exposed; minimum " + FormatNumber(min.Value) + " RPM";
            }

            return "No live speed exposed";
        }

        private static Dictionary<string, string> ReadMethodDetails(ManagementBaseObject outParams)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (outParams == null)
            {
                return details;
            }

            foreach (PropertyData property in outParams.Properties)
            {
                if (property == null || property.Value == null)
                {
                    continue;
                }

                details["Output " + property.Name] = FormatWmiValue(property.Value);
            }

            return details;
        }

        private static bool TryReadMethodNumber(ManagementBaseObject outParams, out double value)
        {
            value = 0;
            if (outParams == null)
            {
                return false;
            }

            foreach (var name in new[] { "Speed", "CurrentSpeed", "CurrentFanSpeed", "FanSpeed", "Data", "Value", "ReturnValue" })
            {
                var property = outParams.Properties[name];
                if (property == null || property.Value == null)
                {
                    continue;
                }

                if (double.TryParse(Convert.ToString(property.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }

            foreach (PropertyData property in outParams.Properties)
            {
                if (property == null || property.Value == null)
                {
                    continue;
                }

                if (double.TryParse(Convert.ToString(property.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatWmiValue(object value)
        {
            var array = value as Array;
            if (array != null)
            {
                var values = new List<string>();
                foreach (var item in array)
                {
                    values.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? "");
                }

                return string.Join(", ", values.ToArray());
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static string FirstValue(Dictionary<string, string> details, params string[] keys)
        {
            foreach (var key in keys)
            {
                string value;
                if (details.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        private static double? FirstNumber(Dictionary<string, string> details, params string[] keys)
        {
            foreach (var key in keys)
            {
                string text;
                if (!details.TryGetValue(key, out text))
                {
                    continue;
                }

                double value;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool LooksLikeAcpiTemperature(double raw)
        {
            return raw > 1000;
        }

        private static double ConvertAcpiTemperature(double raw)
        {
            return (raw / 10.0) - 273.15;
        }

        private static bool IsPlausibleTemperature(double celsius)
        {
            return celsius > -30 && celsius < 150;
        }

        private static string StableIdentifier(string text)
        {
            var chars = (text ?? "").Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray();
            return new string(chars).Trim('-');
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(Math.Abs(value) >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static SensorReading CloneReading(SensorReading reading)
        {
            return new SensorReading
            {
                Type = reading.Type,
                Hardware = reading.Hardware,
                Name = reading.Name,
                Identifier = reading.Identifier,
                Value = reading.Value,
                DisplayValue = reading.DisplayValue,
                Source = reading.Source,
                Details = reading.Details == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(reading.Details, StringComparer.OrdinalIgnoreCase)
            };
        }

        private sealed class CandidateClass
        {
            public CandidateClass(string scopePath, string className)
            {
                ScopePath = scopePath;
                ClassName = className;
            }

            public string ScopePath { get; private set; }
            public string ClassName { get; private set; }
        }
    }
}
