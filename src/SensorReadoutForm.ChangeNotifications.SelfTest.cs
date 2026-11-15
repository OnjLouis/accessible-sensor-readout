using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private static void SelfTestSensorChangeRefresh()
    {
        var cpu = new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Value = 20 };
        var removed = new SensorRow { Type = "SMART", Hardware = "Removed", Name = "Health", ChangeSection = SensorChange.Storage };
        var battery = new SensorRow { Type = "Battery", Hardware = "Internal", Name = "Charge", ChangeSection = SensorChange.Power };
        var disconnectedWifi = new SensorRow { Type = "Network", Hardware = "Wi-Fi", Name = "Status", ChangeSection = SensorChange.Network };
        var publicIp = new SensorRow { Type = "Network", Hardware = "Internet", Name = "Public IP address" };
        var sources = new SensorRow { Type = "Performance", Identifier = "data-source/old" };
        var spoken = new SensorRow { Identifier = "spoken-hotkeys|sample|old" };
        var freshDisk = new SensorRow { Type = "SMART", Hardware = "New", Name = "Health", ChangeSection = SensorChange.Storage };
        var freshPower = new SensorRow { Type = "Battery", Hardware = "Internal", Name = "Charge", ChangeSection = SensorChange.Power };
        var merged = MergeChangedSensorRows(new[] { cpu, removed, battery, disconnectedWifi, publicIp, sources, spoken },
            SensorChange.Storage | SensorChange.Power, new List<SensorRow> { freshDisk, freshPower, CloneSensorRow(freshDisk) });
        Require(merged.SequenceEqual(new[] { cpu, disconnectedWifi, publicIp, freshDisk, freshPower }), "Targeted refresh did not replace only its selected sources.");
        var withoutWifi = MergeChangedSensorRows(merged, SensorChange.Network, new List<SensorRow>());
        Require(withoutWifi.Contains(publicIp) && !withoutWifi.Contains(disconnectedWifi), "Removing an adapter must not remove independent Internet readings.");
        Require(CloneSensorRow(freshPower).ChangeSection == SensorChange.Power, "A snapshot clone lost refresh metadata.");
        Require(!JsonConvert.SerializeObject(freshPower).Contains("ChangeSection"), "Local refresh metadata leaked into serialized readings.");
        Require(!CanRefreshSensorChanges(false, false, false, true) &&
            !CanRefreshSensorChanges(true, true, false, true) &&
            !CanRefreshSensorChanges(true, false, true, false) &&
            CanRefreshSensorChanges(true, false, false, false), "Targeted refresh bypassed pause, report view, or focused refresh preferences.");
        SelfTestVolumeChangeNotification();
    }

    private static void SelfTestVolumeChangeNotification()
    {
        const int deviceChange = 0x0219, arrival = 0x8000, removal = 0x8004, headerBytes = 12;
        var header = Marshal.AllocHGlobal(headerBytes);
        try
        {
            Marshal.WriteInt32(header, headerBytes);
            Marshal.WriteInt32(header, sizeof(int), 2);
            var message = Message.Create(IntPtr.Zero, deviceChange, (IntPtr)arrival, header);
            Require(DiskInterfaceNotifications.IsVolumeChange(message), "Volume mount must refresh storage inventory.");
            message.WParam = (IntPtr)removal;
            Require(DiskInterfaceNotifications.IsVolumeChange(message), "Volume removal must refresh storage inventory.");
            Marshal.WriteInt32(header, sizeof(int), 3);
            Require(!DiskInterfaceNotifications.IsVolumeChange(message), "An unrelated USB port notification must not refresh storage inventory.");
        }
        finally { Marshal.FreeHGlobal(header); }
    }
}
