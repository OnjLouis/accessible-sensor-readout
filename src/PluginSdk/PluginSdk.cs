using System;
using System.Collections.Generic;

namespace SensorReadout.PluginSdk
{
    public interface ISensorReadoutPlugin
    {
        PluginInfo Info { get; }
        IEnumerable<SensorReading> GetReadings(IPluginContext context);
    }

    public interface IFanControllablePlugin
    {
        bool TrySetFanPercent(string identifier, int percent);
        bool TryResetFan(string identifier);
    }

    public interface IPluginContext
    {
        MachineInfo Machine { get; }
        string PluginDirectory { get; }
        void Log(string level, string message);
    }

    public sealed class PluginInfo
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Description = "";
    }

    public sealed class MachineInfo
    {
        public string Manufacturer = "";
        public string Model = "";
    }

    public sealed class SensorReading
    {
        public string Type = "";
        public string Hardware = "";
        public string Name = "";
        public string Identifier = "";
        public float? Value;
        public string DisplayValue = "";
        public string Source = "";
        public Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
