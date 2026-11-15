using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

internal sealed class DiskSoundSetting
{
    private const int DefaultVolume = 30;
    private const double MaximumEqBoost = 12;
    internal const int NeutralEqPosition = 50;
    public bool Enabled;
    public int Volume = DefaultVolume;
    public int Pan;
    public double Bass;
    public double Mid;
    public double Treble;

    internal void ResetMix()
    {
        Volume = DefaultVolume;
        Pan = 0;
        Bass = Mid = Treble = 0;
    }

    public DiskSoundSetting Copy()
    {
        return new DiskSoundSetting
        {
            Enabled = Enabled,
            Volume = Clamp(Volume, 0, 100),
            Pan = Clamp(Pan, -100, 100),
            Bass = BoundEq(Bass),
            Mid = BoundEq(Mid),
            Treble = BoundEq(Treble)
        };
    }

    private static double BoundEq(double decibels)
    {
        return double.IsNaN(decibels) ? 0 : Math.Max(-MaximumEqBoost, Math.Min(MaximumEqBoost, decibels));
    }

    internal static int EqPosition(double decibels)
    {
        return NeutralEqPosition + (int)Math.Round(BoundEq(decibels) * NeutralEqPosition / MaximumEqBoost);
    }

    internal static double EqDecibels(int position)
    {
        return (Clamp(position, 0, 100) - NeutralEqPosition) * MaximumEqBoost / NeutralEqPosition;
    }

    internal static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}

internal sealed class DiskActivityAudio
{
    internal const int SampleRate = 48000;
    private const int ClickMilliseconds = 15;
    private const int SecondOnsetMilliseconds = 5;
    private const int FlushMilliseconds = 25;
    private const int MaximumEventAgeMilliseconds = 150;
    private const int SampleCount = SampleRate * ClickMilliseconds / 1000 * 2;
    private const int SecondOnsetSamples = SampleRate * SecondOnsetMilliseconds / 1000 * 2;
    internal const int RenderSampleCount = SampleCount + SecondOnsetSamples;

    private sealed class PendingClicks
    {
        internal int Read;
        internal int Write;
    }

    private sealed class DiskMix
    {
        internal readonly short[] Read;
        internal readonly short[] Write;
        internal readonly PendingClicks Pending;

        internal DiskMix(DiskSoundSetting setting, PendingClicks pending = null)
        {
            Read = Synthesize(setting, false);
            Write = Synthesize(setting, true);
            Pending = pending ?? new PendingClicks();
        }
    }

    private Dictionary<int, DiskMix> disks;

    internal DiskActivityAudio(Dictionary<int, DiskSoundSetting> settings)
    {
        UpdateMix(settings);
    }

    internal void UpdateMix(Dictionary<int, DiskSoundSetting> settings)
    {
        var next = settings.Where(pair => pair.Value.Enabled && pair.Value.Volume > 0)
            .ToDictionary(pair => pair.Key, pair => new DiskMix(pair.Value));
        Volatile.Write(ref disks, next);
    }

    internal void UpdateDiskMix(int diskNumber, DiskSoundSetting setting)
    {
        var next = new Dictionary<int, DiskMix>(Volatile.Read(ref disks));
        if (setting.Enabled && setting.Volume > 0)
        {
            DiskMix previous;
            next.TryGetValue(diskNumber, out previous);
            next[diskNumber] = new DiskMix(setting, previous == null ? null : previous.Pending);
        }
        else
            next.Remove(diskNumber);
        Volatile.Write(ref disks, next);
    }

    internal void Run(CancellationToken stop, Action started)
    {
        stop.ThrowIfCancellationRequested();
        using (var output = new DiskClickOutput(RenderSampleCount))
        {
            RunTrace(stop, started, output.TryPlay);
        }
    }

