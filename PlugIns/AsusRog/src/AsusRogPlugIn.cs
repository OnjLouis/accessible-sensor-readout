using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using SensorReadout.PluginSdk;

namespace SensorReadout.AsusRogPlugIn
{
    public sealed class AsusRogPlugIn : ISensorReadoutPlugin, IFanControllablePlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.asus.rog.experimental",
            Name = "Asus ROG Support (experimental)",
            Version = "0.3.0",
            Author = "Jason Fayre, Claude Code, and Sensor Readout contributors",
            Description = "Experimental, opt-in Asus ROG probe. Reads ASUS WMI/ATKACPI fan tachometer and temperature values where available, avoids laptop fan-curve writes on desktop towers, and attempts ATKACPI fan control only on supported laptop profiles. Based in part on G-Helper ACPI research."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();
        private DateTime acpiRetryUtc = DateTime.MinValue;
        private IPluginContext lastContext;
        private bool? cachedAsusDetection;
        private bool profileLoaded;
        private AsusMachineProfile cachedProfile;

        // Current manual percentages, kept so GetReadings can show the right DisplayValue for "Fan Control" rows.
        private readonly Dictionary<string, int> manualPercents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public PluginInfo Info { get { return info; } }

        // Device path and control code from ghelper/app/AsusACPI.cs
        private const string AtkAcpiDevice = @"\\.\\ATKACPI";
        private const uint ControlCode = 0x0022240C;
        private const uint Dsts = 0x53545344;
        private const uint Devs = 0x53564544;
        private const uint Init = 0x54494E49;

        // Performance mode IDs from ghelper/app/AsusACPI.cs
        private const uint StatusModeId = 0x00090031;      // reads current mode: 0=balanced 1=turbo 2=silent
        private const uint PerformanceModeId = 0x00120075; // writes performance mode, restoring BIOS fan control
        private const uint VivoBookModeId = 0x00110019;    // fallback performance mode endpoint

        // Fan read device IDs from ghelper/app/AsusACPI.cs
        private const uint CpuFanId = 0x00110013;
        private const uint GpuFanId = 0x00110014;
        private const uint MidFanId = 0x00110031;

        // Fan curve write device IDs from ghelper/app/AsusACPI.cs
        private const uint CpuFanCurveId = 0x00110024;
        private const uint GpuFanCurveId = 0x00110025;
        private const uint MidFanCurveId = 0x00110032;

        // Fan range write device IDs from ghelper/app/AsusACPI.cs.
        // Some models accept these when the full curve write is ignored.
        private const uint CpuFanRangeId = 0x00110022;
        private const uint GpuFanRangeId = 0x00110023;

        // Temperature device IDs from ghelper/app/AsusACPI.cs
        private const uint TempCpuId = 0x00120094;
        private const uint TempGpuId = 0x00120097;

