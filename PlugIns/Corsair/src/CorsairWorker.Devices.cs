using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SensorReadout.CorsairPlugIn
{
    // The device-facing half of CorsairWorker: finding and opening Corsair devices, polling them,
    // and remembering what the user asked each of them for across the lifetime of the device
    // objects themselves. Split from CorsairWorker.cs only to keep both files inside the 2000-line
    // limit; it is the same class and the same worker thread.
    //
    // Everything here runs with deviceLock held -- either taken by the worker's own tick or, for
    // the intent bookkeeping, by whichever control method is calling in.
    public sealed partial class CorsairWorker
    {
        // Sticky-control marker. Written into the plug-in's own directory while a hub under this
        // plug-in's control has any channel off its default duty (a manual setting or a fan curve),
        // removed again when a reset returns every channel to its default, and read on every later
        // connect. See ResumeControlIfMarked for why it has to exist at all.
        private const string HubControlMarkerPrefix = "corsair-hub-";
        private const string HubControlMarkerSuffix = ".controlled";
        private const string HubControlMarkerContent =
            "Sensor Readout's Corsair plug-in has put the fans of this iCUE LINK hub under its control on this machine. While this file exists, the plug-in resumes fan control of that hub automatically at start-up instead of waiting for the hub to be readable. Sensor Readout removes it again when every channel of the hub is returned to its default (for example by \"All fans reset\"), and re-creates it when a fan is set; delete it yourself to go back to strictly read-only behaviour until a fan control is used again.";

        // How often a hub that connected hardware-mode-blocked gets its marker-driven take retried
        // from RefreshHub. Matches the device layer's own blocked-enumeration cadence: the take is a
        // mode change plus an enumeration under the shared guard, not something to hammer.
        private const int BlockedResumeRetryIntervalMs = 30000;

        // ---- Device refresh ---------------------------------------------------------------------

        /// <summary>
        /// Refreshes every device, taking and releasing the device lock once per device rather than
        /// once per tick, so a control call arriving mid-sweep waits for one device's refresh
        /// instead of all of them. (The scan path is the longer hold -- see the class summary.)
        /// </summary>
        private void RefreshAllDevices()
        {
            List<HubEntry> hubList;
            List<PsuEntry> psuList;
            lock (deviceLock)
            {
                hubList = new List<HubEntry>(hubs);
                psuList = new List<PsuEntry>(psus);
            }

            for (var i = 0; i < hubList.Count && !stopRequested; i++)
            {
                lock (deviceLock)
                {
                    RefreshHub(hubList[i]);
                }
            }

            for (var i = 0; i < psuList.Count && !stopRequested; i++)
            {
                lock (deviceLock)
                {
                    RefreshPsu(psuList[i]);
                }
            }
        }

        private void RefreshHub(HubEntry entry)
        {
            if (entry.Device == null || entry.Closed)
            {
                return;
            }

            if (unchecked(Environment.TickCount - entry.NextDueTicks) < 0)
            {
                return;
            }

            var ok = entry.Device.RefreshSensors();

            if (entry.Device.IsGone)
            {
                DropHub(entry, "it disappeared from the HID bus");
                return;
            }

            NoteDeviceResult(entry, ok, "iCUE LINK hub " + entry.Serial);

            // A hub that connected hardware-mode-blocked gets its marker-driven take retried here at
            // a slow cadence, because nothing else would: the device layer's blocked retry only
            // re-enumerates (a read must never take the hub), a guard timeout does not make the hub
            // "gone" so no reconnect follows, and a blocked hub has no per-channel control rows
            // through which the host could ask.
            if (entry.Device.HardwareModeBlocked && unchecked(Environment.TickCount - entry.NextResumeAttemptTicks) >= 0)
            {
                ResumeControlIfMarked(entry, false);
            }

            // Keep the recorded intent in step with what the device actually did: the device layer
            // can take software control on its own recovery path, and the shutdown restore has to
            // know about it.
            var intent = FindHubIntent(entry.Serial);
            if (entry.Device.OwnsSoftwareControl)
            {
                if (intent == null)
                {
                    intent = CreateHubIntent(entry.Serial);
                }

                NoteHubOwned(entry, intent);

                if (entry.Device.LastReadWrongMode)
                {
                    // Belt and braces. The device layer already re-asserts from inside its own
                    // refresh when it owns the hub and a read comes back "hardware mode", so this
                    // is only reachable if that ever stops being true -- and it is safe precisely
                    // because OwnsSoftwareControl says the control is ours to resume.
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial
                        + " still answers in hardware mode while this plug-in owns it; re-asserting control.");
                    if (entry.Device.ReassertControl())
                    {
                        ReapplyHubPercents(entry, intent);
                    }
                }
            }
        }

        private void RefreshPsu(PsuEntry entry)
        {
            if (entry.Device == null || entry.Closed)
            {
                return;
            }

            if (unchecked(Environment.TickCount - entry.NextDueTicks) < 0)
            {
                return;
            }

            // False here means a core read failed (temperatures, fan speed, fan mode). Input
            // voltage and output power are best-effort extras on models that implement them and
            // never influence this result.
            var ok = entry.Device.RefreshSensors();

            if (entry.Device.IsGone)
            {
                DropPsu(entry, "it disappeared from the HID bus");
                return;
            }

            NoteDeviceResult(entry, ok, "Corsair PSU " + entry.PidHex);

            if (entry.Device.RequestedPercent >= PsuManualThresholdPercent)
            {
                RecordPsuIntent(entry, false);
            }
        }

        private void NoteDeviceResult(DeviceEntry entry, bool ok, string what)
        {
            if (ok)
            {
                if (entry.BackedOff)
                {
                    entry.BackedOff = false;
                    Log("Debug", "Corsair plug-in: " + what + " is answering again; it is back on the normal polling interval.");
                }

                entry.ConsecutiveFailures = 0;
                entry.NextDueTicks = Environment.TickCount;
                return;
            }

            Interlocked.Increment(ref failedDeviceReads);
            entry.ConsecutiveFailures++;

            if (entry.ConsecutiveFailures < MaxConsecutiveFailures)
            {
                return;
            }

            // Five failures in a row is a device that is busy, wedged, or owned by a program that
            // will not share. Backing off keeps the log and the HID bus quiet without giving up.
            if (!entry.BackedOff)
            {
                entry.BackedOff = true;
                Log("Debug", "Corsair plug-in: " + what + " has failed " + entry.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)
                    + " reads in a row; slowing it to one attempt every "
                    + (DeviceBackoffMs / 1000).ToString(CultureInfo.InvariantCulture) + " s until one succeeds.");
            }

            entry.NextDueTicks = unchecked(Environment.TickCount + DeviceBackoffMs);
        }

        private void DropHub(HubEntry entry, string why)
        {
            Log("Debug", "Corsair plug-in: closing the session with iCUE LINK hub " + entry.Serial + " because " + why + ".");
            try
            {
                // restoreHardwareMode: false -- the device is not reachable, so a restore write can
                // only fail, and the intent record keeps what the user asked for.
                entry.Device.Disconnect(false);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: closing iCUE LINK hub " + entry.Serial + " threw (" + ex.Message + ").");
            }

            entry.Closed = true;
            hubs.Remove(entry);
            scanRequested = true;
        }

        private void DropPsu(PsuEntry entry, string why)
        {
            Log("Debug", "Corsair plug-in: closing the session with Corsair PSU " + entry.PidHex + " because " + why + ".");
            try
            {
                entry.Device.Disconnect(false);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: closing Corsair PSU " + entry.PidHex + " threw (" + ex.Message + ").");
            }

            entry.Closed = true;
            psus.Remove(entry);
            scanRequested = true;
        }

        // ---- Scanning and connecting -------------------------------------------------------------

        private bool EnsureGuard()
        {
            lock (deviceLock)
            {
                if (guard != null)
                {
                    return true;
                }

                try
                {
                    guard = new CorsairDeviceGuard();
                    return true;
                }
                catch (Exception ex)
                {
                    NoteError("creating the shared Corsair device guard", ex);
                    return false;
                }
            }
        }

        private void ScanDevices()
        {
            var summaryBuilder = new StringBuilder();
            List<CorsairHidDeviceInfo> found;
            try
            {
                found = CorsairHidEnumerator.FindCorsairDevices(delegate(string message)
                {
                    if (summaryBuilder.Length > 0)
                    {
                        summaryBuilder.Append(Environment.NewLine);
                    }

                    summaryBuilder.Append(message);
                });
            }
            catch (Exception ex)
            {
                NoteError("enumerating Corsair HID devices", ex);
                found = new List<CorsairHidDeviceInfo>();
            }

            var summary = summaryBuilder.ToString();
            if (!string.Equals(summary, lastScanSummary, StringComparison.Ordinal))
            {
                // Only when the HID picture actually changed: this runs every 30 s while nothing is
                // found, and repeating the same three lines forever would drown the Debug log.
                lastScanSummary = summary;
                if (summary.Length > 0)
                {
                    Log("Debug", "Corsair plug-in: HID enumeration found:" + Environment.NewLine + summary);
                }
            }

            var added = 0;
            var failedConnects = 0;
            lock (deviceLock)
            {
                for (var i = 0; i < found.Count; i++)
                {
                    var info = found[i];
                    if (info == null || string.IsNullOrEmpty(info.Path))
                    {
                        continue;
                    }

                    if (info.ProductId == HubProductId)
                    {
                        if (FindHubByPath(info.Path) == null)
                        {
                            if (ConnectHub(info))
                            {
                                added++;
                            }
                            else
                            {
                                failedConnects++;
                            }
                        }
                    }
                    else if (IsPsuProductId(info.ProductId))
                    {
                        if (FindPsuByPath(info.Path) == null)
                        {
                            if (ConnectPsu(info))
                            {
                                added++;
                            }
                            else
                            {
                                failedConnects++;
                            }
                        }
                    }
                }

                var open = hubs.Count + psus.Count;
                statusMessage = open > 0 ? string.Empty : NoDevicesStatus;

                if (open > peakDeviceSessions)
                {
                    peakDeviceSessions = open;
                }

                // "We are short a device we used to have, or the bus shows one we cannot open."
                // Either way this process is missing a session it should have, which is a different
                // situation from the steady state PresentRescanMs was chosen for.
                var missing = failedConnects > 0 || open < peakDeviceSessions;
                recoveryScans = missing ? recoveryScans + 1 : 0;
                nextScanTicks = unchecked(Environment.TickCount + NextScanDelayMs(open, missing, recoveryScans));
            }

            if (added > 0)
            {
                Log("Debug", "Corsair plug-in: " + added.ToString(CultureInfo.InvariantCulture)
                    + " Corsair device session(s) opened.");
            }
        }

        /// <summary>
        /// How long to wait before the next device scan.
        ///
        /// The steady state is a slow watch for a device plugged in later, and the empty state is a
        /// brisk look for the first device. What was missing is the state in between: a session this
        /// process had and lost, or a device the HID bus shows that will not open.
        ///
        /// That state cost five minutes of a water loop running its hub's own firmware curve on
        /// 2026-08-21. The machine resumed at 17:10:56, a single HID read timed out one second
        /// later, sub-device enumeration failed, and the hub session did not come back -- but the
        /// PSU session did, so the scan that had just failed to re-open the hub counted "a device is
        /// present" and scheduled its next attempt PresentRescanMs later. Recovery took 301 s and
        /// the plug-in's rows sat at 6 instead of 36 for all of it.
        ///
        /// So a scan that comes up short retries at the absent-device cadence, then doubles up to
        /// the present-device one. Bounded at both ends and it cannot spin: the first retry is
        /// ScanIntervalMs away, not immediate, and a device that is never coming back (unplugged for
        /// good, or held open by another program) settles onto exactly the slow cadence it has now
        /// after four attempts rather than re-enumerating every 30 s for the life of the process.
        /// </summary>
        internal static int NextScanDelayMs(int openSessions, bool missingSession, int consecutiveRecoveryScans)
        {
            if (openSessions <= 0)
            {
                return ScanIntervalMs;
            }

            if (!missingSession)
            {
                return PresentRescanMs;
            }

            var delay = ScanIntervalMs;
            for (var i = 1; i < consecutiveRecoveryScans && delay < PresentRescanMs; i++)
            {
                delay *= 2;
            }

            return delay > PresentRescanMs ? PresentRescanMs : delay;
        }

        // Called with deviceLock held.
        private bool ConnectHub(CorsairHidDeviceInfo info)
        {
            var device = new CorsairLinkHubDevice(info, guard, Log);
            var connected = false;
            try
            {
                connected = device.Connect();
            }
            catch (Exception ex)
            {
                NoteError("connecting to the iCUE LINK hub at " + info.Path, ex);
            }

            if (!connected)
            {
                try
                {
                    device.Disconnect(false);
                }
                catch (Exception)
                {
                }

                return false;
            }

            var entry = new HubEntry();
            entry.Device = device;
            entry.Info = info;
            // Sanitize once, here, at the single point a hub's serial
            // enters this worker's bookkeeping. entry.Serial is the canonical key from this point
            // on -- FindHub, hubIntents, and BuildSnapshot's HubSnapshot.Serial (and therefore every
            // row identifier Rows.cs builds) all read it back unchanged, so there is exactly one
            // place that can disagree with the others, and there is none.
            //
            // Sanitization can turn two genuinely
            // different hubs' serials into the same key (most plainly, two hubs that both report no
            // serial at all would both sanitize to "hub0"). MakeUniqueHubKey is called against the
            // list this hub is about to join, before it joins it, so a collision cannot silently
            // merge two hubs' FindHub/hubIntents/identifier bookkeeping into one.
            var sanitizedSerial = SanitizeHubKey(device.Serial);
            entry.Serial = MakeUniqueHubKey(sanitizedSerial);
            if (!string.Equals(entry.Serial, sanitizedSerial, StringComparison.OrdinalIgnoreCase))
            {
                Log("Debug", "Corsair plug-in: the iCUE LINK hub at " + info.Path + " sanitizes to the key \""
                    + sanitizedSerial + "\", already used by another connected hub; using \"" + entry.Serial + "\" instead.");
            }

            entry.NextDueTicks = Environment.TickCount;
            entry.NextResumeAttemptTicks = Environment.TickCount;
            hubs.Add(entry);

            Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial + " is connected with "
                + device.Channels.Count.ToString(CultureInfo.InvariantCulture) + " channel(s).");

            RestoreHubIntent(entry);

            // After RestoreHubIntent, not before: a hub this worker has already owned in *this*
            // process is resumed from the intent record, which also clears the blocked state, and
            // there is then nothing left for the marker path to do.
            ResumeControlIfMarked(entry, true);
            return true;
        }

        /// <summary>
        /// Resumes fan control of a hub that connected in the hardware-mode-blocked state, but only
        /// on a machine that has already used Sensor Readout for fan control.
        ///
        /// The problem this solves: a hub in hardware mode refuses sub-device enumeration outright
        /// (annex §2/§7 editor's note, measured 2026-08-07 on firmware 3.12.650). So after a clean
        /// exit -- which deliberately hands the hub back to its own profile -- the next launch has
        /// no rows, no controls, and no temperature source for a fan curve, and nothing can ever ask
        /// for software mode again. The marker file is the narrowest fact that breaks that deadlock:
        /// this machine has used this plug-in to drive this hub's fans before, so resuming is
        /// restoring the user's own arrangement rather than seizing someone else's hardware. Without
        /// the marker the hub is left exactly as found, and the status row says how to start.
        /// </summary>
        private void ResumeControlIfMarked(HubEntry entry, bool announceWhenUnmarked)
        {
            if (entry.Device == null || !entry.Device.HardwareModeBlocked)
            {
                return;
            }

            // Whatever happens below, the next attempt is a retry interval away: RefreshHub keeps
            // asking while the hub stays blocked, so a transient failure here (the shared guard held
            // by another program for longer than the take's bounded wait, a post-resume HID timeout)
            // no longer leaves the hub unreadable for the rest of the session.
            entry.NextResumeAttemptTicks = unchecked(Environment.TickCount + BlockedResumeRetryIntervalMs);

            if (!HubControlMarkerExists(entry.Serial))
            {
                if (announceWhenUnmarked)
                {
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial
                        + " is running its own hardware profile and this machine has not used Sensor Readout for its fan control,"
                        + " so it is left alone; its readings appear once its take-control entry or a fan control here is used, or another program takes the hub.");
                }

                return;
            }

            Log("Debug", "Corsair plug-in: resuming fan control of hub " + entry.Serial
                + " (this machine previously used Sensor Readout for fan control).");

            if (!entry.Device.TakeControlIfBlocked())
            {
                Log("Debug", "Corsair plug-in: taking software control of iCUE LINK hub " + entry.Serial
                    + " did not succeed; it stays in hardware mode and the take will be retried in about "
                    + (BlockedResumeRetryIntervalMs / 1000).ToString(CultureInfo.InvariantCulture) + " seconds.");
                return;
            }

            RecordHubIntent(entry);
        }

        // Called with deviceLock held.
        private bool ConnectPsu(CorsairHidDeviceInfo info)
        {
            var device = new CorsairHidPsuDevice(info, guard, Log);
            var connected = false;
            try
            {
                connected = device.Connect();
            }
            catch (Exception ex)
            {
                NoteError("connecting to the Corsair PSU at " + info.Path, ex);
            }

            if (!connected)
            {
                try
                {
                    device.Disconnect(false);
                }
                catch (Exception)
                {
                }

                return false;
            }

            var entry = new PsuEntry();
            entry.Device = device;
            entry.Info = info;
            entry.PidHex = device.PidHex;
            entry.NextDueTicks = Environment.TickCount;
            psus.Add(entry);

            Log("Debug", "Corsair plug-in: Corsair PSU " + device.ModelName + " [" + entry.PidHex + "] is connected.");

            RestorePsuIntent(entry);
            return true;
        }

        /// <summary>
        /// Canonicalizes a hub serial into the key every row identifier,
        /// intent-dictionary lookup and <see cref="FindHub"/> comparison uses. <c>device.Serial</c>
        /// is already lower-cased by <c>CorsairLinkHubDevice.Connect</c>, but nothing there
        /// guarantees it is free of '|' or '/' -- a <c>SensorReading.Identifier</c> may never
        /// contain '|' or start with '/' (see <c>Docs/Plug-In-development.md</c>), and either
        /// character would also make this plug-in's own <c>TryParseControlIdentifier</c> gate,
        /// which splits on '/', reject its own rows. Falls back to "hub0" when
        /// nothing alphanumeric survives, matching <c>CorsairLinkHubDevice.FallbackSerial</c>'s
        /// intent for a device that reports no serial at all.
        /// </summary>
        private static string SanitizeHubKey(string rawSerial)
        {
            var lowered = string.IsNullOrEmpty(rawSerial) ? string.Empty : rawSerial.ToLowerInvariant();
            var builder = new StringBuilder(lowered.Length);
            for (var i = 0; i < lowered.Length; i++)
            {
                var ch = lowered[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
            }

            return builder.Length == 0 ? "hub0" : builder.ToString();
        }

        /// <summary>
        /// Returns <paramref name="candidateKey"/>
        /// unchanged when no currently-connected hub already uses it, otherwise the same key with
        /// "-2", "-3", ... appended until one is free. Called with <c>deviceLock</c> held (same as
        /// <see cref="ConnectHub"/>, its only caller), so the <see cref="FindHub"/> check below sees
        /// every hub this worker currently has open. The HID-path dedupe in <see cref="ScanDevices"/>
        /// (<c>FindHubByPath</c>) already stops the same physical hub from being opened twice; this
        /// guards the separate, rarer case of two genuinely different hubs whose serials collide
        /// after <see cref="SanitizeHubKey"/> strips non-alphanumeric characters -- most plainly, two
        /// hubs that both report no serial at all would otherwise both become "hub0".
        /// </summary>
        private string MakeUniqueHubKey(string candidateKey)
        {
            if (FindHub(candidateKey) == null)
            {
                return candidateKey;
            }

            for (var suffix = 2; suffix < 1000; suffix++)
            {
                var attempt = candidateKey + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                if (FindHub(attempt) == null)
                {
                    return attempt;
                }
            }

            // Effectively unreachable (998 simultaneously connected hubs sharing one sanitized key),
            // but every path must return a key rather than loop forever.
            return candidateKey + "-" + Environment.TickCount.ToString(CultureInfo.InvariantCulture);
        }

        // ---- Intent: what the user asked for, across device object lifetimes ----------------------

        /// <summary>
        /// Re-takes a hub this worker had already taken, then replays every channel the user moved
        /// off its default. Does nothing at all unless this worker recorded taking the hub in this
        /// process: <c>ReassertControl</c> takes control unconditionally, so calling it speculatively
        /// would steal the hub from whatever program owns it.
        /// </summary>
        private void RestoreHubIntent(HubEntry entry)
        {
            var intent = FindHubIntent(entry.Serial);
            if (intent == null || !intent.EverOwned)
            {
                return;
            }

            Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial
                + " was under this plug-in's control before it was re-opened; re-asserting control and re-applying the requested duties.");

            if (!entry.Device.ReassertControl())
            {
                Log("Debug", "Corsair plug-in: re-asserting control of iCUE LINK hub " + entry.Serial
                    + " did not succeed; it will be retried after the next reconnect.");
                return;
            }

            ReapplyHubPercents(entry, intent);
        }

        // A re-created device object starts every channel at its enumeration default, so the
        // percentages the user chose only exist in the intent record.
        private void ReapplyHubPercents(HubEntry entry, HubIntent intent)
        {
            if (intent.Percents.Count == 0)
            {
                return;
            }

            var channels = new List<int>(intent.Percents.Keys);
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                int percent;
                if (!intent.Percents.TryGetValue(channel, out percent))
                {
                    continue;
                }

                if (!entry.Device.SetChannelPercent(channel, percent))
                {
                    Log("Debug", "Corsair plug-in: re-applying " + percent.ToString(CultureInfo.InvariantCulture)
                        + " % to channel " + channel.ToString(CultureInfo.InvariantCulture)
                        + " of iCUE LINK hub " + entry.Serial + " did not reach the hardware.");
                }
            }

            RecordHubIntent(entry);
        }

        /// <summary>
        /// Re-applies the PSU's manual duty after the device object was re-created. Only ever fires
        /// when this worker recorded a manual duty at or above the PSU's 30 % floor -- there is no
        /// path here that writes a fan register on its own initiative.
        /// </summary>
        private void RestorePsuIntent(PsuEntry entry)
        {
            var intent = FindPsuIntent(entry.PidHex);
            if (intent == null || !intent.EverSetManual)
            {
                return;
            }

            if (intent.RequestedPercent < PsuManualThresholdPercent)
            {
                // Manual control was taken and the hand-back to the PSU's own curve did not land
                // before the device went away. The new device object has no memory of that, so
                // without this the PSU could sit in manual mode indefinitely -- the one hazard the
                // shutdown ordering exists to prevent. This is the give-it-back direction only
                // (duty 0, then mode 0x00); nothing here can put a fan under manual control.
                Log("Debug", "Corsair plug-in: the fan of Corsair PSU " + entry.PidHex
                    + " may still be in manual mode from an incomplete hand-back; returning it to automatic control.");
                if (entry.Device.ResetFan())
                {
                    intent.EverSetManual = false;
                }

                intent.RequestedPercent = entry.Device.RequestedPercent;
                return;
            }

            Log("Debug", "Corsair plug-in: Corsair PSU " + entry.PidHex + " was running a manual fan duty of "
                + intent.RequestedPercent.ToString(CultureInfo.InvariantCulture) + " % before it was re-opened; re-applying it.");

            if (!entry.Device.SetFanPercent(intent.RequestedPercent))
            {
                Log("Debug", "Corsair plug-in: re-applying the manual fan duty to Corsair PSU " + entry.PidHex + " did not reach the hardware.");
            }

            RecordPsuIntent(entry, false);
        }

        // Mirrors the device's live channel state into the intent record. Called with deviceLock
        // held, after anything that can change a duty.
        private void RecordHubIntent(HubEntry entry)
        {
            var intent = FindHubIntent(entry.Serial);
            if (intent == null)
            {
                intent = CreateHubIntent(entry.Serial);
            }

            if (entry.Device.OwnsSoftwareControl)
            {
                NoteHubOwned(entry, intent);
            }

            var channels = entry.Device.Channels;

            // Forget ports the hub no longer reports. Without this, unplugging a fan from port 5
            // and plugging a different one in later would replay its predecessor's percentage onto
            // it -- the intent record is keyed by port number, and a port is not a device.
            //
            // Only when the hub reported something: an enumeration that came back empty (a wire
            // failure, a hub mid-reset) says nothing about which ports exist, and treating it as
            // "every port is gone" would throw away the user's settings over a transient fault.
            if (channels.Count > 0)
            {
                var stale = new List<int>();
                foreach (var channel in intent.Percents.Keys)
                {
                    if (!HasChannel(channels, channel))
                    {
                        stale.Add(channel);
                    }
                }

                for (var i = 0; i < stale.Count; i++)
                {
                    intent.Percents.Remove(stale[i]);
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial + " no longer reports port "
                        + stale[i].ToString(CultureInfo.InvariantCulture) + "; forgetting the duty that was requested for it.");
                }
            }

            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                if (state.PercentIsDefault)
                {
                    intent.Percents.Remove(state.Channel);
                }
                else
                {
                    intent.Percents[state.Channel] = state.RequestedPercent;
                }
            }
        }

        private static bool HasChannel(List<LinkChannelState> channels, int channel)
        {
            for (var i = 0; i < channels.Count; i++)
            {
                if (channels[i].Channel == channel)
                {
                    return true;
                }
            }

            return false;
        }

        // Mirrors the PSU's live state into the intent record. <paramref name="handedBack"/> is set
        // by the two call sites that successfully returned the fan to the PSU -- the device clears
        // its own "manual was taken" flag only on success, and this record has to match, or a
        // shutdown would send a pointless restore (or, worse, skip a needed one).
        private void RecordPsuIntent(PsuEntry entry, bool handedBack)
        {
            var intent = FindPsuIntent(entry.PidHex);
            if (intent == null)
            {
                intent = new PsuIntent();
                psuIntents[entry.PidHex] = intent;
            }

            intent.RequestedPercent = entry.Device.RequestedPercent;
            if (handedBack)
            {
                intent.EverSetManual = false;
            }
            else if (intent.RequestedPercent >= PsuManualThresholdPercent)
            {
                intent.EverSetManual = true;
            }
        }

        // ---- Sticky-control marker ----------------------------------------------------------------
        //
        // The intent record above only lives as long as this process, which is all it needs for
        // reconnects and sleep/wake. Surviving a restart is a different question with a different
        // answer: the only thing recorded on disk is the single bit "fan control has been used for
        // this hub on this machine", and its only effect is to let ResumeControlIfMarked take a hub
        // that would otherwise be unreadable. No duty, no percentage, nothing about what the user
        // asked for -- those still come from the host's own saved fan-control state.

        /// <summary>
        /// Records that this worker now owns the hub, and writes the marker file once the hub has a
        /// channel off its default duty -- a manual setting or a curve, i.e. something the user would
        /// want back after a restart. A bare take (the hub-wide take-control entry, or one-click
        /// diagnostics after it has restored every control) leaves no marker, so it cannot by itself
        /// make every later launch seize the hub. Both places that can observe ownership --
        /// <see cref="RefreshHub"/> and <see cref="RecordHubIntent"/> -- go through here, so the
        /// marker cannot depend on which of them happened to notice first.
        /// </summary>
        private void NoteHubOwned(HubEntry entry, HubIntent intent)
        {
            if (intent == null)
            {
                return;
            }

            intent.EverOwned = true;
            if (!intent.MarkerPresent && AnyChannelOffDefault(entry.Device))
            {
                // Marked present before the attempt so a failed write is tried once per episode,
                // not once per tick; the file's absence is what the next launch acts on anyway.
                intent.MarkerPresent = true;
                WriteHubControlMarker(entry.Serial);
            }
        }

        // The intent record is keyed by the canonical serial; its marker flag is seeded from disk so
        // a marker left by an earlier session can be cleared by this one.
        private HubIntent CreateHubIntent(string serial)
        {
            var intent = new HubIntent();
            intent.MarkerPresent = HubControlMarkerExists(serial);
            hubIntents[serial] = intent;
            return intent;
        }

        // Called with deviceLock held. True when the plug-in owns the hub and at least one channel
        // that accepts a duty is off its default -- the state worth resuming after a restart.
        private static bool AnyChannelOffDefault(CorsairLinkHubDevice device)
        {
            if (device == null || !device.OwnsSoftwareControl)
            {
                return false;
            }

            var channels = device.Channels;
            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                if (state != null && state.Device != null && state.Device.HasControl && !state.PercentIsDefault)
                {
                    return true;
                }
            }

            return false;
        }

        // Called with deviceLock held, after a successful reset. Once every channel of an owned hub is
        // back at its default there is nothing to resume at the next start, so the marker goes; the
        // next manual setting or curve step writes it again. Only for an owned hub: a reset on a hub
        // this plug-in does not own (a blocked hub at start-up, another program's hub) is bookkeeping
        // and must not discard a marker the retry in RefreshHub is still waiting to act on.
        private void ClearHubControlMarkerIfAllDefault(HubEntry entry)
        {
            var intent = FindHubIntent(entry.Serial);
            if (intent == null || !intent.MarkerPresent || entry.Device == null
                || !entry.Device.OwnsSoftwareControl || AnyChannelOffDefault(entry.Device))
            {
                return;
            }

            intent.MarkerPresent = false;
            DeleteHubControlMarker(entry.Serial);
        }

        // Null whenever the marker feature is off (no plug-in directory was supplied) or there is no
        // serial to key it by. entry.Serial is already sanitized to [a-z0-9] plus a possible "-N"
        // uniqueness suffix (SanitizeHubKey / MakeUniqueHubKey), so it is safe as a file name.
        private string HubControlMarkerPath(string serial)
        {
            var directory = pluginDirectory;
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(serial))
            {
                return null;
            }

            try
            {
                return Path.Combine(directory, HubControlMarkerPrefix + serial + HubControlMarkerSuffix);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: could not build the fan-control marker path for iCUE LINK hub "
                    + serial + " (" + ex.Message + "); the marker is not used for it.");
                return null;
            }
        }

        // Every marker operation is best effort: a read-only plug-in directory, a roaming profile, a
        // locked file -- none of that is a reason to fail a control call the user asked for, and the
        // only consequence is that the next launch waits to be asked again.
        private void WriteHubControlMarker(string serial)
        {
            var path = HubControlMarkerPath(serial);
            if (path == null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    return;
                }

                File.WriteAllText(path, HubControlMarkerContent, Encoding.UTF8);
                Log("Debug", "Corsair plug-in: iCUE LINK hub " + serial
                    + " is now under this plug-in's fan control; recorded it in " + path
                    + " so control resumes automatically the next time the app starts.");
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: could not write the fan-control marker file " + path + " ("
                    + ex.Message + "); fan control will simply have to be used again after the next restart.");
            }
        }

        private void DeleteHubControlMarker(string serial)
        {
            var path = HubControlMarkerPath(serial);
            if (path == null)
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                Log("Debug", "Corsair plug-in: every channel of iCUE LINK hub " + serial
                    + " is back at its default, so " + path
                    + " was removed; the next start leaves the hub alone until a fan control here is used again.");
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: could not remove the fan-control marker file " + path + " ("
                    + ex.Message + "); the next start will still resume fan control of the hub.");
            }
        }

        private bool HubControlMarkerExists(string serial)
        {
            var path = HubControlMarkerPath(serial);
            if (path == null)
            {
                return false;
            }

            try
            {
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: could not check for the fan-control marker file " + path + " ("
                    + ex.Message + "); treating it as absent.");
                return false;
            }
        }

        private HubIntent FindHubIntent(string serial)
        {
            HubIntent intent;
            return (serial != null && hubIntents.TryGetValue(serial, out intent)) ? intent : null;
        }

        private PsuIntent FindPsuIntent(string pidHex)
        {
            PsuIntent intent;
            return (pidHex != null && psuIntents.TryGetValue(pidHex, out intent)) ? intent : null;
        }
    }
}
