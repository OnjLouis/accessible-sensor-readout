using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

public sealed partial class SensorReadoutForm : Form
{
    private IEnumerable<SensorRow> GetBatteryRows()
    {
        var rows = new List<SensorRow>();
        var batteries = GetNativeBatteryInfo();
        if (batteries.Count == 0)
        {
            return rows;
        }

        var wmiInfo = GetWmiBatteryInfo();
        for (var i = 0; i < batteries.Count; i++)
        {
            var battery = batteries[i];
            var hardware = string.IsNullOrWhiteSpace(battery.Name) ? "Battery " + (i + 1) : battery.Name;
            WmiBatteryInfo wmi;
            if (!wmiInfo.TryGetValue(i, out wmi))
            {
                wmi = null;
            }

            var percent = GetBatteryPercent(battery, wmi);
            if (percent.HasValue)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Charge",
                    Identifier = "battery/" + i + "/charge",
                    Value = (float)percent.Value,
                    DisplayValue = FormatNumber(Math.Round(percent.Value, 1), "0.0") + "%",
                    Source = "Windows Battery"
                });
            }

            var status = BuildBatteryStatusText(battery, wmi);
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Status",
                    Identifier = "battery/" + i + "/status",
                    DisplayValue = status,
                    Source = "Windows Battery"
                });
            }

            AddBatteryCapacityRows(rows, battery, hardware, i);
            if (battery.CycleCount > 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Cycle count",
                    Identifier = "battery/" + i + "/cycle-count",
                    Value = battery.CycleCount,
                    DisplayValue = battery.CycleCount.ToString(),
                    Source = "Windows Battery"
                });
            }

            if (battery.VoltageMillivolts > 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Voltage",
                    Identifier = "battery/" + i + "/voltage",
                    Value = battery.VoltageMillivolts / 1000f,
                    DisplayValue = FormatNumber(Math.Round(battery.VoltageMillivolts / 1000.0, 2), "0.00") + " V",
                    Source = "Windows Battery"
                });
            }

            if (battery.RateMilliwatts != int.MinValue && battery.RateMilliwatts != 0)
            {
                rows.Add(new SensorRow
                {
                    Type = "Battery",
                    Hardware = hardware,
                    Name = "Power rate",
                    Identifier = "battery/" + i + "/power-rate",
                    Value = battery.RateMilliwatts / 1000f,
                    DisplayValue = FormatNumber(Math.Round(battery.RateMilliwatts / 1000.0, 2), "0.00") + " W",
                    Source = "Windows Battery"
                });
            }
        }

        return rows;
    }

    private static double? GetBatteryPercent(NativeBatteryInfo battery, WmiBatteryInfo wmi)
    {
        if (wmi != null && wmi.EstimatedChargeRemaining >= 0 && wmi.EstimatedChargeRemaining <= 100)
        {
            return wmi.EstimatedChargeRemaining;
        }

        if (battery.FullChargeCapacity > 0 && battery.CurrentCapacity >= 0)
        {
            return Math.Max(0, Math.Min(100, battery.CurrentCapacity * 100.0 / battery.FullChargeCapacity));
        }

        return null;
    }

    private static void AddBatteryCapacityRows(List<SensorRow> rows, NativeBatteryInfo battery, string hardware, int index)
    {
        if (battery.CurrentCapacity >= 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Current capacity", "current-capacity", battery.CurrentCapacity));
        }

        if (battery.FullChargeCapacity > 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Full charge capacity", "full-charge-capacity", battery.FullChargeCapacity));
        }

        if (battery.DesignedCapacity > 0)
        {
            rows.Add(CreateBatteryCapacityRow(hardware, index, "Design capacity", "design-capacity", battery.DesignedCapacity));
        }

        if (battery.FullChargeCapacity > 0 && battery.DesignedCapacity > 0)
        {
            var health = Math.Max(0, Math.Min(100, battery.FullChargeCapacity * 100.0 / battery.DesignedCapacity));
            rows.Add(new SensorRow
            {
                Type = "Battery",
                Hardware = hardware,
                Name = "Battery health",
                Identifier = "battery/" + index + "/health",
                Value = (float)health,
                DisplayValue = FormatNumber(Math.Round(health, 1), "0.0") + "%",
                Source = "Windows Battery"
            });
        }
    }

    private static SensorRow CreateBatteryCapacityRow(string hardware, int index, string name, string id, int milliwattHours)
    {
        return new SensorRow
        {
            Type = "Battery",
            Hardware = hardware,
            Name = name,
            Identifier = "battery/" + index + "/" + id,
            Value = milliwattHours,
            DisplayValue = FormatNumber(milliwattHours, "0") + " mWh",
            Source = "Windows Battery"
        };
    }

    private static string BuildBatteryStatusText(NativeBatteryInfo battery, WmiBatteryInfo wmi)
    {
        var parts = new List<string>();
        if (battery.PowerState.HasFlag(BatteryPowerState.Charging))
        {
            parts.Add("Charging");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Discharging))
        {
            parts.Add("Discharging");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Online))
        {
            parts.Add("AC connected");
        }
        if (battery.PowerState.HasFlag(BatteryPowerState.Critical))
        {
            parts.Add("Critical");
        }

        if (parts.Count == 0 && wmi != null && !string.IsNullOrWhiteSpace(wmi.Status))
        {
            parts.Add(wmi.Status);
        }

        return string.Join(", ", parts.ToArray());
    }

    private static List<NativeBatteryInfo> GetNativeBatteryInfo()
    {
        var result = new List<NativeBatteryInfo>();
        var deviceInfoSet = IntPtr.Zero;
        try
        {
            var batteryGuid = BatteryDeviceInterfaceGuid;
            deviceInfoSet = BatterySetupDiGetClassDevs(ref batteryGuid, null, IntPtr.Zero, DeviceGetClassFlags.Present | DeviceGetClassFlags.DeviceInterface);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
            {
                return result;
            }

            for (uint index = 0; ; index++)
            {
                var interfaceData = new DeviceInterfaceData();
                interfaceData.Size = Marshal.SizeOf(typeof(DeviceInterfaceData));
                if (!BatterySetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref batteryGuid, index, ref interfaceData))
                {
                    break;
                }

                string devicePath;
                if (!TryGetBatteryDevicePath(deviceInfoSet, interfaceData, out devicePath))
                {
                    continue;
                }

                NativeBatteryInfo battery;
                if (TryReadNativeBattery(devicePath, (int)index, out battery))
                {
                    result.Add(battery);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (deviceInfoSet != IntPtr.Zero && deviceInfoSet.ToInt64() != -1)
            {
                BatterySetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        return result;
    }

    private static bool TryGetBatteryDevicePath(IntPtr deviceInfoSet, DeviceInterfaceData interfaceData, out string path)
    {
        path = "";
        var detailData = new DeviceInterfaceDetailData();
        detailData.Size = IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize;
        uint requiredSize;
        if (!BatterySetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, ref detailData, 1024, out requiredSize, IntPtr.Zero))
        {
            return false;
        }

        path = detailData.DevicePath;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryReadNativeBattery(string devicePath, int index, out NativeBatteryInfo info)
    {
        info = null;
        using (var handle = BatteryCreateFile(devicePath, FileAccess.Read, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero))
        {
            if (handle == null || handle.IsInvalid)
            {
                return false;
            }

            uint tag = 0;
            uint bytesReturned;
            uint zero = 0;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryTag, ref zero, 0, ref tag, (uint)Marshal.SizeOf(typeof(uint)), out bytesReturned, IntPtr.Zero) || tag == 0)
            {
                return false;
            }

            var batteryInfo = new BatteryInformationNative();
            if (!TryQueryBatteryInformation(handle, tag, BatteryQueryInformationLevel.BatteryInformation, ref batteryInfo))
            {
                return false;
            }

            var status = new BatteryStatusNative();
            TryQueryBatteryStatus(handle, tag, ref status);

            var name = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryDeviceName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryManufactureName);
            }

            info = new NativeBatteryInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Battery " + (index + 1) : name.Trim(),
                DesignedCapacity = batteryInfo.DesignedCapacity,
                FullChargeCapacity = batteryInfo.FullChargedCapacity,
                CycleCount = batteryInfo.CycleCount,
                PowerState = status.PowerState,
                CurrentCapacity = status.Capacity,
                VoltageMillivolts = status.Voltage,
                RateMilliwatts = status.Rate
            };
            return true;
        }
    }

    private static bool TryQueryBatteryInformation<T>(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level, ref T value) where T : struct
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var querySize = Marshal.SizeOf(typeof(BatteryQueryInformation));
        var valueSize = Marshal.SizeOf(typeof(T));
        var queryPointer = Marshal.AllocHGlobal(querySize);
        var valuePointer = Marshal.AllocHGlobal(valueSize);
        try
        {
            Marshal.StructureToPtr(query, queryPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryInformation, queryPointer, (uint)querySize, valuePointer, (uint)valueSize, out bytesReturned, IntPtr.Zero))
            {
                return false;
            }

            value = (T)Marshal.PtrToStructure(valuePointer, typeof(T));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(queryPointer);
            Marshal.FreeHGlobal(valuePointer);
        }
    }

    private static string QueryBatteryString(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var querySize = Marshal.SizeOf(typeof(BatteryQueryInformation));
        var queryPointer = Marshal.AllocHGlobal(querySize);
        var valuePointer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.StructureToPtr(query, queryPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryInformation, queryPointer, (uint)querySize, valuePointer, 512, out bytesReturned, IntPtr.Zero))
            {
                return "";
            }

            return Marshal.PtrToStringUni(valuePointer) ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(queryPointer);
            Marshal.FreeHGlobal(valuePointer);
        }
    }

    private static bool TryQueryBatteryStatus(SafeFileHandle handle, uint tag, ref BatteryStatusNative status)
    {
        var waitStatus = new BatteryWaitStatus { BatteryTag = tag };
        var waitSize = Marshal.SizeOf(typeof(BatteryWaitStatus));
        var statusSize = Marshal.SizeOf(typeof(BatteryStatusNative));
        var waitPointer = Marshal.AllocHGlobal(waitSize);
        var statusPointer = Marshal.AllocHGlobal(statusSize);
        try
        {
            Marshal.StructureToPtr(waitStatus, waitPointer, false);
            uint bytesReturned;
            if (!BatteryDeviceIoControl(handle, IoctlBatteryQueryStatus, waitPointer, (uint)waitSize, statusPointer, (uint)statusSize, out bytesReturned, IntPtr.Zero))
            {
                return false;
            }

            status = (BatteryStatusNative)Marshal.PtrToStructure(statusPointer, typeof(BatteryStatusNative));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(waitPointer);
            Marshal.FreeHGlobal(statusPointer);
        }
    }

    private static Dictionary<int, WmiBatteryInfo> GetWmiBatteryInfo()
    {
        var result = new Dictionary<int, WmiBatteryInfo>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, EstimatedChargeRemaining, BatteryStatus, Status FROM Win32_Battery"))
            {
                var index = 0;
                foreach (ManagementObject battery in searcher.Get())
                {
                    result[index] = new WmiBatteryInfo
                    {
                        EstimatedChargeRemaining = ToInt(battery["EstimatedChargeRemaining"], -1),
                        BatteryStatus = ToInt(battery["BatteryStatus"], 0),
                        Status = Convert.ToString(battery["Status"]) ?? ""
                    };
                    index++;
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static int ToInt(object value, int fallback)
    {
        if (value == null)
        {
            return fallback;
        }

        int parsed;
        return int.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
    }

    private static readonly Guid BatteryDeviceInterfaceGuid = new Guid(0x72631E54, 0x78A4, 0x11D0, 0xBC, 0xF7, 0x00, 0xAA, 0x00, 0xB7, 0xB3, 0x2A);
    private const uint IoctlBatteryQueryTag = (0x29u << 16) | (1u << 14) | (0x10u << 2);
    private const uint IoctlBatteryQueryInformation = (0x29u << 16) | (1u << 14) | (0x11u << 2);
    private const uint IoctlBatteryQueryStatus = (0x29u << 16) | (1u << 14) | (0x13u << 2);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BatterySetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, DeviceGetClassFlags flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    private static extern bool BatterySetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool BatterySetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref DeviceInterfaceData deviceInterfaceData, ref DeviceInterfaceDetailData deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    private static extern bool BatterySetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle BatteryCreateFile(string fileName, FileAccess desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, FileAttributes flags, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool BatteryDeviceIoControl(SafeFileHandle handle, uint controlCode, ref uint inBuffer, uint inBufferSize, ref uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool BatteryDeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [Flags]
    private enum DeviceGetClassFlags
    {
        Present = 0x00000002,
        DeviceInterface = 0x00000010
    }

    private enum BatteryQueryInformationLevel
    {
        BatteryInformation = 0,
        BatteryDeviceName = 4,
        BatteryManufactureName = 6
    }

    [Flags]
    private enum BatteryPowerState : uint
    {
        Online = 0x00000001,
        Discharging = 0x00000002,
        Charging = 0x00000004,
        Critical = 0x00000008
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInterfaceDetailData
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public BatteryQueryInformationLevel InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformationNative
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;
        public int DesignedCapacity;
        public int FullChargedCapacity;
        public int DefaultAlert1;
        public int DefaultAlert2;
        public int CriticalBias;
        public int CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public BatteryPowerState PowerState;
        public int LowCapacity;
        public int HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatusNative
    {
        public BatteryPowerState PowerState;
        public int Capacity;
        public int Voltage;
        public int Rate;
    }

    private sealed class NativeBatteryInfo
    {
        public string Name;
        public int DesignedCapacity;
        public int FullChargeCapacity;
        public int CycleCount;
        public BatteryPowerState PowerState;
        public int CurrentCapacity = -1;
        public int VoltageMillivolts;
        public int RateMilliwatts = int.MinValue;
    }

    private sealed class WmiBatteryInfo
    {
        public int EstimatedChargeRemaining = -1;
        public int BatteryStatus;
        public string Status;
    }
}
