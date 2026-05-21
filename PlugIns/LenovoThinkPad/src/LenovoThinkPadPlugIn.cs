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
            Version = "0.1.0",
            Author = "Sensor Readout",
            Description = "Experimental read-only probe for Lenovo fan WMI, Windows fan, ACPI thermal zone, and Lenovo WMI interfaces."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsLenovoComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
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
            return ContainsAny(manufacturer, "Lenovo", "ThinkPad")
                || ContainsAny(model, "Lenovo", "ThinkPad", "ThinkBook", "IdeaPad", "Yoga", "Legion", "LOQ");
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
            rows.AddRange(ReadWin32Fan(context, details));
            rows.AddRange(ReadAcpiThermalZones(context, details));

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
                || string.Equals(className, "MSAcpi_ThermalZoneTemperature", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCandidateClassName(string className)
        {
            return ContainsAny(className, "Lenovo", "Thermal", "Temperature", "Temp")
                || Regex.IsMatch(className ?? "", @"(^|[_\W])Fan($|[_\W])", RegexOptions.IgnoreCase);
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
