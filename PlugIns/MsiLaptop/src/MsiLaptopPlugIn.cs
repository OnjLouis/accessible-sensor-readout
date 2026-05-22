using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using SensorReadout.PluginSdk;

namespace SensorReadout.MsiLaptopPlugIn
{
    public sealed class MsiLaptopPlugIn : ISensorReadoutPlugin, IFanControllablePlugin
    {
        private const string CpuControlIdentifier = "msi/acpi/control/cpu";
        private const string GpuControlIdentifier = "msi/acpi/control/gpu";
        private const string MsiAcpiScopePath = @"root\WMI";
        private const string MsiAcpiClassName = "MSI_ACPI";
        private const string PackageClassName = "Package_32";
        private const string GetFanMethodName = "Get_Fan";
        private const string SetFanMethodName = "Set_Fan";
        private const string GetApMethodName = "Get_AP";
        private const string SetApMethodName = "Set_AP";
        private const byte CpuFanTableSubfeature = 0x01;
        private const byte GpuFanTableSubfeature = 0x02;
        private const byte FanModeSubfeature = 0x01;
        private const byte EnableFanTablesMask = 0x80;

        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.msi.laptop.experimental",
            Name = "MSI Laptop Support (experimental)",
            Version = "0.1.0",
            Author = "Sensor Readout",
            Description = "Experimental MSI ACPI WMI fan, fan-control, and thermal interface support."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();
        private readonly Dictionary<string, MsiFanSnapshot> fanSnapshots = new Dictionary<string, MsiFanSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> manualPercents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly object fanWriteLock = new object();
        private IPluginContext lastContext;

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            lastContext = context;
            if (!IsMsiComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
            }

            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(30))
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = ProbeMsi(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        public bool TrySetFanPercent(string identifier, int percent)
        {
            byte subfeature;
            if (!TryGetFanTableSubfeature(identifier, out subfeature))
            {
                return false;
            }

            lock (fanWriteLock)
            {
                var p = Math.Max(0, Math.Min(100, percent));
                try
                {
                    var scope = ConnectMsiScope();
                    using (var instance = FirstMsiAcpiInstance(scope))
                    {
                        if (instance == null)
                        {
                            Log(lastContext, "Debug", "MSI fan write failed: no MSI_ACPI instance.");
                            return false;
                        }

                        EnsureFanSnapshot(instance, identifier, subfeature);

                        var fanTable = ReadPackage(instance, GetFanMethodName, subfeature);
                        if (fanTable == null || fanTable.Length < 8)
                        {
                            Log(lastContext, "Debug", "MSI fan write failed: could not read fan table for " + identifier + ".");
                            return false;
                        }

                        fanTable[0] = subfeature;
                        for (var index = 2; index <= 7 && index < fanTable.Length; index++)
                        {
                            fanTable[index] = (byte)p;
                        }

                        var fanAccepted = WritePackage(instance, SetFanMethodName, fanTable);
                        Log(lastContext, "Debug", "MSI fan write Set_Fan for " + identifier + " to " + p.ToString(CultureInfo.InvariantCulture) + "% returned " + fanAccepted + ".");
                        if (!fanAccepted)
                        {
                            return false;
                        }

                        var ap = ReadPackage(instance, GetApMethodName, FanModeSubfeature) ?? new byte[32];
                        ap[0] = FanModeSubfeature;
                        if (ap.Length > 1)
                        {
                            ap[1] = (byte)(ap[1] | EnableFanTablesMask);
                        }

                        var apAccepted = WritePackage(instance, SetApMethodName, ap);
                        Log(lastContext, "Debug", "MSI fan write Set_AP enable fan tables for " + identifier + " returned " + apAccepted + ".");
                        if (!apAccepted)
                        {
                            return false;
                        }

                        manualPercents[identifier] = p;
                        cachedRows.Clear();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log(lastContext, "Error", "MSI fan write failed for " + identifier + ": " + ex.Message);
                    return false;
                }
            }
        }

        public bool TryResetFan(string identifier)
        {
            byte subfeature;
            if (!TryGetFanTableSubfeature(identifier, out subfeature))
            {
                return false;
            }

            lock (fanWriteLock)
            {
                try
                {
                    var scope = ConnectMsiScope();
                    using (var instance = FirstMsiAcpiInstance(scope))
                    {
                        if (instance == null)
                        {
                            return false;
                        }

                        MsiFanSnapshot snapshot;
                        var hasSnapshot = fanSnapshots.TryGetValue(identifier, out snapshot) && snapshot != null;
                        var restoredTable = true;
                        if (hasSnapshot && snapshot.FanTable != null && snapshot.FanTable.Length == 32)
                        {
                            restoredTable = WritePackage(instance, SetFanMethodName, snapshot.FanTable);
                            Log(lastContext, "Debug", "MSI fan reset Set_Fan original table for " + identifier + " returned " + restoredTable + ".");
                        }

                        var ap = hasSnapshot && snapshot.ApMode != null && snapshot.ApMode.Length == 32
                            ? CloneBytes(snapshot.ApMode)
                            : ReadPackage(instance, GetApMethodName, FanModeSubfeature) ?? new byte[32];
                        ap[0] = FanModeSubfeature;
                        if (!hasSnapshot && ap.Length > 1)
                        {
                            ap[1] = (byte)(ap[1] & ~EnableFanTablesMask);
                        }

                        var restoredMode = WritePackage(instance, SetApMethodName, ap);
                        Log(lastContext, "Debug", "MSI fan reset Set_AP original/automatic mode for " + identifier + " returned " + restoredMode + ".");
                        if (restoredTable && restoredMode)
                        {
                            manualPercents.Remove(identifier);
                            cachedRows.Clear();
                            return true;
                        }

                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log(lastContext, "Error", "MSI fan reset failed for " + identifier + ": " + ex.Message);
                    return false;
                }
            }
        }

        private static bool IsMsiComputer(IPluginContext context)
        {
            var machine = context == null ? null : context.Machine;
            var manufacturer = machine == null ? "" : machine.Manufacturer ?? "";
            var model = machine == null ? "" : machine.Model ?? "";
            return ContainsAny(manufacturer, "MSI", "Micro-Star", "Micro Star")
                || ContainsAny(model, "MSI", "Venture", "Prestige", "Modern", "Stealth", "Raider", "Katana", "Cyborg", "Vector", "Creator", "Summit");
        }

        private IEnumerable<SensorReading> ProbeMsi(IPluginContext context)
        {
            var rows = new List<SensorReading>();
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mode", "Experimental MSI ACPI WMI probe" },
                { "Fan control", "Enabled when MSI_ACPI exposes compatible CPU/GPU fan-table controls. Diagnostics may briefly set exposed controls to 100% and restore automatic/original state." }
            };

            rows.AddRange(ReadMsiAcpiFans(context, details));
            rows.AddRange(ReadWin32Fan(context, details));
            rows.AddRange(ReadAcpiThermalZones(context, details));
            rows.AddRange(ReadMsiFanControls(context, details));

            var candidateClasses = DiscoverCandidateClasses(context).ToList();
            details["Useful WMI class candidate count"] = candidateClasses.Count.ToString(CultureInfo.InvariantCulture);
            details["Useful WMI class candidates"] = FormatCandidateClasses(candidateClasses);

            if (context != null && candidateClasses.Count > 0)
            {
                context.Log("Debug", "MSI useful WMI class candidates: " + string.Join(", ", candidateClasses.Select(c => c.ScopePath + "\\" + c.ClassName).ToArray()));
            }

            if (rows.Count == 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "MSI Plug-In",
                    Identifier = "msi/plugin/status",
                    DisplayValue = details.ContainsKey("MSI_ACPI error")
                        ? "MSI WMI interface could not be read"
                        : "No extra MSI fan or temperature values found",
                    Source = "MSI Laptop Support Plug-In",
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

                    row.Details["MSI plug-in mode"] = "Experimental read/write when compatible MSI fan-table controls are exposed";
                    foreach (var detail in details)
                    {
                        var key = "MSI probe " + detail.Key;
                        if (!row.Details.ContainsKey(key))
                        {
                            row.Details[key] = detail.Value;
                        }
                    }
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadMsiAcpiFans(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string scopePath = MsiAcpiScopePath;
            const string className = MsiAcpiClassName;
            const string packageClassName = PackageClassName;
            const string methodName = GetFanMethodName;

            summaryDetails["MSI_ACPI namespace"] = scopePath;
            summaryDetails["MSI_ACPI method"] = methodName;

            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                using (var managementClass = new ManagementClass(scope, new ManagementPath(className), null))
                using (var packageClass = new ManagementClass(scope, new ManagementPath(packageClassName), null))
                using (var instances = managementClass.GetInstances())
                {
                    var instanceCount = 0;
                    foreach (ManagementObject instance in instances)
                    {
                        instanceCount++;
                        var details = ReadDetails(instance);
                        details["Namespace"] = scopePath;
                        details["WMI class"] = className;
                        details["WMI method"] = methodName;
                        details["Mode"] = "Read-only MSI ACPI fan probe";

                        using (var inParams = instance.GetMethodParameters(methodName))
                        using (var package = packageClass.CreateInstance())
                        {
                            if (package != null)
                            {
                                package["Bytes"] = new byte[32];
                            }

                            if (inParams != null && inParams.Properties["Data"] != null)
                            {
                                inParams["Data"] = package;
                            }

                            using (var outParams = instance.InvokeMethod(methodName, inParams, null))
                            {
                                var bytes = ReadPackageBytes(outParams == null ? null : outParams["Data"]);
                                if (bytes == null || bytes.Length == 0)
                                {
                                    details["MSI_ACPI fan data"] = "No package bytes returned";
                                    continue;
                                }

                                details["MSI_ACPI fan raw bytes"] = BitConverter.ToString(bytes);
                                if (bytes[0] == 0)
                                {
                                    details["MSI_ACPI fan status"] = "Firmware returned failure status";
                                    continue;
                                }

                                var fanRows = DecodeFanRows(bytes, details).ToList();
                                foreach (var row in fanRows)
                                {
                                    rows.Add(row);
                                }
                            }
                        }
                    }

                    summaryDetails["MSI_ACPI instances"] = instanceCount.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["MSI_ACPI error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "MSI WMI probe failed for " + scopePath + "\\" + className + "." + methodName + ": " + ex.Message);
                }
            }

            return rows;
        }

        private IEnumerable<SensorReading> ReadMsiFanControls(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            summaryDetails["MSI fan write controls"] = "Checking MSI_ACPI CPU/GPU fan-table controls.";
            try
            {
                var scope = ConnectMsiScope();
                using (var instance = FirstMsiAcpiInstance(scope))
                {
                    if (instance == null)
                    {
                        summaryDetails["MSI fan write controls"] = "No MSI_ACPI instance.";
                        return rows;
                    }

                    rows.Add(MakeFanControlRow(CpuControlIdentifier, "MSI CPU fan table", CpuFanTableSubfeature, instance));
                    rows.Add(MakeFanControlRow(GpuControlIdentifier, "MSI GPU fan table", GpuFanTableSubfeature, instance));
                    summaryDetails["MSI fan write controls"] = "CPU and GPU fan-table controls exposed.";
                }
            }
            catch (Exception ex)
            {
                summaryDetails["MSI fan write control error"] = ex.Message;
                Log(context, "Debug", "MSI fan write control discovery failed: " + ex.Message);
            }

            return rows;
        }

        private SensorReading MakeFanControlRow(string identifier, string name, byte subfeature, ManagementObject instance)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Interface", "MSI_ACPI WMI" },
                { "WMI class", MsiAcpiClassName },
                { "Fan table subfeature", "0x" + subfeature.ToString("X2", CultureInfo.InvariantCulture) },
                { "Mode", "Writes a flat six-point MSI fan table and enables MSI fan-table mode through Set_AP, then reset restores the original table and AP mode when available." },
                { "Safety", "Exposed only after the user enables the MSI Laptop Support plug-in. Original fan table and AP mode are captured before the first manual write and restored on automatic/default." },
                { "Source note", "Based on the public Linux msi-wmi-platform fan-table interface." }
            };

            try
            {
                var table = ReadPackage(instance, GetFanMethodName, subfeature);
                details["Current fan table raw bytes"] = table == null ? "Unavailable" : BitConverter.ToString(table);
                details["Current fan table percents"] = FormatFanTablePercents(table);
            }
            catch (Exception ex)
            {
                details["Current fan table error"] = ex.Message;
            }

            int manualPercent;
            var isManual = manualPercents.TryGetValue(identifier, out manualPercent);
            return new SensorReading
            {
                Type = "Fan Control",
                Hardware = "MSI fan controls",
                Name = name,
                Identifier = identifier,
                Value = isManual ? (float?)manualPercent : null,
                DisplayValue = isManual ? manualPercent.ToString(CultureInfo.InvariantCulture) + "% manual test" : "automatic or firmware managed",
                Source = "MSI Laptop Support Plug-In",
                Details = details
            };
        }

        private static IEnumerable<SensorReading> DecodeFanRows(byte[] bytes, Dictionary<string, string> details)
        {
            var rows = new List<SensorReading>();
            for (var index = 0; index < 4; index++)
            {
                var offset = 1 + (index * 2);
                if (offset + 1 >= bytes.Length)
                {
                    break;
                }

                var reading = (bytes[offset] << 8) | bytes[offset + 1];
                details["Fan " + (index + 1).ToString(CultureInfo.InvariantCulture) + " raw tachometer"] = reading.ToString(CultureInfo.InvariantCulture);
                if (reading <= 0)
                {
                    continue;
                }

                var rpm = 480000.0 / reading;
                if (rpm <= 0 || rpm > 30000)
                {
                    details["Fan " + (index + 1).ToString(CultureInfo.InvariantCulture) + " ignored"] = "Calculated RPM was outside a plausible range.";
                    continue;
                }

                var roundedRpm = Math.Round(rpm);
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = "MSI",
                    Name = "Fan " + (index + 1).ToString(CultureInfo.InvariantCulture),
                    Identifier = "msi/acpi/fan/" + index.ToString(CultureInfo.InvariantCulture),
                    Value = (float)roundedRpm,
                    DisplayValue = FormatNumber(roundedRpm) + " RPM",
                    Source = "MSI Laptop Support Plug-In",
                    Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                    {
                        { "Fan index", (index + 1).ToString(CultureInfo.InvariantCulture) },
                        { "Formula", "RPM = 480000 / raw tachometer reading" }
                    }
                });
            }

            return rows;
        }