        private const string CpuFanIdentifier = "asus/rog/fan/cpu";
        private const string GpuFanIdentifier = "asus/rog/fan/gpu";
        private const string MidFanIdentifier = "asus/rog/fan/mid";
        private const string CpuControlIdentifier = "asus/rog/control/cpu";
        private const string GpuControlIdentifier = "asus/rog/control/gpu";
        private const string MidControlIdentifier = "asus/rog/control/mid";
        private const string SourceName = "Asus ROG Support Plug-In";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize,
            ref uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            lastContext = context;
            if (!IsAsusComputer(context))
                return Enumerable.Empty<SensorReading>();

            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(5))
                return cachedRows.Select(CloneReading).ToList();

            var rows = ReadAsusSensors(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        public bool TrySetFanPercent(string identifier, int percent)
        {
            uint curveId;
            if (!TryGetCurveId(identifier, out curveId))
                return false;

            var handle = OpenAtkAcpi();
            if (handle == new IntPtr(-1))
            {
                Log("Debug", "Asus ROG plug-in: fan write could not open ATKACPI for " + identifier + " (error " + Marshal.GetLastWin32Error() + ").");
                return false;
            }

            try
            {
                var p = Math.Max(0, Math.Min(100, percent));
                var initStatus = DeviceInit(handle);
                Log("Debug", "Asus ROG plug-in: init result=" + BitConverter.ToString(initStatus) + ".");

                var currentMode = DeviceGet(handle, StatusModeId);
                var baseMode = currentMode >= 0 && currentMode <= 2 ? (uint)currentMode : 0;
                Log("Debug", "Asus ROG plug-in: setting " + identifier + " to " + p + "%. Current mode=" + currentMode + ", base mode=" + baseMode + ".");

                int modeError;
                bool modeIoOk;
                var modeResult = DeviceSetValue(handle, PerformanceModeId, baseMode, out modeIoOk, out modeError);
                Log("Debug", "Asus ROG plug-in: performance mode result=" + modeResult + ", ioOk=" + modeIoOk + ", error=" + modeError + ".");
                if (modeResult != 1)
                {
                    int vivoModeError;
                    bool vivoModeIoOk;
                    var vivoModeResult = DeviceSetValue(handle, VivoBookModeId, MapVivoBookMode(baseMode), out vivoModeIoOk, out vivoModeError);
                    Log("Debug", "Asus ROG plug-in: VivoBook/TUF mode fallback result=" + vivoModeResult + ", ioOk=" + vivoModeIoOk + ", error=" + vivoModeError + ".");
                }
                Thread.Sleep(100);

                int curveError;
                bool curveIoOk;
                var curveResult = DeviceSet(handle, curveId, MakeFlatCurve(p), out curveIoOk, out curveError);
                Log("Debug", "Asus ROG plug-in: curve write result for " + identifier + "=" + curveResult + ", ioOk=" + curveIoOk + ", error=" + curveError + ".");

                var rangeResult = int.MinValue;
                uint rangeId;
                if (curveResult != 1 && TryGetRangeId(identifier, out rangeId))
                {
                    int rangeError;
                    bool rangeIoOk;
                    rangeResult = DeviceSet(handle, rangeId, MakeFlatRange(p), out rangeIoOk, out rangeError);
                    Log("Debug", "Asus ROG plug-in: fan range write result for " + identifier + "=" + rangeResult + ", ioOk=" + rangeIoOk + ", error=" + rangeError + ".");
                }

                uint readId;
                if (TryGetReadId(identifier, out readId))
                {
                    var after = GetFanRpm(handle, readId);
                    Log("Debug", "Asus ROG plug-in: fan tachometer after write for " + identifier + "=" + after + " RPM.");
                }

                var ok = curveResult == 1 || rangeResult == 1;
                if (!ok)
                {
                    // Some ASUS laptops, including ExpertBook/VivoBook-class machines, reject
                    // direct fan curves but do accept broader thermal mode writes. Use that as
                    // a truthful fallback instead of presenting a control that never changes
                    // anything on those systems.
                    var targetMode = PercentToPerformanceMode(p);
                    int percentModeError;
                    bool percentModeIoOk;
                    var percentModeResult = DeviceSetValue(handle, PerformanceModeId, targetMode, out percentModeIoOk, out percentModeError);
                    Log("Debug", "Asus ROG plug-in: percent thermal mode fallback result=" + percentModeResult + ", mode=" + targetMode + ", ioOk=" + percentModeIoOk + ", error=" + percentModeError + ".");

                    if (percentModeResult != 1)
                    {
                        var targetVivoMode = PercentToVivoBookMode(p);
                        int percentVivoModeError;
                        bool percentVivoModeIoOk;
                        var percentVivoModeResult = DeviceSetValue(handle, VivoBookModeId, targetVivoMode, out percentVivoModeIoOk, out percentVivoModeError);
                        Log("Debug", "Asus ROG plug-in: percent VivoBook/TUF thermal mode fallback result=" + percentVivoModeResult + ", mode=" + targetVivoMode + ", ioOk=" + percentVivoModeIoOk + ", error=" + percentVivoModeError + ".");
                        ok = percentVivoModeResult == 1;
                    }
                    else
                    {
                        ok = true;
                    }
                }

                if (ok)
                {
                    manualPercents[identifier] = p;
                    cachedRows.Clear();
                }
                else
                {
                    Log("Debug", "Asus ROG plug-in: fan write was not accepted for " + identifier + ".");
                }

                return ok;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public bool TryResetFan(string identifier)
        {
            // Only handle identifiers we own.
            uint curveId;
            if (!TryGetCurveId(identifier, out curveId))
                return false;

            var handle = OpenAtkAcpi();
            if (handle == new IntPtr(-1))
            {
                Log("Debug", "Asus ROG plug-in: fan reset could not open ATKACPI for " + identifier + " (error " + Marshal.GetLastWin32Error() + ").");
                return false;
            }

            try
            {
                // Writing a curve (even the original) keeps the fan in curve-managed mode.
                // Re-applying the current performance mode signals the BIOS to resume its
                // own thermal management - the same mechanism ghelper uses when switching modes.
                int currentMode = DeviceGet(handle, StatusModeId);
                if (currentMode < 0 || currentMode > 2) currentMode = 0;

                int modeError;
                bool modeIoOk;
                var modeResult = DeviceSetValue(handle, PerformanceModeId, (uint)currentMode, out modeIoOk, out modeError);
                Log("Debug", "Asus ROG plug-in: reset " + identifier + " to BIOS mode " + currentMode + ", result=" + modeResult + ", ioOk=" + modeIoOk + ", error=" + modeError + ".");
                if (modeResult != 1)
                {
                    int vivoModeError;
                    bool vivoModeIoOk;
                    var vivoModeResult = DeviceSetValue(handle, VivoBookModeId, MapVivoBookMode((uint)currentMode), out vivoModeIoOk, out vivoModeError);
                    Log("Debug", "Asus ROG plug-in: reset VivoBook/TUF fallback result=" + vivoModeResult + ", ioOk=" + vivoModeIoOk + ", error=" + vivoModeError + ".");
                    modeResult = vivoModeResult;
                }

                var ok = modeResult == 1;
                if (ok)
                {
                    manualPercents.Remove(identifier);
                    cachedRows.Clear();
                }

                return ok;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool TryGetRangeId(string identifier, out uint rangeId)
        {
            if (string.Equals(identifier, CpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                rangeId = CpuFanRangeId;
                return true;
            }

            if (string.Equals(identifier, GpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                rangeId = GpuFanRangeId;
                return true;
            }

            rangeId = 0;
            return false;
        }

        private static bool TryGetReadId(string identifier, out uint readId)
        {
            if (string.Equals(identifier, CpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                readId = CpuFanId;
                return true;
            }

            if (string.Equals(identifier, GpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                readId = GpuFanId;
                return true;
            }

            if (string.Equals(identifier, MidControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                readId = MidFanId;
                return true;
            }

            readId = 0;
            return false;
        }

        private static bool TryGetCurveId(string identifier, out uint curveId)
        {
            if (string.Equals(identifier, CpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                curveId = CpuFanCurveId;
                return true;
            }

            if (string.Equals(identifier, GpuControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                curveId = GpuFanCurveId;
                return true;
            }

            if (string.Equals(identifier, MidControlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                curveId = MidFanCurveId;
                return true;
            }

            curveId = 0;
            return false;
        }

        private IEnumerable<SensorReading> ReadAsusSensors(IPluginContext context)
        {
            var profile = GetAsusMachineProfile(context);
            var profileRows = MakeProfileRows(profile).ToList();

            if (DateTime.UtcNow >= acpiRetryUtc)
            {
                var acpiRows = ReadAcpiSensors(context, profile).ToList();
                if (!IsOnlyUnavailableRow(acpiRows))
                {
                    return profileRows.Concat(acpiRows).ToList();
                }
            }

            var wmiRows = ReadAsusWmiSensors(context).ToList();
            if (wmiRows.Count > 0)
            {
                return profileRows.Concat(wmiRows).ToList();
            }

            if (profileRows.Count > 0)
            {
                return profileRows;
            }

            return MakeUnavailableRow(profile);
        }

        private IEnumerable<SensorReading> ReadAcpiSensors(IPluginContext context, AsusMachineProfile profile)
        {
            var handle = OpenAtkAcpi();
            if (handle == new IntPtr(-1))
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: cannot open ATKACPI (error " + Marshal.GetLastWin32Error() + "), retrying in 60 s.");
                }
                acpiRetryUtc = DateTime.UtcNow.AddSeconds(60);
                return MakeUnavailableRow(profile);
            }

            acpiRetryUtc = DateTime.MinValue;

            try
            {
                return ReadAllSensors(handle, profile).ToList();
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private IEnumerable<SensorReading> ReadAllSensors(IntPtr handle, AsusMachineProfile profile)
        {
            var rows = new List<SensorReading>();
            var exposeFanControls = ShouldExposeLaptopFanControls(profile);

            var cpuFan = GetFanRpm(handle, CpuFanId);
            if (cpuFan >= 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = "Asus",
                    Name = "CPU fan",
                    Identifier = CpuFanIdentifier,
                    Value = cpuFan,
                    DisplayValue = cpuFan + " RPM",
                    Source = SourceName,
                    Details = MakeAcpiDetails("CPU fan tachometer", CpuFanId)
                });
                if (exposeFanControls)
                {
                    rows.Add(MakeFanControlRow(CpuControlIdentifier, "CPU Fan / ASUS thermal mode"));
                }
            }

            var gpuFan = GetFanRpm(handle, GpuFanId);
            if (gpuFan >= 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = "Asus",
                    Name = "GPU fan",
                    Identifier = GpuFanIdentifier,
                    Value = gpuFan,
                    DisplayValue = gpuFan + " RPM",
                    Source = SourceName,
                    Details = MakeAcpiDetails("GPU fan tachometer", GpuFanId)
                });
                if (exposeFanControls)
                {
                    rows.Add(MakeFanControlRow(GpuControlIdentifier, "GPU Fan / ASUS thermal mode"));
                }
            }

            // Mid fan is only present on some models; skip if the device ID is unsupported.
            var midFan = GetFanRpm(handle, MidFanId);
            if (midFan >= 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = "Asus",
                    Name = "Mid fan",
                    Identifier = MidFanIdentifier,
                    Value = midFan,
                    DisplayValue = midFan + " RPM",
                    Source = SourceName,
                    Details = MakeAcpiDetails("Mid fan tachometer", MidFanId)
                });
                if (exposeFanControls)
                {
                    rows.Add(MakeFanControlRow(MidControlIdentifier, "Mid Fan / ASUS thermal mode"));
                }
            }

            var cpuTemp = GetTemperature(handle, TempCpuId);
            if (cpuTemp.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = "Asus",
                    Name = "CPU",
                    Identifier = "asus/rog/temperature/cpu",
                    Value = cpuTemp.Value,
                    DisplayValue = cpuTemp.Value.ToString("0", CultureInfo.InvariantCulture) + " C",
                    Source = SourceName,
                    Details = MakeAcpiDetails("CPU temperature", TempCpuId)
                });
            }

            var gpuTemp = GetTemperature(handle, TempGpuId);
            if (gpuTemp.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = "Asus",
                    Name = "GPU",
                    Identifier = "asus/rog/temperature/gpu",
                    Value = gpuTemp.Value,
                    DisplayValue = gpuTemp.Value.ToString("0", CultureInfo.InvariantCulture) + " C",
                    Source = SourceName,
                    Details = MakeAcpiDetails("GPU temperature", TempGpuId)
                });
            }

            if (rows.Count == 0)
                rows.AddRange(MakeUnavailableRow(profile));

            return rows;
        }

        private IEnumerable<SensorReading> ReadAsusWmiSensors(IPluginContext context)
        {
            const string scopePath = @"root\WMI";
            const string className = "AsusAtkWmi_WMNB";
            var rows = new List<SensorReading>();
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Namespace", scopePath },
                { "WMI class", className },
                { "Mode", "Read-only ASUS WMI DSTS probe" }
            };

            try
            {
                var scope = new ManagementScope(scopePath);
                scope.Connect();
                using (var managementClass = new ManagementClass(scope, new ManagementPath(className), null))
                using (var instances = managementClass.GetInstances())
                {
                    var foundInstance = false;
                    foreach (ManagementObject instance in instances)
                    {
                        foundInstance = true;
                        details["Instance"] = SafeManagementValue(instance, "InstanceName");

                        AddWmiFanRow(rows, details, instance, CpuFanId, "CPU fan", CpuFanIdentifier);
                        AddWmiFanRow(rows, details, instance, GpuFanId, "GPU fan", GpuFanIdentifier);
                        AddWmiFanRow(rows, details, instance, MidFanId, "Mid fan", MidFanIdentifier);
                        AddWmiTemperatureRow(rows, details, instance, TempCpuId, "CPU", "asus/wmi/temperature/cpu");
                        AddWmiTemperatureRow(rows, details, instance, TempGpuId, "GPU", "asus/wmi/temperature/gpu");

                        var fanLevel = InvokeWmiDsts(instance, 0x00110018, details, "Fan level");
                        if (fanLevel.HasValue && fanLevel.Value.Normalized >= 0 && fanLevel.Value.Normalized <= 100)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Overview",
                                Name = "Asus fan level",
                                Identifier = "asus/wmi/fan-level",
                                Value = fanLevel.Value.Normalized,
                                DisplayValue = fanLevel.Value.Normalized.ToString(CultureInfo.InvariantCulture),
                                Source = SourceName,
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        break;
                    }

                    if (!foundInstance)
                    {
                        details["Status"] = "ASUS WMI class found, but no instances were returned";
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus laptop plug-in: WMI probe failed for " + scopePath + "\\" + className + ": " + ex.Message);
                }

                details["Error"] = ex.Message;
            }

            if (rows.Count == 0 && details.Count > 3)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Asus Plug-In",
                    DisplayValue = details.ContainsKey("Error")
                        ? "ASUS WMI interface could not be read"
                        : "ASUS WMI interface found, but no readable fan or temperature values",
                    Source = SourceName,
                    Details = details
                });
            }

            return rows;
        }

        private static void AddWmiFanRow(List<SensorReading> rows, Dictionary<string, string> details, ManagementObject instance, uint deviceId, string name, string identifier)
        {
            var value = InvokeWmiDsts(instance, deviceId, details, name);
            if (!value.HasValue)
            {
                return;
            }

            var fan = NormalizeFanRpm(value.Value.Raw, value.Value.Normalized);
            if (!fan.HasValue)
            {
                return;
            }

            rows.Add(new SensorReading
            {
                Type = "Fan",
                Hardware = "Asus",
                Name = name,
                Identifier = identifier,
                Value = fan.Value,
                DisplayValue = fan.Value.ToString(CultureInfo.InvariantCulture) + " RPM",
                Source = SourceName,
                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
            });
        }

        private static void AddWmiTemperatureRow(List<SensorReading> rows, Dictionary<string, string> details, ManagementObject instance, uint deviceId, string name, string identifier)
        {
            var value = InvokeWmiDsts(instance, deviceId, details, name + " temperature");
            if (!value.HasValue)
            {
                return;
            }

            var temperature = NormalizeTemperature(value.Value.Raw, value.Value.Normalized);
            if (!temperature.HasValue)
            {
                return;
            }

            rows.Add(new SensorReading
            {
                Type = "Temperature",
                Hardware = "Asus",
                Name = name,
                Identifier = identifier,
                Value = temperature.Value,
                DisplayValue = temperature.Value.ToString("0", CultureInfo.InvariantCulture) + " C",
                Source = SourceName,
                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
            });
        }

        private static WmiDstsValue? InvokeWmiDsts(ManagementObject instance, uint deviceId, Dictionary<string, string> details, string label)
        {
            try
            {
                using (var inParams = instance.GetMethodParameters("DSTS"))
                {
                    inParams["Device_ID"] = deviceId;
                    using (var outParams = instance.InvokeMethod("DSTS", inParams, null))
                    {
                        var rawObject = outParams == null ? null : outParams["device_status"];
                        if (rawObject == null)
                        {
                            details[label + " WMI DSTS " + FormatDeviceId(deviceId)] = "No status returned";
                            return null;
                        }

                        var raw = Convert.ToUInt32(rawObject, CultureInfo.InvariantCulture);
                        var normalized = unchecked((int)raw) - 65536;
                        details[label + " WMI DSTS " + FormatDeviceId(deviceId)] = raw.ToString(CultureInfo.InvariantCulture) + " (normalized " + normalized.ToString(CultureInfo.InvariantCulture) + ")";
                        return new WmiDstsValue(raw, normalized);
                    }
                }
            }
            catch (Exception ex)
            {
                details[label + " WMI DSTS " + FormatDeviceId(deviceId)] = "Error: " + ex.Message;
                return null;
            }
        }

        private static int? NormalizeFanRpm(uint raw, int normalized)
        {
            if (raw == 0)
            {
                return null;
            }

            if (normalized >= 0 && normalized <= 200)
            {
                return normalized * 100;
            }

            if (normalized > 200 && normalized <= 20000)
            {
                return normalized;
            }

            if (raw <= 200)
            {
                return (int)raw * 100;
            }

            if (raw <= 20000)
            {
                return (int)raw;
            }

            return null;
        }

        private static float? NormalizeTemperature(uint raw, int normalized)
        {
            if (normalized > 0 && normalized <= 150)
            {
                return normalized;
            }

            if (raw > 0 && raw <= 150)
            {
                return raw;
            }

            return null;
        }

        private static string SafeManagementValue(ManagementObject instance, string propertyName)
        {
            try
            {
                var value = instance == null ? null : instance[propertyName];
                return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string FormatDeviceId(uint deviceId)
        {
            return "0x" + deviceId.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static bool IsOnlyUnavailableRow(ICollection<SensorReading> rows)
        {
            if (rows == null || rows.Count != 1)
            {
                return false;
            }

            var row = rows.First();
            return string.Equals(row.Name, "Asus ROG Plug-In", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(row.Source, SourceName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.DisplayValue, "ATKACPI driver not available", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.DisplayValue, "ASUS desktop profile detected; ASUS laptop ATKACPI fan controls are not used", StringComparison.OrdinalIgnoreCase));
        }

        private SensorReading MakeFanControlRow(string controlIdentifier, string name)
        {
            int manualPercent;
            var isManual = manualPercents.TryGetValue(controlIdentifier, out manualPercent);
            return new SensorReading
            {
                Type = "Fan Control",
                Hardware = "Fan controls",
                Name = name,
                Identifier = controlIdentifier,
                Value = isManual ? (float?)manualPercent : null,
                DisplayValue = isManual ? FormatManualFanControlValue(manualPercent) : "automatic or firmware managed",
                Source = SourceName,
                Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Interface", "ATKACPI" },
                    { "Mode", "True fan-curve control is attempted first. If the laptop rejects direct fan writes, Sensor Readout falls back to ASUS thermal modes." },
                    { "Quiet range", "0-33% requests ASUS quiet/silent mode when thermal-mode fallback is used." },
                    { "Balanced range", "34-66% requests ASUS balanced mode when thermal-mode fallback is used." },
                    { "Performance range", "67-100% requests ASUS performance/turbo mode when thermal-mode fallback is used." },
                    { "Note", "On some ASUS laptops, including ExpertBook/VivoBook-class firmware, the mode request is accepted but the firmware still decides the actual fan speed." }
                }
            };
        }

        private static bool ShouldExposeLaptopFanControls(AsusMachineProfile profile)
        {
            return profile == null || !profile.IsDesktopTower;
        }

        private AsusMachineProfile GetAsusMachineProfile(IPluginContext context)
        {
            if (profileLoaded)
            {
                return cachedProfile;
            }

            var profile = new AsusMachineProfile();
            if (context != null && context.Machine != null)
            {
                profile.Manufacturer = context.Machine.Manufacturer ?? "";
                profile.Model = context.Machine.Model ?? "";
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily, PCSystemType, PCSystemTypeEx FROM Win32_ComputerSystem"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject system in instances)
                    {
                        profile.Manufacturer = FirstNonEmpty(SafeManagementValue(system, "Manufacturer"), profile.Manufacturer);
                        profile.Model = FirstNonEmpty(SafeManagementValue(system, "Model"), profile.Model);
                        profile.SystemFamily = SafeManagementValue(system, "SystemFamily");
                        profile.PcSystemType = SafeManagementValue(system, "PCSystemType");
                        profile.PcSystemTypeEx = SafeManagementValue(system, "PCSystemTypeEx");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: computer-system profile probe failed: " + ex.Message);
                }
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject board in instances)
                    {
                        profile.BaseboardManufacturer = SafeManagementValue(board, "Manufacturer");
                        profile.BaseboardProduct = SafeManagementValue(board, "Product");
                        profile.BaseboardVersion = SafeManagementValue(board, "Version");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: baseboard profile probe failed: " + ex.Message);
                }
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject enclosure in instances)
                    {
                        profile.ChassisTypes = FormatChassisTypes(enclosure["ChassisTypes"]);
                        profile.HasDesktopChassis = HasDesktopChassis(enclosure["ChassisTypes"]);
                        profile.HasLaptopChassis = HasLaptopChassis(enclosure["ChassisTypes"]);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: enclosure profile probe failed: " + ex.Message);
                }
            }

            PopulateAsusDeviceProfile(profile, context);
            PopulateAsusDriverProfile(profile, context);
            profile.IsDesktopTower = IsAsusRogDesktopTower(profile);

            cachedProfile = profile;
            profileLoaded = true;
            return cachedProfile;
        }

        private static IEnumerable<SensorReading> MakeProfileRows(AsusMachineProfile profile)
        {
            if (profile == null || !profile.IsDesktopTower)
            {
                return Enumerable.Empty<SensorReading>();
            }

            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddProfileDetail(details, "Manufacturer", profile.Manufacturer);
            AddProfileDetail(details, "Model", profile.Model);
            AddProfileDetail(details, "System family", profile.SystemFamily);
            AddProfileDetail(details, "PC system type", profile.PcSystemType);
            AddProfileDetail(details, "PC system type extended", profile.PcSystemTypeEx);
            AddProfileDetail(details, "Baseboard manufacturer", profile.BaseboardManufacturer);
            AddProfileDetail(details, "Baseboard product", profile.BaseboardProduct);
            AddProfileDetail(details, "Baseboard version", profile.BaseboardVersion);
            AddProfileDetail(details, "Chassis types", profile.ChassisTypes);
            AddProfileDetail(details, "ACPI fan devices", profile.AcpiFanDeviceCount.ToString(CultureInfo.InvariantCulture));
            AddProfileDetail(details, "ASUS System Control Interface", profile.AsusSystemControlInterfacePresent ? "present" : "not detected");
            AddProfileDetail(details, "ATK package device", profile.AtkPackageDevicePresent ? "present" : "not detected");
            AddProfileDetail(details, "ATKWMIACPI driver", profile.AtkWmiAcpiDriverState);
            AddProfileDetail(details, "ASUS System Analysis driver", profile.AsusSystemAnalysisDriverState);
            AddProfileDetail(details, "PawnIO driver", profile.PawnIoInstalled ? "detected" : "not detected");
            AddProfileDetail(details, "Fan control policy", "ASUS laptop fan-curve writes are disabled for this desktop profile. Motherboard fan controls should come from LibreHardwareMonitor/PawnIO when the board exposes them.");

            var rows = new List<SensorReading>
            {
                new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "ASUS ROG desktop profile",
                    Identifier = "asus/rog/profile/desktop",
                    DisplayValue = BuildDesktopProfileSummary(profile),
                    Source = SourceName,
                    Details = details
                }
            };

            if (!profile.PawnIoInstalled)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Motherboard fan support",
                    Identifier = "asus/rog/profile/pawnio",
                    DisplayValue = "PawnIO driver not detected; motherboard fan RPM and fan-control rows may be missing.",
                    Source = SourceName,
                    Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                });
            }

            return rows;
        }

        private static string BuildDesktopProfileSummary(AsusMachineProfile profile)
        {
            var parts = new List<string>();
            var model = FirstNonEmpty(profile.Model, profile.BaseboardProduct, "ASUS ROG desktop");
            parts.Add(model);
            if (profile.AcpiFanDeviceCount > 0)
            {
                parts.Add(profile.AcpiFanDeviceCount.ToString(CultureInfo.InvariantCulture) + " ACPI fan device" + (profile.AcpiFanDeviceCount == 1 ? "" : "s"));
            }
            if (profile.AsusSystemControlInterfacePresent)
            {
                parts.Add("ASUS System Control Interface present");
            }
            parts.Add(profile.PawnIoInstalled ? "PawnIO detected" : "PawnIO not detected");
            return string.Join(", ", parts.ToArray());
        }

        private static void AddProfileDetail(Dictionary<string, string> details, string name, string value)
        {
            if (details != null && !string.IsNullOrWhiteSpace(value))
            {
                details[name] = value.Trim();
            }
        }

        private static void PopulateAsusDeviceProfile(AsusMachineProfile profile, IPluginContext context)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'ACPI\\\\PNP0C0B%' OR DeviceID LIKE 'ACPI\\\\ASUS%' OR Name LIKE '%ASUS%' OR Name LIKE '%ATK%'"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject device in instances)
                    {
                        var name = SafeManagementValue(device, "Name");
                        var deviceId = SafeManagementValue(device, "DeviceID");
                        if (deviceId.StartsWith(@"ACPI\PNP0C0B", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.AcpiFanDeviceCount++;
                        }
                        if (Contains(name, "ASUS System Control Interface") || Contains(deviceId, "ASUS1000"))
                        {
                            profile.AsusSystemControlInterfacePresent = true;
                        }
                        if (Contains(name, "ATK Package") || Contains(deviceId, "ASUS7000"))
                        {
                            profile.AtkPackageDevicePresent = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: ASUS device profile probe failed: " + ex.Message);
                }
            }
        }

        private static void PopulateAsusDriverProfile(AsusMachineProfile profile, IPluginContext context)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State FROM Win32_SystemDriver"))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject driver in instances)
                    {
                        var name = SafeManagementValue(driver, "Name");
                        var displayName = SafeManagementValue(driver, "DisplayName");
                        var state = SafeManagementValue(driver, "State");
                        if (name.Equals("ATKWMIACPIIO", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.AtkWmiAcpiDriverState = string.IsNullOrWhiteSpace(state) ? "detected" : state;
                        }
                        if (name.Equals("ASUSSAIO", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.AsusSystemAnalysisDriverState = string.IsNullOrWhiteSpace(state) ? "detected" : state;
                        }
                        if (Contains(name, "PawnIO") || Contains(displayName, "PawnIO"))
                        {
                            profile.PawnIoInstalled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Asus ROG plug-in: ASUS driver profile probe failed: " + ex.Message);
                }
            }
        }

        private static bool IsAsusRogDesktopTower(AsusMachineProfile profile)
        {
            if (profile == null)
            {
                return false;
            }

            if (profile.HasLaptopChassis && !profile.HasDesktopChassis)
            {
                return false;
            }

            return IsKnownRogDesktopModel(profile.Model)
                || IsKnownRogDesktopModel(profile.BaseboardProduct)
                || (profile.HasDesktopChassis
                    && (Contains(profile.Manufacturer, "ASUS")
                        || Contains(profile.BaseboardManufacturer, "ASUS")
                        || Contains(profile.Model, "ROG")
                        || Contains(profile.SystemFamily, "ROG")));
        }

        private static bool IsKnownRogDesktopModel(string value)
        {
            return Contains(value, "GL10")
                || Contains(value, "GL12")
                || Contains(value, "G10CE")
                || Contains(value, "G10DK")
                || Contains(value, "G15CK")
                || Contains(value, "G15CE")
                || Contains(value, "G15CF")
                || Contains(value, "G16CHR")
                || Contains(value, "G21")
                || Contains(value, "G22")
                || Contains(value, "G35CZ")
                || Contains(value, "G35CG")
                || Contains(value, "ROG Strix GL10")
                || Contains(value, "ROG Strix G10")
                || Contains(value, "ROG Strix G15");
        }

        private static string FormatChassisTypes(object value)
        {
            var values = value as ushort[];
            if (values == null || values.Length == 0)
            {
                return "";
            }

            return string.Join(", ", values.Select(FormatChassisType).ToArray());
        }

        private static string FormatChassisType(ushort value)
        {
            if (value == 3) return "Desktop";
            if (value == 4) return "Low-profile desktop";
            if (value == 5) return "Pizza box";
            if (value == 6) return "Mini tower";
            if (value == 7) return "Tower";
            if (value == 8) return "Portable";
            if (value == 9) return "Laptop";
            if (value == 10) return "Notebook";
            if (value == 14) return "Sub-notebook";
            if (value == 15) return "Space-saving";
            if (value == 16) return "Lunch box";
            if (value == 30) return "Tablet";
            if (value == 31) return "Convertible";
            if (value == 32) return "Detachable";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static bool HasDesktopChassis(object value)
        {
            var values = value as ushort[];
            return values != null && values.Any(v => v == 3 || v == 4 || v == 5 || v == 6 || v == 7 || v == 15 || v == 16);
        }

        private static bool HasLaptopChassis(object value)
        {
            var values = value as ushort[];
            return values != null && values.Any(v => v == 8 || v == 9 || v == 10 || v == 14 || v == 30 || v == 31 || v == 32);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        private static string FormatManualFanControlValue(int percent)
        {
            return percent + "% manual, or " + PercentToModeName(percent) + " thermal mode";
        }

        private static string PercentToModeName(int percent)
        {
            if (percent >= 67) return "performance";
            if (percent <= 33) return "quiet";
            return "balanced";
        }

        private static Dictionary<string, string> MakeAcpiDetails(string label, uint deviceId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Interface", "ATKACPI" },
                { "Reading", label },
                { "Device ID", FormatDeviceId(deviceId) },
                { "Mode", "Read through ASUS ATKACPI" }
            };
        }

        // Returns fan speed in RPM, or -1 if the device ID is not supported.
        // ASUS WMI/ATKACPI fan tachometer values are commonly reported as RPM / 100.
        private static int GetFanRpm(IntPtr handle, uint deviceId)
        {
            int fan = DeviceGet(handle, deviceId);
            if (fan < 0)
            {
                fan += 65536;
                if (fan <= 0) return -1;
            }

            if (fan >= 0 && fan <= 200)
            {
                return fan * 100;
            }

            if (fan > 200 && fan <= 20000)
            {
                return fan;
            }

            return -1;
        }

        // Returns temperature in degrees C, or null if the device ID is not supported.
        // DeviceGet already subtracts 65536, so a valid 65 C reading comes back as 65.
        private static float? GetTemperature(IntPtr handle, uint deviceId)
        {
            int raw = DeviceGet(handle, deviceId);
            if (raw < 0 || raw > 150) return null;
            return raw;
        }

        // Sends a DSTS (read) request to ATKACPI and returns (raw_int32 - 65536).
        // Based on ghelper/app/AsusACPI.cs CallMethod() and DeviceGet().
        private static int DeviceGet(IntPtr handle, uint deviceId)
        {
            byte[] args = new byte[8];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);

            byte[] outputBuffer = new byte[16];
            int error;
            CallAcpi(handle, Dsts, args, outputBuffer, out error);
            return BitConverter.ToInt32(outputBuffer, 0) - 65536;
        }

        // Reads the current 16-byte fan curve for the given curve device ID.
        // Based on ghelper/app/AsusACPI.cs DeviceGetBuffer().
        // Sends a DEVS (write) request to set a fan curve.
        // Returns the status int32 from ATKACPI (>= 0 means success).
        // Based on ghelper/app/AsusACPI.cs DeviceSet(uint, byte[], string).
        private static int DeviceSet(IntPtr handle, uint deviceId, byte[] parameters, out bool ioOk, out int win32Error)
        {
            byte[] args = new byte[4 + parameters.Length];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);
            parameters.CopyTo(args, 4);

            byte[] outputBuffer = new byte[16];
            ioOk = CallAcpi(handle, Devs, args, outputBuffer, out win32Error);
            return BitConverter.ToInt32(outputBuffer, 0);
        }

        // Integer-value variant of DeviceSet - used for mode switches.
        // Matches ghelper/app/AsusACPI.cs DeviceSet(uint DeviceID, int Status, string logName).
        private static int DeviceSetValue(IntPtr handle, uint deviceId, uint value, out bool ioOk, out int win32Error)
        {
            byte[] args = new byte[8];
            BitConverter.GetBytes(deviceId).CopyTo(args, 0);
            BitConverter.GetBytes(value).CopyTo(args, 4);

            byte[] outputBuffer = new byte[16];
            ioOk = CallAcpi(handle, Devs, args, outputBuffer, out win32Error);
            return BitConverter.ToInt32(outputBuffer, 0);
        }

        private static byte[] DeviceInit(IntPtr handle)
        {
            byte[] args = new byte[8];
            byte[] outputBuffer = new byte[16];
            int error;
            CallAcpi(handle, Init, args, outputBuffer, out error);
            return outputBuffer;
        }

        // Common ATKACPI call: builds the [MethodID][ArgsLen][Args] buffer and sends via DeviceIoControl.
        // Based on ghelper/app/AsusACPI.cs CallMethod() and Control().
        private static bool CallAcpi(IntPtr handle, uint methodId, byte[] args, byte[] outputBuffer, out int win32Error)
        {
            byte[] inputBuffer = new byte[8 + args.Length];
            BitConverter.GetBytes(methodId).CopyTo(inputBuffer, 0);
            BitConverter.GetBytes((uint)args.Length).CopyTo(inputBuffer, 4);
            Array.Copy(args, 0, inputBuffer, 8, args.Length);

            uint bytesReturned = 0;
            var ok = DeviceIoControl(handle, ControlCode, inputBuffer, (uint)inputBuffer.Length,
                outputBuffer, (uint)outputBuffer.Length, ref bytesReturned, IntPtr.Zero);
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        private static IntPtr OpenAtkAcpi()
        {
            return CreateFile(
                AtkAcpiDevice,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);
        }

        // Builds a 16-byte flat fan curve: 8 temperature thresholds + 8 identical speed values.
        // Temperature thresholds are the same defaults ghelper uses.
        private static byte[] MakeFlatCurve(int percent)
        {
            var curve = new byte[16];
            // Temperature thresholds (C)
            curve[0] = 30; curve[1] = 40; curve[2] = 50; curve[3] = 60;
            curve[4] = 70; curve[5] = 80; curve[6] = 90; curve[7] = 100;
            // Fan speeds (0-100%)
            var speed = (byte)percent;
            curve[8] = speed; curve[9] = speed; curve[10] = speed; curve[11] = speed;
            curve[12] = speed; curve[13] = speed; curve[14] = speed; curve[15] = speed;
            return curve;
        }

        private static byte[] MakeFlatRange(int percent)
        {
            var scaled = (byte)Math.Max(0, Math.Min(255, percent * 255 / 100));
            return new[] { scaled, scaled };
        }

        private static uint MapVivoBookMode(uint mode)
        {
            if (mode == 1) return 2;
            if (mode == 2) return 1;
            return mode;
        }

        private static uint PercentToPerformanceMode(int percent)
        {
            if (percent >= 67) return 1; // Turbo/performance
            if (percent <= 33) return 2; // Silent/quiet
            return 0;                    // Balanced/standard
        }

        private static uint PercentToVivoBookMode(int percent)
        {
            if (percent >= 67) return 2; // Performance
            if (percent <= 33) return 1; // Quiet
            return 0;                    // Balanced
        }

        private void Log(string level, string message)
        {
            if (lastContext != null)
                lastContext.Log(level, message);
        }

        private static IEnumerable<SensorReading> MakeUnavailableRow(AsusMachineProfile profile)
        {
            var display = profile != null && profile.IsDesktopTower
                ? "ASUS desktop profile detected; ASUS laptop ATKACPI fan controls are not used"
                : "ATKACPI driver not available";
            return new[]
            {
                new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Asus ROG Plug-In",
                    DisplayValue = display,
                    Source = SourceName
                }
            };
        }

        private sealed class AsusMachineProfile
        {
            public string Manufacturer = "";
            public string Model = "";
            public string SystemFamily = "";
            public string PcSystemType = "";
            public string PcSystemTypeEx = "";
            public string BaseboardManufacturer = "";
            public string BaseboardProduct = "";
            public string BaseboardVersion = "";
            public string ChassisTypes = "";
            public int AcpiFanDeviceCount;
            public bool AsusSystemControlInterfacePresent;
            public bool AtkPackageDevicePresent;
            public string AtkWmiAcpiDriverState = "";
            public string AsusSystemAnalysisDriverState = "";
            public bool PawnIoInstalled;
            public bool HasDesktopChassis;
            public bool HasLaptopChassis;
            public bool IsDesktopTower;
        }

        private struct WmiDstsValue
        {
            public readonly uint Raw;
            public readonly int Normalized;

            public WmiDstsValue(uint raw, int normalized)
            {
                Raw = raw;
                Normalized = normalized;
            }
        }

        private bool IsAsusComputer(IPluginContext context)
        {
            if (context != null && context.Machine != null &&
                (Contains(context.Machine.Manufacturer, "ASUS")
                || Contains(context.Machine.Model, "ASUS")
                || Contains(context.Machine.Model, "ROG")
                || Contains(context.Machine.Model, "TUF")
                || Contains(context.Machine.Model, "Zephyrus")
                || Contains(context.Machine.Model, "Strix")
                || Contains(context.Machine.Model, "Flow")))
            {
                return true;
            }

            if (cachedAsusDetection.HasValue)
            {
                return cachedAsusDetection.Value;
            }

            cachedAsusDetection = DetectAsusComputerFromWindows(context);
            return cachedAsusDetection.Value;
        }

        private static bool DetectAsusComputerFromWindows(IPluginContext context)
        {
            if (HasMatchingWmiValue(@"root\CIMV2", "SELECT Manufacturer, Product FROM Win32_BaseBoard", context, "ASUS", "ASUSTeK"))
            {
                return true;
            }

            if (HasMatchingWmiValue(@"root\CIMV2", "SELECT Name, Manufacturer, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'ACPI\\\\ASUS%'", context, "ASUS", "ASUSTeK"))
            {
                return true;
            }

            if (WmiClassHasInstances(@"root\WMI", "AsusAtkWmi_WMNB", context))
            {
                return true;
            }

            return false;
        }

        private static bool HasMatchingWmiValue(string scopePath, string query, IPluginContext context, params string[] needles)
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
                            var value = Convert.ToString(property.Value, CultureInfo.InvariantCulture) ?? "";
                            foreach (var needle in needles)
                            {
                                if (Contains(value, needle))
                                {
                                    return true;
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
                    context.Log("Debug", "Asus laptop plug-in: fallback identity probe failed for " + scopePath + ": " + ex.Message);
                }
            }

            return false;
        }

        private static bool WmiClassHasInstances(string scopePath, string className, IPluginContext context)
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
                    context.Log("Debug", "Asus laptop plug-in: fallback identity probe failed for " + scopePath + "\\" + className + ": " + ex.Message);
                }

                return false;
            }
        }

        private static bool Contains(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static SensorReading CloneReading(SensorReading r)
        {
            return new SensorReading
            {
                Type = r.Type,
                Hardware = r.Hardware,
                Name = r.Name,
                Identifier = r.Identifier,
                Value = r.Value,
                DisplayValue = r.DisplayValue,
                Source = r.Source,
                Details = r.Details == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(r.Details, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
