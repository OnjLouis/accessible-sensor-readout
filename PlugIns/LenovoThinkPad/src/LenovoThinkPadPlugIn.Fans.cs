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
        private static IEnumerable<SensorReading> ReadLenovoFanMethod(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = @"root\WMI";
            const string className = "LENOVO_FAN_METHOD";
            const string methodName = "Fan_GetCurrentFanSpeed";
            const string probeKey = @"root\WMI\LENOVO_FAN_METHOD";
            if (IsWmiClassUnavailable(scopePath, className, probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                using (var managementClass = new ManagementClass(scope, new ManagementPath(className), null))
                {
                    var inputParameterNames = GetMethodInputParameterNames(managementClass, methodName).ToList();
                    if (inputParameterNames.Count > 0)
                    {
                        summaryDetails["LENOVO_FAN_METHOD input parameters"] = string.Join(", ", inputParameterNames.ToArray());
                    }

                    var attempts = 0;
                    var errors = new List<string>();
                    using (var searcher = CreateSearcher(scopePath, "SELECT * FROM " + className))
                    using (var instances = searcher.Get())
                    {
                        var instanceIndex = 0;
                        foreach (ManagementObject instance in instances)
                        {
                            using (instance)
                            {
                                instanceIndex++;
                                rows.AddRange(ReadLenovoFanMethodTarget(instance, methodName, inputParameterNames, instanceIndex, ref attempts, errors));
                            }
                        }

                        if (instanceIndex == 0)
                        {
                            rows.AddRange(ReadLenovoFanMethodTarget(managementClass, methodName, inputParameterNames, 1, ref attempts, errors));
                        }

                        summaryDetails["LENOVO_FAN_METHOD instances"] = instanceIndex.ToString(CultureInfo.InvariantCulture);
                    }

                    summaryDetails["LENOVO_FAN_METHOD attempts"] = attempts.ToString(CultureInfo.InvariantCulture);
                    if (rows.Count > 0)
                    {
                        summaryDetails["LENOVO_FAN_METHOD live fans"] = rows.Count.ToString(CultureInfo.InvariantCulture);
                    }
                    if (errors.Count > 0)
                    {
                        summaryDetails["LENOVO_FAN_METHOD attempt errors"] = string.Join("; ", errors.Take(6).ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                summaryDetails["LENOVO_FAN_METHOD error"] = ex.Message;
                BackOffMissingWmiProbe(probeKey, ex, summaryDetails);
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\WMI\\LENOVO_FAN_METHOD." + methodName + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<string> GetMethodInputParameterNames(ManagementClass managementClass, string methodName)
        {
            ManagementBaseObject inParams = null;
            try
            {
                inParams = managementClass.GetMethodParameters(methodName);
                if (inParams == null)
                {
                    yield break;
                }

                foreach (PropertyData property in inParams.Properties)
                {
                    if (property != null && !string.IsNullOrWhiteSpace(property.Name))
                    {
                        yield return property.Name;
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

        private static IEnumerable<SensorReading> ReadLenovoFanMethodTarget(ManagementObject target, string methodName, List<string> inputParameterNames, int targetIndex, ref int attempts, List<string> errors)
        {
            var rows = new List<SensorReading>();
            var targetName = targetIndex.ToString(CultureInfo.InvariantCulture);

            // Some Legion WMI providers reject an empty input object but accept null parameters.
            SensorReading row;
            if (TryReadLenovoFanMethodValue(target, methodName, null, null, targetName, ref attempts, errors, out row))
            {
                rows.Add(row);
                return rows;
            }

            var fanIdParameterNames = inputParameterNames
                .Where(name => name.IndexOf("fan", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("id", StringComparison.OrdinalIgnoreCase) >= 0)
                .DefaultIfEmpty("")
                .ToList();
            foreach (var parameterName in fanIdParameterNames)
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    continue;
                }

                for (byte fanId = 0; fanId < 4; fanId++)
                {
                    if (TryReadLenovoFanMethodValue(target, methodName, parameterName, fanId, targetName, ref attempts, errors, out row))
                    {
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        private static bool TryReadLenovoFanMethodValue(ManagementObject target, string methodName, string parameterName, byte? fanId, string targetName, ref int attempts, List<string> errors, out SensorReading row)
        {
            row = null;
            attempts++;
            ManagementBaseObject inParams = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(parameterName) && fanId.HasValue)
                {
                    inParams = target.GetMethodParameters(methodName);
                    if (inParams == null || inParams.Properties[parameterName] == null)
                    {
                        return false;
                    }

                    inParams[parameterName] = fanId.Value;
                }

                using (var outParams = target.InvokeMethod(methodName, inParams, null))
                {
                    var details = ReadMethodDetails(outParams);
                    details["Namespace"] = @"root\WMI";
                    details["WMI class"] = "LENOVO_FAN_METHOD";
                    details["WMI method"] = methodName;
                    details["Invocation target"] = target.Path == null ? "" : target.Path.RelativePath ?? "";
                    details["Invocation mode"] = string.IsNullOrWhiteSpace(parameterName) ? "No input parameters" : parameterName + "=" + fanId.Value.ToString(CultureInfo.InvariantCulture);

                    double speed;
                    if (TryReadMethodNumber(outParams, out speed) && speed >= 0 && speed < 30000)
                    {
                        var suffix = fanId.HasValue ? (fanId.Value + 1).ToString(CultureInfo.InvariantCulture) : targetName;
                        row = new SensorReading
                        {
                            Type = "Fan",
                            Hardware = "Lenovo",
                            Name = "Fan " + suffix,
                            Identifier = StableIdentifier("lenovo/fan/method/" + suffix),
                            Value = (float)speed,
                            DisplayValue = FormatNumber(speed) + " RPM",
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = details
                        };
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 12)
                {
                    var mode = string.IsNullOrWhiteSpace(parameterName) ? "no parameters" : parameterName + "=" + (fanId.HasValue ? fanId.Value.ToString(CultureInfo.InvariantCulture) : "");
                    errors.Add(mode + ": " + ex.Message);
                }
            }
            finally
            {
                if (inParams != null)
                {
                    inParams.Dispose();
                }
            }

            return false;
        }

        private static IEnumerable<SensorReading> ReadLenovoFanTestData(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = @"root\WMI";
            const string className = "LENOVO_FAN_TEST_DATA";
            const string probeKey = @"root\WMI\LENOVO_FAN_TEST_DATA";
            if (IsWmiClassUnavailable(scopePath, className, probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                using (var searcher = CreateSearcher(scopePath, "SELECT * FROM " + className))
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
                BackOffMissingWmiProbe(probeKey, ex, summaryDetails);
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
            const string probeKey = @"root\cimv2\Win32_Fan";
            if (ShouldSkipWmiProbe(probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                using (var searcher = CreateSearcher(@"root\cimv2", "SELECT * FROM Win32_Fan"))
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
                BackOffMissingWmiProbe(probeKey, ex, summaryDetails);
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\cimv2\\Win32_Fan: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadAcpiFanDevices(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string probeKey = @"root\cimv2\Win32_PnPEntity ACPI fan devices";
            if (ShouldSkipWmiProbe(probeKey, summaryDetails))
            {
                return rows;
            }

            try
            {
                using (var searcher = CreateSearcher(@"root\cimv2", "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI\\\\PNP0C0B%'"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject fan in instances)
                    {
                        count++;
                        var details = ReadDetails(fan);
                        details["Namespace"] = @"root\cimv2";
                        details["WMI class"] = "Win32_PnPEntity";
                        details["Fan speed"] = "Not exposed by Windows for this ACPI fan device.";
                        details["Fan control"] = "Not exposed by Windows for this ACPI fan device.";

                        var name = FirstValue(details, "Name", "DeviceID", "Description", "Caption");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = "ACPI fan " + count.ToString(CultureInfo.InvariantCulture);
                        }

                        rows.Add(new SensorReading
                        {
                            Type = "Performance",
                            Hardware = "Overview",
                            Name = "Lenovo fan interface",
                            Identifier = StableIdentifier("lenovo/acpi/fan-present/" + name),
                            DisplayValue = name + " present; no live speed exposed",
                            Source = "Lenovo Laptop Support Plug-In",
                            Details = details
                        });
                    }

                    summaryDetails["ACPI fan devices"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["ACPI fan device error"] = ex.Message;
                BackOffMissingWmiProbe(probeKey, ex, summaryDetails);
                if (context != null)
                {
                    context.Log("Debug", "Lenovo WMI probe failed for root\\cimv2\\Win32_PnPEntity ACPI fan devices: " + ex.Message);
                }
            }

            return rows;
        }

    }
}