        private static byte[] ReadPackageBytes(object data)
        {
            var package = data as ManagementBaseObject;
            if (package != null)
            {
                return package["Bytes"] as byte[];
            }

            return data as byte[];
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
                                Hardware = "MSI",
                                Name = name,
                                Identifier = StableIdentifier("msi/win32fan/" + name),
                                Value = (float)value.Value,
                                DisplayValue = FormatNumber(value.Value) + " RPM",
                                Source = "MSI Laptop Support Plug-In",
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
                    context.Log("Debug", "MSI WMI probe failed for root\\cimv2\\Win32_Fan: " + ex.Message);
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
                            Hardware = "MSI ACPI",
                            Name = name,
                            Identifier = StableIdentifier("msi/acpi/thermal/" + name),
                            Value = (float)celsius,
                            DisplayValue = FormatNumber(celsius) + " C",
                            Source = "MSI Laptop Support Plug-In",
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
                    context.Log("Debug", "MSI WMI probe failed for root\\wmi\\MSAcpi_ThermalZoneTemperature: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<CandidateClass> DiscoverCandidateClasses(IPluginContext context)
        {
            var results = new List<CandidateClass>();
            foreach (var scopePath in new[] { @"root\wmi", @"root\cimv2" })
            {
                foreach (var pattern in new[] { "MSI%", "%Micro%Star%", "%Fan%", "%Thermal%", "%Temperature%" })
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

                                if (!IsUsefulCandidateClass(className))
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
                            context.Log("Debug", "MSI WMI class discovery failed for " + scopePath + " pattern " + pattern + ": " + ex.Message);
                        }
                    }
                }
            }

