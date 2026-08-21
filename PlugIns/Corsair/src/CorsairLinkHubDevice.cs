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
    /// Exactly three entry points can put the hub into software mode:
    /// <list type="bullet">
    /// <item><see cref="SetChannelPercent"/> -- takes control if this plug-in does not already have
    /// it, because the user has explicitly asked for a duty.</item>
    /// <item><see cref="ReassertControl"/> -- takes control *unconditionally*, baselining every
    /// channel to its default and writing the whole set. It is a resume path, not a query: call it
    /// only from a caller that has itself recorded prior ownership.</item>
    /// <item><see cref="TakeControlIfBlocked"/> -- the same unconditional take, for a hub that
    /// connected in the <see cref="HardwareModeBlocked"/> state. Same rule: only from a caller that
    /// has established this machine already uses Sensor Readout for fan control.</item>
    /// </list>
    /// <see cref="ResetChannel"/> is not one of them. When this plug-in does not own the hub, a
    /// reset is bookkeeping only and sends nothing -- there is no control to hand back, and entering
    /// software mode to "reset" would take the hub from its real owner.
    ///
    /// Hardware-mode blocking (measured 2026-08-07 on firmware 3.12.650, annex §2/§7 editor's note):
    /// a hub running its own profile refuses *every* endpoint read with status 0x03, sub-device
    /// enumeration included. So there is a legitimate connected state with no channel map at all:
    /// <see cref="Connect"/> keeps the session open, sets <see cref="HardwareModeBlocked"/>, and
    /// returns true. <see cref="RefreshSensors"/> then re-tries enumeration at a slow cadence, in
    /// case another program puts the hub into software mode, and reports success rather than a
    /// failure so the caller's failure backoff does not fire on a perfectly healthy hub.
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

        /// <summary>
        /// The channel number of the hub-wide "take fan control" entry the row builder exposes for
        /// a hub that connected hardware-mode-blocked. Such a hub has no channel map, and every host
        /// path that can ask this plug-in to take a hub starts from a Fan Control row, so without
        /// this entry a machine with no marker file yet (a first install, or right after an app
        /// update replaced the plug-in folder) could never get the hub out of hardware mode. Ports
        /// are numbered from 1, so 0 can never collide with a real channel. Setting it takes software
        /// control of the whole hub; resetting it sends nothing.
        /// </summary>
        public const int HubWideControlChannel = 0;

        // A hub that floods input reports must not be able to hold a response poll open for the
        // whole budget one 0-ms read at a time.
        private const int MaxResponseReads = 64;

        // How often a hardware-mode-blocked hub re-tries sub-device enumeration. Slow on purpose:
        // the only thing that can change the answer is another program taking software control, and
        // until that happens each attempt is four wasted transactions holding the shared guard.
        private const int BlockedRetryIntervalMs = 30000;

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

        // Set when the hub refused sub-device enumeration because it is running its own hardware
        // profile. The session stays open and usable -- there is simply nothing to read yet -- so
        // this is a waiting state, not a failure.
        private bool hardwareModeBlocked;

        // Environment.TickCount of the next allowed enumeration retry while blocked; only
        // meaningful while hardwareModeBlocked is true.
        private int blockedRetryTicks;

        // Why the most recent EnumerateChannels failed: true when it was status 0x03 (wrong mode)
        // rather than a transport failure or another error status. Recorded by EnumerateChannels
        // itself, because Connect has to tell the two apart long after the bracket has finished.
        private bool lastEnumerationWrongMode;

        // One Error line per session for SendCommand's echo-mismatch fallback (see there), then
        // Debug: a hub whose acknowledgements never echo-match would otherwise log at Error on
        // every duty write for the life of the process.
        private bool unverifiedAcknowledgementReported;

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

        /// <summary>
        /// True while the hub is connected but running its own hardware profile, which makes it
        /// refuse sub-device enumeration outright (status 0x03) -- so there is no channel map, no
        /// reading, and no control row until something puts it into software mode. Not an error
        /// state: the session is open and healthy, and <see cref="RefreshSensors"/> keeps checking.
        /// </summary>
        public bool HardwareModeBlocked
        {
            get
            {
                lock (sync)
                {
                    return hardwareModeBlocked;
                }
            }
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
        /// write. Returns false only when the device cannot be opened or enumeration fails for a
        /// reason other than the hub's mode; a hub that answers "hardware mode" to the sensor reads
        /// still connects successfully with its channels enumerated (see
        /// <see cref="LastReadWrongMode"/>), and a hub that answers "hardware mode" to *enumeration
        /// itself* connects with no channels and <see cref="HardwareModeBlocked"/> set.
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
                hardwareModeBlocked = false;
                lastEnumerationWrongMode = false;
                unverifiedAcknowledgementReported = false;
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
                    if (lastEnumerationWrongMode)
                    {
                        // Measured 2026-08-07 on firmware 3.12.650: a hub running its own profile
                        // refuses endpoint reads outright, enumeration included. That is a normal
                        // state of perfectly working hardware, not a failed connection -- dropping
                        // the session here is what used to leave the plug-in permanently silent
                        // after every clean restart, because the hub only ever leaves hardware mode
                        // when some program asks it to, and no rows meant nothing ever asked.
                        hardwareModeBlocked = true;
                        blockedRetryTicks = unchecked(Environment.TickCount + BlockedRetryIntervalMs);
                        Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial
                            + " is in hardware mode; readings are unavailable until a program takes software control.");
                        return true;
                    }

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
            hardwareModeBlocked = false;
            lastEnumerationWrongMode = false;
            unverifiedAcknowledgementReported = false;
            dutiesDirty = false;
            dutyFailureReported = false;
            isGone = false;
        }

        // ---- Sensor reads --------------------------------------------------------------------

        /// <summary>
        /// Reads the speed and temperature arrays and folds them into <see cref="Channels"/>. Values
        /// are only touched when their transaction succeeded, so a skipped or failed read leaves the
        /// previous readings in place rather than blanking the UI.
        ///
        /// While <see cref="HardwareModeBlocked"/> is set there is nothing to read at all, so this
        /// re-tries enumeration at most once every <see cref="BlockedRetryIntervalMs"/> ms and
        /// otherwise reports success: the hub is healthy and simply not ours to read, and reporting
        /// failure would push the caller's consecutive-failure backoff for a state that is expected
        /// to last hours.
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

                if (hardwareModeBlocked && !RetryEnumerationWhileBlocked())
                {
                    return true;
                }

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

        /// <summary>
        /// One throttled attempt to leave the blocked state, called from <see cref="RefreshSensors"/>
        /// with the monitor held. Returns true when the hub has since been put into software mode by
        /// something (another program, or this plug-in's own control path) and its channels are now
        /// enumerated, so the caller can carry on with an ordinary refresh.
        ///
        /// Sends nothing but the read bracket: recovering here must never be a way for a read to
        /// take the hub. unchecked because Environment.TickCount wraps roughly every 24.9 days and
        /// the subtraction is still correct across the wraparound.
        /// </summary>
        private bool RetryEnumerationWhileBlocked()
        {
            if (unchecked(Environment.TickCount - blockedRetryTicks) < 0)
            {
                return false;
            }

            blockedRetryTicks = unchecked(Environment.TickCount + BlockedRetryIntervalMs);
            if (!EnumerateChannels())
            {
                return false;
            }

            hardwareModeBlocked = false;
            Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " is no longer in hardware mode; it enumerated "
                + channels.Count.ToString(CultureInfo.InvariantCulture) + " channel(s) and readings resume now.");
            return true;
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
                if (channel == HubWideControlChannel)
                {
                    // The hub-wide take-control entry: the percent is irrelevant, the request is "put
                    // this hub under Sensor Readout's control". A blocked hub is taken through the
                    // blocked path (mode change, enumeration, baseline); a readable one simply
                    // through the ordinary first take, and a hub already owned has nothing to do.
                    return hardwareModeBlocked ? TakeControlWhileBlocked() : EnsureSoftwareControl();
                }

                if (hardwareModeBlocked && !TakeControlWhileBlocked())
                {
                    // Before FindChannel on purpose: a blocked hub has no channel map at all, so
                    // looking the channel up first would reject every duty the user asks for and the
                    // hub could never be taken.
                    return false;
                }

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
                if (channel == HubWideControlChannel)
                {
                    // Resetting the hub-wide take-control entry asks for nothing: there is no channel
                    // behind it, and giving a hub back mid-session is what exit and disable do. Its
                    // only effect is host-side -- a saved "automatic" for it does not re-take the hub
                    // at the next start, where a saved manual setting would.
                    return true;
                }

                if (hardwareModeBlocked)
                {
                    // "Reset" means "let the hub manage this channel itself", and a blocked hub is
                    // already doing exactly that -- so this succeeds by doing nothing. Taking
                    // control here would be perverse, and it would also make the host's start-up
                    // re-apply of saved automatic states seize a hub nobody asked it to touch.
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial
                        + " is running its own hardware profile, which is what a reset asks for; channel "
                        + channel.ToString(CultureInfo.InvariantCulture) + " was left to it and nothing was sent.");
                    return true;
                }

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
                    if (hardwareModeBlocked)
                    {
                        // No channel map to re-assert over yet: the blocked take-control path enters
                        // software mode, enumerates, and then runs this very same first-take
                        // sequence against the channels that appear.
                        return TakeControlWhileBlocked();
                    }

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

        /// <summary>
        /// Takes software control of a hub that connected in the <see cref="HardwareModeBlocked"/>
        /// state, so that its channels can be enumerated at all. Returns true when the hub is now
        /// under this plug-in's control; false when it was not blocked (nothing to do) or the take
        /// did not succeed.
        ///
        /// Unconditional, exactly like <see cref="ReassertControl"/>: call it only from a caller
        /// that has itself established that this machine already uses Sensor Readout for fan
        /// control. A blocked hub is a hub happily running its own profile, and taking it because it
        /// happens to be blocked would be the control-stealing this plug-in exists not to do.
        /// </summary>
        public bool TakeControlIfBlocked()
        {
            lock (sync)
            {
                if (!hardwareModeBlocked)
                {
                    return false;
                }

                return TakeControlWhileBlocked();
            }
        }

        // Called with the monitor held, from SetChannelPercent, TakeControlIfBlocked and
        // ReassertControl. Order matters: the mode change has to land before enumeration is even
        // possible, and only a successful enumeration produces the channel list that the shared
        // first-take sequence (baseline every channel, claim ownership, write the whole set) needs.
        private bool TakeControlWhileBlocked()
        {
            if (stream == null)
            {
                return false;
            }

            if (!EnterSoftwareMode())
            {
                NoteControlFailure();
                return false;
            }

            if (!EnumerateChannels())
            {
                Log(ControlFailureLevel(), "Corsair plug-in: iCUE LINK hub " + serial
                    + " still refused sub-device enumeration after the software-mode command; it stays unreadable for now.");

                // The mode change landed but is useless without a channel map: a hub sitting in
                // software mode that this plug-in never wrote a duty to, and does not consider
                // itself the owner of, is a state nothing later can reason about (Disconnect would
                // not restore it, either). So give straight back exactly what was just taken.
                GiveBackHardwareMode();
                NoteControlFailure();
                return false;
            }

            hardwareModeBlocked = false;
            Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial + " is now in software mode with "
                + channels.Count.ToString(CultureInfo.InvariantCulture) + " channel(s) enumerated.");

            // modeAlreadyEntered: the enter-software-mode command above is the one this would
            // otherwise send, and enumeration only answered because it landed.
            if (!EnsureSoftwareControl(true))
            {
                return false;
            }

            return WriteAllDuties();
        }

        // Best effort, and Debug-level throughout: this only ever runs on a path that has already
        // reported its own failure at Error, and a hub that ignores this command is left exactly as
        // the annex §2 fallback describes -- it returns to its own profile once nothing drives it.
        private void GiveBackHardwareMode()
        {
            Log("Debug", "Corsair plug-in: returning iCUE LINK hub " + serial
                + " to hardware mode; the software-mode take could not be completed.");
            if (RunDirectCommand(LinkHubData.EnterHardwareMode, null, "Debug", GuardTimeoutMs) == null)
            {
                Log("Debug", "Corsair plug-in: the hardware-mode hand-back on iCUE LINK hub " + serial
                    + " did not complete; the hub returns to its own profile on its own once nothing drives it.");
            }
        }

        private bool EnsureSoftwareControl()
        {
            return EnsureSoftwareControl(false);
        }

        private bool EnsureSoftwareControl(bool modeAlreadyEntered)
        {
            if (ownsSoftwareControl)
            {
                return true;
            }

            if (!modeAlreadyEntered && !EnterSoftwareMode())
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
                // Recorded here rather than read from lastStatusWrongMode at the call site: a
                // caller that has to distinguish "the hub is in hardware mode" from "the wire
                // failed" needs the answer for *this* enumeration, and the bracket's own status
                // fields are reset by whatever transaction happens next.
                lastEnumerationWrongMode = lastStatusWrongMode;
                return false;
            }

            lastEnumerationWrongMode = false;
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

            byte[] firstReport = null;
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
                if (firstReport == null)
                {
                    firstReport = buffer;
                }

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

            if (waitForType == null && firstReport != null && !IsCloseEndpointCommand(command))
            {
                // Echo matching (annex §4.5, response offset 3) is a best-effort extra filter on
                // commands that have no data type. It may only ever pick a *better* report, never
                // turn a real response into a failure, so an unmatched poll falls back to the first
                // report that arrived. Data-type matching gets no such fallback: mis-parsing another
                // program's sensor report as ours would produce silently wrong readings.
                //
                // Not for the defensive CloseEndpoint sends, whose result nobody reads: the only thing
                // a fallback could do there is let a stray status-0x03 report set lastStatusWrongMode
                // inside a bracket that then fails for a transport reason, which would be misread as
                // "the hub is in hardware mode".
                //
                // Logged for every command that takes the fallback, not only duty writes: a mode
                // change accepted this way sets ownsSoftwareControl on an acknowledgement the hub may
                // never have sent, and a duty write clears dutiesDirty. Once at Error per session,
                // then at Debug -- a hub whose acknowledgements never echo-match would otherwise
                // write an Error line per duty write for the life of the process.
                Log(unverifiedAcknowledgementReported ? "Debug" : "Error",
                    "Corsair plug-in: could not identify the acknowledgement for a "
                    + (IsWriteCommand(command) ? "duty write" : "mode or endpoint command")
                    + " to iCUE LINK hub " + serial + "; falling back to the first report received, so its success is unverified."
                    + (unverifiedAcknowledgementReported ? "" : " Further such fallbacks on this hub are logged at Debug."));
                unverifiedAcknowledgementReported = true;

                return CheckStatus(firstReport, failureLevel);
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

        private static bool IsWriteCommand(byte[] command)
        {
            return command != null
                && command.Length > 0
                && LinkHubData.WriteEndpoint.Length > 0
                && command[0] == LinkHubData.WriteEndpoint[0];
        }

        private static bool IsCloseEndpointCommand(byte[] command)
        {
            return command != null
                && command.Length > 0
                && LinkHubData.CloseEndpoint.Length > 0
                && command[0] == LinkHubData.CloseEndpoint[0];
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
