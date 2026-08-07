using System;
using System.Collections.Generic;
using System.Globalization;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// Row building and identifier helpers for <see cref="CorsairPlugIn"/>. Everything here is pure
    /// data transformation over an already-captured <see cref="CorsairSnapshot"/> -- no device I/O,
    /// no locking, no blocking call -- which is what keeps <see cref="CorsairPlugIn.GetReadings"/>
    /// without blocking the Sensor Readout UI.
    ///
    /// Identifier shape: lowercase, never
    /// leading '/', never containing '|'.
    /// <code>
    /// hub:  corsair/link/&lt;serial&gt;/fan/&lt;N&gt;   corsair/link/&lt;serial&gt;/temperature/&lt;N&gt;
    /// psu:  corsair/psu/&lt;pid&gt;/fan/0  /temperature/0  /temperature/1  /voltage/in  /power/out
    /// </code>
    /// </summary>
    public sealed partial class CorsairPlugIn
    {
        private const string SourceName = "Corsair Support Plug-In";
        private const string HubHardwareName = "Corsair iCUE LINK Hub";
        private const string StatusIdentifier = "corsair/status";
        private const string DiagnosticsIdentifier = "corsair/diagnostics";

        private const string InteroperabilityNote =
            "Every Corsair transaction from this plug-in runs inside the shared Global\\CorsairLinkReadWriteGuardMutex. Monitoring can run alongside HWiNFO or Fan Control. Do not run this plug-in together with Corsair iCUE because iCUE does not use that shared guard.";

        // CorsairWorker.NoteDeviceResult backs a device off to a 30 s retry interval after
        // MaxConsecutiveFailures reads in a row fail; the last successfully read values keep being
        // shown until it recovers, so every row for that device says so rather than looking current.
        private const string BackedOffNote =
            "Readings are stale: the device stopped responding and is being retried every 30 seconds";

        private const string WrongModeNoteFormat =
            "This iCUE LINK hub answered the last sensor read with hardware mode. Another program or the hub firmware may be managing it. Readings shown are the most recent successful read and may be stale or absent.";

        // Shown instead of silence when a hub is connected but contributes no channel rows, for
        // example because it is answering hardware-mode to every
        // sensor read.
        private const string HubUnavailableDisplayValue =
            "This iCUE LINK hub was detected, but no readings are available right now. It may be in hardware mode under another program's control, or idle.";

        // ---- Identifier helpers -------------------------------------------------------------

        internal static string HubIdentifier(string serial, string kind, int channel)
        {
            return "corsair/link/" + LowerOrEmpty(serial) + "/" + kind + "/" + channel.ToString(CultureInfo.InvariantCulture);
        }

        internal static string PsuIdentifier(string pidHex, string kind, string suffix)
        {
            return "corsair/psu/" + LowerOrEmpty(pidHex) + "/" + kind + "/" + suffix;
        }

        private static string LowerOrEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();
        }

        // ---- Row building -------------------------------------------------------------------

        // Internal so the no-hardware test harness can exercise row generation directly.
        internal static List<SensorReading> BuildRows(CorsairSnapshot snapshot, bool diagnosticsMode, CorsairWorker worker)
        {
            var rows = new List<SensorReading>();

            if (snapshot == null)
            {
                rows.Add(MakeStatusRow("Corsair support has not produced a reading yet."));
                return rows;
            }

            var hubs = snapshot.Hubs ?? new List<HubSnapshot>();
            var multiHub = hubs.Count > 1;
            for (var h = 0; h < hubs.Count; h++)
            {
                var suffix = multiHub ? "Hub " + (h + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
                AddHubRows(rows, hubs[h], suffix);
            }

            var psus = snapshot.Psus ?? new List<PsuSnapshot>();
            for (var p = 0; p < psus.Count; p++)
            {
                AddPsuRows(rows, psus[p]);
            }

            // Status is empty whenever at least one supported device is present.
            if (!string.IsNullOrEmpty(snapshot.Status))
            {
                rows.Add(MakeStatusRow(snapshot.Status));
            }

            if (diagnosticsMode && worker != null)
            {
                rows.Add(BuildDiagnosticsRow(snapshot, worker));
            }

            return rows;
        }

        private static SensorReading MakeStatusRow(string displayValue)
        {
            return new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Corsair Plug-In",
                Identifier = StatusIdentifier,
                DisplayValue = displayValue,
                Source = SourceName,
                Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        // ---- Hub rows ---------------------------------------------------------------------------

        private static void AddHubRows(List<SensorReading> rows, HubSnapshot hub, string nameSuffix)
        {
            if (hub == null)
            {
                return;
            }

            var hardwareName = AppendHubNameSuffix(HubHardwareName, nameSuffix);
            var wrongModeNote = hub.WrongModeReadFailure ? WrongModeNoteFormat : null;
            var emittedAnyChannelRow = false;

            var channels = hub.Channels ?? new List<HubChannelSnapshot>();
            for (var c = 0; c < channels.Count; c++)
            {
                var channel = channels[c];
                if (channel == null)
                {
                    continue;
                }

                // A channel with neither sensor has no useful row to show.
                if (channel.Rpm == null && channel.TemperatureC == null)
                {
                    continue;
                }

                AddHubChannelRows(rows, hub, channel, wrongModeNote, hardwareName, nameSuffix);
                emittedAnyChannelRow = true;
            }

            // Keep a detected hub visible even when its current mode exposes no sensor rows.
            if (!emittedAnyChannelRow)
            {
                rows.Add(MakeHubUnavailableRow(hub, nameSuffix));
            }
        }

        private static string AppendHubNameSuffix(string baseName, string nameSuffix)
        {
            return string.IsNullOrEmpty(nameSuffix) ? baseName : baseName + " (" + nameSuffix + ")";
        }

        private static SensorReading MakeHubUnavailableRow(HubSnapshot hub, string nameSuffix)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Firmware"] = string.IsNullOrEmpty(hub.FirmwareVersion) ? "Unknown" : hub.FirmwareVersion;
            details["Channels enumerated"] = (hub.Channels == null ? 0 : hub.Channels.Count).ToString(CultureInfo.InvariantCulture);
            AddNote(details, "Hardware mode", hub.WrongModeReadFailure ? WrongModeNoteFormat : null);
            details["Interoperability"] = InteroperabilityNote;

            return new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = AppendHubNameSuffix("Corsair Plug-In", nameSuffix),
                // A distinct internal identifier prevents multiple unavailable hubs from colliding.
                Identifier = "corsair/status/hub-" + (string.IsNullOrEmpty(hub.Serial) ? "hub0" : hub.Serial),
                DisplayValue = HubUnavailableDisplayValue,
                Source = SourceName,
                Details = details
            };
        }

        private static void AddHubChannelRows(List<SensorReading> rows, HubSnapshot hub, HubChannelSnapshot channel, string wrongModeNote, string hardwareName, string nameSuffix)
        {
            var deviceName = string.IsNullOrEmpty(channel.DeviceName) ? "Corsair device" : channel.DeviceName;
            var portLabel = "Port " + channel.Channel.ToString(CultureInfo.InvariantCulture);
            var baseFanName = channel.IsPump ? portLabel + " " + deviceName + " pump" : portLabel + " " + deviceName;
            var baseTempName = channel.IsPump
                ? portLabel + " " + deviceName + " liquid temperature"
                : portLabel + " " + deviceName + " temperature";

            var fanName = AppendHubNameSuffix(baseFanName, nameSuffix);
            var tempName = AppendHubNameSuffix(baseTempName, nameSuffix);

            if (channel.Rpm.HasValue)
            {
                var details = MakeHubChannelDetails(hub, channel);
                AddNote(details, "Hardware mode", wrongModeNote);
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = hardwareName,
                    Name = fanName,
                    Identifier = HubIdentifier(hub.Serial, "fan", channel.Channel),
                    Value = (float)channel.Rpm.Value,
                    DisplayValue = FormatRpm(channel.Rpm.Value),
                    Source = SourceName,
                    Details = details
                });
            }

            if (channel.TemperatureC.HasValue)
            {
                var details = MakeHubChannelDetails(hub, channel);
                AddNote(details, "Hardware mode", wrongModeNote);
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = hardwareName,
                    Name = tempName,
                    Identifier = HubIdentifier(hub.Serial, "temperature", channel.Channel),
                    Value = channel.TemperatureC.Value,
                    DisplayValue = FormatTemperature(channel.TemperatureC.Value),
                    Source = SourceName,
                    Details = details
                });
            }

        }

        private static Dictionary<string, string> MakeHubChannelDetails(HubSnapshot hub, HubChannelSnapshot channel)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Port"] = channel.Channel.ToString(CultureInfo.InvariantCulture);
            details["Device"] = string.IsNullOrEmpty(channel.DeviceName) ? "Unknown Corsair device" : channel.DeviceName;
            details["Firmware"] = string.IsNullOrEmpty(hub.FirmwareVersion) ? "Unknown" : hub.FirmwareVersion;

            details["Interoperability"] = InteroperabilityNote;
            AddNote(details, "Status", hub.BackedOff ? BackedOffNote : null);
            return details;
        }

        // ---- PSU rows ---------------------------------------------------------------------------

        private static void AddPsuRows(List<SensorReading> rows, PsuSnapshot psu)
        {
            if (psu == null)
            {
                return;
            }

            var hardware = string.IsNullOrEmpty(psu.ModelName) ? "Corsair PSU" : "Corsair " + psu.ModelName + " PSU";

            if (psu.FanRpm.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = hardware,
                    Name = "PSU fan",
                    Identifier = PsuIdentifier(psu.PidHex, "fan", "0"),
                    Value = (float)psu.FanRpm.Value,
                    DisplayValue = FormatRpm(psu.FanRpm.Value),
                    Source = SourceName,
                    Details = MakePsuDetails(psu)
                });
            }

            if (psu.Temperature1C.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = hardware,
                    Name = "PSU VRM temperature",
                    Identifier = PsuIdentifier(psu.PidHex, "temperature", "0"),
                    Value = psu.Temperature1C.Value,
                    DisplayValue = FormatTemperature(psu.Temperature1C.Value),
                    Source = SourceName,
                    Details = MakePsuDetails(psu)
                });
            }

            if (psu.Temperature2C.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = hardware,
                    Name = "PSU case temperature",
                    Identifier = PsuIdentifier(psu.PidHex, "temperature", "1"),
                    Value = psu.Temperature2C.Value,
                    DisplayValue = FormatTemperature(psu.Temperature2C.Value),
                    Source = SourceName,
                    Details = MakePsuDetails(psu)
                });
            }

            if (psu.InputVoltage.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "PSU input voltage",
                    Identifier = PsuIdentifier(psu.PidHex, "voltage", "in"),
                    Value = psu.InputVoltage.Value,
                    DisplayValue = FormatVoltage(psu.InputVoltage.Value),
                    Source = SourceName,
                    Details = MakePsuDetails(psu)
                });
            }

            if (psu.OutputPowerW.HasValue)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = hardware,
                    Name = "PSU output power",
                    Identifier = PsuIdentifier(psu.PidHex, "power", "out"),
                    Value = psu.OutputPowerW.Value,
                    DisplayValue = FormatWatts(psu.OutputPowerW.Value),
                    Source = SourceName,
                    Details = MakePsuDetails(psu)
                });
            }

        }

        private static Dictionary<string, string> MakePsuDetails(PsuSnapshot psu)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Device"] = string.IsNullOrEmpty(psu.ModelName) ? "Unknown Corsair PSU" : psu.ModelName;
            details["Model"] = "Corsair HID PSU, product id 0x" + (psu.PidHex ?? string.Empty).ToUpperInvariant();

            details["Interoperability"] = InteroperabilityNote;
            AddNote(details, "Status", psu.BackedOff ? BackedOffNote : null);
            return details;
        }

        // ---- Diagnostics bundle --------------------------------------------------------------

        private static SensorReading BuildDiagnosticsRow(CorsairSnapshot snapshot, CorsairWorker worker)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Running"] = worker.IsRunning.ToString(CultureInfo.InvariantCulture);
            details["Dormant"] = worker.IsDormant.ToString(CultureInfo.InvariantCulture);
            details["Started ticks"] = worker.StartedTicks.ToString(CultureInfo.InvariantCulture);
            details["Completed ticks"] = worker.CompletedTicks.ToString(CultureInfo.InvariantCulture);
            details["Failed device reads"] = worker.FailedDeviceReads.ToString(CultureInfo.InvariantCulture);
            details["Last tick duration"] = worker.LastTickDurationMs.ToString(CultureInfo.InvariantCulture) + " ms";
            details["Last error"] = string.IsNullOrEmpty(worker.LastError) ? "(none)" : worker.LastError;

            var lastErrorUtc = worker.LastErrorUtc;
            details["Last error time (UTC)"] = lastErrorUtc.HasValue
                ? lastErrorUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : "(none)";
            details["Last scan summary"] = string.IsNullOrEmpty(worker.LastScanSummary) ? "(none)" : worker.LastScanSummary;

            var hubs = snapshot.Hubs ?? new List<HubSnapshot>();
            for (var h = 0; h < hubs.Count; h++)
            {
                var hub = hubs[h];
                var prefix = "Hub " + hub.Serial + " ";
                details[prefix + "firmware"] = string.IsNullOrEmpty(hub.FirmwareVersion) ? "(unknown)" : hub.FirmwareVersion;
                details[prefix + "owns software control"] = hub.OwnsSoftwareControl.ToString(CultureInfo.InvariantCulture);
                details[prefix + "wrong-mode read failure"] = hub.WrongModeReadFailure.ToString(CultureInfo.InvariantCulture);
                details[prefix + "duties pending"] = hub.DutiesPending.ToString(CultureInfo.InvariantCulture);
                details[prefix + "last status byte"] = "0x" + hub.LastStatusByte.ToString("x2", CultureInfo.InvariantCulture);

                var channels = hub.Channels ?? new List<HubChannelSnapshot>();
                for (var c = 0; c < channels.Count; c++)
                {
                    var channel = channels[c];
                    if (channel == null)
                    {
                        continue;
                    }

                    var channelPrefix = prefix + "port " + channel.Channel.ToString(CultureInfo.InvariantCulture) + " ";
                    details[channelPrefix + "device"] = string.IsNullOrEmpty(channel.DeviceName) ? "(unknown)" : channel.DeviceName;
                    details[channelPrefix + "device id"] = string.IsNullOrEmpty(channel.DeviceId) ? "(none)" : channel.DeviceId;
                    // The friendly name does not uniquely identify the model and variant bytes.
                    details[channelPrefix + "model code"] = "0x" + channel.ModelCode.ToString("x2", CultureInfo.InvariantCulture)
                        + " / variant 0x" + channel.VariantCode.ToString("x2", CultureInfo.InvariantCulture);
                }
            }

            var psus = snapshot.Psus ?? new List<PsuSnapshot>();
            for (var p = 0; p < psus.Count; p++)
            {
                var psu = psus[p];
                var prefix = "PSU " + psu.PidHex + " ";
                details[prefix + "model"] = string.IsNullOrEmpty(psu.ModelName) ? "(unknown)" : psu.ModelName;
                details[prefix + "fan is manual"] = psu.FanIsManual.ToString(CultureInfo.InvariantCulture);
                details[prefix + "requested percent"] = psu.RequestedPercent.ToString(CultureInfo.InvariantCulture);
            }

            return new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Corsair Plug-In Diagnostics",
                Identifier = DiagnosticsIdentifier,
                DisplayValue = worker.CompletedTicks.ToString(CultureInfo.InvariantCulture) + " tick(s) completed, "
                    + worker.FailedDeviceReads.ToString(CultureInfo.InvariantCulture) + " failed device read(s)",
                Source = SourceName,
                Details = details
            };
        }

        // ---- Small helpers ----------------------------------------------------------------------

        private static void AddNote(Dictionary<string, string> details, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                details[key] = value;
            }
        }

        private static string FormatRpm(int rpm)
        {
            return rpm.ToString(CultureInfo.InvariantCulture) + " RPM";
        }

        private static string FormatTemperature(float celsius)
        {
            return celsius.ToString("0.0", CultureInfo.InvariantCulture) + " C";
        }

        private static string FormatVoltage(float volts)
        {
            return volts.ToString("0.0", CultureInfo.InvariantCulture) + " V";
        }

        private static string FormatWatts(float watts)
        {
            return Math.Round(watts).ToString("0", CultureInfo.InvariantCulture) + " W";
        }
    }
}
