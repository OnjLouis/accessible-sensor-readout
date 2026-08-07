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
    /// on the order of microseconds (brief item K).
    ///
    /// Identifier shape (host-conventions.md sec 1.3, constraints.md sec 5): lowercase, never
    /// leading '/', never containing '|'.
    /// <code>
    /// hub:  corsair/link/&lt;serial&gt;/fan/&lt;N&gt;   corsair/link/&lt;serial&gt;/temperature/&lt;N&gt;   corsair/link/&lt;serial&gt;/control/&lt;N&gt;
    /// psu:  corsair/psu/&lt;pid&gt;/fan/0  /temperature/0  /temperature/1  /control/0  /voltage/in  /power/out
    /// </code>
    /// A Fan row and its Fan Control row always share the same <see cref="SensorReading.Name"/>, and
    /// their identifiers differ only by the literal "/fan/" &lt;-&gt; "/control/" swap the host's own
    /// <c>GuessControlIdentifier</c>/<c>GuessFanIdentifier</c> perform.
    /// </summary>
    public sealed partial class CorsairPlugIn
    {
        private const string SourceName = "Corsair Support Plug-In";
        private const string HubHardwareName = "Corsair iCUE LINK Hub";
        private const string StatusIdentifier = "corsair/status";
        private const string DiagnosticsIdentifier = "corsair/diagnostics";

        // Annex-derived safety facts (constraints.md sec 7/9). Repeated verbatim on every control
        // row rather than computed, so the wording cannot drift between hub and PSU rows.
        private const string HubControlSafetyNote =
            "Taking control of a channel here puts the whole iCUE LINK hub into software mode. Pump channels (AIO/XD5/XD6 pumps) never run below 50 percent duty; a lower request is raised automatically. Diagnostics may briefly set every exposed control to 100 percent and restore automatic/original state afterward. Only one program should drive this hub's fans at a time.";

        private const string PsuControlSafetyNote =
            "A requested percent below 30 hands the fan back to the PSU's own automatic curve (including zero-RPM operation) instead of setting a duty. Diagnostics may briefly set every exposed control to 100 percent and restore automatic/original state afterward. Only one program should drive this PSU's fan at a time.";

        private const string InteroperabilityNote =
            "Every Corsair transaction from this plug-in runs inside the shared Global\\CorsairLinkReadWriteGuardMutex, so reading here is safe alongside other Corsair tools. Do not run this plug-in together with Corsair iCUE -- iCUE and this plug-in would both try to drive the same hardware.";

        private const string PumpMinimumNote = "This pump channel never runs below 50 percent.";

        private const string WrongModeNoteFormat =
            "This iCUE LINK hub answered the last sensor read with hardware mode: another program (or its own firmware) is driving it right now, not this plug-in. Readings shown are the most recent successful read and may be stale or absent. Readings resume once a supported program controls the hub again, or once Sensor Readout takes fan control of one of its channels.";

        // ---- Identifier helpers (Step 1) -----------------------------------------------------

        internal static string HubIdentifier(string serial, string kind, int channel)
        {
            return "corsair/link/" + LowerOrEmpty(serial) + "/" + kind + "/" + channel.ToString(CultureInfo.InvariantCulture);
        }

        internal static string PsuIdentifier(string pidHex, string kind, string suffix)
        {
            return "corsair/psu/" + LowerOrEmpty(pidHex) + "/" + kind + "/" + suffix;
        }

        /// <summary>
        /// Cheap ownership gate for <see cref="CorsairPlugIn.TrySetFanPercent"/>/<see cref="CorsairPlugIn.TryResetFan"/>:
        /// string parsing only, no I/O. Accepts exactly <c>corsair/link/&lt;key&gt;/control/&lt;n&gt;</c>
        /// and <c>corsair/psu/&lt;key&gt;/control/0</c>; everything else -- including every other
        /// plug-in's identifiers and LibreHardwareMonitor's leading-'/' ones -- returns false.
        /// </summary>
        internal static bool TryParseControlIdentifier(string identifier, out bool isHub, out string deviceKey, out int channel)
        {
            isHub = false;
            deviceKey = null;
            channel = 0;

            if (string.IsNullOrEmpty(identifier))
            {
                return false;
            }

            if (!identifier.StartsWith("corsair/", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = identifier.Split('/');
            if (parts.Length != 5 || !string.Equals(parts[3], "control", StringComparison.Ordinal))
            {
                return false;
            }

            var kind = parts[1];
            var key = parts[2];
            if (key.Length == 0)
            {
                return false;
            }

            int parsedChannel;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedChannel))
            {
                return false;
            }

            if (string.Equals(kind, "link", StringComparison.Ordinal))
            {
                isHub = true;
                deviceKey = key;
                channel = parsedChannel;
                return true;
            }

            if (string.Equals(kind, "psu", StringComparison.Ordinal) && parsedChannel == 0)
            {
                isHub = false;
                deviceKey = key;
                channel = 0;
                return true;
            }

            return false;
        }

        private static string LowerOrEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();
        }

        // ---- Row building (Step 2) ------------------------------------------------------------

        private static List<SensorReading> BuildRows(CorsairSnapshot snapshot, bool diagnosticsMode, CorsairWorker worker)
        {
            var rows = new List<SensorReading>();

            if (snapshot == null)
            {
                rows.Add(MakeStatusRow("Corsair support has not produced a reading yet."));
                return rows;
            }

            var hubs = snapshot.Hubs ?? new List<HubSnapshot>();
            for (var h = 0; h < hubs.Count; h++)
            {
                AddHubRows(rows, hubs[h]);
            }

            var psus = snapshot.Psus ?? new List<PsuSnapshot>();
            for (var p = 0; p < psus.Count; p++)
            {
                AddPsuRows(rows, psus[p]);
            }

            // CorsairWorker.StatusFor leaves Status empty exactly when at least one device is
            // present, so this doubles as the "nothing to show otherwise" gate (task-7 carry (e)).
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

        private static void AddHubRows(List<SensorReading> rows, HubSnapshot hub)
        {
            if (hub == null || hub.Channels == null)
            {
                return;
            }

            var wrongModeNote = hub.WrongModeReadFailure ? WrongModeNoteFormat : null;

            for (var c = 0; c < hub.Channels.Count; c++)
            {
                var channel = hub.Channels[c];
                if (channel == null)
                {
                    continue;
                }

                // Task-5 carry: a channel with neither sensor has nothing to show (e.g. the
                // unknown model 0x06 on Robin's port 6, likely a TITAN LCD cap the hub does not
                // separately identify) -- emit no rows at all for it rather than a blank one.
                if (channel.Rpm == null && channel.TemperatureC == null)
                {
                    continue;
                }

                AddHubChannelRows(rows, hub, channel, wrongModeNote);
            }
        }

        private static void AddHubChannelRows(List<SensorReading> rows, HubSnapshot hub, HubChannelSnapshot channel, string wrongModeNote)
        {
            var deviceName = string.IsNullOrEmpty(channel.DeviceName) ? "Corsair device" : channel.DeviceName;
            var portLabel = "Port " + channel.Channel.ToString(CultureInfo.InvariantCulture);
            var fanName = channel.IsPump ? portLabel + " " + deviceName + " pump" : portLabel + " " + deviceName;
            var tempName = channel.IsPump
                ? portLabel + " " + deviceName + " liquid temperature"
                : portLabel + " " + deviceName + " temperature";

            if (channel.Rpm.HasValue)
            {
                var details = MakeHubChannelDetails(hub, channel, false);
                AddNote(details, "Hardware mode", wrongModeNote);
                rows.Add(new SensorReading
                {
                    Type = "Fan",
                    Hardware = HubHardwareName,
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
                var details = MakeHubChannelDetails(hub, channel, false);
                AddNote(details, "Hardware mode", wrongModeNote);
                rows.Add(new SensorReading
                {
                    Type = "Temperature",
                    Hardware = HubHardwareName,
                    Name = tempName,
                    Identifier = HubIdentifier(hub.Serial, "temperature", channel.Channel),
                    Value = channel.TemperatureC.Value,
                    DisplayValue = FormatTemperature(channel.TemperatureC.Value),
                    Source = SourceName,
                    Details = details
                });
            }

            if (channel.HasControl)
            {
                var details = MakeHubChannelDetails(hub, channel, true);
                AddNote(details, "Hardware mode", wrongModeNote);
                if (channel.IsPump)
                {
                    details["Pump minimum"] = PumpMinimumNote;
                }

                float? controlValue;
                string controlDisplay;
                DescribeHubControl(hub, channel, out controlValue, out controlDisplay);

                rows.Add(new SensorReading
                {
                    Type = "Fan Control",
                    Hardware = HubHardwareName,
                    Name = fanName,
                    Identifier = HubIdentifier(hub.Serial, "control", channel.Channel),
                    Value = controlValue,
                    DisplayValue = controlDisplay,
                    Source = SourceName,
                    Details = details
                });
            }
        }

        // Constraint D. RequestedPercent/PercentIsDefault only describe the hardware while
        // OwnsSoftwareControl is true (CorsairSnapshot.cs doc comment); when it is false the hub (or
        // whoever else owns it) decides the real duty, so nothing here can honestly report a number.
        private static void DescribeHubControl(HubSnapshot hub, HubChannelSnapshot channel, out float? value, out string displayValue)
        {
            if (!hub.OwnsSoftwareControl)
            {
                value = null;
                displayValue = "automatic or firmware managed";
                return;
            }

            value = (float)channel.RequestedPercent;
            displayValue = channel.RequestedPercent.ToString(CultureInfo.InvariantCulture)
                + (channel.PercentIsDefault ? "% default" : "% manual");
        }

        private static Dictionary<string, string> MakeHubChannelDetails(HubSnapshot hub, HubChannelSnapshot channel, bool isControlRow)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Port"] = channel.Channel.ToString(CultureInfo.InvariantCulture);
            details["Device"] = string.IsNullOrEmpty(channel.DeviceName) ? "Unknown Corsair device" : channel.DeviceName;
            details["Device id"] = string.IsNullOrEmpty(channel.DeviceId) ? "(none reported)" : channel.DeviceId;
            details["Firmware"] = string.IsNullOrEmpty(hub.FirmwareVersion) ? "Unknown" : hub.FirmwareVersion;

            if (isControlRow)
            {
                details["Safety"] = HubControlSafetyNote;
            }

            details["Interoperability"] = InteroperabilityNote;
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
                    Details = MakePsuDetails(psu, false)
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
                    Details = MakePsuDetails(psu, false)
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
                    Details = MakePsuDetails(psu, false)
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
                    Details = MakePsuDetails(psu, false)
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
                    Details = MakePsuDetails(psu, false)
                });
            }

            // Emitted unconditionally (unlike the sensor rows above) so the paired "/fan/" ->
            // "/control/" identifier always exists and "automatic" still has something to say even
            // on a tick where the fan speed read itself failed.
            float? controlValue;
            string controlDisplay;
            DescribePsuControl(psu, out controlValue, out controlDisplay);

            var controlDetails = MakePsuDetails(psu, true);
            rows.Add(new SensorReading
            {
                Type = "Fan Control",
                Hardware = hardware,
                Name = "PSU fan",
                Identifier = PsuIdentifier(psu.PidHex, "control", "0"),
                Value = controlValue,
                DisplayValue = controlDisplay,
                Source = SourceName,
                Details = controlDetails
            });
        }

        // Constraint E. RequestedPercent is -1 while the fan is under the PSU's own control
        // (CorsairSnapshot.cs doc comment), and otherwise only ever a manual duty the device itself
        // clamped to its 30-100 range -- the 1..29 branch exists for the same reason the brief spells
        // it out: nothing here may assume the device-layer invariant instead of checking it.
        private static void DescribePsuControl(PsuSnapshot psu, out float? value, out string displayValue)
        {
            var percent = psu.RequestedPercent;
            if (percent < 0)
            {
                value = null;
                displayValue = "automatic or firmware managed";
                return;
            }

            if (percent < 30)
            {
                value = null;
                displayValue = "automatic (PSU zero-RPM control)";
                return;
            }

            value = (float)percent;
            displayValue = percent.ToString(CultureInfo.InvariantCulture) + "% manual";
        }

        private static Dictionary<string, string> MakePsuDetails(PsuSnapshot psu, bool isControlRow)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Device"] = string.IsNullOrEmpty(psu.ModelName) ? "Unknown Corsair PSU" : psu.ModelName;
            details["Model"] = "Corsair HID PSU, product id 0x" + LowerOrEmpty(psu.PidHex).ToUpperInvariant();

            if (isControlRow)
            {
                details["Safety"] = PsuControlSafetyNote;
            }

            details["Interoperability"] = InteroperabilityNote;
            return details;
        }

        // ---- Diagnostics bundle (Step 2 / brief item J) ----------------------------------------

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
                    var channelPrefix = prefix + "port " + channel.Channel.ToString(CultureInfo.InvariantCulture) + " ";
                    // The snapshot does not carry the raw model/variant bytes for a recognized
                    // device (only its friendly name) -- device name plus device id is the closest
                    // per-channel identity this layer can report honestly.
                    details[channelPrefix + "device"] = string.IsNullOrEmpty(channel.DeviceName) ? "(unknown)" : channel.DeviceName;
                    details[channelPrefix + "device id"] = string.IsNullOrEmpty(channel.DeviceId) ? "(none)" : channel.DeviceId;
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
