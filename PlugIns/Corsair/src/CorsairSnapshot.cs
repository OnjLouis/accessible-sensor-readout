using System;
using System.Collections.Generic;

namespace SensorReadout.CorsairPlugIn
{
    // The data the plug-in's row model is built from: a flat, immutable-by-convention picture of
    // every Corsair device as of one completed worker tick.
    //
    // These types exist because the live device objects are not safe to read from another thread --
    // their channel list is mutable and a control call can be rewriting it at any moment. So
    // CorsairWorker builds one of these on its own thread while it holds the device lock, publishes
    // it, and hands out a deep clone to every caller. Nothing outside the worker ever holds a
    // reference into a device.
    //
    // Plain fields on purpose (matching CorsairHidDeviceInfo and LinkChannelState): callers treat
    // them as data, and a clone is cheap enough that nobody needs to mutate one in place.

    /// <summary>
    /// One iCUE LINK hub channel: which device is on it, its readings, and the duty this plug-in
    /// has asked for. <see cref="RequestedPercent"/> only describes the hardware while the owning
    /// hub's <see cref="HubSnapshot.OwnsSoftwareControl"/> is true.
    /// </summary>
    public sealed class HubChannelSnapshot
    {
        public int Channel;
        public string DeviceName;
        public string DeviceId;
        public bool IsPump;
        public bool HasRpm;
        public bool HasTemp;
        public bool HasControl;
        public int? Rpm;
        public float? TemperatureC;
        public int RequestedPercent;
        public bool PercentIsDefault;

        // Diagnostics only: the raw enumeration bytes.
        // DeviceName is a friendly name that does not round-trip to these -- e.g. "H100i" is
        // model 0x07 with variant 0x00 *or* 0x04 -- and a support bundle needs the real values.
        public byte ModelCode;
        public byte VariantCode;

        public HubChannelSnapshot Clone()
        {
            var clone = new HubChannelSnapshot();
            clone.Channel = Channel;
            clone.DeviceName = DeviceName;
            clone.DeviceId = DeviceId;
            clone.IsPump = IsPump;
            clone.HasRpm = HasRpm;
            clone.HasTemp = HasTemp;
            clone.HasControl = HasControl;
            clone.Rpm = Rpm;
            clone.TemperatureC = TemperatureC;
            clone.RequestedPercent = RequestedPercent;
            clone.PercentIsDefault = PercentIsDefault;
            clone.ModelCode = ModelCode;
            clone.VariantCode = VariantCode;
            return clone;
        }
    }

    /// <summary>
    /// One iCUE LINK hub. <see cref="WrongModeReadFailure"/> means the hub answered the sensor
    /// reads with "hardware mode", i.e. another program is driving it -- readings are unavailable
    /// but nothing is broken.
    /// </summary>
    public sealed class HubSnapshot
    {
        public string Serial;
        public string FirmwareVersion;
        public bool OwnsSoftwareControl;
        public bool WrongModeReadFailure;
        public List<HubChannelSnapshot> Channels;

        // Diagnostics only, not part of the row model: an outstanding duty write that has not
        // reached the hub yet, and the last non-zero response status (0x03 = hardware mode).
        public bool DutiesPending;
        public byte LastStatusByte;

        // True once this hub has failed enough reads in a row that CorsairWorker.NoteDeviceResult
        // has backed it off to the slow retry interval -- i.e. the readings below may be stale.
        public bool BackedOff;

        public HubSnapshot Clone()
        {
            var clone = new HubSnapshot();
            clone.Serial = Serial;
            clone.FirmwareVersion = FirmwareVersion;
            clone.OwnsSoftwareControl = OwnsSoftwareControl;
            clone.WrongModeReadFailure = WrongModeReadFailure;
            clone.DutiesPending = DutiesPending;
            clone.LastStatusByte = LastStatusByte;
            clone.BackedOff = BackedOff;
            clone.Channels = new List<HubChannelSnapshot>();
            if (Channels != null)
            {
                for (var i = 0; i < Channels.Count; i++)
                {
                    clone.Channels.Add(Channels[i].Clone());
                }
            }

            return clone;
        }
    }

    /// <summary>
    /// One Corsair HXi/RMi power supply. <see cref="InputVoltage"/> and <see cref="OutputPowerW"/>
    /// are best-effort extras and stay null on models that do not implement them.
    /// <see cref="RequestedPercent"/> is -1 whenever the fan is under the PSU's own control.
    /// </summary>
    public sealed class PsuSnapshot
    {
        public string ModelName;
        public string PidHex;
        public float? Temperature1C;
        public float? Temperature2C;
        public int? FanRpm;
        public bool FanIsManual;
        public float? InputVoltage;
        public float? OutputPowerW;
        public int RequestedPercent;

        // True once this PSU has failed enough reads in a row that CorsairWorker.NoteDeviceResult
        // has backed it off to the slow retry interval -- i.e. the readings below may be stale.
        public bool BackedOff;

        public PsuSnapshot Clone()
        {
            var clone = new PsuSnapshot();
            clone.ModelName = ModelName;
            clone.PidHex = PidHex;
            clone.Temperature1C = Temperature1C;
            clone.Temperature2C = Temperature2C;
            clone.FanRpm = FanRpm;
            clone.FanIsManual = FanIsManual;
            clone.InputVoltage = InputVoltage;
            clone.OutputPowerW = OutputPowerW;
            clone.RequestedPercent = RequestedPercent;
            clone.BackedOff = BackedOff;
            return clone;
        }
    }

    /// <summary>
    /// Everything the plug-in needs to build its rows, captured atomically. <see cref="Status"/> is
    /// empty when at least one device is present and carries a human explanation otherwise.
    /// </summary>
    public sealed class CorsairSnapshot
    {
        public DateTime CapturedUtc;
        public string Status;
        public List<HubSnapshot> Hubs;
        public List<PsuSnapshot> Psus;

        public CorsairSnapshot Clone()
        {
            var clone = new CorsairSnapshot();
            clone.CapturedUtc = CapturedUtc;
            clone.Status = Status;
            clone.Hubs = new List<HubSnapshot>();
            clone.Psus = new List<PsuSnapshot>();

            if (Hubs != null)
            {
                for (var i = 0; i < Hubs.Count; i++)
                {
                    clone.Hubs.Add(Hubs[i].Clone());
                }
            }

            if (Psus != null)
            {
                for (var i = 0; i < Psus.Count; i++)
                {
                    clone.Psus.Add(Psus[i].Clone());
                }
            }

            return clone;
        }
    }
}
