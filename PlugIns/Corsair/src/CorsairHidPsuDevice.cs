using System;
using System.Globalization;
using System.Text;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// One session against a Corsair HXi/RMi HID power supply (PIDs 0x1C03-0x1C0D, 0x1C1E, 0x1C1F,
    /// 0x1C23, 0x1C27), implementing the PMBus-over-HID protocol in
    /// <c>PlugIns/Corsair/docs/hidpsu-protocol.md</c> on top of <see cref="CorsairHidStream"/>.
    ///
    /// Deliberate policy difference from the studied reference implementation: connecting is
    /// strictly read-only. <see cref="Connect"/> performs the handshake and one sensor sweep and
    /// nothing else -- it never writes the fan mode register and never writes a duty, so merely
    /// having this plug-in enabled cannot take the PSU fan away from whatever program (Fan Control,
    /// HWiNFO, ...) the user already has driving it, and cannot suppress the PSU's own zero-RPM
    /// behaviour.
    ///
    /// Exactly four places put a byte into the fan duty (0x3B) or fan mode (0xF0) register:
    /// <see cref="SetFanPercent"/>, <see cref="ResetFan"/>, the automatic-fallback reconcile inside
    /// <see cref="RefreshSensors"/> (which can only fire after <see cref="SetFanPercent"/> asked for
    /// a manual duty), and the restore inside <see cref="Disconnect"/> (only when this session
    /// actually took manual control). Every other code path is read-only.
    ///
    /// Every wire transaction -- the handshake preamble and the command exchange together, as annex
    /// §4.2 requires -- runs inside one hold of the shared <see cref="CorsairDeviceGuard"/> mutex.
    /// The wait is bounded at <see cref="GuardTimeoutMs"/> ms; when it expires the operation reports
    /// failure and changes nothing, so the caller keeps the data it already had.
    ///
    /// The protocol is not safe against concurrent use of one device, so a private monitor
    /// serializes the public methods.
    /// </summary>
    public sealed class CorsairHidPsuDevice
    {
        // Annex §8 timing rules: 500 ms for a HID read and for a HID write alike.
        private const int TransferTimeoutMs = 500;

        // constraints.md #8: bounded wait on the cross-process guard.
        private const int GuardTimeoutMs = 2000;

        // Shutdown is on a budget: a ProcessExit handler gets roughly two seconds for everything it
        // has to do, and the automatic-control restore is two transactions of four HID transfers
        // each. At the normal 500 ms per transfer that alone could run past five seconds, so the
        // restore shortens both halves of its bound -- the guard wait and every individual
        // transfer. Worst case: 500 + 4x150 for the first write, then a floored 100 + 4x150 for the
        // second, i.e. about 1.8 s including a fully contended guard.
        private const int ShutdownGuardTimeoutMs = 500;
        private const int ShutdownTransferTimeoutMs = 150;

        // Floor for the second half of a restore: the mode write is the safety-critical one
        // (annex §7.4) and must still get a real chance at the guard even if the duty write ate
        // the whole shutdown budget.
        private const int MinimumRestoreGuardMs = 100;

        // Annex §3 framing. These are Windows buffer offsets, i.e. the report-id byte at 0 is
        // included -- this machine's HX1200i measures in=65/out=65, so every annex payload offset k
        // is buffer offset k+1 and the tables in §3 already read that way.
        private const int ModeOffset = 1;
        private const int CommandOffset = 2;
        private const int DataOffset = 3;

        // This unit answers the handshake with its marketing string rather than the bare model
        // name the annex quotes; the suffix is dropped so the name reads as a model (see
        // NormalizeModelName).
        private const string ModelNameSuffix = " Power Supply";

        private const byte ModeRead = 0x03;
        private const byte ModeWrite = 0x02;

        // 0xFE triples as the handshake command, the device's error marker, and (in a handshake)
        // the value sitting in the mode-byte slot -- annex §3 and §10.
        private const byte HandshakeCommand = 0xFE;
        private const byte HandshakeArgument = 0x03;

        private const byte CommandInputVoltage = 0x88;      // PMBus READ_VIN
        private const byte CommandTemperature1 = 0x8D;      // PMBus READ_TEMPERATURE_1 (VRM)
        private const byte CommandTemperature2 = 0x8E;      // PMBus READ_TEMPERATURE_2 (case)
        private const byte CommandFanRpm = 0x90;            // PMBus READ_FAN_SPEED_1
        private const byte CommandOutputPower = 0xEE;       // total output power
        private const byte CommandFanDuty = 0x3B;           // PMBus FAN_COMMAND_1
        private const byte CommandFanMode = 0xF0;           // vendor fan control mode

        private const byte FanModeAutomatic = 0x00;         // PSU firmware curve, including zero-RPM
        private const byte FanModeManual = 0x01;            // fan runs at the last duty written

        // Annex §7.2: the PSU's fan is never driven manually below this duty. A request under it
        // means "give the fan back to the PSU", which is also the only way to get zero-RPM back.
        private const int ManualDutyThresholdPercent = 30;

        // Plausibility windows. A value outside its window is discarded rather than surfaced: the
        // response could be another program's reply that happened to echo our bytes, and a wild
        // temperature or wattage in the UI is worse than no reading at all.
        private const float MinTemperatureC = -10f;
        private const float MaxTemperatureC = 150f;
        private const float MinFanRpm = 0f;
        private const float MaxFanRpm = 10000f;
        private const float MinInputVoltage = 80f;
        private const float MaxInputVoltage = 260f;
        private const float MinOutputPowerW = 0f;
        private const float MaxOutputPowerW = 2000f;

        private const int NoRequestedPercent = -1;

        private readonly CorsairHidDeviceInfo info;
        private readonly CorsairDeviceGuard guard;
        private readonly Action<string, string> log;
        private readonly object sync = new object();

        private CorsairHidStream stream;
        private string modelName = string.Empty;
        private bool isGone;

        private float? temperature1C;
        private float? temperature2C;
        private int? fanRpm;
        private bool fanIsManual;
        private float? inputVoltage;
        private float? outputPowerW;

        private int requestedPercent = NoRequestedPercent;

        // True once this session has asked the PSU for manual fan control. Set before the duty
        // write goes out, not after it succeeds, so a half-applied take-over still arms the
        // restore on the way down (annex §7.4).
        private bool everSetManual;

        // Log-volume latch for the control path: true once a control failure has been reported at
        // Error, so the identical failure on every subsequent tick is logged at Debug instead. See
        // the control-failure log latch section below.
        private bool controlFailureReported;

        public CorsairHidPsuDevice(CorsairHidDeviceInfo info, CorsairDeviceGuard guard, Action<string, string> log)
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

        /// <summary>
        /// Model name as the PSU itself reports it in the handshake reply ("HX1200i"), not derived
        /// from the product id (annex §1). Empty until <see cref="Connect"/> succeeds.
        /// </summary>
        public string ModelName
        {
            get { return modelName; }
        }

        public string PidHex
        {
            get { return info.ProductId.ToString("x4", CultureInfo.InvariantCulture); }
        }

        public bool IsGone
        {
            get { return isGone; }
        }

        public float? Temperature1C
        {
            get { return temperature1C; }
        }

        public float? Temperature2C
        {
            get { return temperature2C; }
        }

        public int? FanRpm
        {
            get { return fanRpm; }
        }

        /// <summary>
        /// Fan mode as of the last successful 0xF0 readback: true = manual duty, false = the PSU's
        /// own curve (which is what allows the fan to stop entirely). A failed readback leaves the
        /// previous answer in place.
        /// </summary>
        public bool FanIsManual
        {
            get { return fanIsManual; }
        }

        public float? InputVoltage
        {
            get { return inputVoltage; }
        }

        public float? OutputPowerW
        {
            get { return outputPowerW; }
        }

        /// <summary>
        /// The manual duty this session last asked for, or -1 when the fan is (or has been handed
        /// back to) the PSU's own control. Only ever holds a value at or above
        /// <see cref="ManualDutyThresholdPercent"/>.
        /// </summary>
        public int RequestedPercent
        {
            get { return requestedPercent; }
        }

        // ---- Session lifecycle ---------------------------------------------------------------

        /// <summary>
        /// Opens the PSU, reads its model name via the handshake, and takes one sensor sweep.
        /// Writes nothing to the fan registers. Returns false only when the device cannot be opened
        /// or the handshake does not produce a name; a PSU whose sensor reads fail still connects,
        /// because the next refresh may succeed.
        /// </summary>
        public bool Connect()
        {
            lock (sync)
            {
                if (stream != null)
                {
                    return true;
                }

                ClearSessionState();
                modelName = string.Empty;

                // The framing needs a mode byte, a command byte and a little-endian data word; a
                // shorter report would mean this is not the PSU's vendor interface at all.
                if (info.OutputReportLength < DataOffset + 1 || info.InputReportLength < DataOffset + 2)
                {
                    Log("Error", "Corsair plug-in: the Corsair PSU at " + info.Path + " reports HID report lengths (in="
                        + info.InputReportLength.ToString(CultureInfo.InvariantCulture) + ", out="
                        + info.OutputReportLength.ToString(CultureInfo.InvariantCulture) + ") too short for the PSU protocol.");
                    return false;
                }

                stream = CorsairHidStream.Open(info);
                if (stream == null)
                {
                    Log("Error", "Corsair plug-in: could not open the Corsair PSU at " + info.Path + ".");
                    return false;
                }

                modelName = ReadModelName();
                if (modelName.Length == 0)
                {
                    Log("Error", "Corsair plug-in: the handshake with the Corsair PSU at " + info.Path + " returned no model name.");
                    stream.Dispose();
                    stream = null;
                    return false;
                }

                Log("Debug", "Corsair plug-in: Corsair PSU " + Identity() + " answered the handshake with model name \"" + modelName + "\".");

                // First reading. A failed sensor read here is not fatal.
                RefreshSensors();
                return true;
            }
        }

        /// <summary>
        /// Closes the session. The automatic-control restore is sent only when this session
        /// actually took manual control AND the caller asks for it, so a read-only session can
        /// never push the PSU out of the mode another program put it in.
        ///
        /// Shutdown-safe: the restore is best effort and fully contained, and releasing the HID
        /// handle happens in a finally, so a throw on the way out (a disposed guard, a device that
        /// vanished) can never leak the handle. The restore shortens both of its bounds --
        /// <see cref="ShutdownGuardTimeoutMs"/> for the guard and
        /// <see cref="ShutdownTransferTimeoutMs"/> per HID transfer, rather than the usual
        /// <see cref="GuardTimeoutMs"/> and <see cref="TransferTimeoutMs"/> -- so that the whole
        /// two-write restore fits inside the roughly two seconds a ProcessExit handler gets.
        /// </summary>
        public void Disconnect(bool restoreAutomatic)
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
                    if (everSetManual && restoreAutomatic)
                    {
                        Log("Debug", "Corsair plug-in: returning the fan of Corsair PSU " + Identity() + " to automatic control.");
                        bool modeRestored;
                        if (!ResetFanCore(ShutdownGuardTimeoutMs, ShutdownTransferTimeoutMs, out modeRestored))
                        {
                            if (modeRestored)
                            {
                                // The half that matters landed: the fan is back on the PSU's own
                                // curve, and the duty byte that did not get cleared is inert there.
                                Log("Debug", "Corsair plug-in: the fan of Corsair PSU " + Identity()
                                    + " is back under PSU control, but the duty byte could not be cleared; it stays inert while the PSU drives the fan.");
                            }
                            else
                            {
                                // Annex §7.4 calls this out explicitly: a PSU left in manual mode
                                // stays there until something writes 0xF0 = 0x00 or it is
                                // power-cycled.
                                Log("Error", "Corsair plug-in: the automatic-control restore on Corsair PSU " + Identity()
                                    + " did not complete; the PSU keeps the last manual fan duty until another program resets it or it is power-cycled.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never let a shutdown-path failure prevent the handle from being released.
                    Log("Error", "Corsair plug-in: the automatic-control restore on Corsair PSU " + Identity()
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
            requestedPercent = NoRequestedPercent;
            everSetManual = false;
            fanIsManual = false;
            controlFailureReported = false;
            isGone = false;
            temperature1C = null;
            temperature2C = null;
            fanRpm = null;
            inputVoltage = null;
            outputPowerW = null;
        }

        // ---- Sensor reads --------------------------------------------------------------------

        /// <summary>
        /// Reads both temperatures, the fan speed, the fan mode, the input voltage and the output
        /// power, each as its own guarded transaction (annex §9: the guard is per-transaction, so
        /// other programs may interleave between them -- safe, because every transaction starts
        /// with its own handshake). A reading is only replaced when its transaction succeeded and
        /// the decoded value is plausible, so a failed or interfered-with read leaves the previous
        /// value in place rather than blanking the UI.
        ///
        /// Returns whether the *core* reads landed: the two temperatures, the fan speed and the fan
        /// mode. Input voltage (0x88) and output power (0xEE) are best-effort extras -- they are
        /// verified on this machine's 1c27 only and are not in the annex's command set, so a model
        /// that does not implement them must not report a permanently failing refresh; their
        /// properties simply stay null.
        /// </summary>
        public bool RefreshSensors()
        {
            lock (sync)
            {
                if (stream == null)
                {
                    return false;
                }

                // Tracks the core reads only (see the summary): the extras must never be able to
                // hold this at false forever on a model that does not implement them.
                var ok = true;

                var temp1 = ReadLinear11(CommandTemperature1, "temperature 1", MinTemperatureC, MaxTemperatureC);
                if (temp1.HasValue)
                {
                    temperature1C = temp1;
                }
                else
                {
                    ok = false;
                }

                var temp2 = ReadLinear11(CommandTemperature2, "temperature 2", MinTemperatureC, MaxTemperatureC);
                if (temp2.HasValue)
                {
                    temperature2C = temp2;
                }
                else
                {
                    ok = false;
                }

                var rpm = ReadLinear11(CommandFanRpm, "fan speed", MinFanRpm, MaxFanRpm);
                if (rpm.HasValue)
                {
                    // Annex §5.1: RPM is the same LINEAR11 encoding, truncated to a whole number.
                    fanRpm = (int)rpm.Value;
                }
                else
                {
                    ok = false;
                }

                ushort modeWord;
                var modeKnown = false;
                if (TryReadWord(CommandFanMode, "fan mode", out modeWord))
                {
                    // The mode is a single byte at buffer offset 3, i.e. the low half of the word.
                    var modeByte = (byte)(modeWord & 0xFF);
                    if (modeByte == FanModeAutomatic || modeByte == FanModeManual)
                    {
                        fanIsManual = modeByte == FanModeManual;
                        modeKnown = true;
                    }
                    else
                    {
                        // Anything else is not a mode this protocol defines, so it is unknown rather
                        // than automatic -- reading it as automatic would let one garbled reply
                        // trigger a reconcile that takes the fan over.
                        Log("Debug", "Corsair plug-in: the fan mode read on Corsair PSU " + Identity() + " answered 0x"
                            + modeByte.ToString("x2", CultureInfo.InvariantCulture)
                            + ", which is neither automatic (0x00) nor manual (0x01); the mode is treated as unknown this cycle.");
                    }
                }

                if (!modeKnown)
                {
                    ok = false;
                }

                // Best-effort extras from here on: their failure never reaches `ok`.
                var volts = ReadLinear11(CommandInputVoltage, "input voltage", MinInputVoltage, MaxInputVoltage);
                if (volts.HasValue)
                {
                    inputVoltage = volts;
                }

                var watts = ReadLinear11(CommandOutputPower, "output power", MinOutputPowerW, MaxOutputPowerW);
                if (watts.HasValue)
                {
                    outputPowerW = watts;
                }

                if (modeKnown && !fanIsManual && requestedPercent >= ManualDutyThresholdPercent)
                {
                    // The PSU is running its own curve although this plug-in asked for a manual
                    // duty. That happens after standby/wake, after a PSU reset, or when another
                    // program wrote 0xF0 = 0x00. Re-send duty + manual exactly once per refresh;
                    // if it fails the next refresh tries again. This is the only write in the
                    // sensor path and it can only fire once a manual duty has been requested.
                    //
                    // Announced at Debug, not at the control-failure level: a successful reconcile
                    // clears no latch (there is nothing failing), so at Error a program fighting us
                    // over 0xF0 would emit one Error line per second forever. The failure of the
                    // re-send below is what gets the Error treatment, latched as usual.
                    Log("Debug", "Corsair plug-in: Corsair PSU " + Identity()
                        + " fell back to automatic fan control while a manual duty of "
                        + requestedPercent.ToString(CultureInfo.InvariantCulture) + " % was requested; re-sending it once.");
                    if (!ApplyManual(requestedPercent, GuardTimeoutMs, TransferTimeoutMs))
                    {
                        ok = false;
                    }
                }

                return ok;
            }
        }

        // ---- Control path --------------------------------------------------------------------

        /// <summary>
        /// Asks for a manual fan duty. Annex §7.2: the threshold is compared against the raw
        /// request, before clamping, and anything below <see cref="ManualDutyThresholdPercent"/>
        /// hands the fan back to the PSU (mode 0x00) instead of running it manually at a low duty
        /// -- that is also the only state in which the fan may stop entirely.
        /// </summary>
        public bool SetFanPercent(int percent)
        {
            lock (sync)
            {
                if (stream == null)
                {
                    Log(ControlFailureLevel(), "Corsair plug-in: a fan duty was requested for Corsair PSU " + Identity()
                        + " while it was not connected; nothing was sent.");
                    NoteControlFailure();
                    return false;
                }

                if (percent < ManualDutyThresholdPercent)
                {
                    return HandBackToAutomatic(GuardTimeoutMs, TransferTimeoutMs);
                }

                return ApplyManual(percent, GuardTimeoutMs, TransferTimeoutMs);
            }
        }

        /// <summary>
        /// Returns the fan to the PSU's own control: duty 0 followed by mode 0x00, and forgets the
        /// requested percent. Annex §7.4 -- this is the state the PSU must be left in.
        /// </summary>
        public bool ResetFan()
        {
            lock (sync)
            {
                if (stream == null)
                {
                    Log("Debug", "Corsair plug-in: a fan reset was requested for Corsair PSU " + Identity()
                        + " while it was not connected; nothing was sent.");
                    return false;
                }

                bool modeRestored;
                return ResetFanCore(GuardTimeoutMs, TransferTimeoutMs, out modeRestored);
            }
        }

        // Duty (0x3B) then mode (0xF0) = manual, in that order (annex §7.3). Callers: the explicit
        // control entry point and the automatic-fallback reconcile. This is the single funnel to
        // the duty byte, so the 30-100 clamp lives here.
        private bool ApplyManual(int percent, int guardTimeoutMs, int transferTimeoutMs)
        {
            var level = ControlFailureLevel();
            var duty = percent;
            if (duty < ManualDutyThresholdPercent)
            {
                duty = ManualDutyThresholdPercent;
            }
            else if (duty > 100)
            {
                duty = 100;
            }

            if (duty != percent)
            {
                Log("Debug", "Corsair plug-in: a manual fan duty of " + percent.ToString(CultureInfo.InvariantCulture)
                    + " % was requested for Corsair PSU " + Identity() + "; clamping it to "
                    + duty.ToString(CultureInfo.InvariantCulture) + " % (annex §7.2 allows 30-100 under manual control).");
            }

            // Both of these record *intent*, and both are armed before the write rather than after
            // a confirmed echo. everSetManual so that a half-applied take-over still arms the
            // restore on the way down; requestedPercent so that a lost echo cannot leave a stale
            // duty behind as the reconcile's idea of what the user asked for.
            everSetManual = true;
            requestedPercent = duty;

            if (!WriteRegister(CommandFanDuty, (byte)duty, level, guardTimeoutMs, transferTimeoutMs))
            {
                Log(level, "Corsair plug-in: the fan duty write to Corsair PSU " + Identity() + " did not reach the hardware."
                    + (controlFailureReported ? "" : " Further failures are logged at Debug until one succeeds."));
                NoteControlFailure();
                return false;
            }

            if (!WriteRegister(CommandFanMode, FanModeManual, level, guardTimeoutMs, transferTimeoutMs))
            {
                Log(level, "Corsair plug-in: the fan mode switch to manual on Corsair PSU " + Identity() + " did not reach the hardware."
                    + (controlFailureReported ? "" : " Further failures are logged at Debug until one succeeds."));
                NoteControlFailure();
                return false;
            }

            fanIsManual = true;
            NoteControlSuccess();
            return true;
        }

        // Mode 0x00 only: the duty byte is inert under PSU control, so there is nothing to zero.
        private bool HandBackToAutomatic(int guardTimeoutMs, int transferTimeoutMs)
        {
            var level = ControlFailureLevel();

            // Intent first (see ApplyManual): the user has asked for the PSU's own control, so the
            // reconcile must stop wanting the old manual duty even if the write below is lost.
            requestedPercent = NoRequestedPercent;

            if (!WriteRegister(CommandFanMode, FanModeAutomatic, level, guardTimeoutMs, transferTimeoutMs))
            {
                Log(level, "Corsair plug-in: handing the fan of Corsair PSU " + Identity()
                    + " back to automatic control did not reach the hardware."
                    + (controlFailureReported ? "" : " Further failures are logged at Debug until one succeeds."));
                NoteControlFailure();
                return false;
            }

            fanIsManual = false;
            everSetManual = false;
            NoteControlSuccess();
            return true;
        }

        // Duty 0 then mode 0x00. Both bounds are parameters because the shutdown path can afford
        // neither the full guard wait nor the full per-transfer timeout. <paramref
        // name="modeRestored"/> reports the safety-critical half on its own: when it is true the fan
        // is back on the PSU's curve even if this method returned false.
        private bool ResetFanCore(int guardBudgetMs, int transferTimeoutMs, out bool modeRestored)
        {
            var level = ControlFailureLevel();
            var startTicks = Environment.TickCount;

            // Intent first (see ApplyManual).
            requestedPercent = NoRequestedPercent;

            var dutyOk = WriteRegister(CommandFanDuty, 0, level, guardBudgetMs, transferTimeoutMs);

            // The mode write is the safety-critical half (annex §7.4), so it is attempted even when
            // the duty write failed, and it always gets at least MinimumRestoreGuardMs of guard
            // wait no matter how much of the budget the duty write consumed. unchecked: TickCount
            // wraps roughly every 24.9 days and unchecked subtraction still yields the correct
            // elapsed duration across the wraparound.
            var remaining = guardBudgetMs - unchecked(Environment.TickCount - startTicks);
            if (remaining < MinimumRestoreGuardMs)
            {
                remaining = MinimumRestoreGuardMs;
            }

            var modeOk = WriteRegister(CommandFanMode, FanModeAutomatic, level, remaining, transferTimeoutMs);
            modeRestored = modeOk;
            if (modeOk)
            {
                // The fan is back under PSU control; a duty that did not land is inert in that mode.
                fanIsManual = false;
            }

            if (dutyOk && modeOk)
            {
                everSetManual = false;
                NoteControlSuccess();
                return true;
            }

            Log(level, "Corsair plug-in: returning the fan of Corsair PSU " + Identity() + " to automatic control did not fully land ("
                + (dutyOk ? "duty cleared" : "duty write failed") + ", " + (modeOk ? "mode restored" : "mode write failed") + ")."
                + (controlFailureReported ? "" : " Further failures are logged at Debug until one succeeds."));
            NoteControlFailure();
            return false;
        }

        // ---- Control-failure log latch --------------------------------------------------------
        //
        // A PSU that refuses writes -- because another program is hammering the guard, say -- fails
        // the same way on every refresh tick, and the reconcile inside RefreshSensors retries once
        // per second. At Error level that is a steady drip into a log that rotates at 256 KB, which
        // would erase the very history someone needs to diagnose the problem. So the failing state
        // is reported once, in full detail, at Error; while it persists every repeat drops to
        // Debug; and the next control command that succeeds clears the latch and says so.

        private string ControlFailureLevel()
        {
            return controlFailureReported ? "Debug" : "Error";
        }

        private void NoteControlFailure()
        {
            controlFailureReported = true;
        }

        private void NoteControlSuccess()
        {
            if (!controlFailureReported)
            {
                return;
            }

            controlFailureReported = false;

            // At Error so that a reader who only has the Error log sees the failure resolved. An
            // Error entry with no recorded clearance reads as a problem that is still happening.
            Log("Error", "Corsair plug-in: control commands to Corsair PSU " + Identity() + " are reaching the hardware again.");
        }

        // ---- Reads and decoding ----------------------------------------------------------------

        /// <summary>
        /// PMBus LINEAR11 (annex §5.1): the low 11 bits are a two's-complement mantissa, the high 5
        /// bits a two's-complement exponent, and the value is mantissa x 2^exponent.
        /// </summary>
        public static float FromLinear11(ushort raw)
        {
            var exponent = raw >> 11;
            var mantissa = raw & 0x07FF;
            if (exponent > 15)
            {
                exponent -= 32;
            }

            if (mantissa > 1023)
            {
                mantissa -= 2048;
            }

            return (float)(mantissa * Math.Pow(2.0, exponent));
        }

        private float? ReadLinear11(byte command, string what, float minimum, float maximum)
        {
            ushort raw;
            if (!TryReadWord(command, what, out raw))
            {
                return null;
            }

            var value = FromLinear11(raw);
            if (float.IsNaN(value) || value < minimum || value > maximum)
            {
                Log("Debug", "Corsair plug-in: the " + what + " read on Corsair PSU " + Identity() + " decoded to "
                    + value.ToString("0.###", CultureInfo.InvariantCulture) + " (raw 0x" + raw.ToString("X4", CultureInfo.InvariantCulture)
                    + "), outside the plausible " + minimum.ToString("0.###", CultureInfo.InvariantCulture) + " to "
                    + maximum.ToString("0.###", CultureInfo.InvariantCulture) + " range; the reading is discarded.");
                return null;
            }

            return value;
        }

        private bool TryReadWord(byte command, string what, out ushort value)
        {
            value = 0;

            // Reads are logged at Debug throughout: another program's interleaved traffic makes the
            // occasional unusable answer ordinary chatter, not a fault.
            var response = RunTransaction(ModeRead, command, 0x00, "Debug", GuardTimeoutMs, TransferTimeoutMs);
            if (response == null)
            {
                Log("Debug", "Corsair plug-in: the " + what + " read on Corsair PSU " + Identity() + " did not complete.");
                return false;
            }

            // Annex §5.1: little-endian word at buffer offsets 3-4.
            value = (ushort)(response[DataOffset] | (response[DataOffset + 1] << 8));
            return true;
        }

        private bool WriteRegister(byte command, byte data, string failureLevel, int guardTimeoutMs, int transferTimeoutMs)
        {
            return RunTransaction(ModeWrite, command, data, failureLevel, guardTimeoutMs, transferTimeoutMs) != null;
        }

        // ---- Transaction core ------------------------------------------------------------------

        /// <summary>
        /// Runs one complete transaction -- drain, handshake write, handshake read, command write,
        /// command read -- inside a single hold of the device guard, as annex §4.2 requires.
        /// Returns the validated response report, or null when anything about the exchange was off.
        ///
        /// A response whose echo bytes do not match the request is treated as interference from
        /// another program rather than as a device fault: it is logged at Debug and the caller
        /// keeps whatever value it already had.
        ///
        /// Both bounds are parameters because the shutdown restore has to fit inside a ProcessExit
        /// handler: <paramref name="guardTimeoutMs"/> bounds the wait for the cross-process guard,
        /// <paramref name="transferTimeoutMs"/> each of the four HID transfers this transaction
        /// performs. Total worst case is therefore guard + 4 x transfer.
        /// </summary>
        private byte[] RunTransaction(byte mode, byte command, byte data, string failureLevel, int guardTimeoutMs, int transferTimeoutMs)
        {
            var localStream = stream;
            if (localStream == null)
            {
                return null;
            }

            if (!guard.TryEnter(guardTimeoutMs))
            {
                Log(failureLevel, "Corsair plug-in: another Corsair program held the device guard for "
                    + guardTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms; command 0x"
                    + command.ToString("x2", CultureInfo.InvariantCulture) + " was not sent to Corsair PSU " + Identity() + ".");
                return null;
            }

            try
            {
                // Annex §8: the HID input queue also receives the responses other Corsair programs
                // provoke, so everything queued before a new exchange is stale by definition. Once
                // per transaction, ahead of the handshake -- the command write that follows must
                // not drain, or it would swallow its own handshake's trailing traffic.
                localStream.DrainInput();

                if (!Handshake(localStream, "command preamble", transferTimeoutMs))
                {
                    return null;
                }

                if (!localStream.Write(BuildRequest(mode, command, data), transferTimeoutMs))
                {
                    NoteTransportFailure(localStream, "command write");
                    return null;
                }

                var response = ReadReport(localStream, "command read", transferTimeoutMs);
                if (response == null)
                {
                    return null;
                }

                if (!ResponseMatches(response, mode, command))
                {
                    Log("Debug", "Corsair plug-in: Corsair PSU " + Identity() + " answered command 0x"
                        + command.ToString("x2", CultureInfo.InvariantCulture) + " (mode 0x"
                        + mode.ToString("x2", CultureInfo.InvariantCulture) + ") with " + ToHex(response, 4)
                        + "; treating it as another program's traffic and discarding it.");
                    return null;
                }

                return response;
            }
            finally
            {
                guard.Exit();
            }
        }

        /// <summary>
        /// Writes the handshake (annex §4.1 swaps the first two bytes: 0xFE in the mode slot, 0x03
        /// in the command slot) and validates the reply's echo. MUST be called with the guard held.
        /// </summary>
        private bool Handshake(CorsairHidStream localStream, string what, int transferTimeoutMs)
        {
            byte[] response;
            return TryHandshake(localStream, what, transferTimeoutMs, out response);
        }

        private bool TryHandshake(CorsairHidStream localStream, string what, int transferTimeoutMs, out byte[] response)
        {
            response = null;

            if (!localStream.Write(BuildRequest(HandshakeCommand, HandshakeArgument, 0x00), transferTimeoutMs))
            {
                NoteTransportFailure(localStream, what + " handshake write");
                return false;
            }

            var reply = ReadReport(localStream, what + " handshake read", transferTimeoutMs);
            if (reply == null)
            {
                return false;
            }

            // The annex §3 validation rules, spelled out one per clause so they map 1:1 onto the
            // document even where one subsumes another:
            //   rule 1 -- 0xFE in the mode slot is the handshake echo, but 0xFE there *together
            //             with* 0xFE in the first data byte is the device's failure reply;
            //   rule 2 -- 0xFE in the command slot is a failure marker (subsumed by rule 3 here,
            //             since the value required there is 0x03);
            //   rule 3 -- both echo bytes must match what was sent (0xFE, 0x03).
            if (reply[ModeOffset] != HandshakeCommand
                || reply[DataOffset] == HandshakeCommand
                || reply[CommandOffset] == HandshakeCommand
                || reply[CommandOffset] != HandshakeArgument)
            {
                Log("Debug", "Corsair plug-in: the " + what + " handshake with Corsair PSU " + Identity()
                    + " was answered with " + ToHex(reply, 4) + " instead of an 0xfe 0x03 echo.");
                return false;
            }

            response = reply;
            return true;
        }

        private string ReadModelName()
        {
            var localStream = stream;
            if (localStream == null)
            {
                return string.Empty;
            }

            if (!guard.TryEnter(GuardTimeoutMs))
            {
                Log("Error", "Corsair plug-in: another Corsair program held the device guard for "
                    + GuardTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms; the Corsair PSU at " + info.Path
                    + " could not be identified.");
                return string.Empty;
            }

            try
            {
                localStream.DrainInput();

                byte[] response;
                if (!TryHandshake(localStream, "identification", TransferTimeoutMs, out response))
                {
                    return string.Empty;
                }

                var reported = ParseModelName(response);
                Log("Debug", "Corsair plug-in: handshake reply from the Corsair PSU at " + info.Path + ": " + ToHex(response, 16)
                    + " (reported name \"" + reported + "\").");
                return NormalizeModelName(reported);
            }
            finally
            {
                guard.Exit();
            }
        }

        /// <summary>
        /// Annex §5.3: NUL-terminated ASCII, non-printable bytes rendered as '?', result trimmed.
        ///
        /// The name starts at the ordinary data offset. Measured on this machine's HX1200i, whose
        /// reply reads <c>00 fe 03 48 58 31 32 30 30 69 20 50 6f 77 65 72 ...</c> -- the handshake
        /// reply is framed exactly like any other reply (0xfe echoed in the mode slot, 0x03 in the
        /// command slot), with the string beginning one byte later than annex §4.1's prose about
        /// the swapped bytes might suggest.
        /// </summary>
        private static string ParseModelName(byte[] response)
        {
            var builder = new StringBuilder(24);
            for (var i = DataOffset; i < response.Length; i++)
            {
                var value = response[i];
                if (value == 0)
                {
                    break;
                }

                builder.Append(value >= 0x20 && value < 0x7F ? (char)value : '?');
            }

            return builder.ToString().Trim();
        }

        // The 2025 HX1200i reports "HX1200i Power Supply" where the annex quotes a bare "HX1200i".
        // The name ends up in sensor labels, where the suffix is noise ("HX1200i Power Supply Fan
        // #1"), so it is dropped; the string the device actually sent is logged verbatim at connect
        // time for anyone who needs it.
        private static string NormalizeModelName(string name)
        {
            if (name.Length > ModelNameSuffix.Length && name.EndsWith(ModelNameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - ModelNameSuffix.Length).TrimEnd();
            }

            return name;
        }

        private byte[] BuildRequest(byte mode, byte command, byte data)
        {
            // Annex §2/§3: exactly one output report long, byte 0 the report id, the rest zero.
            var request = new byte[info.OutputReportLength];
            request[0] = 0x00;
            request[ModeOffset] = mode;
            request[CommandOffset] = command;
            request[DataOffset] = data;
            return request;
        }

        private byte[] ReadReport(CorsairHidStream localStream, string what, int transferTimeoutMs)
        {
            var buffer = new byte[info.InputReportLength];
            if (!localStream.Read(buffer, transferTimeoutMs))
            {
                NoteTransportFailure(localStream, what);
                return null;
            }

            return buffer;
        }

        // Annex §3 rule 3: a valid command reply echoes both the mode and the command byte. Rules 1
        // and 2 (0xFE as the device's error marker in either slot) need no separate clause here:
        // this is only ever called for a real command, and neither mode (0x02, 0x03) nor any
        // command this class sends is 0xFE, so an error marker can never satisfy the echo test.
        private static bool ResponseMatches(byte[] response, byte mode, byte command)
        {
            return response[ModeOffset] == mode && response[CommandOffset] == command;
        }

        private void NoteTransportFailure(CorsairHidStream localStream, string operation)
        {
            if (localStream.IsDeviceGone)
            {
                isGone = true;
                Log("Debug", "Corsair plug-in: Corsair PSU " + Identity() + " disappeared during a HID " + operation + ".");
                return;
            }

            Log("Debug", "Corsair plug-in: a HID " + operation + " on Corsair PSU " + Identity()
                + " did not complete within its timeout.");
        }

        // ---- Small helpers ---------------------------------------------------------------------

        private string Identity()
        {
            return (modelName.Length == 0 ? "(unidentified)" : modelName) + " [" + PidHex + "]";
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
