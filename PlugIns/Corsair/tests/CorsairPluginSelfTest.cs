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
                TestUnstartedLifecycle();
                Console.WriteLine("Corsair plug-in self-test passed: " + checks + " checks.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Corsair plug-in self-test failed: " + ex);
                return 1;
            }
        }

        private static void TestWriteBoundary()
        {
            var worker = CorsairWorker.Instance;
            Require(!worker.SetHubChannelPercent("TEST", 1, 50), "Hub fan writes must fail closed in the read-only release.");
            Require(!worker.ResetHubChannel("TEST", 1), "Hub fan resets must fail closed in the read-only release.");
            Require(!worker.SetPsuFanPercent("1c27", 50), "PSU fan writes must fail closed in the read-only release.");
            Require(!worker.ResetPsuFan("1c27"), "PSU fan resets must fail closed in the read-only release.");
        }

        private static void TestReleaseSurface()
        {
            var plugIn = new CorsairPlugIn();
            Require(plugIn is ISensorReadoutPlugin, "The Corsair plug-in must implement the sensor interface.");
            Require(plugIn is IPluginLifecycle, "The Corsair plug-in must implement explicit shutdown.");
            Require(!typeof(IFanControllablePlugin).IsAssignableFrom(plugIn.GetType()), "The public Corsair release must remain read-only.");
            Require(plugIn.Info != null && plugIn.Info.Description.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0,
                "The plug-in metadata must state that this release is read-only.");
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
            Require(!rows.Any(row => string.Equals(row.Type, "Fan Control", StringComparison.OrdinalIgnoreCase)),
                "The read-only release must not expose fan controls.");
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

        private static void TestUnstartedLifecycle()
        {
            var first = CorsairWorker.Instance;
            new CorsairPlugIn().Shutdown();
            var second = CorsairWorker.Instance;
            Require(!object.ReferenceEquals(first, second), "Disabling before the first reading must leave a fresh worker available for re-enable.");
            second.StopAndRestore();
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
