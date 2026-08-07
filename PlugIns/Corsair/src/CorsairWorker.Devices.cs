using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SensorReadout.CorsairPlugIn
{
    // The device-facing half of CorsairWorker: finding and opening Corsair devices, polling them,
    // and remembering what the user asked each of them for across the lifetime of the device
    // objects themselves. Split from CorsairWorker.cs only to keep both files inside the 2000-line
    // limit (constraints.md #2); it is the same class and the same worker thread.
    //
    // Everything here runs with deviceLock held -- either taken by the worker's own tick or, for
    // the intent bookkeeping, by whichever control method is calling in.
    public sealed partial class CorsairWorker
    {
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

            // Keep the recorded intent in step with what the device actually did: the device layer
            // can take software control on its own recovery path, and the shutdown restore has to
            // know about it.
            var intent = FindHubIntent(entry.Serial);
            if (entry.Device.OwnsSoftwareControl)
            {
                if (intent == null)
                {
                    intent = new HubIntent();
                    hubIntents[entry.Serial] = intent;
                }

                intent.EverOwned = true;

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
                        if (FindHubByPath(info.Path) == null && ConnectHub(info))
                        {
                            added++;
                        }
                    }
                    else if (IsPsuProductId(info.ProductId))
                    {
                        if (FindPsuByPath(info.Path) == null && ConnectPsu(info))
                        {
                            added++;
                        }
                    }
                }

                statusMessage = (hubs.Count + psus.Count) > 0 ? string.Empty : NoDevicesStatus;

                // While nothing is found, look again soon; once something is, keep a slow re-scan
                // so a device plugged in later is still noticed.
                nextScanTicks = unchecked(Environment.TickCount + ((hubs.Count + psus.Count) > 0 ? PresentRescanMs : ScanIntervalMs));
            }

            if (added > 0)
            {
                Log("Debug", "Corsair plug-in: " + added.ToString(CultureInfo.InvariantCulture)
                    + " Corsair device session(s) opened.");
            }
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
            entry.Serial = device.Serial;
            entry.NextDueTicks = Environment.TickCount;
            hubs.Add(entry);

            Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial + " is connected with "
                + device.Channels.Count.ToString(CultureInfo.InvariantCulture) + " channel(s).");

            RestoreHubIntent(entry);
            return true;
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
                intent = new HubIntent();
                hubIntents[entry.Serial] = intent;
            }

            if (entry.Device.OwnsSoftwareControl)
            {
                intent.EverOwned = true;
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
