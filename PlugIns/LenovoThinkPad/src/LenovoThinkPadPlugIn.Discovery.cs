using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed partial class LenovoThinkPadPlugIn
    {
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

        private static void AddInterestingClassSchemas(IPluginContext context, IEnumerable<CandidateClass> candidates, Dictionary<string, string> summaryDetails)
        {
            foreach (var candidate in candidates)
            {
                if (!ContainsAny(candidate.ClassName, "EnhancedEC", "WmiOpcode", "FunctionRequest", "Fan"))
                {
                    continue;
                }

                try
                {
                    var scope = new ManagementScope(candidate.ScopePath);
                    scope.Connect();
                    using (var managementClass = new ManagementClass(scope, new ManagementPath(candidate.ClassName), null))
                    {
                        var propertyNames = managementClass.Properties
                            .Cast<PropertyData>()
                            .Select(p => p.Name)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var methodNames = managementClass.Methods
                            .Cast<MethodData>()
                            .Select(m => m.Name)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        var keyPrefix = "Class schema " + candidate.ScopePath + "\\" + candidate.ClassName;
                        summaryDetails[keyPrefix + " properties"] = propertyNames.Length == 0 ? "None" : string.Join(", ", propertyNames);
                        summaryDetails[keyPrefix + " methods"] = methodNames.Length == 0 ? "None" : string.Join(", ", methodNames);
                    }
                }
                catch (Exception ex)
                {
                    summaryDetails["Class schema " + candidate.ScopePath + "\\" + candidate.ClassName + " error"] = ex.Message;
                    if (context != null)
                    {
                        context.Log("Debug", "Lenovo WMI schema probe failed for " + candidate.ScopePath + "\\" + candidate.ClassName + ": " + ex.Message);
                    }
                }
            }
        }

        private static void AddInterestingClassInstanceSnapshots(IPluginContext context, IEnumerable<CandidateClass> candidates, Dictionary<string, string> summaryDetails)
        {
            foreach (var candidate in candidates)
            {
                if (!ContainsAny(candidate.ClassName, "EnhancedEC", "WmiOpcode", "FunctionRequest", "Fan"))
                {
                    continue;
                }

                try
                {
                    using (var searcher = new ManagementObjectSearcher(candidate.ScopePath, "SELECT * FROM " + candidate.ClassName))
                    using (var instances = searcher.Get())
                    {
                        var count = 0;
                        var snapshots = new List<string>();
                        var currentSettings = new List<string>();
                        var relevantCurrentSettings = new List<string>();
                        foreach (ManagementObject instance in instances)
                        {
                            using (instance)
                            {
                                count++;
                                var details = ReadDetails(instance);
                                string currentSetting;
                                if (details.TryGetValue("CurrentSetting", out currentSetting) && !string.IsNullOrWhiteSpace(currentSetting))
                                {
                                    var cleanCurrentSetting = currentSetting.Trim();
                                    currentSettings.Add("#" + count.ToString(CultureInfo.InvariantCulture) + " " + cleanCurrentSetting);
                                    if (ContainsAny(cleanCurrentSetting, "Fan", "Cooling", "Cool", "Thermal", "Temperature", "Temp", "Performance", "Power", "Throttle"))
                                    {
                                        relevantCurrentSettings.Add("#" + count.ToString(CultureInfo.InvariantCulture) + " " + cleanCurrentSetting);
                                    }
                                }

                                if (snapshots.Count >= 5)
                                {
                                    continue;
                                }

                                var parts = new List<string>();
                                AddSnapshotPart(parts, details, "InstanceName");
                                AddSnapshotPart(parts, details, "Active");
                                AddSnapshotPart(parts, details, "CurrentSetting");
                                AddSnapshotPart(parts, details, "Name");
                                AddSnapshotPart(parts, details, "Description");
                                AddSnapshotPart(parts, details, "DeviceID");
                                AddSnapshotPart(parts, details, "CurrentReading");
                                AddSnapshotPart(parts, details, "CurrentSpeed");
                                AddSnapshotPart(parts, details, "DesiredSpeed");
                                AddSnapshotPart(parts, details, "Speed");
                                AddSnapshotPart(parts, details, "Value");
                                if (parts.Count > 0)
                                {
                                    snapshots.Add("#" + count.ToString(CultureInfo.InvariantCulture) + " " + string.Join("; ", parts.ToArray()));
                                }
                            }
                        }

                        var keyPrefix = "Class instances " + candidate.ScopePath + "\\" + candidate.ClassName;
                        summaryDetails[keyPrefix + " count"] = count.ToString(CultureInfo.InvariantCulture);
                        if (snapshots.Count > 0)
                        {
                            summaryDetails[keyPrefix + " snapshot"] = string.Join(" | ", snapshots.ToArray());
                        }

                        if (currentSettings.Count > 0 && ContainsAny(candidate.ClassName, "FunctionRequest"))
                        {
                            summaryDetails[keyPrefix + " CurrentSetting values"] = JoinLimited(currentSettings, 6000);
                            if (relevantCurrentSettings.Count > 0)
                            {
                                summaryDetails[keyPrefix + " thermal/fan candidates"] = JoinLimited(relevantCurrentSettings, 3000);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    summaryDetails["Class instances " + candidate.ScopePath + "\\" + candidate.ClassName + " error"] = ex.Message;
                    if (context != null)
                    {
                        context.Log("Debug", "Lenovo WMI instance probe failed for " + candidate.ScopePath + "\\" + candidate.ClassName + ": " + ex.Message);
                    }
                }
            }
        }

        private static string JoinLimited(IEnumerable<string> values, int maxLength)
        {
            var joined = string.Join(" | ", values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());
            if (joined.Length <= maxLength)
            {
                return joined;
            }

            return joined.Substring(0, Math.Max(0, maxLength - 20)).TrimEnd() + " ... [truncated]";
        }

        private static void AddSnapshotPart(List<string> parts, Dictionary<string, string> details, string key)
        {
            string value;
            if (details.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(key + "=" + value.Trim());
            }
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
                || string.Equals(className, "LENOVO_SR_EVENT", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(className ?? "", @"(^|[_\W])Fan($|[_\W])", RegexOptions.IgnoreCase);
        }

        private static bool IsCandidateClassName(string className)
        {
            return ContainsAny(className, "Lenovo", "Thermal", "Temperature", "Temp")
                || Regex.IsMatch(className ?? "", @"(^|[_\W])Fan($|[_\W])", RegexOptions.IgnoreCase);
        }

    }
}
