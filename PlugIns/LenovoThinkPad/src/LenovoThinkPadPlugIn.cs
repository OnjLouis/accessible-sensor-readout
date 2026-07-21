using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed partial class LenovoThinkPadPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.lenovo.laptop.experimental",
            Name = "Lenovo Laptop Support (experimental)",
            Version = "0.3.0",
            Author = "Sensor Readout",
            Description = "Experimental read-only probe for Lenovo laptops. Reads ThinkPad fan WMI where exposed, ACPI thermal zones, Lenovo thermal modes and thermal driver presence, ACPI battery health (cycle count, full-charge capacity, charge/discharge rate, voltage, estimated runtime, power state, design capacity, chemistry, manufacturer), IdeaPad Lenovo_BatteryInformation (manufacturer, hardware ID, manufacture date), thermal throttle reasons and passive limits, system thermal state, storage health/temperature/wear for NVMe and SATA drives, and reports presence of Lenovo vendor WMI interfaces."
        };

        private static readonly TimeSpan NormalCacheDuration = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DiagnosticCacheDuration = TimeSpan.FromMinutes(2);

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();
        private DateTime cachedDiagnosticRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedDiagnosticRows = new List<SensorReading>();
        private readonly object cacheLock = new object();

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

            var diagnosticsMode = context != null && context.DiagnosticsMode;
            var cacheDuration = diagnosticsMode ? DiagnosticCacheDuration : NormalCacheDuration;
            lock (cacheLock)
            {
                var cacheRows = diagnosticsMode ? cachedDiagnosticRows : cachedRows;
                var cacheUtc = diagnosticsMode ? cachedDiagnosticRowsUtc : cachedRowsUtc;
                if (cacheRows.Count > 0 && DateTime.UtcNow - cacheUtc < cacheDuration)
                {
                    return cacheRows.Select(CloneReading).ToList();
                }
            }

            var rows = ProbeLenovo(context, diagnosticsMode).ToList();
            lock (cacheLock)
            {
                if (diagnosticsMode)
                {
                    cachedDiagnosticRows = rows.Select(CloneReading).ToList();
                    cachedDiagnosticRowsUtc = DateTime.UtcNow;
                }
                else
                {
                    cachedRows = rows.Select(CloneReading).ToList();
                    cachedRowsUtc = DateTime.UtcNow;
                }
            }

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

        private static IEnumerable<SensorReading> ProbeLenovo(IPluginContext context, bool diagnosticsMode)
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
            rows.AddRange(ReadAcpiFanDevices(context, details));
            rows.AddRange(ReadLenovoThermalDriverDevices(context, details));
            rows.AddRange(ReadLenovoThermalModes(context, details));
            AddLenovoFanTelemetryStatus(rows, details);
            rows.AddRange(ReadAcpiThermalZones(context, details));
            rows.AddRange(ReadAcpiBatteryHealth(context, details));
            rows.AddRange(ReadAcpiBatteryStatus(context, details));
            rows.AddRange(ReadAcpiBatteryRuntime(context, details));
            rows.AddRange(ReadPowerState(context, details));
            rows.AddRange(ReadLenovoBatteryInformation(context, details));
            rows.AddRange(ReadLenovoVendorPresence(context, details));
            rows.AddRange(ReadThermalThrottling(context, details));
            rows.AddRange(ReadSystemThermalState(context, details));
            if (diagnosticsMode)
            {
                rows.AddRange(ReadStorageHealth(context, details));

                var candidateClasses = DiscoverCandidateClasses(context).ToList();
                details["Candidate WMI classes found"] = candidateClasses.Count == 0
                    ? "None"
                    : string.Join(", ", candidateClasses.Select(c => c.ScopePath + "\\" + c.ClassName).ToArray());
                AddInterestingClassSchemas(context, candidateClasses, details);
                AddInterestingClassInstanceSnapshots(context, candidateClasses, details);

                foreach (var candidate in candidateClasses)
                {
                    if (IsBuiltInClass(candidate.ClassName))
                    {
                        continue;
                    }

                    rows.AddRange(ReadGenericSensorClass(context, candidate.ScopePath, candidate.ClassName));
                }
            }
            else
            {
                details["Lenovo diagnostic discovery"] = "Skipped during live refresh to avoid repeated Lenovo/Vantage WMI provider polling. Run diagnostics for full Lenovo class discovery.";
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