    internal void RunTrace(CancellationToken stop, Action started, Action<short[]> play)
    {
        stop.ThrowIfCancellationRequested();
        using (var session = new TraceEventSession("SensorReadout-DiskAudio-" + Guid.NewGuid().ToString("N")))
        {
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO);
            var source = session.Source;
            source.Kernel.DiskIORead += data => Record(data.DiskNumber, false, data.TimeStamp);
            source.Kernel.DiskIOWrite += data => Record(data.DiskNumber, true, data.TimeStamp);
            var consume = Task.Factory.StartNew(() => source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                if (!stop.IsCancellationRequested)
                {
                    started();
                }

                var pcm = new short[RenderSampleCount];
                var sum = new int[RenderSampleCount];
                while (!stop.WaitHandle.WaitOne(FlushMilliseconds))
                {
                    if (consume.IsCompleted)
                    {
                        consume.GetAwaiter().GetResult();
                        throw new InvalidOperationException("The disk trace ended.");
                    }

                    // Default ETW buffering can delay isolated I/O until a buffer fills.
                    session.Flush();
                    if (Render(sum, pcm))
                    {
                        play(pcm);
                    }
                }
            }
            finally
            {
                source.StopProcessing();
                session.Stop();
                consume.GetAwaiter().GetResult();
            }
        }
    }

    internal void Record(int diskNumber, bool write, DateTime timestamp)
    {
        DiskMix mix;
        if (!Volatile.Read(ref disks).TryGetValue(diskNumber, out mix))
        {
            return;
        }

        if (write && Volatile.Read(ref mix.Pending.Write) == 0 &&
            (DateTime.Now - timestamp).TotalMilliseconds <= MaximumEventAgeMilliseconds)
        {
            Volatile.Write(ref mix.Pending.Write, 1);
        }
        else if (!write && Volatile.Read(ref mix.Pending.Read) == 0 &&
            (DateTime.Now - timestamp).TotalMilliseconds <= MaximumEventAgeMilliseconds)
        {
            Volatile.Write(ref mix.Pending.Read, 1);
        }
    }

    internal bool Render(int[] sum, short[] pcm)
    {
        Array.Clear(sum, 0, sum.Length);
        var activeDisks = 0;
        foreach (var mix in Volatile.Read(ref disks).Values)
        {
            var read = Interlocked.Exchange(ref mix.Pending.Read, 0) != 0;
            var write = Interlocked.Exchange(ref mix.Pending.Write, 0) != 0;
            if (!read && !write) continue;

            // Alternating onsets keeps opposite-panned copies of the same click from summing to mono.
            var offset = activeDisks++ % 2 == 0 ? 0 : SecondOnsetSamples;
            if (read) Add(sum, mix.Read, offset);
            if (write) Add(sum, mix.Write, offset);
        }
        if (activeDisks != 0)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                pcm[i] = (short)DiskSoundSetting.Clamp(sum[i], short.MinValue, short.MaxValue);
            }
        }
        return activeDisks != 0;
    }

    private static void Add(int[] sum, short[] samples, int offset)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            sum[offset + i] += samples[i];
        }
    }

    internal static short[] Synthesize(DiskSoundSetting setting, bool write)
    {
        setting = setting.Copy();
        var pcm = new short[SampleCount];
        var pan = setting.Pan / 100.0;
        // Make modest offsets easier to hear while keeping center and the hard endpoints unchanged.
        var angle = (1 + pan * (2 - Math.Abs(pan))) * Math.PI / 4;
        var level = 3500 * setting.Volume / 100.0;
        var left = level * Math.Cos(angle);
        var right = level * Math.Sin(angle);
        var bass = Math.Pow(10, setting.Bass / 20.0);
        var mid = Math.Pow(10, setting.Mid / 20.0);
        var treble = Math.Pow(10, setting.Treble / 20.0);
        var pitch = write ? 0.68 : 1.0;
        // The source is synthesized in three bands, so EQ is baked into each click.
        for (var frame = 0; frame < pcm.Length / 2; frame++)
        {
            var progress = 2.0 * frame / pcm.Length;
            var phase = 2 * Math.PI * pitch * frame / SampleRate;
            var envelope = Math.Min(1, frame / 16.0) * Math.Pow(1 - progress, 3);
            var sample = envelope * (bass * Math.Sin(phase * 360) +
                mid * Math.Sin(phase * 1250) + treble * Math.Sin(phase * 3900));
            pcm[2 * frame] = (short)DiskSoundSetting.Clamp((int)(sample * left), short.MinValue, short.MaxValue);
            pcm[2 * frame + 1] = (short)DiskSoundSetting.Clamp((int)(sample * right), short.MinValue, short.MaxValue);
        }
        return pcm;
    }
}
