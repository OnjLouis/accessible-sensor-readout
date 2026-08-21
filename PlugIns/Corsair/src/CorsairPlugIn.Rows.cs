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

        // Annex-derived safety facts (constraints §7/§9). Repeated verbatim on every control row
        // rather than computed, so the wording cannot drift between hub and PSU rows.
        //
        // The diagnostics sentence describes what TryResetFan actually does -- it writes the hub's
        // canned defaults (50 % fans / 100 % pump) and the plug-in keeps the hub in software mode
        // until the process exits -- rather than promising a return to the hub's own curve.
        private const string HubControlSafetyNote =
            "Taking control of any channel here (including via diagnostics) puts the whole iCUE LINK hub into software mode and keeps it there under Sensor Readout's control until the app exits, at which point the hub returns to its own hardware profile. Diagnostics briefly sets every exposed control to 100 percent, then returns it to its default -- 50 percent for fans, 100 percent for pumps, not the hub's own automatic curve. Pump channels never run below 50 percent duty; a lower request is raised automatically. Only one program should drive this hub's fans at a time.";

        private const string PsuControlSafetyNote =
            "A requested percent below 30 hands the fan back to the PSU's own automatic curve (including zero-RPM operation) instead of setting a duty. Diagnostics may briefly set every exposed control to 100 percent and restore automatic/original state afterward. Only one program should drive this PSU's fan at a time.";

        // The host's fan-control panel hides a control by default once its paired Fan row shows
        // 0 RPM (ShouldShowFanControl) -- which is routine for this PSU family in automatic zero-RPM
        // mode. Hosts with the zero-RPM marker support keep the control visible because of the
        // "Zero RPM capable" Details key below; on older hosts "Show stopped fans" reveals it.
        private const string PsuVisibilityNote =
            "On Sensor Readout versions without zero-RPM marker support, enable \"Show stopped fans\" in the Fan Controls dialog to reveal this control while the fan is stopped.";

        // Opt-in marker honoured by ShouldShowFanControl on hosts that support it: the paired fan
        // idling at 0 RPM is normal for this device, so the control must not be hidden as an
        // unused fan header. The value is explanatory text only; the KEY is the contract.
        private const string PsuZeroRpmMarkerNote =
            "This power supply stops its fan at low load. A reading of 0 RPM is normal and does not mean the control is unused.";

        private const string InteroperabilityNote =
            "Every Corsair transaction from this plug-in runs inside the shared Global\\CorsairLinkReadWriteGuardMutex. Monitoring can run alongside HWiNFO or Fan Control, but only one program should drive the fans. Do not run this plug-in together with Corsair iCUE because iCUE does not use that shared guard.";

        private const string PumpMinimumNote = "This pump channel never runs below 50 percent.";

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

        // The specific, and far more common, reason for the row above: a hub running its own stored
        // profile refuses even to list the devices plugged into it (annex §2/§7 editor's note,
        // measured 2026-08-07 on firmware 3.12.650), so there is nothing to show and no way out of
        // it that does not involve some program taking software control. Worth its own wording
        // because the way out is a concrete action the reader can take.
        private const string HubHardwareModeBlockedDisplayValue =
            "This iCUE LINK hub is running its own hardware fan profile, so it does not report the devices connected to it and no readings are available. Readings and fan controls appear as soon as Sensor Readout takes fan control of it: open the Fan Controls dialog and set the hub's \"Take fan control\" entry to any manual percent once, or restart the app after fan control has been used on this machine. They also appear if another supported program puts the hub into software mode.";

        // The one row that exists purely to be acted on. A hardware-mode-blocked hub has no channel
        // map, so it has no per-channel Fan Control rows -- and every host path that can ask this
        // plug-in to take a hub (the Fan Controls dialog, saved settings at start-up, curves,
        // diagnostics, fan profiles) starts from a Fan Control row. Without this entry a machine with
        // no marker file yet (a first install, or right after an app update, which replaces the
        // plug-in folder) could never get the hub out of hardware mode. Setting it to any manual
        // percent puts the hub into software mode, after which the real channel rows replace it on
        // the next refresh; a saved manual setting for it also re-takes the hub at the next start.
        private const string HubTakeControlName = "Take fan control";
        private const string HubTakeControlDisplayValue =
            "hardware managed; set any manual percent to put this hub under Sensor Readout's control";
        private const string HubTakeControlNote =
            "This entry exists because the hub is running its own hardware profile and does not list its fans. Setting it to any manual percent takes software control of the whole hub (fans 50 percent, pump 100 percent until a curve or a manual setting changes them) and the individual fan controls then appear in its place. Returning it to automatic sends nothing; it only means the hub is not re-taken automatically at the next start.";
        private const string HubTakeControlZeroRpmNote =
            "There is no fan reading paired with this entry; it is a control for the hub as a whole, so it stays visible without one.";

        // ---- Identifier helpers -------------------------------------------------------------

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
        /// and <c>corsair/psu/&lt;key&gt;/control/0</c>, case-insensitively (the host is
        /// case-insensitive everywhere -- a hand-edited settings file with uppercase should be a
        /// clean miss or accept, never a thrown status-bar error); everything else -- including
        /// every other plug-in's identifiers and LibreHardwareMonitor's leading-'/' ones -- returns
        /// false. <paramref name="deviceKey"/> comes back lower-cased so it matches the canonical
        /// key <see cref="CorsairWorker"/> keys its hub/PSU lookups and intent dictionaries by.
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

            if (!identifier.StartsWith("corsair/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = identifier.Split('/');
            if (parts.Length != 5 || !string.Equals(parts[3], "control", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var kind = parts[1];
            var key = parts[2];
            if (key.Length == 0)
            {
                return false;
            }

            // NumberStyles.None: no leading sign, no whitespace -- matches the doc's "exactly
            // corsair/link/<key>/control/<n>". A negative channel could only ever be a foreign or
            // malformed identifier, so it is rejected explicitly too, even though NumberStyles.None
            // already makes int.TryParse fail on a leading '-'.
            int parsedChannel;
            if (!int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out parsedChannel) || parsedChannel < 0)
            {
                return false;
            }

            if (string.Equals(kind, "link", StringComparison.OrdinalIgnoreCase))
            {
                isHub = true;
                deviceKey = key.ToLowerInvariant();
                channel = parsedChannel;
                return true;
            }

            if (string.Equals(kind, "psu", StringComparison.OrdinalIgnoreCase) && parsedChannel == 0)
            {
                isHub = false;
                deviceKey = key.ToLowerInvariant();
                channel = 0;
                return true;
            }

            return false;
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

                // And give a hub that is merely running its own profile the one control through
                // which it can be taken at all (see HubTakeControlName).
                if (hub.HardwareModeBlocked)
                {
                    rows.Add(MakeHubTakeControlRow(hub, nameSuffix, hardwareName));
                }
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
                // Still exactly one row, with the same identifier, either way -- only the
                // explanation changes when the cause is known to be the hub's own profile.
                DisplayValue = hub.HardwareModeBlocked ? HubHardwareModeBlockedDisplayValue : HubUnavailableDisplayValue,
                Source = SourceName,
                Details = details
            };
        }

        private static SensorReading MakeHubTakeControlRow(HubSnapshot hub, string nameSuffix, string hardwareName)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            details["Purpose"] = HubTakeControlNote;
            details["Safety"] = HubControlSafetyNote;
            details["Interoperability"] = InteroperabilityNote;
            // The host hides a fan control whose paired fan reads no RPM unless it carries this key;
            // this entry has no paired fan at all, and hiding it would defeat its purpose.
            details["Zero RPM capable"] = HubTakeControlZeroRpmNote;

            return new SensorReading
            {
                Type = "Fan Control",
                Hardware = hardwareName,
                Name = AppendHubNameSuffix(HubTakeControlName, nameSuffix),
                Identifier = HubIdentifier(hub.Serial, "control", CorsairLinkHubDevice.HubWideControlChannel),
                Value = null,
                DisplayValue = HubTakeControlDisplayValue,
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

            // Shared locals: the Fan row and its Fan Control row must keep an identical Name (that
            // is how the host pairs them), so the suffix goes on once here rather than being
            // appended separately at each call site below.
            var fanName = AppendHubNameSuffix(baseFanName, nameSuffix);
            var tempName = AppendHubNameSuffix(baseTempName, nameSuffix);

            if (channel.Rpm.HasValue)
            {
                var details = MakeHubChannelDetails(hub, channel, false);
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
                var details = MakeHubChannelDetails(hub, channel, false);
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
                    Hardware = hardwareName,
                    Name = fanName,
                    Identifier = HubIdentifier(hub.Serial, "control", channel.Channel),
                    Value = controlValue,
                    DisplayValue = controlDisplay,
                    Source = SourceName,
                    Details = details
                });
            }
        }

        // RequestedPercent/PercentIsDefault only describe the hardware while OwnsSoftwareControl is
        // true (CorsairSnapshot.cs doc comment); when it is false the hub (or whoever else owns it)
        // decides the real duty, so nothing here can honestly report a number.
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
            details["Firmware"] = string.IsNullOrEmpty(hub.FirmwareVersion) ? "Unknown" : hub.FirmwareVersion;

            if (isControlRow)
            {
                details["Safety"] = HubControlSafetyNote;
            }

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

            rows.Add(new SensorReading
            {
                Type = "Fan Control",
                Hardware = hardware,
                Name = "PSU fan",
                Identifier = PsuIdentifier(psu.PidHex, "control", "0"),
                Value = controlValue,
                DisplayValue = controlDisplay,
                Source = SourceName,
                Details = MakePsuDetails(psu, true)
            });
        }

        // RequestedPercent is -1 while the fan is under the PSU's own control (CorsairSnapshot.cs
        // doc comment), and otherwise only ever a manual duty the device itself clamped to its
        // 30-100 range -- the 1..29 branch exists because nothing here may assume the device-layer
        // invariant instead of checking it.
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
            details["Model"] = "Corsair HID PSU, product id 0x" + (psu.PidHex ?? string.Empty).ToUpperInvariant();

            if (isControlRow)
            {
                details["Safety"] = PsuControlSafetyNote;
                details["Visibility"] = PsuVisibilityNote;
                details["Zero RPM capable"] = PsuZeroRpmMarkerNote;
            }

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
                details[prefix + "hardware-mode blocked"] = hub.HardwareModeBlocked.ToString(CultureInfo.InvariantCulture);
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
