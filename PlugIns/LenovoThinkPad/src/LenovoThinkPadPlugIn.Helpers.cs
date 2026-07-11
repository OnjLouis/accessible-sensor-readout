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
        private static readonly object wmiProbeBackoffLock = new object();
        private static readonly Dictionary<string, DateTime> wmiProbeBackoffUntilUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MissingWmiProbeBackoff = TimeSpan.FromHours(6);

        private static ManagementObjectSearcher CreateSearcher(string query)
        {
            var searcher = new ManagementObjectSearcher(query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(5);
            return searcher;
        }

        private static ManagementObjectSearcher CreateSearcher(string scopePath, string query)
        {
            var searcher = new ManagementObjectSearcher(scopePath, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(5);
            return searcher;
        }

        private static bool ShouldSkipWmiProbe(string key, Dictionary<string, string> summaryDetails)
        {
            lock (wmiProbeBackoffLock)
            {
                DateTime untilUtc;
                if (wmiProbeBackoffUntilUtc.TryGetValue(key, out untilUtc) && DateTime.UtcNow < untilUtc)
                {
                    if (summaryDetails != null)
                    {
                        summaryDetails[key + " skipped"] = "Known unavailable until " + untilUtc.ToString("u", CultureInfo.InvariantCulture);
                    }

                    return true;
                }
            }

            return false;
        }

        private static void BackOffMissingWmiProbe(string key, Exception ex, Dictionary<string, string> summaryDetails)
        {
            if (!IsMissingWmiClassError(ex))
            {
                return;
            }

            BackOffWmiProbe(key, "Known unavailable", summaryDetails);
        }

        private static void BackOffWmiProbe(string key, string reason, Dictionary<string, string> summaryDetails)
        {
            var untilUtc = DateTime.UtcNow.Add(MissingWmiProbeBackoff);
            lock (wmiProbeBackoffLock)
            {
                wmiProbeBackoffUntilUtc[key] = untilUtc;
            }

            if (summaryDetails != null)
            {
                summaryDetails[key + " backoff"] = reason + "; will retry after " + untilUtc.ToString("u", CultureInfo.InvariantCulture);
            }
        }

        private static bool IsWmiClassUnavailable(string scopePath, string className, string probeKey, Dictionary<string, string> summaryDetails)
        {
            if (ShouldSkipWmiProbe(probeKey, summaryDetails))
            {
                return true;
            }

            try
            {
                var escapedClass = (className ?? "").Replace("'", "''");
                using (var searcher = CreateSearcher(scopePath, "SELECT * FROM meta_class WHERE __CLASS = '" + escapedClass + "'"))
                using (var classes = searcher.Get())
                {
                    foreach (ManagementObject ignored in classes)
                    {
                        using (ignored)
                        {
                            return false;
                        }
                    }
                }

                if (summaryDetails != null)
                {
                    summaryDetails[className + " class"] = "Not present";
                }

                BackOffWmiProbe(probeKey, "Class is not present", summaryDetails);
                return true;
            }
            catch (Exception ex)
            {
                if (summaryDetails != null)
                {
                    summaryDetails[className + " class check error"] = ex.Message;
                }

                BackOffMissingWmiProbe(probeKey, ex, summaryDetails);
                return IsMissingWmiClassError(ex);
            }
        }

        private static bool IsMissingWmiClassError(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            var management = ex as ManagementException;
            if (management != null && (management.ErrorCode == ManagementStatus.InvalidClass || management.ErrorCode == ManagementStatus.NotFound))
            {
                return true;
            }

            var message = ex.Message ?? "";
            return message.IndexOf("Invalid class", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool QueryWmiContains(string scopePath, string query, params string[] needles)
        {
            try
            {
                using (var searcher = CreateSearcher(scopePath, query))
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
                using (var searcher = CreateSearcher(scopePath, query))
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

    }
}
