using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// Live state of one iCUE LINK hub channel: which device is plugged into it, its most recent
    /// sensor readings, and the duty this plug-in has asked for. <see cref="RequestedPercent"/> only
    /// means anything while <see cref="CorsairLinkHubDevice.OwnsSoftwareControl"/> is true --
    /// otherwise the hub (or whichever program owns it) decides the real duty.
    /// </summary>
    public sealed class LinkChannelState
    {
        public int Channel;
        public LinkKnownDevice Device;
        public string DeviceId;
        public int? Rpm;
        public float? TemperatureC;
        public int RequestedPercent;
        public bool PercentIsDefault;
    }

    /// <summary>
    /// One session against a Corsair iCUE LINK Hub (PID 0x0C3F), implementing the wire protocol in
    /// <c>PlugIns/Corsair/docs/icuelink-protocol.md</c> on top of <see cref="CorsairHidStream"/>.
    ///
    /// Deliberate policy difference from the studied reference implementation: connecting is
    /// strictly read-only. <see cref="Connect"/> never changes the hub's mode and never writes a
    /// duty, so simply having this plug-in enabled cannot take the hub away from whatever program
    /// (Fan Control, SignalRGB, ...) is already driving the user's fans.
    ///
    /// Exactly two entry points can put the hub into software mode:
    /// <list type="bullet">
    /// <item><see cref="SetChannelPercent"/> -- takes control if this plug-in does not already have
    /// it, because the user has explicitly asked for a duty.</item>
    /// <item><see cref="ReassertControl"/> -- takes control *unconditionally*, baselining every
    /// channel to its default and writing the whole set. It is a resume path, not a query: call it
    /// only from a caller that has itself recorded prior ownership.</item>
    /// </list>
    /// <see cref="ResetChannel"/> is not one of them. When this plug-in does not own the hub, a
    /// reset is bookkeeping only and sends nothing -- there is no control to hand back, and entering
    /// software mode to "reset" would take the hub from its real owner.
    ///
    /// Every wire transaction runs inside the shared <see cref="CorsairDeviceGuard"/> mutex, held
    /// for a whole endpoint bracket (close, open, read/write, close) as the annex requires. The
    /// guard wait is bounded at 2000 ms; when it expires the operation simply reports failure and
    /// changes nothing, so the caller keeps the data it already had.
    ///
    /// Not thread-safe against itself by design of the protocol, so a private monitor serializes the
    /// public methods; it is reentrant, which is what lets a wrong-mode sensor read call
    /// <see cref="ReassertControl"/> from inside <see cref="RefreshSensors"/>.
    /// </summary>
    public sealed class CorsairLinkHubDevice
    {
        // Annex §9 timing rules.
        private const int WriteTimeoutMs = 500;
        private const int ReadTimeoutMs = 500;
        private const int ResponseBudgetMs = 500;

        // Keep cross-process guard waits bounded.
        private const int GuardTimeoutMs = 2000;

        // Shutdown is on a budget (a ProcessExit handler gets roughly two seconds for everything),
        // and the hardware-mode restore is best effort, so it does not get the full guard wait.
        private const int ShutdownGuardTimeoutMs = 500;

        // Annex §8 duty policy.
        private const int DefaultFanPercent = 50;
        private const int DefaultPumpPercent = 100;
        private const int MinimumPumpPercent = 50;

        // A hub that floods input reports must not be able to hold a response poll open for the
        // whole budget one 0-ms read at a time.
        private const int MaxResponseReads = 64;

        private const string FallbackSerial = "hub0";

        private readonly CorsairHidDeviceInfo info;
        private readonly CorsairDeviceGuard guard;
        private readonly Action<string, string> log;
        private readonly List<LinkChannelState> channels = new List<LinkChannelState>();
        private readonly object sync = new object();

        private CorsairHidStream stream;
        private string serial = FallbackSerial;
        private string firmwareVersion = string.Empty;
        private bool twoReadEnumeration;
        private bool ownsSoftwareControl;
        private bool lastReadWrongMode;
        private bool isGone;

        // Same-thread recursion tripwire, not a cross-thread lock: the monitor above already
        // serializes callers, and it is reentrant, so the only way back into ReassertControl is this
        // thread re-entering through RefreshSensors' wrong-mode branch. Annex §10 calls for mode
        // changes to be refused rather than nested when that happens.
        private bool reassertInFlight;

        // Set when a full-set duty write did not reach the hub. While it is set, the in-memory
        // RequestedPercent values do not describe what the fans are doing, so RefreshSensors re-sends
        // the whole set once per tick until a write finally lands.
        private bool dutiesDirty;

        // Log-volume latch for the control path: true once a control failure has been reported at
        // Error, so the identical failure on every subsequent tick is logged at Debug instead. See
        // the control-failure log latch section below.
        private bool dutyFailureReported;

        // Status of the most recent failing command within the current bracket; both are reset when
        // a bracket starts and are only meaningful after that bracket reported failure.
        private byte lastStatus;
        private bool lastStatusWrongMode;

        public CorsairLinkHubDevice(CorsairHidDeviceInfo info, CorsairDeviceGuard guard, Action<string, string> log)
        {
            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            if (guard == null)
            {
                throw new ArgumentNullException("guard");
            }

            this.info = info;
            this.guard = guard;
            this.log = log;
        }

        public string Serial
        {
            get { return serial; }
        }

        public string FirmwareVersion
        {
            get { return firmwareVersion; }
        }

        public bool OwnsSoftwareControl
        {
            get { return ownsSoftwareControl; }
        }

        public bool LastReadWrongMode
        {
            get { return lastReadWrongMode; }
        }

        public bool IsGone
        {
            get { return isGone; }
        }

        /// <summary>
        /// True while a requested duty set has not reached the hardware and is waiting to be re-sent
        /// on the next refresh. Lets a diagnostics view show an outstanding duty write honestly
        /// instead of implying the fans are already running at the requested percentages.
        /// </summary>
        public bool DutiesPending
        {
            get { return dutiesDirty; }
        }

        /// <summary>
        /// Response status byte recorded during the most recent transaction (annex §4.5: 0x00 OK,
        /// 0x03 wrong mode, anything else an error). Reset to 0x00 when each transaction starts, so
        /// it describes the last one only, and is meaningful only when that transaction reported
        /// failure. Exposed for diagnostics.
        /// </summary>
        public byte LastStatusByte
        {
            get { return lastStatus; }
        }

        public List<LinkChannelState> Channels
        {
            get { return channels; }
        }

        // ---- Session lifecycle ---------------------------------------------------------------

        /// <summary>
        /// Opens the hub, reads its identity and firmware, enumerates the connected sub-devices into
        /// <see cref="Channels"/>, and takes one sensor reading. Sends no mode change and no duty
        /// write. Returns false only when the device cannot be opened or enumeration fails on the
        /// wire; a hub that answers "hardware mode" to the sensor reads still connects successfully
        /// with its channels enumerated (see <see cref="LastReadWrongMode"/>).
        /// </summary>
        public bool Connect()
        {
            lock (sync)
            {
                if (stream != null)
                {
                    return true;
                }

                serial = string.IsNullOrEmpty(info.SerialNumber) ? FallbackSerial : info.SerialNumber.ToLowerInvariant();
                firmwareVersion = string.Empty;
                twoReadEnumeration = false;
                lastReadWrongMode = false;
                channels.Clear();

                if (info.OutputReportLength < 7 || info.InputReportLength < 9)
                {
                    Log("Error", "Corsair plug-in: the iCUE LINK hub at " + info.Path + " reports HID report lengths (in="
                        + info.InputReportLength.ToString(CultureInfo.InvariantCulture) + ", out="
                        + info.OutputReportLength.ToString(CultureInfo.InvariantCulture) + ") too short for the hub protocol.");
                    return false;
                }

                stream = CorsairHidStream.Open(info);
                if (stream == null)
                {
                    Log("Error", "Corsair plug-in: could not open the iCUE LINK hub at " + info.Path + ".");
                    return false;
                }

                firmwareVersion = ReadFirmwareVersion();
                if (firmwareVersion.Length == 0)
                {
                    Log("Debug", "Corsair plug-in: the iCUE LINK hub did not report a firmware version; falling back to single-read enumeration.");
                }

                twoReadEnumeration = SupportsTwoReadEnumeration(firmwareVersion);
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " reports firmware "
                    + (firmwareVersion.Length == 0 ? "(unknown)" : firmwareVersion)
                    + "; using " + (twoReadEnumeration ? "two-read" : "single-read") + " sub-device enumeration.");

                if (!EnumerateChannels())
                {
                    Log("Error", "Corsair plug-in: sub-device enumeration failed on iCUE LINK hub " + serial + ".");
                    stream.Dispose();
                    stream = null;
                    return false;
                }

                // First reading. A failure here (including "hub is in hardware mode") is not fatal:
                // the channel map is what makes the device usable, and the next refresh may succeed.
                RefreshSensors();
                return true;
            }
        }

        /// <summary>
        /// Closes the session. The hardware-mode restore is sent only when this plug-in actually
        /// took software control AND the caller asks for it, so a read-only session can never push
        /// the hub out of the mode another program put it in.
        ///
        /// Shutdown-safe: the restore is best effort and fully contained, and releasing the HID
        /// handle happens in a finally, so a throw on the way out (a disposed guard, a device that
        /// vanished) can never leak the handle. The restore waits at most
        /// <see cref="ShutdownGuardTimeoutMs"/> ms for the guard rather than the usual
        /// <see cref="GuardTimeoutMs"/>, because a ProcessExit handler has roughly two seconds in
        /// total for everything it needs to do.
        /// </summary>
        public void Disconnect(bool restoreHardwareMode)
        {
            lock (sync)
            {
                var localStream = stream;
                if (localStream == null)
                {
                    ClearSessionState();
                    return;
                }

                try
                {
                    if (ownsSoftwareControl && restoreHardwareMode)
                    {
                        Log("Debug", "Corsair plug-in: returning iCUE LINK hub " + serial + " to hardware mode.");
                        if (RunDirectCommand(LinkHubData.EnterHardwareMode, null, "Error", ShutdownGuardTimeoutMs) == null)
                        {
                            // Best effort only (annex §2): the hub keeps the last written duties until
                            // it resets or another program takes over, which is a safe steady state.
                            Log("Error", "Corsair plug-in: the hardware-mode restore on iCUE LINK hub " + serial
                                + " did not complete; the hub keeps the last written duties until it resets.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never let a shutdown-path failure prevent the handle from being released.
                    Log("Error", "Corsair plug-in: the hardware-mode restore on iCUE LINK hub " + serial
                        + " threw during shutdown (" + ex.Message + "); closing the device anyway.");
                }
                finally
                {
                    ClearSessionState();
                    stream = null;
                    localStream.Dispose();
                }
            }
        }

        private void ClearSessionState()
        {
            ownsSoftwareControl = false;
            lastReadWrongMode = false;
            dutiesDirty = false;
            dutyFailureReported = false;
            isGone = false;
        }

        // ---- Sensor reads --------------------------------------------------------------------

        /// <summary>
        /// Reads the speed and temperature arrays and folds them into <see cref="Channels"/>. Values
        /// are only touched when their transaction succeeded, so a skipped or failed read leaves the
        /// previous readings in place rather than blanking the UI.
        /// </summary>
        public bool RefreshSensors()
        {
            lock (sync)
            {
                if (stream == null)
                {
                    return false;
                }

                // Reflects the most recent refresh only.
                lastReadWrongMode = false;

                if (dutiesDirty && ownsSoftwareControl)
                {
                    // Annex §9 polling order: a pending duty write goes out ahead of the sensor
                    // reads. Exactly one attempt per refresh -- WriteAllDuties clears the flag when
                    // it lands, so this keeps retrying on later ticks until it does.
                    Log("Debug", "Corsair plug-in: re-sending the duty set to iCUE LINK hub " + serial
                        + " after an earlier write failed to reach it.");
                    WriteAllDuties();
                }

                var reasserted = false;
                var ok = true;

                byte[] speeds;
                byte[] unused;
                if (ReadEndpointBracket(LinkHubData.EndpointSpeeds, LinkHubData.DataTypeSpeeds, false, out speeds, out unused))
                {
                    ApplySpeeds(LinkHubData.ParseSensorRecords(speeds));
                }
                else
                {
                    ok = false;
                    HandleSensorReadFailure("speed", ref reasserted);
                }

                byte[] temperatures;
                if (ReadEndpointBracket(LinkHubData.EndpointTemperatures, LinkHubData.DataTypeTemperatures, false, out temperatures, out unused))
                {
                    ApplyTemperatures(LinkHubData.ParseSensorRecords(temperatures));
                }
                else
                {
                    ok = false;
                    HandleSensorReadFailure("temperature", ref reasserted);
                }

                return ok;
            }
        }

        private void ApplySpeeds(List<LinkSensorRecord> records)
        {
            var byChannel = IndexByChannel(records);
            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                LinkSensorRecord record;
                var usable = byChannel.TryGetValue(state.Channel, out record)
                    && record.Available
                    && state.Device != null
                    && state.Device.HasRpm;
                state.Rpm = usable ? (int?)record.RawValue : null;
            }
        }

        private void ApplyTemperatures(List<LinkSensorRecord> records)
        {
            var byChannel = IndexByChannel(records);
            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                LinkSensorRecord record;
                var usable = byChannel.TryGetValue(state.Channel, out record)
                    && record.Available
                    && state.Device != null
                    && state.Device.HasTemp;

                // Annex §7: temperature records are tenths of a degree Celsius.
                state.TemperatureC = usable ? (float?)(record.RawValue / 10f) : null;
            }
        }

        private static Dictionary<int, LinkSensorRecord> IndexByChannel(List<LinkSensorRecord> records)
        {
            var map = new Dictionary<int, LinkSensorRecord>();
            if (records == null)
            {
                return map;
            }

            for (var i = 0; i < records.Count; i++)
            {
                map[records[i].Channel] = records[i];
            }

            return map;
        }

        private void HandleSensorReadFailure(string what, ref bool reasserted)
        {
            if (!lastStatusWrongMode)
            {
                Log("Debug", "Corsair plug-in: the " + what + " read on iCUE LINK hub " + serial + " did not complete.");
                return;
            }

            if (!ownsSoftwareControl)
            {
                // Somebody else owns the hub, or it is running its own profile. Record it and leave
                // the mode alone -- taking the hub here would silently steal control.
                lastReadWrongMode = true;
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " answered the " + what
                    + " read with hardware mode; leaving its mode untouched.");
                return;
            }

            if (reasserted)
            {
                // Once per refresh, never recursively.
                return;
            }

            reasserted = true;
            Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " dropped out of software mode; re-asserting control once.");
            ReassertControl();
        }

        // ---- Control path --------------------------------------------------------------------

        /// <summary>
        /// Sets one channel's duty. Percent is clamped to 0-100, and pump channels never go below
        /// <see cref="MinimumPumpPercent"/> (annex §8: a stalled AIO pump reads as a pump failure).
        /// Takes software control of the hub if this plug-in does not already have it.
        /// </summary>
        public bool SetChannelPercent(int channel, int percent)
        {
            lock (sync)
            {
                var state = FindChannel(channel);
                if (state == null)
                {
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " has no channel "
                        + channel.ToString(CultureInfo.InvariantCulture) + " to set.");
                    return false;
                }

                if (state.Device == null || !state.Device.HasControl)
                {
                    Log("Debug", "Corsair plug-in: channel " + channel.ToString(CultureInfo.InvariantCulture)
                        + " on iCUE LINK hub " + serial + " does not accept a duty setting.");
                    return false;
                }

                var target = ClampPercent(state.Device, percent);
                if (!EnsureSoftwareControl())
                {
                    return false;
                }

                // Captured after EnsureSoftwareControl, which baselines every channel on the first
                // take of control.
                var previousPercent = state.RequestedPercent;
                var previousWasDefault = state.PercentIsDefault;

                state.RequestedPercent = target;
                state.PercentIsDefault = false;
                if (WriteAllDuties())
                {
                    return true;
                }

                // The write never reached the hub, so this channel is not running at `target`.
                // Reverting keeps the in-memory state honest about what the hardware is doing;
                // WriteAllDuties has flagged the set as dirty, so the next refresh re-sends it.
                state.RequestedPercent = previousPercent;
                state.PercentIsDefault = previousWasDefault;
                return false;
            }
        }

        /// <summary>
        /// Returns a channel to its default duty (fans 50 %, pumps 100 %) and marks it as no longer
        /// manually set. Stays in software mode when this plug-in already owns the hub; when it does
        /// not, the reset is purely bookkeeping and nothing is sent -- there is no control to hand
        /// back, and entering software mode just to "reset" would take the hub from its real owner.
        /// </summary>
        public bool ResetChannel(int channel)
        {
            lock (sync)
            {
                var state = FindChannel(channel);
                if (state == null)
                {
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " has no channel "
                        + channel.ToString(CultureInfo.InvariantCulture) + " to reset.");
                    return false;
                }

                var previousPercent = state.RequestedPercent;
                var previousWasDefault = state.PercentIsDefault;

                state.RequestedPercent = DefaultPercentFor(state.Device);
                state.PercentIsDefault = true;

                if (!ownsSoftwareControl)
                {
                    Log("Debug", "Corsair plug-in: channel " + channel.ToString(CultureInfo.InvariantCulture)
                        + " on iCUE LINK hub " + serial + " was reset locally; the hub is not under this plug-in's control, so nothing was sent.");
                    return true;
                }

                if (state.Device == null || !state.Device.HasControl)
                {
                    return true;
                }

                if (WriteAllDuties())
                {
                    return true;
                }

                // Same rollback as SetChannelPercent: the default never reached the hub, so claiming
                // the channel is back at its default would be a lie.
                state.RequestedPercent = previousPercent;
                state.PercentIsDefault = previousWasDefault;
                return false;
            }
        }

        /// <summary>
        /// Re-enters software mode and re-sends every requested duty. Used to resume after the hub
        /// silently fell back to its hardware profile (sleep/wake, hub reset, another program taking
        /// over) -- annex §2 and §10.
        ///
        /// Takes control unconditionally. If this plug-in was not already the owner, every channel is
        /// baselined to its default (fans 50 %, pumps 100 %) and that set is written. Call it only
        /// from a caller that has itself established that it previously held control; it is not a
        /// safe "check and resume" probe.
        /// </summary>
        public bool ReassertControl()
        {
            lock (sync)
            {
                if (stream == null)
                {
                    return false;
                }

                if (reassertInFlight)
                {
                    // Same-thread recursion tripwire (annex §10: mode changes are refused rather than
                    // nested). The monitor is reentrant, so this is the guard against re-entering via
                    // RefreshSensors' wrong-mode branch, not against another thread.
                    Log("Debug", "Corsair plug-in: a control re-assert is already running on iCUE LINK hub " + serial + "; ignoring the nested request.");
                    return false;
                }

                reassertInFlight = true;
                try
                {
                    if (!EnterSoftwareMode())
                    {
                        // Latch here too: a hub that refuses the mode change fails this way on every
                        // tick, and without the latch it would never reach the duty write that
                        // normally sets it.
                        NoteControlFailure();
                        return false;
                    }

                    if (!ownsSoftwareControl)
                    {
                        ApplyDefaultPercents();
                        ownsSoftwareControl = true;
                    }

                    lastReadWrongMode = false;
                    return WriteAllDuties();
                }
                finally
                {
                    reassertInFlight = false;
                }
            }
        }

        private bool EnsureSoftwareControl()
        {
            if (ownsSoftwareControl)
            {
                return true;
            }

            if (!EnterSoftwareMode())
            {
                NoteControlFailure();
                return false;
            }

            // The hub obeys the whole duty set at once, so before the first manual change every
            // channel needs a defined starting value (annex §2 initialization order).
            ApplyDefaultPercents();
            ownsSoftwareControl = true;
            lastReadWrongMode = false;
            return true;
        }

        private bool EnterSoftwareMode()
        {
            Log("Debug", "Corsair plug-in: taking software control of iCUE LINK hub " + serial + ".");
            return RunDirectCommand(LinkHubData.EnterSoftwareMode, null, ControlFailureLevel(), GuardTimeoutMs) != null;
        }

        private void ApplyDefaultPercents()
        {
            for (var i = 0; i < channels.Count; i++)
            {
                channels[i].RequestedPercent = DefaultPercentFor(channels[i].Device);
                channels[i].PercentIsDefault = true;
            }
        }

        // Annex §8/§11.6: duty writes are full-set -- every controllable channel goes out in one
        // packet, in ascending channel order, on every write.
        private bool WriteAllDuties()
        {
            var entries = new List<KeyValuePair<int, int>>();
            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                if (state.Device == null || !state.Device.HasControl)
                {
                    continue;
                }

                entries.Add(new KeyValuePair<int, int>(state.Channel, ClampPercent(state.Device, state.RequestedPercent)));
            }

            if (entries.Count == 0)
            {
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " has no controllable channels; skipping the duty write.");
                dutiesDirty = false;
                NoteControlSuccess(false);
                return true;
            }

            var block = LinkHubData.BuildWriteBlock(LinkHubData.DataTypeDuty, LinkHubData.BuildDutyInnerData(entries));
            if (WriteEndpointBracket(LinkHubData.EndpointDutyWrite, block))
            {
                dutiesDirty = false;
                NoteControlSuccess(true);
                return true;
            }

            if (lastStatusWrongMode)
            {
                // The hub slipped back to hardware mode between the mode change and the write. Take
                // it once more and retry the write exactly once (annex §10) -- no loop, no recursion.
                Log(ControlFailureLevel(), "Corsair plug-in: the duty write hit hardware mode on iCUE LINK hub " + serial
                    + "; retrying once after re-entering software mode.");
                if (EnterSoftwareMode() && WriteEndpointBracket(LinkHubData.EndpointDutyWrite, block))
                {
                    dutiesDirty = false;
                    NoteControlSuccess(true);
                    return true;
                }
            }

            // Nothing reached the hardware, so the requested duties are now out of sync with what the
            // fans are actually doing. Flag it: the next refresh re-sends the whole set, and keeps
            // re-sending until one write lands.
            dutiesDirty = true;
            Log(ControlFailureLevel(), "Corsair plug-in: the duty write to iCUE LINK hub " + serial
                + " did not reach the hardware; the requested duties will be re-sent on the next refresh."
                + (dutyFailureReported ? "" : " Further failures are logged at Debug until one succeeds."));
            NoteControlFailure();
            return false;
        }

        // ---- Control-failure log latch --------------------------------------------------------
        //
        // A hub that is stuck -- left in hardware mode while this plug-in still believes it owns it,
        // say -- fails the same way on every refresh tick, and one tick walks the whole chain twice
        // (the dutiesDirty re-send plus the wrong-mode ReassertControl). At Error level for every
        // step that is roughly ten lines a second, and the host's Error log rotates at 256 KB, so a
        // few minutes of it would erase the very history someone would need to diagnose the problem.
        // So the failing *state* is reported once, in full detail, at Error; while it persists every
        // step of the chain drops to Debug; and the next control command that succeeds clears the
        // latch and says so.

        private string ControlFailureLevel()
        {
            return dutyFailureReported ? "Debug" : "Error";
        }

        private void NoteControlFailure()
        {
            dutyFailureReported = true;
        }

        private void NoteControlSuccess(bool announceRecovery)
        {
            if (!dutyFailureReported)
            {
                return;
            }

            dutyFailureReported = false;
            if (announceRecovery)
            {
                // At Error so that a reader who only has the Error log sees the failure resolved. An
                // Error entry with no recorded clearance reads as a problem that is still happening.
                Log("Error", "Corsair plug-in: control commands to iCUE LINK hub " + serial + " are reaching the hardware again.");
            }
        }

        // ---- Enumeration ---------------------------------------------------------------------

        private string ReadFirmwareVersion()
        {
            var response = RunDirectCommand(LinkHubData.ReadFirmwareVersion, null, "Debug", GuardTimeoutMs);
            return response == null ? string.Empty : LinkHubData.ParseFirmwareVersion(response);
        }

        // Annex §5: the two-read (24-device) enumeration needs firmware 2.5 or newer.
        private static bool SupportsTwoReadEnumeration(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            var parts = version.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            int major;
            int minor;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            {
                return false;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
            {
                return false;
            }

            return (major == 2 && minor >= 5) || major >= 3;
        }

        private bool EnumerateChannels()
        {
            byte[] first;
            byte[] continuation;
            if (!ReadEndpointBracket(LinkHubData.EndpointSubDevices, LinkHubData.DataTypeSubDevices, twoReadEnumeration, out first, out continuation))
            {
                return false;
            }

            var devices = LinkHubData.ParseSubDevices(first, continuation);
            channels.Clear();

            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                var known = LinkKnownDevices.Find(device.Model, device.Variant);
                if (known == null)
                {
                    // Keep the channel: the sensor arrays still say whether it reports RPM and
                    // temperature, and showing an unnamed reading beats hiding a device the user can
                    // plainly see. Duty control is withheld because an unknown model's safe duty
                    // range is exactly what is unknown.
                    known = new LinkKnownDevice
                    {
                        Model = device.Model,
                        Variant = device.Variant,
                        Name = "Corsair device (model 0x" + device.Model.ToString("x2", CultureInfo.InvariantCulture) + ")",
                        IsPump = false,
                        HasTemp = true,
                        HasRpm = true,
                        HasControl = false
                    };

                    Log("Debug", "Corsair plug-in: channel " + device.Channel.ToString(CultureInfo.InvariantCulture)
                        + " on iCUE LINK hub " + serial + " reports unknown model 0x"
                        + device.Model.ToString("x2", CultureInfo.InvariantCulture) + " variant 0x"
                        + device.Variant.ToString("x2", CultureInfo.InvariantCulture)
                        + "; reporting its sensors but not offering duty control.");
                }

                channels.Add(new LinkChannelState
                {
                    Channel = device.Channel,
                    Device = known,
                    DeviceId = device.DeviceId,
                    RequestedPercent = DefaultPercentFor(known),
                    PercentIsDefault = true
                });
            }

            if (channels.Count == 0)
            {
                // Annex §11.2 flags a firmware quirk where the continuation report can arrive a byte
                // short. Dump the raw stream so an implausible parse can be diagnosed from a log.
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " enumerated no sub-devices. First report: "
                    + ToHex(first, 48) + " / continuation: " + ToHex(continuation, 48));
            }

            return true;
        }

        // ---- Transaction brackets --------------------------------------------------------------

        // Annex §4.6 read bracket: close (defensive), open, read, optional continuation read, close.
        // The guard is held for the whole bracket and released in a finally; the endpoint is closed
        // in a finally so no failure path can leave it open.
        private bool ReadEndpointBracket(byte endpoint, byte[] dataType, bool twoRead, out byte[] first, out byte[] continuation)
        {
            first = null;
            continuation = null;

            if (stream == null)
            {
                return false;
            }

            // Cleared before the guard wait, not after it: a bracket that never runs because the
            // guard was busy must not leave the previous bracket's wrong-mode flag standing, or the
            // caller would react to a mode problem that this transaction never observed.
            lastStatus = LinkHubData.StatusOk;
            lastStatusWrongMode = false;

            if (!guard.TryEnter(GuardTimeoutMs))
            {
                Log("Debug", "Corsair plug-in: another Corsair program held the device guard for "
                    + GuardTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms; skipping the endpoint 0x"
                    + endpoint.ToString("x2", CultureInfo.InvariantCulture) + " read.");
                return false;
            }

            try
            {
                var endpointData = new byte[] { endpoint };
                try
                {
                    // Annex §11.4: the hub tolerates closing an endpoint that is not open, and a
                    // crashed predecessor may have left this one open. A failure here is not fatal --
                    // the open below is the real gate.
                    SendCommand(LinkHubData.CloseEndpoint, endpointData, null, false, "Debug");

                    if (SendCommand(LinkHubData.OpenEndpoint, endpointData, null, false, "Debug") == null)
                    {
                        return false;
                    }

                    first = SendCommand(LinkHubData.ReadEndpoint, null, dataType, false, "Debug");
                    if (first == null)
                    {
                        return false;
                    }

                    if (twoRead)
                    {
                        // Annex §4.3/§6: the continuation report carries no data type, so it is taken
                        // as-is. A missing continuation is not fatal; the parser tolerates it.
                        continuation = SendCommand(LinkHubData.ReadEndpoint, null, null, true, "Debug");
                    }

                    return true;
                }
                finally
                {
                    SendCommand(LinkHubData.CloseEndpoint, endpointData, null, false, "Debug");
                }
            }
            finally
            {
                guard.Exit();
            }
        }

        // Annex §4.6 write bracket: close (defensive), open, write, close.
        private bool WriteEndpointBracket(byte endpoint, byte[] writeBlock)
        {
            if (stream == null)
            {
                return false;
            }

            lastStatus = LinkHubData.StatusOk;
            lastStatusWrongMode = false;

            // One level for the whole bracket: Error while this is the first failure, Debug while a
            // known-bad state persists (see the control-failure log latch).
            var level = ControlFailureLevel();

            if (!guard.TryEnter(GuardTimeoutMs))
            {
                Log(level, "Corsair plug-in: another Corsair program held the device guard for "
                    + GuardTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms; the endpoint 0x"
                    + endpoint.ToString("x2", CultureInfo.InvariantCulture) + " write was not sent.");
                return false;
            }

            try
            {
                var endpointData = new byte[] { endpoint };
                try
                {
                    SendCommand(LinkHubData.CloseEndpoint, endpointData, null, false, level);

                    if (SendCommand(LinkHubData.OpenEndpoint, endpointData, null, false, level) == null)
                    {
                        return false;
                    }

                    return SendCommand(LinkHubData.WriteEndpoint, writeBlock, null, false, level) != null;
                }
                finally
                {
                    SendCommand(LinkHubData.CloseEndpoint, endpointData, null, false, level);
                }
            }
            finally
            {
                guard.Exit();
            }
        }

        // Direct (non-endpoint) commands: firmware version and the two mode changes. One command,
        // one response, guard held for the pair. The guard budget is a parameter because the
        // shutdown path cannot afford the full 2000 ms wait.
        private byte[] RunDirectCommand(byte[] command, byte[] data, string failureLevel, int guardTimeoutMs)
        {
            if (stream == null)
            {
                return null;
            }

            lastStatus = LinkHubData.StatusOk;
            lastStatusWrongMode = false;

            if (!guard.TryEnter(guardTimeoutMs))
            {
                Log(failureLevel, "Corsair plug-in: another Corsair program held the device guard for "
                    + guardTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms; the hub command was not sent.");
                return null;
            }

            try
            {
                return SendCommand(command, data, null, false, failureLevel);
            }
            finally
            {
                guard.Exit();
            }
        }

        // ---- Transaction core ------------------------------------------------------------------

        /// <summary>
        /// Writes one command and returns its response report, or null when the command failed.
        /// MUST be called with the device guard already held -- callers are the bracket helpers
        /// above, which own the guard for the whole bracket.
        ///
        /// <paramref name="isContinuation"/> marks the second read of a two-report enumeration. That
        /// read does not start a new exchange: its report may already be queued from the first read
        /// command, so the input queue must NOT be drained beforehand (draining would swallow it),
        /// and the report is taken as-is without filtering (annex §4.3 -- it carries no data type).
        ///
        /// <paramref name="failureLevel"/> is the log level used for a non-zero response status:
        /// "Error" on the control path, where a failure means a fan command did not land, and
        /// "Debug" for sensor reads, where wrong-mode answers are ordinary chatter.
        /// </summary>
        private byte[] SendCommand(byte[] command, byte[] data, byte[] waitForType, bool isContinuation, string failureLevel)
        {
            var localStream = stream;
            if (localStream == null)
            {
                return null;
            }

            var echoByte = isContinuation ? -1 : CommandEcho(command);
            var packet = LinkHubData.BuildCommandPacket(info.OutputReportLength, command, data);

            if (!isContinuation)
            {
                // Annex §4.5: the HID input queue also receives the responses other Corsair programs
                // provoke, so everything queued before a new exchange is stale by definition.
                localStream.DrainInput();
            }

            if (!localStream.Write(packet, WriteTimeoutMs))
            {
                NoteTransportFailure(localStream, "write", WriteTimeoutMs);
                return null;
            }

            var startTicks = Environment.TickCount;
            var reads = 0;

            while (reads < MaxResponseReads)
            {
                var buffer = new byte[info.InputReportLength];
                if (!localStream.Read(buffer, ReadTimeoutMs))
                {
                    NoteTransportFailure(localStream, "read", ReadTimeoutMs);
                    break;
                }

                reads++;
                if (ReportMatches(buffer, waitForType, echoByte))
                {
                    return CheckStatus(buffer, failureLevel);
                }

                // unchecked: Environment.TickCount wraps roughly every 24.9 days, and unchecked
                // subtraction still yields the correct elapsed duration across that wraparound. The
                // read above blocks for up to ReadTimeoutMs, so this loop never busy-waits.
                if (unchecked(Environment.TickCount - startTicks) >= ResponseBudgetMs)
                {
                    break;
                }
            }

            return null;
        }

        internal static bool ReportMatches(byte[] report, byte[] waitForType, int echoByte)
        {
            if (waitForType != null)
            {
                if (LinkHubData.ResponseTypeMatches(report, waitForType))
                {
                    return true;
                }

                // An error response to an endpoint read need not carry a data type at all -- the
                // payload the type would label is exactly what the hub failed to produce. Without
                // this clause a status-3 answer would simply never match, the poll would time out,
                // and the wrong-mode recovery protocol would never fire. Such a report is still
                // routed through CheckStatus, which returns null, so it can never be parsed as data.
                return IsEchoedError(report, echoByte);
            }

            if (echoByte < 0)
            {
                return true;
            }

            return EchoMatches(report, echoByte);
        }

        private static bool EchoMatches(byte[] report, int echoByte)
        {
            return report != null && report.Length > 3 && report[3] == (byte)echoByte;
        }

        private static bool IsEchoedError(byte[] report, int echoByte)
        {
            return echoByte >= 0
                && EchoMatches(report, echoByte)
                && report.Length > 4
                && report[4] != LinkHubData.StatusOk;
        }

        private byte[] CheckStatus(byte[] response, string failureLevel)
        {
            var status = LinkHubData.ResponseStatus(response);
            if (status == LinkHubData.StatusOk)
            {
                return response;
            }

            lastStatus = status;
            var level = string.IsNullOrEmpty(failureLevel) ? "Debug" : failureLevel;
            if (status == LinkHubData.StatusWrongMode)
            {
                lastStatusWrongMode = true;
                Log(level, "Corsair plug-in: iCUE LINK hub " + serial + " answered with status 0x03 (the hub is in hardware mode).");
            }
            else
            {
                Log(level, "Corsair plug-in: iCUE LINK hub " + serial + " answered with error status 0x"
                    + status.ToString("x2", CultureInfo.InvariantCulture) + ".");
            }

            return null;
        }

        private void NoteTransportFailure(CorsairHidStream localStream, string operation, int timeoutMs)
        {
            if (localStream.IsDeviceGone)
            {
                isGone = true;
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " disappeared during a HID " + operation + ".");
                return;
            }

            Log("Debug", "Corsair plug-in: a HID " + operation + " to iCUE LINK hub " + serial + " did not complete within its "
                + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms timeout.");
        }

        private static int CommandEcho(byte[] command)
        {
            return (command != null && command.Length > 0) ? command[0] : -1;
        }

        // ---- Small helpers ---------------------------------------------------------------------

        private LinkChannelState FindChannel(int channel)
        {
            for (var i = 0; i < channels.Count; i++)
            {
                if (channels[i].Channel == channel)
                {
                    return channels[i];
                }
            }

            return null;
        }

        private static int DefaultPercentFor(LinkKnownDevice device)
        {
            return (device != null && device.IsPump) ? DefaultPumpPercent : DefaultFanPercent;
        }

        private static int ClampPercent(LinkKnownDevice device, int percent)
        {
            var value = percent;
            if (value < 0)
            {
                value = 0;
            }

            if (value > 100)
            {
                value = 100;
            }

            if (device != null && device.IsPump && value < MinimumPumpPercent)
            {
                value = MinimumPumpPercent;
            }

            return value;
        }

        private static string ToHex(byte[] buffer, int maxBytes)
        {
            if (buffer == null)
            {
                return "(none)";
            }

            var count = Math.Min(buffer.Length, maxBytes);
            var builder = new StringBuilder((count * 3) + 4);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(buffer[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            if (count < buffer.Length)
            {
                builder.Append(" ...");
            }

            return builder.ToString();
        }

        private void Log(string level, string message)
        {
            if (log != null)
            {
                log(level, message);
            }
        }
    }
}
