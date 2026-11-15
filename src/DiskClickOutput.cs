using System;
using System.IO;
using System.Runtime.InteropServices;

internal sealed class DiskClickOutput : IDisposable
{
    private const int DefaultWaveDevice = -1;
    private const int HeaderDone = 1;
    private static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));
    private static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WaveHeader), "Flags").ToInt32();
    private IntPtr device;
    private IntPtr buffer;
    private IntPtr header;
    private bool prepared;
    private bool playing;

    internal DiskClickOutput(int sampleCount)
    {
        var format = new WaveFormat
        {
            Format = 1, Channels = 2, SampleRate = DiskActivityAudio.SampleRate,
            BlockAlign = 4, BitsPerSample = 16, BytesPerSecond = DiskActivityAudio.SampleRate * 4
        };
        Check(waveOutOpen(out device, DefaultWaveDevice, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        try
        {
            var byteCount = sampleCount * sizeof(short);
            buffer = Marshal.AllocHGlobal(byteCount);
            header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = buffer, Length = byteCount }, header, false);
            Check(waveOutPrepareHeader(device, header, HeaderSize));
            prepared = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void TryPlay(short[] samples)
    {
        if (playing && (Marshal.ReadInt32(header, FlagsOffset) & HeaderDone) == 0)
        {
            return;
        }

        Marshal.Copy(samples, 0, buffer, samples.Length);
        Check(waveOutWrite(device, header, HeaderSize));
        playing = true;
    }

    public void Dispose()
    {
        if (device == IntPtr.Zero)
        {
            return;
        }
        if (playing)
        {
            waveOutReset(device);
        }
        if (prepared)
        {
            prepared = waveOutUnprepareHeader(device, header, HeaderSize) != 0;
        }
        if (waveOutClose(device) == 0)
        {
            if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            device = IntPtr.Zero;
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new IOException("Windows audio error " + result + ".");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short Format, Channels;
        public int SampleRate, BytesPerSecond;
        public short BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int Length, Recorded;
        public UIntPtr User;
        public int Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr output, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr output, IntPtr header, int size);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr output, IntPtr header, int size);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr output);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr output, IntPtr header, int size);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr output);
}
