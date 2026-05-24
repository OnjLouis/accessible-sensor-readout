using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private static bool TryGetGpuBiosInfo(string gpuName, string pnpDeviceId, out string bios, out string biosDate)
    {
        bios = "";
        biosDate = "";
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return false;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                bios = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosString"));
                                biosDate = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosDate"));
                                return !string.IsNullOrWhiteSpace(bios) || !string.IsNullOrWhiteSpace(biosDate);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static object GetGpuAdapterMemoryBytes(string gpuName, object fallback)
    {
        var registryBytes = GetGpuAdapterMemoryBytesFromRegistry(gpuName);
        return registryBytes > 0 ? (object)registryBytes : fallback;
    }

    private static ulong GetGpuAdapterMemoryBytesFromRegistry(string gpuName)
    {
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return 0;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                var qwMemory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.qwMemorySize"));
                                if (qwMemory > 0)
                                {
                                    return qwMemory;
                                }

                                var memory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.MemorySize"));
                                if (memory > 0)
                                {
                                    return memory;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static bool GpuRegistryEntryMatches(string gpuName, string adapterString, string driverDesc)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(adapterString) && gpuName.IndexOf(adapterString, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && gpuName.IndexOf(driverDesc, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(adapterString) && adapterString.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && driverDesc.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
