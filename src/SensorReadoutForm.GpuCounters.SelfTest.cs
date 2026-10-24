using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CounterSamples = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Diagnostics.CounterSample>>;

public sealed partial class SensorReadoutForm
{
    private static CounterSample GpuTestSample(long raw, long time, PerformanceCounterType type)
    {
        return new CounterSample(raw, 0, 10000000, 10000000, time, time, type, time);
    }

    private static void SelfTestGpuCounterSampling()
    {
        long now = 0;
        var reads = 0;
        var fail = false;
        var samples = new CounterSamples(StringComparer.OrdinalIgnoreCase);
        samples["Utilization Percentage"] = new Dictionary<string, CounterSample>
        {
            { "pid_42_engtype_3D", GpuTestSample(1000000, 10000000, PerformanceCounterType.Timer100Ns) }
        };
        samples["Dedicated Usage"] = new Dictionary<string, CounterSample>
        {
            { "pid_42", GpuTestSample(1048576, 10000000, PerformanceCounterType.NumberOfItems64) }
        };
        var sampler = new GpuCounterSampler(category =>
        {
            reads++;
            if (fail) throw new InvalidOperationException("Counter provider unavailable");
            var snapshot = new CounterSamples(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in samples) snapshot[pair.Key] = new Dictionary<string, CounterSample>(pair.Value);
            return snapshot;
        }, () => now);
        Require(sampler.ReadValues("GPU", "Utilization Percentage")["pid_42_engtype_3D"] == 0, "GPU rate did not warm up from an initial sample.");
        Require(sampler.ReadValues("GPU", "Dedicated Usage")["pid_42"] == 1048576, "GPU memory gauge required a second sample.");
        Require(reads == 1, "GPU consumers reread the provider within one snapshot.");
        now = 2000;
        samples["Utilization Percentage"]["pid_42_engtype_3D"] = GpuTestSample(6000000, 30000000, PerformanceCounterType.Timer100Ns);
        Require(Math.Abs(sampler.ReadValues("GPU", "Utilization Percentage")["pid_42_engtype_3D"] - 25) < 0.01, "GPU utilization lost its two-sample percentage calculation.");
        var stable = sampler.ReadValues("GPU", "Utilization Percentage");
        samples["Utilization Percentage"].Clear();
        samples["Utilization Percentage"]["pid_99_engtype_Compute"] = GpuTestSample(500000, 40000000, PerformanceCounterType.Timer100Ns);
        now += 2000;
        var changed = sampler.ReadValues("GPU", "Utilization Percentage");
        Require(!changed.ContainsKey("pid_42_engtype_3D") && changed["pid_99_engtype_Compute"] == 0, "Exited GPU instances survived, or new instances reused another process's history.");
        Require(stable.ContainsKey("pid_42_engtype_3D"), "A new GPU sample mutated an in-use snapshot.");
        now += 2000;
        fail = true;
        Require(sampler.ReadValues("GPU", "Dedicated Usage").Count == 0, "Failed GPU provider returned stale values.");
        now += 2000;
        fail = false;
        Require(sampler.ReadValues("GPU", "Utilization Percentage")["pid_99_engtype_Compute"] == 0, "Recovered GPU provider reused pre-failure history.");
        var readsBefore = reads;
        Parallel.For(0, 20, i => sampler.ReadValues("GPU", "Dedicated Usage"));
        Require(reads == readsBefore, "Concurrent GPU consumers duplicated provider work.");
        Require(sampler.ReadValues("GPU", "Missing counter").Count == 0, "Missing GPU counter did not return an empty sample.");
        for (var i = 0; i < 200; i++)
        {
            now += 1000;
            samples["Utilization Percentage"].Clear();
            samples["Utilization Percentage"]["pid_" + i] = GpuTestSample(i, now * 10000, PerformanceCounterType.Timer100Ns);
            Require(sampler.ReadValues("GPU", "Utilization Percentage").Count == 1, "GPU process churn accumulated stale counter instances.");
        }
    }
}
