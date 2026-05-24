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
    private IEnumerable<SensorRow> GetCoreTempRows()
    {
        try
        {
            using (var mappedFile = MemoryMappedFile.OpenExisting("CoreTempMappingObjectEx"))
            using (var stream = mappedFile.CreateViewStream(0, Marshal.SizeOf(typeof(CoreTempSharedDataEx)), MemoryMappedFileAccess.Read))
            {
                var size = Marshal.SizeOf(typeof(CoreTempSharedDataEx));
                var buffer = new byte[size];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset < size)
                {
                    return Enumerable.Empty<SensorRow>();
                }

                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var data = (CoreTempSharedDataEx)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(CoreTempSharedDataEx));
                    return BuildCoreTempRows(data);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private static IEnumerable<SensorRow> BuildCoreTempRows(CoreTempSharedDataEx data)
    {
        var rows = new List<SensorRow>();
        var coreCount = Math.Max(0, Math.Min(256, (int)data.UiCoreCnt));
        if (coreCount == 0 || data.FTemp == null)
        {
            return rows;
        }

        var cpuName = string.IsNullOrWhiteSpace(data.CpuName) ? "CPU" : data.CpuName.Trim();
        var temps = new List<float>();
        for (var i = 0; i < coreCount; i++)
        {
            var temp = data.FTemp[i];
            if (float.IsNaN(temp) || float.IsInfinity(temp) || temp <= 0)
            {
                continue;
            }

            if (data.UcFahrenheit != 0)
            {
                temp = (temp - 32.0f) * 5.0f / 9.0f;
            }

            if (data.UcDeltaToTjMax != 0 && data.UiTjMax != null && i < data.UiTjMax.Length && data.UiTjMax[i] > 0)
            {
                temp = data.UiTjMax[i] - temp;
            }

            temps.Add(temp);
            rows.Add(new SensorRow
            {
                Type = "Temperature",
                Hardware = cpuName,
                Name = "Core #" + (i + 1),
                Identifier = "coretemp/core/" + i,
                Value = temp,
                Source = "Core Temp shared memory"
            });
        }

        if (temps.Count > 0)
        {
            rows.Add(new SensorRow
            {
                Type = "Temperature",
                Hardware = cpuName,
                Name = "CPU package",
                Identifier = "coretemp/package",
                Value = temps.Max(),
                Source = "Core Temp shared memory"
            });
        }

        if (data.UiLoad != null)
        {
            var loads = data.UiLoad.Take(coreCount).Where(v => v <= 100).Select(v => (float)v).ToList();
            if (loads.Count > 0)
            {
                var load = loads.Average();
                rows.Add(new SensorRow
                {
                    Type = "Performance",
                    Hardware = "CPU",
                    Name = "CPU usage",
                    Value = load,
                    DisplayValue = FormatNumber(Math.Round(load, 1), "0.0") + "%",
                    Source = "Core Temp shared memory"
                });
            }
        }

        return rows;
    }
}