            return results
                .OrderBy(c => c.ScopePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsUsefulCandidateClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            if (className.StartsWith("MSiSCSI", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("MSi_Storage", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return className.StartsWith("MSI_", StringComparison.OrdinalIgnoreCase)
                || className.IndexOf("MicroStar", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Micro_Star", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Thermal", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Temperature", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatCandidateClasses(List<CandidateClass> candidateClasses)
        {
            if (candidateClasses == null || candidateClasses.Count == 0)
            {
                return "None";
            }

            const int displayLimit = 12;
            var names = candidateClasses
                .Take(displayLimit)
                .Select(c => c.ScopePath + "\\" + c.ClassName)
                .ToList();

            if (candidateClasses.Count > displayLimit)
            {
                names.Add("and " + (candidateClasses.Count - displayLimit).ToString(CultureInfo.InvariantCulture) + " more");
            }

            return string.Join(", ", names.ToArray());
        }

        private static bool TryGetFanTableSubfeature(string identifier, out byte subfeature)
        {
            if (string.Equals(identifier, CpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                subfeature = CpuFanTableSubfeature;
                return true;
            }

            if (string.Equals(identifier, GpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                subfeature = GpuFanTableSubfeature;
                return true;
            }

            subfeature = 0;
            return false;
        }

        private static ManagementScope ConnectMsiScope()
        {
            var scope = new ManagementScope(MsiAcpiScopePath);
            scope.Connect();
            return scope;
        }

        private static ManagementObject FirstMsiAcpiInstance(ManagementScope scope)
        {
            if (scope == null)
            {
                return null;
            }

            using (var managementClass = new ManagementClass(scope, new ManagementPath(MsiAcpiClassName), null))
            using (var instances = managementClass.GetInstances())
            {
                foreach (ManagementObject instance in instances)
                {
                    return instance;
                }
            }

            return null;
        }

        private static byte[] ReadPackage(ManagementObject instance, string methodName, byte subfeature)
        {
            var input = new byte[32];
            input[0] = subfeature;
            return InvokePackageMethod(instance, methodName, input);
        }

        private static bool WritePackage(ManagementObject instance, string methodName, byte[] input)
        {
            var output = InvokePackageMethod(instance, methodName, input);
            return output != null && output.Length > 0 && output[0] != 0;
        }

        private static byte[] InvokePackageMethod(ManagementObject instance, string methodName, byte[] input)
        {
            if (instance == null)
            {
                return null;
            }

            var scope = instance.Scope;
            using (var packageClass = new ManagementClass(scope, new ManagementPath(PackageClassName), null))
            using (var inParams = instance.GetMethodParameters(methodName))
            using (var package = packageClass.CreateInstance())
            {
                if (package != null)
                {
                    package["Bytes"] = NormalizePackageBytes(input);
                }

                if (inParams != null && inParams.Properties["Data"] != null)
                {
                    inParams["Data"] = package;
                }

                using (var outParams = instance.InvokeMethod(methodName, inParams, null))
                {
                    return ReadPackageBytes(outParams == null ? null : outParams["Data"]);
                }
            }
        }

        private void EnsureFanSnapshot(ManagementObject instance, string identifier, byte subfeature)
        {
            if (fanSnapshots.ContainsKey(identifier))
            {
                return;
            }

            var fanTable = ReadPackage(instance, GetFanMethodName, subfeature);
            var apMode = ReadPackage(instance, GetApMethodName, FanModeSubfeature);
            fanSnapshots[identifier] = new MsiFanSnapshot
            {
                FanTable = CloneBytes(fanTable),
                ApMode = CloneBytes(apMode)
            };
            Log(lastContext, "Debug", "MSI fan snapshot captured for " + identifier + ": table=" + FormatPackageForLog(fanTable) + ", ap=" + FormatPackageForLog(apMode) + ".");
        }

        private static byte[] NormalizePackageBytes(byte[] input)
        {
            var bytes = new byte[32];
            if (input != null)
            {
                Array.Copy(input, bytes, Math.Min(input.Length, bytes.Length));
            }

            return bytes;
        }

        private static byte[] CloneBytes(byte[] input)
        {
            return input == null ? null : NormalizePackageBytes(input);
        }

        private static string FormatPackageForLog(byte[] input)
        {
            return input == null ? "null" : BitConverter.ToString(input);
        }

        private static string FormatFanTablePercents(byte[] table)
        {
            if (table == null || table.Length < 8)
            {
                return "Unavailable";
            }

            var values = new List<string>();
            for (var index = 2; index <= 7 && index < table.Length; index++)
            {
                var value = table[index];
                values.Add(value <= 100 ? value.ToString(CultureInfo.InvariantCulture) + "%" : value.ToString(CultureInfo.InvariantCulture) + " raw");
            }

            return values.Count == 0 ? "Unavailable" : string.Join(", ", values.ToArray());
        }

        private static void Log(IPluginContext context, string level, string message)
        {
            if (context != null)
            {
                context.Log(level, message);
            }
        }

        private static Dictionary<string, string> ReadDetails(ManagementBaseObject instance)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (instance == null)
            {
                return details;
            }

            foreach (PropertyData property in instance.Properties)
            {
                var text = FormatPropertyValue(property.Value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    details[property.Name] = text;
                }
            }

            return details;
        }

        private static string FormatPropertyValue(object value)
        {
            if (value == null)
            {
                return "";
            }

            var array = value as Array;
            if (array != null && !(value is byte[]))
            {
                var parts = new List<string>();
                foreach (var item in array)
                {
                    var text = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }

                return string.Join(", ", parts.ToArray());
            }

            var bytes = value as byte[];
            if (bytes != null)
            {
                return BitConverter.ToString(bytes);
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
                    return value;
                }
            }

            return "";
        }

        private static double? FirstNumber(Dictionary<string, string> details, params string[] keys)
        {
            foreach (var key in keys)
            {
                string value;
                if (!details.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static double ConvertAcpiTemperature(double tenthsKelvin)
        {
            return (tenthsKelvin / 10.0) - 273.15;
        }

        private static bool IsPlausibleTemperature(double celsius)
        {
            return celsius > -50 && celsius < 150;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
        }

        private static string StableIdentifier(string text)
        {
            unchecked
            {
                var hash = 23;
                foreach (var ch in text ?? "")
                {
                    hash = (hash * 31) + char.ToLowerInvariant(ch);
                }

                return "msi/" + Math.Abs(hash).ToString(CultureInfo.InvariantCulture);
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

        private sealed class CandidateClass
        {
            public readonly string ScopePath;
            public readonly string ClassName;

            public CandidateClass(string scopePath, string className)
            {
                ScopePath = scopePath;
                ClassName = className;
            }
        }

        private sealed class MsiFanSnapshot
        {
            public byte[] FanTable;
            public byte[] ApMode;
        }
    }
}
