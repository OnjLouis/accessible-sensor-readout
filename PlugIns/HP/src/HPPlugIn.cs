using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using SensorReadout.PluginSdk;

namespace SensorReadout.HPPlugIn
{
    public sealed class HPPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.hp.experimental",
            Name = "HP Hardware Support (experimental)",
            Version = "0.1.0",
            Author = "Sensor Readout",
            Description = "Experimental read-only probe for HP/OMEN/Victus WMI thermal and fan interfaces."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsHpComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
            }

            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(30))
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = ProbeHpWmi(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        private static bool IsHpComputer(IPluginContext context)
        {
            var machine = context == null ? null : context.Machine;
            var manufacturer = machine == null ? "" : machine.Manufacturer ?? "";
            var model = machine == null ? "" : machine.Model ?? "";
            return ContainsAny(manufacturer, "HP", "Hewlett-Packard", "OMEN", "Victus")
                || ContainsAny(model, "HP", "Hewlett-Packard", "OMEN", "Victus");
        }

        private static IEnumerable<SensorReading> ProbeHpWmi(IPluginContext context)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var foundClasses = new List<string>();

            foreach (var className in new[] { "hpqBDataIn", "hpqBIntM" })
            {
                if (ClassExists(@"root\wmi", className, context))
                {
                    foundClasses.Add(className);
                }
            }

            details["Namespace"] = @"root\wmi";
            details["Classes found"] = foundClasses.Count == 0 ? "None" : string.Join(", ", foundClasses.ToArray());
            details["Mode"] = "Read-only experimental probe";

            var status = foundClasses.Count == 0
                ? "HP WMI interface not found"
                : "HP WMI interface found: " + string.Join(", ", foundClasses.ToArray());

            return new[]
            {
                new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "HP Plug-In",
                    DisplayValue = status,
                    Source = "HP Hardware Support Plug-In",
                    Details = details
                }
            };
        }

        private static bool ClassExists(string scopePath, string className, IPluginContext context)
        {
            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                using (var managementClass = new ManagementClass(scope, new ManagementPath(className), null))
                using (var instances = managementClass.GetInstances())
                {
                    foreach (ManagementObject ignored in instances)
                    {
                        return true;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "HP WMI probe failed for " + scopePath + "\\" + className + ": " + ex.Message);
                }

                return false;
            }
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
