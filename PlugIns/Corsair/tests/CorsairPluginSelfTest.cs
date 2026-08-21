using System;
using System.Collections.Generic;
using System.Linq;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    internal static class CorsairPluginSelfTest
    {
        private static int checks;

        public static int Main()
        {
            try
            {
                TestReleaseSurface();
                TestWriteBoundary();
                TestPacketBounds();
                TestProtocolParsers();
                TestRowsAndPrivacy();
                TestScanCadenceAfterSessionLoss();
                TestDeferredTeardownAcrossReload();
                TestUnstartedLifecycle();
                // Last: it leaves a worker that can never complete a hand-back, so the singleton
                // it holds is never replaced again. See PretendWorkerRunningForTest.
                TestShutdownDefersWhileTheWorkerRuns();
                Console.WriteLine("Corsair plug-in self-test passed: " + checks + " checks.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Corsair plug-in self-test failed: " + ex);
                return 1;
            }
        }

        // This release deliberately re-opens the write path that the 5.2.0 monitoring release kept
        // closed, so the two boundary assertions below assert the *new* contract: control calls are
        // routed, and they still send nothing when no matching device is connected. Nothing in this
        // file may reach hardware -- the worker is never started here, so its device lists are empty
        // and every call below is a lookup miss.
        private static void TestWriteBoundary()
        {
            var worker = CorsairWorker.Instance;
            Require(!worker.SetHubChannelPercent("TEST", 1, 50), "A hub fan write for an unknown hub must report failure and send nothing.");
            Require(!worker.ResetHubChannel("TEST", 1), "A hub fan reset for an unknown hub must report failure and send nothing.");
            Require(!worker.SetPsuFanPercent("1c27", 50), "A PSU fan write with no PSU connected must report failure and send nothing.");
            Require(!worker.ResetPsuFan("1c27"), "A PSU fan reset with no PSU connected must report failure and send nothing.");

            // The identifier gate in front of those calls: only this plug-in's own control
            // identifiers may be claimed, so the host can keep offering the rest to other plug-ins.
            bool isHub;
            string deviceKey;
            int channel;
            Require(CorsairPlugIn.TryParseControlIdentifier("corsair/link/hub0/control/4", out isHub, out deviceKey, out channel)
                && isHub && deviceKey == "hub0" && channel == 4, "A hub control identifier must parse into its device key and channel.");
            Require(CorsairPlugIn.TryParseControlIdentifier("corsair/psu/1c23/control/0", out isHub, out deviceKey, out channel)
                && !isHub && deviceKey == "1c23", "A PSU control identifier must parse as the PSU fan control.");
            Require(!CorsairPlugIn.TryParseControlIdentifier("/amdcpu/0/control/1", out isHub, out deviceKey, out channel),
                "LibreHardwareMonitor identifiers must not be claimed by this plug-in.");
            Require(!CorsairPlugIn.TryParseControlIdentifier("corsair/link/hub0/fan/4", out isHub, out deviceKey, out channel),
                "A Fan identifier is not a control identifier.");

            var plugIn = new CorsairPlugIn();
            Require(!plugIn.TrySetFanPercent("/amdcpu/0/control/1", 50), "A foreign identifier must be rejected without touching hardware.");
            Require(!plugIn.TryResetFan(null), "A null identifier must be rejected without touching hardware.");
        }

        private static void TestReleaseSurface()
        {
            var plugIn = new CorsairPlugIn();
            Require(plugIn is ISensorReadoutPlugin, "The Corsair plug-in must implement the sensor interface.");
            Require(plugIn is IPluginLifecycle, "The Corsair plug-in must implement explicit shutdown.");
            // Deliberately re-opened in this release: fan and pump control are back, so the plug-in
            // has to advertise the fan-control interface again.
            Require(typeof(IFanControllablePlugin).IsAssignableFrom(plugIn.GetType()), "This Corsair release must expose fan control.");
            Require(plugIn.Info != null && plugIn.Info.Description.IndexOf("fan control", StringComparison.OrdinalIgnoreCase) >= 0,
                "The plug-in metadata must state that this release can drive fans.");
            Require(plugIn.Info != null && plugIn.Info.Description.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) < 0,
                "The plug-in metadata must not still describe itself as read-only.");
        }

        private static void TestPacketBounds()
        {
            var packet = LinkHubData.BuildCommandPacket(8, new byte[] { 0x02, 0x13 }, new byte[] { 0x44 });
            Require(packet.Length == 8, "Command packet length changed.");
            Require(packet[0] == 0x00 && packet[1] == 0x00 && packet[2] == 0x01 && packet[3] == 0x02 && packet[4] == 0x13 && packet[5] == 0x44,
                "Command packet framing changed.");

            RequireThrows<ArgumentOutOfRangeException>(delegate { LinkHubData.BuildCommandPacket(2, null, null); },
                "Reports shorter than the frame header must be rejected.");
            RequireThrows<ArgumentOutOfRangeException>(delegate { LinkHubData.BuildCommandPacket(5, new byte[] { 1, 2 }, new byte[] { 3 }); },
                "Commands larger than the report must be rejected.");

            var matchingResponse = new byte[9];
            matchingResponse[3] = 0x13;
            matchingResponse[4] = LinkHubData.StatusOk;
            Require(CorsairLinkHubDevice.ReportMatches(matchingResponse, null, 0x13), "A response with the expected command echo must match.");
            Require(!CorsairLinkHubDevice.ReportMatches(matchingResponse, null, 0x14),
                "A status-OK response from another command must not be accepted.");
        }

        private static void TestProtocolParsers()
        {
            var firmware = new byte[9];
            firmware[5] = 2;
            firmware[6] = 5;
            firmware[7] = 0x34;
            firmware[8] = 0x12;
            Require(LinkHubData.ParseFirmwareVersion(firmware) == "2.5.4660", "Firmware parsing changed.");
            Require(LinkHubData.ParseFirmwareVersion(new byte[8]) == string.Empty, "Truncated firmware must be rejected.");

            var sensors = new byte[14];
            sensors[7] = 2;
            sensors[8] = 0;
            sensors[9] = 0xD2;
            sensors[10] = 0x04;
            sensors[11] = 1;
            sensors[12] = 0xFF;
            sensors[13] = 0x7F;
            var records = LinkHubData.ParseSensorRecords(sensors);
            Require(records.Count == 2 && records[0].Available && records[0].RawValue == 1234,
                "Sensor record parsing changed.");
            Require(!records[1].Available && records[1].RawValue == 0, "Unavailable sensor records must not expose stale payload values.");

            var truncated = new byte[10];
            truncated[7] = 2;
            Require(LinkHubData.ParseSensorRecords(truncated).Count == 0, "Truncated sensor records must stop safely.");

            var subDevices = new byte[18];
            subDevices[7] = 1;
            subDevices[10] = 0x01;
            subDevices[11] = 0x00;
            subDevices[15] = 2;
            subDevices[16] = (byte)'Q';
            subDevices[17] = (byte)'X';
            var parsedDevices = LinkHubData.ParseSubDevices(subDevices, null);
            Require(parsedDevices.Count == 1 && parsedDevices[0].Channel == 1 && parsedDevices[0].DeviceId == "QX",
                "Sub-device parsing changed.");
            Require(LinkHubData.ParseSubDevices(new byte[7], null).Count == 0, "Truncated sub-device data must stop safely.");

            var known = LinkKnownDevices.Find(0x01, 0x00);
            Require(known != null && known.HasRpm && known.HasControl && known.Name.IndexOf("QX", StringComparison.OrdinalIgnoreCase) >= 0,
                "Known iCUE LINK device mapping changed.");
            Require(LinkKnownDevices.Find(0xFE, 0x00) == null, "Unknown devices must not be guessed.");
            Require(Math.Abs(CorsairHidPsuDevice.FromLinear11(0x000A) - 10.0f) < 0.001f, "LINEAR11 decoding changed.");
            Require(LinkHubData.ResponseStatus(new byte[4]) == 0xFF, "Malformed responses must not report success.");
        }

        private static void TestRowsAndPrivacy()
        {
            var snapshot = new CorsairSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                Status = string.Empty,
                Hubs = new List<HubSnapshot>
                {
                    MakeHub("PRIVATE-SERIAL-ALPHA", 1, "QX Fan", "PRIVATE-DEVICE-ONE"),
                    MakeHub("PRIVATE-SERIAL-BRAVO", 2, "RX Fan", "PRIVATE-DEVICE-TWO")
                },
                Psus = new List<PsuSnapshot>
                {
                    new PsuSnapshot { ModelName = "HX1200i", PidHex = "1c23", FanRpm = 750, Temperature1C = 42.5f, InputVoltage = 230.1f, OutputPowerW = 410.0f }
                }
            };

            var rows = CorsairPlugIn.BuildRows(snapshot, false, null);
            Require(rows.Count > 0, "A populated snapshot must produce rows.");

            // Deliberately re-opened in this release: an enumerated hub channel that accepts a duty
            // gets a Fan Control row, and the PSU always gets one.
            var hubControl = rows.FirstOrDefault(row => string.Equals(row.Type, "Fan Control", StringComparison.OrdinalIgnoreCase)
                && (row.Identifier ?? string.Empty).StartsWith("corsair/link/", StringComparison.OrdinalIgnoreCase));
            Require(hubControl != null, "An enumerated hub channel with duty control must expose a Fan Control row.");
            Require(hubControl.Identifier.IndexOf("/control/1", StringComparison.OrdinalIgnoreCase) >= 0,
                "A hub control identifier must carry the port number of the channel it drives.");
            Require(rows.Any(row => string.Equals(row.Type, "Fan", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Name, hubControl.Name, StringComparison.Ordinal)
                    && string.Equals(row.Identifier, hubControl.Identifier.Replace("/control/", "/fan/"), StringComparison.OrdinalIgnoreCase)),
                "A Fan Control row must pair with its Fan row by identical Name and the /fan/ <-> /control/ identifier swap.");

            var psuControl = rows.FirstOrDefault(row => string.Equals(row.Type, "Fan Control", StringComparison.OrdinalIgnoreCase)
                && (row.Identifier ?? string.Empty).StartsWith("corsair/psu/", StringComparison.OrdinalIgnoreCase));
            Require(psuControl != null, "The PSU fan control row must be emitted for every connected PSU.");
            Require(psuControl.Details != null && psuControl.Details.ContainsKey("Zero RPM capable"),
                "The semi-passive PSU fan control must carry the zero-RPM marker so the host keeps it visible at 0 RPM.");
            Require(psuControl.Details.ContainsKey("Safety") && hubControl.Details.ContainsKey("Safety"),
                "Every control row must carry its safety note.");

            // A hub that is running its own hardware profile has no channel map and therefore no
            // per-channel control rows; the hub-wide take-control entry is the only way in for a
            // machine without a marker file, so it must be there, be a Fan Control row the host will
            // list, and stay visible although it has no paired fan reading.
            var blockedRows = CorsairPlugIn.BuildRows(new CorsairSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                Status = string.Empty,
                Hubs = new List<HubSnapshot>
                {
                    new HubSnapshot { Serial = "blockedhub", FirmwareVersion = "3.12.650", HardwareModeBlocked = true, Channels = new List<HubChannelSnapshot>() }
                },
                Psus = new List<PsuSnapshot>()
            }, false, null);
            var takeControl = blockedRows.FirstOrDefault(row => string.Equals(row.Type, "Fan Control", StringComparison.OrdinalIgnoreCase));
            Require(takeControl != null
                && string.Equals(takeControl.Identifier, "corsair/link/blockedhub/control/" + CorsairLinkHubDevice.HubWideControlChannel.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                && takeControl.Details != null && takeControl.Details.ContainsKey("Zero RPM capable"),
                "A hardware-mode-blocked hub must expose its hub-wide take-control entry, or nothing could ever take it on a machine without a marker file.");
            Require(rows.Any(row => (row.Name ?? string.Empty).IndexOf("Hub 1", StringComparison.OrdinalIgnoreCase) >= 0),
                "The first of multiple hubs needs a neutral visible suffix.");
            Require(rows.Any(row => (row.Name ?? string.Empty).IndexOf("Hub 2", StringComparison.OrdinalIgnoreCase) >= 0),
                "The second of multiple hubs needs a neutral visible suffix.");
            Require(!rows.Any(row => VisibleText(row).IndexOf("PRIVATE-SERIAL", StringComparison.OrdinalIgnoreCase) >= 0),
                "Visible rows must not expose hub serial fragments.");
            Require(!rows.Any(row => row.Details != null && row.Details.Keys.Any(key => string.Equals(key, "Device id", StringComparison.OrdinalIgnoreCase))),
                "Ordinary Details must not expose raw Corsair device IDs.");
            Require(rows.Any(row => string.Equals(row.Name, "PSU output power", StringComparison.OrdinalIgnoreCase) && row.DisplayValue == "410 W"),
                "PSU power row generation changed.");

            var empty = CorsairPlugIn.BuildRows(new CorsairSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                Status = "No supported Corsair device was found.",
                Hubs = new List<HubSnapshot>(),
                Psus = new List<PsuSnapshot>()
            }, false, null);
            Require(empty.Count == 1 && empty[0].DisplayValue.IndexOf("No supported", StringComparison.OrdinalIgnoreCase) >= 0,
                "No-device status generation changed.");
        }

        private static HubSnapshot MakeHub(string serial, int channel, string name, string deviceId)
        {
            return new HubSnapshot
            {
                Serial = serial,
                FirmwareVersion = "2.5.1",
                Channels = new List<HubChannelSnapshot>
                {
                    new HubChannelSnapshot
                    {
                        Channel = channel,
                        DeviceName = name,
                        DeviceId = deviceId,
                        HasRpm = true,
                        HasTemp = true,
                        HasControl = true,
                        Rpm = 1100 + channel,
                        TemperatureC = 35.0f + channel
                    }
                }
            };
        }

        private static string VisibleText(SensorReading row)
        {
            return (row.Hardware ?? string.Empty) + "|" + (row.Name ?? string.Empty) + "|" + (row.DisplayValue ?? string.Empty);
        }

        // Regression test for host-driven plug-in manager rebuilds. Ordinary preference saves no
        // longer rebuild the manager, but changing the enabled plug-in set still does and the SDK
        // does not identify which plug-in caused it. A release must therefore be deferred, the
        // next host refresh must cancel it, and an elapsed grace period must still hand the devices
        // back so a genuinely disabled plug-in does not keep the hub in software mode.
        //
        // The worker is never started here, so nothing in this test reaches a device: what is
        // under test is the lifecycle decision itself.
        private static void TestDeferredTeardownAcrossReload()
        {
            var worker = CorsairWorker.Instance;

            worker.ArmDeferredTeardown(60000);
            Require(worker.IsTeardownDeferred, "Releasing the plug-in must defer the hardware hand-back rather than perform it.");
            Require(!worker.IsStopped, "A deferred hand-back must leave the worker running so fan control keeps applying.");
            Require(object.ReferenceEquals(worker, CorsairWorker.Instance),
                "A deferred hand-back must keep the same worker, so a reload never has to re-take software control.");
            Require(!worker.CommitDeferredTeardownIfDue(), "The hand-back must not run before its grace period has elapsed.");

            // The host loading the plug-in again -- what the very next GetReadings does.
            Require(worker.AdoptHostContact(), "A reload must find the worker still usable.");
            Require(!worker.IsTeardownDeferred, "The next host refresh must cancel the pending hand-back.");
            Require(!worker.IsStopped && object.ReferenceEquals(worker, CorsairWorker.Instance),
                "After a reload the plug-in must go on using the same running worker.");
            Require(!worker.CommitDeferredTeardownIfDue(), "A cancelled hand-back must never run later.");

            // The safety valve: a plug-in that really was disabled still gives the devices back.
            worker.ArmDeferredTeardown(0);
            Require(worker.CommitDeferredTeardownIfDue(), "An elapsed grace period must hand the Corsair devices back.");
            Require(worker.IsStopped, "The elapsed hand-back must stop the worker.");
            Require(!worker.AdoptHostContact(), "A worker that has handed its devices back must report itself finished.");
            Require(!object.ReferenceEquals(worker, CorsairWorker.Instance),
                "After the hand-back a re-enable must start from a fresh worker.");
        }

        // 2026-08-21 17:10:56 the machine resumed; a single HID read timed out one second later and
        // sub-device enumeration on the iCUE LINK hub failed, so its session did not come back. The
        // PSU session did, and the scan that had just failed to re-open the hub therefore counted "a
        // device is present" and scheduled its next attempt at the five-minute present-device
        // cadence. Recovery took 301 s, during which the loop ran the hub's own firmware curve --
        // safe, but not the user's curves -- and the plug-in published 6 rows instead of 36. A scan
        // that comes up short must retry at the absent-device cadence instead.
        //
        // Numbers rather than the constants on purpose: the constants are what is under test.
        private static void TestScanCadenceAfterSessionLoss()
        {
            Require(CorsairWorker.NextScanDelayMs(0, false, 0) == 30000,
                "With nothing connected the scan cadence must stay the brisk 30 s one.");
            Require(CorsairWorker.NextScanDelayMs(2, false, 0) == 300000,
                "With every device connected the scan must go back to the slow 5 min hot-plug watch.");

            Require(CorsairWorker.NextScanDelayMs(1, true, 1) == 30000,
                "A scan that could not re-open a device this process had must retry at the absent-device cadence, not inherit the five-minute present-device one; that inheritance is what took 301 s to get the hub back after a resume.");
            Require(CorsairWorker.NextScanDelayMs(1, true, 2) == 60000, "The second recovery scan must back off rather than repeat at 30 s.");
            Require(CorsairWorker.NextScanDelayMs(1, true, 3) == 120000, "The recovery backoff must keep doubling.");
            Require(CorsairWorker.NextScanDelayMs(1, true, 4) == 240000, "The recovery backoff must keep doubling.");
            Require(CorsairWorker.NextScanDelayMs(1, true, 5) == 300000,
                "A device that is not coming back must settle onto the slow watch rather than re-enumerating every 30 s for the life of the process.");
            Require(CorsairWorker.NextScanDelayMs(1, true, 500) == 300000, "The recovery backoff must be capped, not unbounded.");

            // Nothing here may ever schedule a scan for "now": the worker would re-enumerate HID in
            // a tight loop with the device guard held.
            Require(CorsairWorker.NextScanDelayMs(1, true, 0) >= 30000, "A recovery scan must never be scheduled immediately.");
        }

        private static void TestUnstartedLifecycle()
        {
            var first = CorsairWorker.Instance;
            new CorsairPlugIn().Shutdown();
            Require(first.IsStopped, "Releasing a worker that never started must finish immediately.");
            Require(!first.IsTeardownDeferred,
                "A worker that never started owns no device, so its release must not be deferred and leave a hand-back armed that nothing will ever run.");
            var second = CorsairWorker.Instance;
            Require(!object.ReferenceEquals(first, second), "Disabling before the first reading must leave a fresh worker available for re-enable.");
            second.StopAndRestore();
        }

        // The one test that goes through the real host entry point -- IPluginLifecycle.Shutdown --
        // with a worker that looks like it is running, which is the only state in which deferring
        // and restoring differ. TestDeferredTeardownAcrossReload above drives the state machine
        // directly, so it would still pass if Shutdown were wired back to StopAndRestore; this one
        // fails outright, which is the point. Still no hardware: the worker's thread object is a
        // stand-in that is never started, and its device lists are empty.
        //
        // Runs last on purpose. That stand-in never executes CleanupOnWorkerThread, so this worker
        // can never finish a hand-back and CorsairWorker.Instance keeps handing it out.
        private static void TestShutdownDefersWhileTheWorkerRuns()
        {
            var worker = CorsairWorker.Instance;
            worker.PretendWorkerRunningForTest();

            new CorsairPlugIn().Shutdown();

            Require(worker.IsTeardownDeferred,
                "IPluginLifecycle.Shutdown must arm a deferred hand-back while the worker is running, not restore the hardware -- restoring here is what made the fans spin up whenever Preferences was opened.");
            Require(!worker.IsStopped,
                "IPluginLifecycle.Shutdown must leave the worker running so a plug-in reload never interrupts fan control.");
            Require(object.ReferenceEquals(worker, CorsairWorker.Instance),
                "IPluginLifecycle.Shutdown must not make the worker replaceable, so the reload reuses the same device sessions.");

            Require(worker.AdoptHostContact(), "The reload after that Shutdown must find the worker still usable.");
            Require(!worker.IsTeardownDeferred, "The reload after that Shutdown must cancel the armed hand-back.");
        }

        private static void Require(bool condition, string message)
        {
            checks++;
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void RequireThrows<T>(Action action, string message) where T : Exception
        {
            checks++;
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
