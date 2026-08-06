using System;
using System.Collections.Generic;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    public sealed partial class CorsairPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.corsair.experimental",
            Name = "Corsair iCUE Link and PSU Support (experimental)",
            Version = "0.1.0",
            Author = "Robin Kipp, Claude Code, and Sensor Readout contributors",
            Description = "Experimental, opt-in support for Corsair iCUE LINK Hub cooling devices and Corsair HXi/RMi digital power supplies."
        };

        public PluginInfo Info
        {
            get { return info; }
        }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            var rows = new List<SensorReading>();
            rows.Add(new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Corsair Plug-In",
                Identifier = "corsair/status",
                DisplayValue = "Corsair support is starting up",
                Source = "Corsair Support Plug-In"
            });
            return rows;
        }
    }
}
