using System;
using System.Collections.Generic;
using System.Diagnostics;
using CounterSamples = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Diagnostics.CounterSample>>;

internal sealed class GpuCounterSampler
{
    private const long SnapshotReuseMilliseconds = 100;
    private sealed class Snapshot
    {
        internal long ReadAt;
        internal CounterSamples Samples;
        internal Dictionary<string, Dictionary<string, double>> Values;
    }

    private readonly object gate = new object();
    private readonly Dictionary<string, Snapshot> snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CounterSamples> readCategory;
    private readonly Func<long> milliseconds;

    internal GpuCounterSampler() : this(ReadCategory, () => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency)) { }

    internal GpuCounterSampler(Func<string, CounterSamples> reader, Func<long> clock)
    {
        readCategory = reader;
        milliseconds = clock;
    }

    internal IReadOnlyDictionary<string, double> ReadValues(string category, string counter)
    {
        lock (gate)
        {
            Snapshot previous;
            var now = milliseconds();
            if (!snapshots.TryGetValue(category, out previous) || now - previous.ReadAt >= SnapshotReuseMilliseconds)
            {
                var current = new Snapshot { ReadAt = now, Values = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase) };
                try
                {
                    current.Samples = readCategory(category);
                    foreach (var counters in current.Samples)
                    {
                        Dictionary<string, CounterSample> oldSamples = null;
                        if (previous != null && previous.Samples != null) previous.Samples.TryGetValue(counters.Key, out oldSamples);
                        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        foreach (var instance in counters.Value)
                        {
                            CounterSample oldSample;
                            var value = oldSamples != null && oldSamples.TryGetValue(instance.Key, out oldSample) && oldSample.CounterType == instance.Value.CounterType
                                ? CounterSample.Calculate(oldSample, instance.Value)
                                : CounterSample.Calculate(instance.Value);
                            if (!float.IsNaN(value) && !float.IsInfinity(value)) values[instance.Key] = Math.Max(0, value);
                        }
                        current.Values[counters.Key] = values;
                    }
                }
                catch
                {
                    // Missing counters and driver resets must not keep old process samples alive.
                    current.Samples = null;
                    current.Values.Clear();
                }
                snapshots[category] = previous = current;
            }
            Dictionary<string, double> result;
            return previous.Values.TryGetValue(counter, out result) ? result : new Dictionary<string, double>();
        }
    }

    private static CounterSamples ReadCategory(string name)
    {
        // Read the provider once, not once per process/engine. Nearby consumers share
        // this snapshot; rate counters retain the preceding sample between refreshes.
        var data = new PerformanceCounterCategory(name).ReadCategory();
        var result = new CounterSamples(StringComparer.OrdinalIgnoreCase);
        foreach (InstanceDataCollection counter in data.Values)
        {
            var samples = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceData instance in counter.Values) samples[instance.InstanceName] = instance.Sample;
            result[counter.CounterName] = samples;
        }
        return result;
    }
}
