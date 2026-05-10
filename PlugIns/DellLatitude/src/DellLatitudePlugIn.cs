using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using SensorReadout.PluginSdk;

namespace SensorReadout.DellLatitudePlugIn
{
    public sealed class DellLatitudePlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.dell.latitude.experimental",
            Name = "Dell Latitude Support (experimental)",
            Version = "0.1.0",
            Author = "Sensor Readout",
            Description = "Experimental read-only probe for Dell Command | Monitor thermal and fan WMI interfaces."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsDellComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
            }

            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(15))
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = ProbeDellWmi(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        private static bool IsDellComputer(IPluginContext context)
        {
            var machine = context == null ? null : context.Machine;
            var manufacturer = machine == null ? "" : machine.Manufacturer ?? "";
            var model = machine == null ? "" : machine.Model ?? "";
            return ContainsAny(manufacturer, "Dell")
                || ContainsAny(model, "Latitude", "Precision", "XPS", "Inspiron", "OptiPlex", "Alienware");
        }

        private static IEnumerable<SensorReading> ProbeDellWmi(IPluginContext context)
        {
            var rows = new List<SensorReading>();
            var foundClasses = new List<string>();
            var namespaces = new[] { @"root\dcim\sysman", @"root\DellOMCI" };
            var classes = new[] { "DCIM_Fan", "DCIM_TemperatureSensor", "DCIM_NumericSensor" };

            foreach (var scopePath in namespaces)
            {
                foreach (var className in classes)
                {
                    rows.AddRange(ReadSensorClass(context, scopePath, className, foundClasses));
                }
            }

            if (rows.Count == 0)
            {
                var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Namespaces checked", string.Join(", ", namespaces) },
                    { "Classes checked", string.Join(", ", classes) },
                    { "Mode", "Read-only experimental probe" }
                };

                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Dell Plug-In",
                    DisplayValue = foundClasses.Count == 0
                        ? "Dell Command | Monitor WMI interface not found"
                        : "Dell WMI interface found, but no readable fan or temperature values",
                    Source = "Dell Latitude Support Plug-In",
                    Details = details
                });
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadSensorClass(IPluginContext context, string scopePath, string className, List<string> foundClasses)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM " + className))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        if (!foundClasses.Contains(scopePath + "\\" + className))
                        {
                            foundClasses.Add(scopePath + "\\" + className);
                        }

                        var row = ToReading(scopePath, className, instance);
                        if (row != null)
                        {
                            rows.Add(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Dell WMI probe failed for " + scopePath + "\\" + className + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static SensorReading ToReading(string scopePath, string className, ManagementObject instance)
        {
            var details = ReadDetails(instance);
            var name = FirstValue(details, "ElementName", "Name", "Description", "DeviceID", "InstanceName");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = className;
            }

            var value = FirstNumber(details, "CurrentReading", "CurrentValue", "Reading", "Value");
            if (!value.HasValue)
            {
                return null;
            }

            details["Namespace"] = scopePath;
            details["WMI class"] = className;

            var isFan = className.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0;
            var isTemperature = className.IndexOf("Temperature", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Temperature", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Thermal", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isFan)
            {
                return new SensorReading
                {
                    Type = "Fan",
                    Hardware = "Dell",
                    Name = name,
                    Identifier = StableIdentifier("dell/fan/" + name),
                    Value = (float)value.Value,
                    DisplayValue = FormatNumber(value.Value) + " RPM",
                    Source = "Dell Latitude Support Plug-In",
                    Details = details
                };
            }

            if (isTemperature)
            {
                return new SensorReading
                {
                    Type = "Temperature",
                    Hardware = "Dell",
                    Name = name,
                    Identifier = StableIdentifier("dell/temperature/" + name),
                    Value = (float)value.Value,
                    DisplayValue = FormatNumber(value.Value) + " C",
                    Source = "Dell Latitude Support Plug-In",
                    Details = details
                };
            }

            return new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Dell " + name,
                Identifier = StableIdentifier("dell/sensor/" + className + "/" + name),
                Value = (float)value.Value,
                DisplayValue = FormatNumber(value.Value),
                Source = "Dell Latitude Support Plug-In",
                Details = details
            };
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

                details[property.Name] = Convert.ToString(property.Value, CultureInfo.InvariantCulture) ?? "";
            }

            return details;
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

        private static string StableIdentifier(string text)
        {
            var chars = (text ?? "").Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray();
            return new string(chars).Trim('-');
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
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
