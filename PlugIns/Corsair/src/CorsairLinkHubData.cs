using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// One row of the iCUE LINK known model/variant database (annex icuelink-protocol.md §6.2).
    /// Pure data, no behavior.
    /// </summary>
    public sealed class LinkKnownDevice
    {
        public byte Model;
        public byte Variant;
        public string Name;
        public bool IsPump;
        public bool HasTemp;
        public bool HasRpm;
        public bool HasControl;
    }

    /// <summary>
    /// The full model/variant lookup table from annex §6.2, plus the capability nuances called out
    /// in the task-4 brief: QX and RX MAX fans carry an in-fan temperature sensor (plain
    /// RX/LX/RX-RGB/RX-MAX-RGB fans do not); XC7 is a temperature-only water block (no RPM, no
    /// duty control); pumps for minimum-duty purposes are models 0x07 (H-series AIO), 0x11 (TITAN
    /// AIO), 0x0C (XD5), 0x19 (XD6). Names are the annex marketing names with color/finish
    /// qualifiers and generic category suffixes ("PSU", "CPU/GPU water block", "pump/res")
    /// stripped, e.g. both XD5 variants are "XD5" and both H150i finishes are "H150i" -- except
    /// model 0x11, whose table entries keep the "AIO" suffix ("TITAN AIO", "TITAN 360 RX RGB AIO")
    /// because TITAN has no bare model number to strip it down to; every other name below is
    /// suffix-free.
    /// </summary>
    public static class LinkKnownDevices
    {
        // Internal row shape supports a variant range so the 24-row annex table (some rows cover
        // several variant bytes, e.g. TITAN 0x00-0x04 and HXi SHIFT 0x00-0x02) can be represented
        // without hand-expanding every individual (model, variant) pair.
        private sealed class Row
        {
            public byte Model;
            public byte VariantMin;
            public byte VariantMax;
            public string Name;
            public bool IsPump;
            public bool HasTemp;
            public bool HasRpm;
            public bool HasControl;

            public Row(byte model, byte variantMin, byte variantMax, string name, bool isPump, bool hasTemp, bool hasRpm, bool hasControl)
            {
                Model = model;
                VariantMin = variantMin;
                VariantMax = variantMax;
                Name = name;
                IsPump = isPump;
                HasTemp = hasTemp;
                HasRpm = hasRpm;
                HasControl = hasControl;
            }
        }

        // Annex §6.2, 24 table rows, in document order.
        private static readonly Row[] Table = new Row[]
        {
            new Row(0x01, 0x00, 0x00, "QX Fan", false, true, true, true),
            new Row(0x02, 0x00, 0x00, "LX Fan", false, false, true, true),
            new Row(0x03, 0x00, 0x00, "RX MAX RGB Fan", false, false, true, true),
            new Row(0x04, 0x00, 0x00, "RX MAX Fan", false, true, true, true),
            new Row(0x07, 0x00, 0x00, "H100i", true, true, true, true),
            new Row(0x07, 0x01, 0x01, "H115i", true, true, true, true),
            new Row(0x07, 0x02, 0x02, "H150i", true, true, true, true),
            new Row(0x07, 0x03, 0x03, "H170i", true, true, true, true),
            new Row(0x07, 0x04, 0x04, "H100i", true, true, true, true),
            new Row(0x07, 0x05, 0x05, "H150i", true, true, true, true),
            new Row(0x09, 0x00, 0x00, "XC7", false, true, false, false),
            new Row(0x09, 0x01, 0x01, "XC7", false, true, false, false),
            new Row(0x0A, 0x00, 0x00, "XG3", false, true, true, true),
            new Row(0x0B, 0x00, 0x02, "HXi SHIFT", false, true, true, true),
            new Row(0x0C, 0x00, 0x00, "XD5", true, true, true, true),
            new Row(0x0C, 0x01, 0x01, "XD5", true, true, true, true),
            new Row(0x0F, 0x00, 0x00, "RX RGB Fan", false, false, true, true),
            new Row(0x10, 0x00, 0x00, "VRM Fan CapSwap Module", false, false, true, true),
            new Row(0x11, 0x00, 0x04, "TITAN AIO", true, true, true, true),
            new Row(0x11, 0x05, 0x05, "TITAN 360 RX RGB AIO", true, true, true, true),
            new Row(0x13, 0x00, 0x00, "RX Fan", false, false, true, true),
            new Row(0x19, 0x00, 0x00, "XD6", true, true, true, true),
            new Row(0x19, 0x01, 0x01, "XD6", true, true, true, true),
            new Row(0x1B, 0x00, 0x00, "COMMANDER DUO", false, true, true, true),
        };

        // Returns null for unknown (model, variant) pairs -- e.g. observed-in-the-wild model 0x0E,
        // per annex §6/§11: unknown models are logged and the channel ignored, not treated as fatal.
        public static LinkKnownDevice Find(byte model, byte variant)
        {
            for (var i = 0; i < Table.Length; i++)
            {
                var row = Table[i];
                if (row.Model == model && variant >= row.VariantMin && variant <= row.VariantMax)
                {
                    return new LinkKnownDevice
                    {
                        Model = model,
                        Variant = variant,
                        Name = row.Name,
                        IsPump = row.IsPump,
                        HasTemp = row.HasTemp,
                        HasRpm = row.HasRpm,
                        HasControl = row.HasControl
                    };
                }
            }

            return null;
        }
    }

    /// <summary>One enumerated sub-device channel (annex §6.1).</summary>
    public sealed class LinkSubDevice
    {
        public int Channel;
        public byte Model;
        public byte Variant;
        public string DeviceId;
    }

    /// <summary>One sensor (speed or temperature) record (annex §7).</summary>
    public sealed class LinkSensorRecord
    {
        public int Channel;
        public bool Available;
        public short RawValue;
    }

    /// <summary>
    /// Pure parsing/building functions and wire constants for the Corsair iCUE LINK hub protocol
    /// (annex icuelink-protocol.md). No I/O, no device access -- every function here operates on
    /// byte buffers already in hand. All parse offsets are the annex's *raw* offsets: report id at
    /// index 0 is always included, matching the raw HID input reports the transport layer hands
    /// back.
    ///
    /// Every parse function is defensive: a buffer that ends before a value can be fully read stops
    /// parsing at that point and returns whatever was already parsed, rather than throwing. This is
    /// required both for genuinely truncated data and for the documented firmware quirk where a
    /// two-report enumeration payload can split a record across the report boundary (annex §11.2).
    /// </summary>
    public static class LinkHubData
    {
        // ---- Command bytes (annex §4.2) --------------------------------------------------------

        public static readonly byte[] EnterSoftwareMode = new byte[] { 0x01, 0x03, 0x00, 0x02 };
        public static readonly byte[] EnterHardwareMode = new byte[] { 0x01, 0x03, 0x00, 0x01 };
        public static readonly byte[] ReadFirmwareVersion = new byte[] { 0x02, 0x13 };
        public static readonly byte[] OpenEndpoint = new byte[] { 0x0D, 0x01 };
        public static readonly byte[] CloseEndpoint = new byte[] { 0x05, 0x01, 0x01 };
        public static readonly byte[] ReadEndpoint = new byte[] { 0x08, 0x01 };
        public static readonly byte[] WriteEndpoint = new byte[] { 0x06, 0x01 };

        // ---- Endpoint addresses (annex §4.3) ---------------------------------------------------

        public const byte EndpointSpeeds = 0x17;
        public const byte EndpointTemperatures = 0x21;
        public const byte EndpointDutyWrite = 0x18;
        public const byte EndpointSubDevices = 0x36;

        // ---- Data types (annex §4.3) ------------------------------------------------------------

        public static readonly byte[] DataTypeSpeeds = new byte[] { 0x25, 0x00 };
        public static readonly byte[] DataTypeTemperatures = new byte[] { 0x10, 0x00 };
        public static readonly byte[] DataTypeDuty = new byte[] { 0x07, 0x00 };
        public static readonly byte[] DataTypeSubDevices = new byte[] { 0x21, 0x00 };

        // ---- Response status (annex §4.5) --------------------------------------------------------

        public const byte StatusOk = 0x00;
        public const byte StatusWrongMode = 0x03;

        // A response too short to contain a status byte cannot be OK or WrongMode; report it as a
        // generic non-zero error rather than fabricating a value that collides with a real status.
        private const byte StatusMalformed = 0xFF;

        // ---- Firmware version (annex §5) ---------------------------------------------------------

        // raw[5] = major, raw[6] = minor, raw[7..8] little-endian 16-bit patch -> "major.minor.patch".
        public static string ParseFirmwareVersion(byte[] raw)
        {
            if (raw == null || raw.Length < 9)
            {
                return string.Empty;
            }

            var major = raw[5];
            var minor = raw[6];
            var patch = (int)raw[7] | ((int)raw[8] << 8);

            return major.ToString(CultureInfo.InvariantCulture) + "."
                + minor.ToString(CultureInfo.InvariantCulture) + "."
                + patch.ToString(CultureInfo.InvariantCulture);
        }

        // ---- Sensor reads (annex §7) --------------------------------------------------------------

        // raw[7] = record count N, N x 3-byte records from raw[8]: byte0 = status (0x00 available,
        // anything else absent), bytes 1-2 = little-endian signed 16-bit value.
        public static List<LinkSensorRecord> ParseSensorRecords(byte[] raw)
        {
            var records = new List<LinkSensorRecord>();
            if (raw == null || raw.Length < 8)
            {
                return records;
            }

            var count = raw[7];
            for (var i = 0; i < count; i++)
            {
                var offset = 8 + (i * 3);
                if (offset + 2 >= raw.Length)
                {
                    // Buffer ends mid-record: stop and return what was already parsed.
                    break;
                }

                var available = raw[offset] == 0x00;
                var rawValue = (short)((int)raw[offset + 1] | ((int)raw[offset + 2] << 8));

                records.Add(new LinkSensorRecord
                {
                    Channel = i,
                    Available = available,
                    RawValue = available ? rawValue : (short)0
                });
            }

            return records;
        }

        // ---- Sub-device enumeration (annex §6) -----------------------------------------------------

        // Concatenates the usable stream (rawFirst from offset 7, rawContinuation from offset 5, per
        // annex §6) and walks it: stream[0] = last channel index, then one record per channel
        // 1..lastChannel. rawContinuation may be null (single-read firmware / no second report).
        // Tolerant of truncation at any point (short arrays, or a record split across the two
        // buffers) -- stops and returns whatever devices were fully parsed rather than throwing.
        public static List<LinkSubDevice> ParseSubDevices(byte[] rawFirst, byte[] rawContinuation)
        {
            var devices = new List<LinkSubDevice>();

            var firstPart = (rawFirst != null && rawFirst.Length > 7)
                ? SubArray(rawFirst, 7, rawFirst.Length - 7)
                : new byte[0];
            var continuationPart = (rawContinuation != null && rawContinuation.Length > 5)
                ? SubArray(rawContinuation, 5, rawContinuation.Length - 5)
                : new byte[0];

            var stream = new byte[firstPart.Length + continuationPart.Length];
            Array.Copy(firstPart, 0, stream, 0, firstPart.Length);
            Array.Copy(continuationPart, 0, stream, firstPart.Length, continuationPart.Length);

            if (stream.Length < 1)
            {
                return devices;
            }

            var lastChannel = stream[0];
            var pos = 1;

            for (var channel = 1; channel <= lastChannel; channel++)
            {
                // Record header is 8 bytes: [0-1] reserved, [2] model, [3] variant, [4-6] reserved,
                // [7] device-id length.
                if (pos + 8 > stream.Length)
                {
                    break;
                }

                var model = stream[pos + 2];
                var variant = stream[pos + 3];
                var idLength = stream[pos + 7];

                if (idLength == 0)
                {
                    // Empty channel: record is exactly 8 bytes, no device.
                    pos += 8;
                    continue;
                }

                var recordLength = 8 + idLength;
                if (pos + recordLength > stream.Length)
                {
                    // Device-id bytes are cut off (truncated stream or unrecovered split). Stop here
                    // rather than fabricate a partial id.
                    break;
                }

                var deviceId = DecodeNulTrimmedAscii(stream, pos + 8, idLength);

                devices.Add(new LinkSubDevice
                {
                    Channel = channel,
                    Model = model,
                    Variant = variant,
                    DeviceId = deviceId
                });

                pos += recordLength;
            }

            return devices;
        }

        private static string DecodeNulTrimmedAscii(byte[] buffer, int offset, int length)
        {
            var builder = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                var b = buffer[offset + i];
                if (b == 0x00)
                {
                    break;
                }
                builder.Append((char)b);
            }
            return builder.ToString();
        }

        // ---- Command / write-block builders (annex §4.1, §4.4, §8) --------------------------------

        // [0]=0x00 report id, [1]=0x00, [2]=0x01 frame marker, command bytes at [3], then data, then
        // zero padding to outLength.
        public static byte[] BuildCommandPacket(int outLength, byte[] command, byte[] data)
        {
            var packet = new byte[outLength];
            packet[0] = 0x00;
            packet[1] = 0x00;
            packet[2] = 0x01;

            var offset = 3;
            if (command != null)
            {
                Array.Copy(command, 0, packet, offset, command.Length);
                offset += command.Length;
            }

            if (data != null)
            {
                Array.Copy(data, 0, packet, offset, data.Length);
            }

            return packet;
        }

        // [0-1] little-endian length = inner data length + 2, [2-3] = 00 00, [4-5] = data type,
        // [6..] = inner data.
        public static byte[] BuildWriteBlock(byte[] dataType, byte[] innerData)
        {
            var inner = innerData ?? new byte[0];
            var length = inner.Length + 2;

            var block = new byte[6 + inner.Length];
            block[0] = (byte)(length & 0xFF);
            block[1] = (byte)((length >> 8) & 0xFF);
            block[2] = 0x00;
            block[3] = 0x00;
            block[4] = (dataType != null && dataType.Length > 0) ? dataType[0] : (byte)0x00;
            block[5] = (dataType != null && dataType.Length > 1) ? dataType[1] : (byte)0x00;

            Array.Copy(inner, 0, block, 6, inner.Length);
            return block;
        }

        // [0] = channel count K, then K x 4-byte entries [channel, 0x00, percent, 0x00], sorted by
        // ascending channel (the annex's worked examples and wire behavior list channels in order).
        public static byte[] BuildDutyInnerData(List<KeyValuePair<int, int>> channelPercents)
        {
            var entries = new List<KeyValuePair<int, int>>();
            if (channelPercents != null)
            {
                entries.AddRange(channelPercents);
            }

            entries.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            var data = new byte[1 + (entries.Count * 4)];
            data[0] = (byte)entries.Count;

            var offset = 1;
            for (var i = 0; i < entries.Count; i++)
            {
                data[offset] = (byte)entries[i].Key;
                data[offset + 1] = 0x00;
                data[offset + 2] = (byte)entries[i].Value;
                data[offset + 3] = 0x00;
                offset += 4;
            }

            return data;
        }

        // ---- Response helpers (annex §4.5) ---------------------------------------------------------

        public static byte ResponseStatus(byte[] raw)
        {
            if (raw == null || raw.Length <= 4)
            {
                return StatusMalformed;
            }

            return raw[4];
        }

        public static bool ResponseTypeMatches(byte[] raw, byte[] dataType)
        {
            if (raw == null || raw.Length < 7 || dataType == null || dataType.Length < 2)
            {
                return false;
            }

            return raw[5] == dataType[0] && raw[6] == dataType[1];
        }

        private static byte[] SubArray(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }
    }
}
