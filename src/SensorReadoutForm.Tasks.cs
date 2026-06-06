using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class TaskProcessSnapshot
    {
        public int ProcessId;
        public string Name;
        public TimeSpan TotalProcessorTime;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
    }

    private sealed class TaskProcessUsage
    {
        public int ProcessId;
        public string Name;
        public double CpuPercent;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
    }

    private sealed class GpuProcessUsage
    {
        public int ProcessId;
        public string Name;
        public double UsagePercent;
        public double DedicatedBytes;
        public double SharedBytes;
        public double OriginalDedicatedBytes;
    }

    private IEnumerable<SensorRow> GetCachedTaskRows(bool refreshSlowRows, bool backgroundRefresh)
    {
        var now = DateTime.UtcNow;
        var minimumAge = backgroundRefresh ? BackgroundTaskRowsMinimumInterval : ForegroundTaskRowsMinimumInterval;
        lock (taskRowsCacheLock)
        {
            if (!refreshSlowRows && cachedTaskRows.Count > 0 && now - cachedTaskRowsUtc < minimumAge)
            {
                return cachedTaskRows.ToList();
            }
        }

        var freshRows = GetTaskRows().ToList();
        lock (taskRowsCacheLock)
        {
            cachedTaskRows = freshRows.ToList();
            cachedTaskRowsUtc = now;
        }

        return freshRows;
    }

    private IEnumerable<SensorRow> GetTaskRows()
    {
        var rows = new List<SensorRow>();
        var currentSnapshots = CaptureTaskProcessSnapshots();
        var usages = CalculateTaskProcessUsage(currentSnapshots);
        var highestCpu = usages
            .Where(p => p.CpuPercent > 0)
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .FirstOrDefault();
        if (highestCpu != null)
        {
            rows.Add(BuildHighestCpuProcessRow(highestCpu));
        }

        var highestMemory = usages
            .Where(p => p.WorkingSetBytes > 0)
            .Where(p => !IsMemoryCompressionProcess(p.Name))
            .OrderByDescending(p => p.WorkingSetBytes)
            .ThenByDescending(p => p.PrivateMemoryBytes)
            .FirstOrDefault();
        if (highestMemory != null)
        {
            rows.Add(BuildHighestMemoryProcessRow(highestMemory));
        }

        var gpuUsages = GetGpuProcessUsages(usages);
        var highestGpuUsage = gpuUsages
            .Where(p => p.UsagePercent > 0)
            .OrderByDescending(p => p.UsagePercent)
            .ThenByDescending(p => p.DedicatedBytes + p.SharedBytes)
            .FirstOrDefault();
        if (highestGpuUsage != null)
        {
            rows.Add(BuildHighestGpuUsageProcessRow(highestGpuUsage));
        }

        var highestGpuMemory = gpuUsages
            .Where(p => p.DedicatedBytes + p.SharedBytes > 0)
            .OrderByDescending(p => p.DedicatedBytes + p.SharedBytes)
            .ThenByDescending(p => p.DedicatedBytes)
            .FirstOrDefault();
        if (highestGpuMemory != null)
        {
            rows.Add(BuildHighestGpuMemoryProcessRow(highestGpuMemory));
        }

        return rows;
    }

    private List<TaskProcessSnapshot> CaptureTaskProcessSnapshots()
    {
        var result = new List<TaskProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    result.Add(new TaskProcessSnapshot
                    {
                        ProcessId = process.Id,
                        Name = FirstNonEmpty(process.ProcessName, "Process"),
                        TotalProcessorTime = process.TotalProcessorTime,
                        WorkingSetBytes = SafeProcessMemoryValue(() => process.WorkingSet64),
                        PrivateMemoryBytes = SafeProcessMemoryValue(() => process.PrivateMemorySize64)
                    });
                }
                catch
                {
                }
            }
        }

        return result;
    }

    private List<TaskProcessUsage> CalculateTaskProcessUsage(List<TaskProcessSnapshot> currentSnapshots)
    {
        currentSnapshots = currentSnapshots ?? new List<TaskProcessSnapshot>();
        Dictionary<int, TaskProcessSnapshot> previousSnapshots;
        DateTime previousUtc;
        var now = DateTime.UtcNow;
        lock (taskSnapshotLock)
        {
            previousSnapshots = new Dictionary<int, TaskProcessSnapshot>(lastTaskProcessSnapshots);
            previousUtc = lastTaskProcessSnapshotUtc;
            lastTaskProcessSnapshots = currentSnapshots
                .GroupBy(p => p.ProcessId)
                .ToDictionary(g => g.Key, g => g.First());
            lastTaskProcessSnapshotUtc = now;
        }

        var elapsedSeconds = previousUtc == DateTime.MinValue ? 0 : (now - previousUtc).TotalSeconds;
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var usages = new List<TaskProcessUsage>();
        foreach (var snapshot in currentSnapshots)
        {
            var cpuPercent = 0.0;
            TaskProcessSnapshot previous;
            if (elapsedSeconds > 0.05 && previousSnapshots.TryGetValue(snapshot.ProcessId, out previous))
            {
                var cpuSeconds = Math.Max(0, (snapshot.TotalProcessorTime - previous.TotalProcessorTime).TotalSeconds);
                cpuPercent = Math.Min(100.0, cpuSeconds / elapsedSeconds / processorCount * 100.0);
            }

            usages.Add(new TaskProcessUsage
            {
                ProcessId = snapshot.ProcessId,
                Name = snapshot.Name,
                CpuPercent = cpuPercent,
                WorkingSetBytes = snapshot.WorkingSetBytes,
                PrivateMemoryBytes = snapshot.PrivateMemoryBytes
            });
        }

        return usages;
    }

    private static long SafeProcessMemoryValue(Func<long> getter)
    {
        try
        {
            return Math.Max(0, getter());
        }
        catch
        {
            return 0;
        }
    }

    private SensorRow BuildHighestCpuProcessRow(TaskProcessUsage process)
    {
        var details = BuildProcessDetails(process.ProcessId, process.Name);
        details["CPU usage"] = FormatNumber(Math.Round(process.CpuPercent, 1), "0.0") + "%";
        details["Working set"] = FormatBytes(process.WorkingSetBytes);
        details["Private memory"] = FormatBytes(process.PrivateMemoryBytes);
        return new SensorRow
        {
            Type = "Tasks",
            Hardware = T("ui.Processes", "Processes"),
            Name = T("reading.Highest CPU process", "Highest CPU process"),
            Identifier = "tasks|highest-cpu-process",
            Value = (float)process.CpuPercent,
            DisplayValue = FormatProcessMetric(process.Name, FormatNumber(Math.Round(process.CpuPercent, 1), "0.0") + "%"),
            Source = "Windows processes",
            Details = details
        };
    }

    private SensorRow BuildHighestMemoryProcessRow(TaskProcessUsage process)
    {
        var details = BuildProcessDetails(process.ProcessId, process.Name);
        details["Working set"] = FormatBytes(process.WorkingSetBytes);
        details["Private memory"] = FormatBytes(process.PrivateMemoryBytes);
        return new SensorRow
        {
            Type = "Tasks",
            Hardware = T("ui.Processes", "Processes"),
            Name = T("reading.Highest memory process", "Highest memory process"),
            Identifier = "tasks|highest-memory-process",
            Value = (float)process.WorkingSetBytes,
            DisplayValue = FormatProcessMetric(process.Name, FormatBytes(process.WorkingSetBytes)),
            Source = "Windows processes",
            Details = details
        };
    }

    private SensorRow BuildHighestGpuUsageProcessRow(GpuProcessUsage process)
    {
        NormalizeGpuProcessMemory(process);
        var details = BuildProcessDetails(process.ProcessId, process.Name);
        details["GPU usage"] = FormatNumber(Math.Round(process.UsagePercent, 1), "0.0") + "%";
        details["Dedicated GPU memory used"] = FormatBytes(process.DedicatedBytes);
        details["Shared GPU memory used"] = FormatBytes(process.SharedBytes);
        AddGpuMemoryCapDetail(details, process);
        details["Aggregation"] = "Highest Windows GPU engine utilization for this process.";
        return new SensorRow
        {
            Type = "Tasks",
            Hardware = T("ui.Processes", "Processes"),
            Name = T("reading.Highest GPU process", "Highest GPU process"),
            Identifier = "tasks|highest-gpu-process",
            Value = (float)process.UsagePercent,
            DisplayValue = FormatProcessMetric(process.Name, FormatNumber(Math.Round(process.UsagePercent, 1), "0.0") + "%"),
            Source = "Windows GPU performance counters",
            Details = details
        };
    }

    private SensorRow BuildHighestGpuMemoryProcessRow(GpuProcessUsage process)
    {
        NormalizeGpuProcessMemory(process);
        var total = process.DedicatedBytes + process.SharedBytes;
        var details = BuildProcessDetails(process.ProcessId, process.Name);
        details["Dedicated GPU memory used"] = FormatBytes(process.DedicatedBytes);
        details["Shared GPU memory used"] = FormatBytes(process.SharedBytes);
        details["Process GPU memory total"] = FormatBytes(total);
        AddGpuMemoryCapDetail(details, process);
        return new SensorRow
        {
            Type = "Tasks",
            Hardware = T("ui.Processes", "Processes"),
            Name = T("reading.Highest GPU memory process", "Highest GPU memory process"),
            Identifier = "tasks|highest-gpu-memory-process",
            Value = (float)total,
            DisplayValue = FormatProcessMetric(process.Name, FormatBytes(total)),
            Source = "Windows GPU performance counters",
            Details = details
        };
    }

    private static string FormatProcessMetric(string processName, string value)
    {
        return FirstNonEmpty(processName, "Process") + ": " + value;
    }

    private static bool IsMemoryCompressionProcess(string processName)
    {
        var name = FirstNonEmpty(processName, "").Trim();
        return name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MemCompression", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeGpuProcessMemory(GpuProcessUsage process)
    {
        if (process == null)
        {
            return;
        }

        var dedicatedTotal = GetTotalGpuAdapterMemoryBytes();
        if (dedicatedTotal <= 0 || process.DedicatedBytes <= dedicatedTotal)
        {
            return;
        }

        process.OriginalDedicatedBytes = process.DedicatedBytes;
        process.DedicatedBytes = dedicatedTotal;
    }

    private static void AddGpuMemoryCapDetail(Dictionary<string, string> details, GpuProcessUsage process)
    {
        if (details == null || process == null || process.OriginalDedicatedBytes <= 0)
        {
            return;
        }

        details["Original Windows dedicated GPU memory counter"] = FormatBytes(process.OriginalDedicatedBytes);
        details["GPU memory counter note"] = "Windows reported more dedicated GPU process memory than the adapter's known dedicated memory, so Sensor Readout ignores impossible counter samples when choosing the highest GPU memory process.";
    }

    private Dictionary<string, string> BuildProcessDetails(int processId, string processName)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Process name", FirstNonEmpty(processName, "Process") },
            { "Process ID", processId.ToString(CultureInfo.InvariantCulture) }
        };

        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                try
                {
                    if (process.StartTime != DateTime.MinValue)
                    {
                        details["Started"] = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                    }
                }
                catch
                {
                }

                try
                {
                    var path = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        details["Executable path"] = path;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return details;
    }

    private static List<GpuProcessUsage> GetGpuProcessUsages(List<TaskProcessUsage> processUsages)
    {
        var processNames = (processUsages ?? new List<TaskProcessUsage>())
            .GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => FirstNonEmpty(g.First().Name, "Process"));
        var result = new Dictionary<int, GpuProcessUsage>();
        AddGpuProcessEngineUsage(result, processNames);
        AddGpuProcessMemoryUsage(result, processNames, "Dedicated Usage", true);
        AddGpuProcessMemoryUsage(result, processNames, "Shared Usage", false);
        return result.Values.ToList();
    }

    private static void AddGpuProcessEngineUsage(Dictionary<int, GpuProcessUsage> result, Dictionary<int, string> processNames)
    {
        const string categoryName = "GPU Engine";
        const string counterName = "Utilization Percentage";
        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return;
            }

            var category = new PerformanceCounterCategory(categoryName);
            foreach (var instance in category.GetInstanceNames().Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var processId = ExtractPerformanceCounterProcessId(instance);
                if (processId <= 0)
                {
                    continue;
                }

                double value;
                if (!TryReadPerformanceCounter(categoryName, counterName, instance, out value) || value <= 0)
                {
                    continue;
                }

                var usage = EnsureGpuProcessUsage(result, processNames, processId);
                if (value > usage.UsagePercent)
                {
                    usage.UsagePercent = value;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddGpuProcessMemoryUsage(Dictionary<int, GpuProcessUsage> result, Dictionary<int, string> processNames, string counterName, bool dedicated)
    {
        const string categoryName = "GPU Process Memory";
        try
        {
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return;
            }

            var category = new PerformanceCounterCategory(categoryName);
            var dedicatedTotal = dedicated ? GetTotalGpuAdapterMemoryBytes() : 0;
            foreach (var instance in category.GetInstanceNames().Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var processId = ExtractPerformanceCounterProcessId(instance);
                if (processId <= 0)
                {
                    continue;
                }

                double value;
                if (!TryReadPerformanceCounter(categoryName, counterName, instance, out value) || value <= 0)
                {
                    continue;
                }

                var usage = EnsureGpuProcessUsage(result, processNames, processId);
                if (dedicated && dedicatedTotal > 0 && value > dedicatedTotal)
                {
                    usage.OriginalDedicatedBytes = Math.Max(usage.OriginalDedicatedBytes, value);
                    continue;
                }

                if (dedicated)
                {
                    usage.DedicatedBytes = Math.Max(usage.DedicatedBytes, value);
                }
                else
                {
                    usage.SharedBytes = Math.Max(usage.SharedBytes, value);
                }
            }
        }
        catch
        {
        }
    }

    private static GpuProcessUsage EnsureGpuProcessUsage(Dictionary<int, GpuProcessUsage> result, Dictionary<int, string> processNames, int processId)
    {
        GpuProcessUsage usage;
        if (!result.TryGetValue(processId, out usage))
        {
            string name;
            processNames.TryGetValue(processId, out name);
            usage = new GpuProcessUsage
            {
                ProcessId = processId,
                Name = FirstNonEmpty(name, "Process")
            };
            result[processId] = usage;
        }

        return usage;
    }

    private static int ExtractPerformanceCounterProcessId(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance))
        {
            return 0;
        }

        var match = Regex.Match(instance, @"pid_([0-9]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        int processId;
        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId) ? processId : 0;
    }
}
