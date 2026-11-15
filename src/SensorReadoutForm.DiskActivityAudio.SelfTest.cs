using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private static void SelfTestDiskActivityAudio()
    {
        SelfTestDiskActivitySliderKeys();
        SelfTestDiskPreviewButton();
        SelfTestDiskInterfaceNotifications();
        var original = new PhysicalStorageDevice { Index = 1, PnpDeviceId = @"USBSTOR\SampleDisk" };
        var reconnected = new PhysicalStorageDevice { Index = 5, PnpDeviceId = @"usbstor\sampledisk" };
        var different = new PhysicalStorageDevice { Index = 1, PnpDeviceId = @"USBSTOR\OtherDisk" };
        var setting = new DiskSoundSetting { Enabled = true, Volume = 30, Pan = -100, Bass = 50 };
        var key = DiskActivityDeviceKey(original);
        var value = new AppSettings { DiskActivityHotKey = "Ctrl+Shift+F10" };
        value.DiskActivitySounds[key] = setting;
        var machine = Newtonsoft.Json.Linq.JObject.FromObject(ExtractMachineSettings(value));
        Require(machine.Value<bool?>("DiskActivityListening") == false &&
            machine.Value<string>("DiskActivityToggleSoundFile") == "",
            "Machine settings do not persist listening and toggle feedback with safe defaults.");
        var legacy = JsonConvert.DeserializeObject<AppSettings>("{\"DiskActivityHotKey\":\"Ctrl+Shift+F10\"}");
        Require(!legacy.DiskActivityListening && legacy.DiskActivityToggleSoundFile == "", "Older settings should default to off and spoken feedback.");
        value.DiskActivityListening = true;
        value.DiskActivityToggleSoundFile = @"unused\SR01.wav";
        machine = Newtonsoft.Json.Linq.JObject.FromObject(ExtractMachineSettings(value));
        var machineJson = JsonConvert.SerializeObject(machine);
        var restored = JsonConvert.DeserializeObject<AppSettings>(machineJson);
        Require(restored.DiskActivityListening && restored.DiskActivityToggleSoundFile == "SR01.wav",
            "An active listener or its toggle sound was not preserved on reload.");
        restored.DiskActivityListening = false;
        var stopped = JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(ExtractMachineSettings(restored)));
        Require(!stopped.DiskActivityListening && stopped.DiskActivityToggleSoundFile == "SR01.wav",
            "Stopping must persist without resetting the selected feedback sound.");
        Require(restored.DiskActivityHotKey == "Ctrl+Shift+F10" && restored.DiskActivitySounds[key].Bass == 12 &&
            restored.DiskActivitySounds[key].Pan == -100, "Disk mix or hotkey did not round trip with bounded values.");
        Require(!machineJson.Contains("SampleDisk") &&
            !JsonConvert.SerializeObject(ExtractSharedSettings(value)).Contains("DiskActivity"),
            "Disk activity settings must not expose raw device IDs or be included in shared settings.");
        var mapped = ResolveDiskActivityDevices(new[] { different, reconnected }, restored.DiskActivitySounds);
        Require(mapped.Count == 1 && mapped.ContainsKey(5), "Reconnected disk did not retain its mix, or a recycled disk number inherited it.");
        Require(ResolveDiskActivityDevices(new[] { different }, restored.DiskActivitySounds).Count == 0,
            "An absent disk should not be monitored.");
        var remapping = new DiskActivityAudio(ResolveDiskActivityDevices(new[] { original }, restored.DiskActivitySounds));
        remapping.UpdateMix(ResolveDiskActivityDevices(new[] { different, reconnected }, restored.DiskActivitySounds));
        var remapPcm = new short[DiskActivityAudio.RenderSampleCount];
        var remapSum = new int[remapPcm.Length];
        remapping.Record(1, false, DateTime.Now);
        Require(!remapping.Render(remapSum, remapPcm), "A replacement disk inherited the old disk's audio.");
        remapping.Record(5, false, DateTime.Now);
        Require(remapping.Render(remapSum, remapPcm), "The reconnected disk did not resume on its new index.");

        var read = DiskActivityAudio.Synthesize(setting, false);
        var write = DiskActivityAudio.Synthesize(setting, true);
        Require(!read.SequenceEqual(write), "Read and write clicks are indistinguishable.");
        Require(read.Where((sample, index) => index % 2 == 0).Any(sample => sample != 0) &&
            read.Where((sample, index) => index % 2 == 1).All(sample => sample == 0), "Hard-left pan leaked into the right channel.");
        var right = DiskActivityAudio.Synthesize(new DiskSoundSetting { Pan = 100 }, false);
        Require(right.Where((sample, index) => index % 2 == 0).All(sample => sample == 0) &&
            right.Where((sample, index) => index % 2 == 1).Any(sample => sample != 0), "Hard-right pan leaked into the left channel.");
        Require(DiskActivityAudio.Synthesize(new DiskSoundSetting { Volume = 0 }, false).All(sample => sample == 0),
            "Zero volume did not mute the click.");
        var opposing = new DiskActivityAudio(new Dictionary<int, DiskSoundSetting>
        {
            { 1, new DiskSoundSetting { Enabled = true, Pan = -20 } },
            { 2, new DiskSoundSetting { Enabled = true, Pan = 20 } }
        });
        var stereo = new short[DiskActivityAudio.RenderSampleCount];
        opposing.Record(1, false, DateTime.Now);
        opposing.Record(2, false, DateTime.Now);
        Require(opposing.Render(new int[stereo.Length], stereo) &&
            Enumerable.Range(0, stereo.Length / 2).Any(i => stereo[i * 2] != stereo[i * 2 + 1]),
            "Simultaneous left- and right-panned disk reads collapsed to identical channels.");
        var flat = DiskActivityAudio.Synthesize(new DiskSoundSetting(), false);
        var frequency = new[] { 360, 1250, 3900 };
        var settings = new[] { new DiskSoundSetting { Bass = 6 }, new DiskSoundSetting { Mid = 6 }, new DiskSoundSetting { Treble = 6 } };
        for (var i = 0; i < settings.Length; i++)
        {
            var boosted = DiskActivityAudio.Synthesize(settings[i], false);
            for (var band = 0; band < frequency.Length; band++)
            {
                var ratio = DiskClickAmplitude(boosted, frequency[band]) / DiskClickAmplitude(flat, frequency[band]);
                Require(band == i ? ratio > 1.7 && ratio < 2.3 : ratio > 0.9 && ratio < 1.1,
                    "An EQ adjustment did not boost the intended frequency independently.");
            }
        }

        var letters = new Dictionary<int, SortedSet<string>> { { 5, new SortedSet<string> { "Z:", "C:" } } };
        reconnected.Model = "Model";
        Require(DiskActivityDriveLabel(reconnected, letters).StartsWith("C:, Z:") &&
            !DiskActivityDriveLabel(different, letters).Contains("C:"), "Drive letters were not matched or sorted correctly.");
        Require(FormatDiskActivityPan(0) != FormatDiskActivityPan(50) && FormatDiskActivityPan(50) != FormatDiskActivityPan(100),
            "Pan endpoints and center must have distinct labels.");

        var audio = new DiskActivityAudio(mapped);
        var pcm = new short[DiskActivityAudio.RenderSampleCount];
        var sum = new int[pcm.Length];
        audio.Record(1, true, DateTime.Now);
        audio.Record(5, true, DateTime.Now.AddSeconds(-1));
        Require(!audio.Render(sum, pcm), "An unknown disk or an old event generated a click.");
        for (var i = 0; i < 1000; i++) audio.Record(5, false, DateTime.Now);
        Require(audio.Render(sum, pcm) && pcm.Take(read.Length).SequenceEqual(read) &&
            pcm.Skip(read.Length).All(sample => sample == 0), "A burst should produce a single click.");
        Require(!audio.Render(sum, pcm), "Old clicks accumulated after the burst.");
        audio.Record(5, false, DateTime.Now);
        audio.Record(5, true, DateTime.Now);
        Require(audio.Render(sum, pcm) && pcm.Take(read.Length).Select((sample, i) => sample == read[i] + write[i]).All(same => same),
            "Concurrent reads and writes were not mixed.");
        audio.Record(5, false, DateTime.Now);
        audio.UpdateMix(new Dictionary<int, DiskSoundSetting> { { 5, new DiskSoundSetting { Enabled = true, Pan = 100 } } });
        Require(!audio.Render(sum, pcm), "Old pending clicks survived a live mix change.");
        audio.Record(5, false, DateTime.Now);
        Require(audio.Render(sum, pcm) && pcm.Take(right.Length).SequenceEqual(right), "Live panning did not reach the output mix.");
        var unaffected = new DiskSoundSetting { Enabled = true, Pan = 100 };
        var tuning = new DiskActivityAudio(new Dictionary<int, DiskSoundSetting> { { 1, setting }, { 2, unaffected } });
        tuning.Record(2, false, DateTime.Now);
        var edited = new DiskSoundSetting { Enabled = true, Volume = 0, Pan = -100, Bass = 12, Mid = -3, Treble = 7 };
        tuning.UpdateDiskMix(1, edited);
        Require(tuning.Render(sum, pcm) && pcm.Take(right.Length).SequenceEqual(right),
            "Editing one disk dropped another disk's pending click.");
        tuning.Record(1, false, DateTime.Now);
        Require(!tuning.Render(sum, pcm), "Moving the selected disk to zero volume should mute it immediately.");
        edited.ResetMix();
        Require(edited.Enabled && edited.Volume == 30 && edited.Pan == 0 &&
            edited.Bass == 0 && edited.Mid == 0 && edited.Treble == 0,
            "Reset should restore only the selected disk's audio defaults.");
        tuning.UpdateDiskMix(1, edited);
        tuning.Record(1, false, DateTime.Now);
        Require(tuning.Render(sum, pcm) && pcm.Take(flat.Length).SequenceEqual(flat),
            "Reset audio defaults did not take effect on the next click.");
        tuning.Record(1, false, DateTime.Now);
        edited.Pan = 100;
        tuning.UpdateDiskMix(1, edited);
        Require(tuning.Render(sum, pcm) && pcm.Take(right.Length).SequenceEqual(right),
            "Dragging a slider dropped a pending click instead of using the new mix.");
        Require(DiskSoundSetting.EqDecibels(50) == 0 && DiskSoundSetting.EqDecibels(0) == -12 &&
            DiskSoundSetting.EqDecibels(100) == 12, "Incorrect neutral or endpoint EQ gain.");
        for (var position = 0; position <= 100; position++)
        {
            Require(DiskSoundSetting.EqPosition(DiskSoundSetting.EqDecibels(position)) == position,
                "EQ percentage did not round trip.");
        }
        var legacyEq = JsonConvert.DeserializeObject<DiskSoundSetting>("{\"Bass\":3,\"Mid\":-2,\"Treble\":7}").Copy();
        Require(legacyEq.Bass == 3 && legacyEq.Mid == -2 && legacyEq.Treble == 7,
            "Legacy integer dB values must keep the same gain.");
        RequireDiskActivityPreview(setting, false, read);
        RequireDiskActivityPreview(setting, true, write);
    }

    private static void SelfTestDiskInterfaceNotifications()
    {
        const int deviceChange = 0x0219, nodesChanged = 7, arrival = 0x8000, removed = 0x8004;
        const int interfaceSize = 32, interfaceType = 5, classOffset = 12;
        var generic = Message.Create(IntPtr.Zero, deviceChange, (IntPtr)nodesChanged, IntPtr.Zero);
        Require(!DiskInterfaceNotifications.IsDiskChange(generic, true), "A generic USB event must not interrupt disk sounds.");
        Require(DiskInterfaceNotifications.IsDiskChange(generic, false), "Missing registration must still allow a quiet remap.");
        using (var window = new Form())
        {
            var registration = DiskInterfaceNotifications.Register(window.Handle);
            Require(registration != IntPtr.Zero, "Windows refused disk-interface notifications.");
            Require(DiskInterfaceNotifications.UnregisterDeviceNotification(registration), "Could not release disk-interface notifications.");
        }
        var payload = Marshal.AllocHGlobal(interfaceSize);
        try
        {
            Marshal.WriteInt32(payload, interfaceSize);
            Marshal.WriteInt32(payload, 4, interfaceType);
            Marshal.WriteInt32(payload, 8, 0);
            var eventMessage = Message.Create(IntPtr.Zero, deviceChange, (IntPtr)arrival, payload);
            Marshal.Copy(new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed").ToByteArray(), 0, IntPtr.Add(payload, classOffset), 16);
            Require(!DiskInterfaceNotifications.IsDiskChange(eventMessage, true), "A non-storage USB interface caused a remap.");
            Marshal.Copy(new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b").ToByteArray(), 0, IntPtr.Add(payload, classOffset), 16);
            Require(DiskInterfaceNotifications.IsDiskChange(eventMessage, true), "A disk arrival must remap indexes.");
            eventMessage.WParam = (IntPtr)removed;
            Require(DiskInterfaceNotifications.IsDiskChange(eventMessage, true), "A disk removal must remap indexes.");
        }
        finally { Marshal.FreeHGlobal(payload); }
    }

    private static void RequireDiskActivityPreview(DiskSoundSetting setting, bool write, short[] click)
    {
        const int wavHeaderBytes = 44;
        var spacing = DiskActivityAudio.SampleRate * DiskPreviewIntervalMilliseconds / 1000 * 2;
        using (var preview = CreateDiskActivityPreview(setting, write))
        using (var reader = new System.IO.BinaryReader(preview))
        {
            Require(DiskPreviewClicks == 10 && preview.Position == 0 &&
                preview.Length == wavHeaderBytes + spacing * DiskPreviewClicks * sizeof(short),
                "The stereo preview must contain ten spaced clicks.");
            preview.Position = wavHeaderBytes;
            for (var i = 0; i < DiskPreviewClicks; i++)
            {
                Require(click.All(sample => reader.ReadInt16() == sample), "A preview click lost its selected mix or pitch.");
                for (var gapSample = click.Length; gapSample < spacing; gapSample++)
                    Require(reader.ReadInt16() == 0, "Preview clicks must be separated by silence.");
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr key, IntPtr details);

    private static void SelfTestDiskPreviewButton()
    {
        const uint keyDown = 0x0100, keyUp = 0x0101, leftDown = 0x0201, leftUp = 0x0202;
        using (var panel = new Panel())
        using (var button = new DiskPreviewButton())
        {
            panel.Controls.Add(button);
            var repeats = 0;
            var releases = 0;
            var taps = 0;
            button.PreviewRequested += repeat => { if (repeat) repeats++; else taps++; };
            button.PreviewReleased += () => releases++;
            button.PerformClick();
            Require(taps == 1, "An ordinary preview button activation must request the ten-click preview.");
            SendMessage(button.Handle, keyDown, (IntPtr)Keys.Space, IntPtr.Zero);
            WaitForDiskPreviewHold(() => repeats == 1);
            SendMessage(button.Handle, keyUp, (IntPtr)Keys.Space, IntPtr.Zero);
            Require(releases == 1 && taps == 1, "Releasing a held Space must stop playback without starting a tap preview.");
            var point = (IntPtr)((5 << 16) | 5);
            SendMessage(button.Handle, leftDown, (IntPtr)1, point);
            WaitForDiskPreviewHold(() => repeats == 2);
            SendMessage(button.Handle, leftUp, IntPtr.Zero, point);
            Require(releases == 2 && taps == 1, "Releasing a held mouse button must stop playback without starting a tap preview.");
        }
    }

    private static void WaitForDiskPreviewHold(Func<bool> started)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!started() && watch.ElapsedMilliseconds < 1500)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
        Require(started(), "Holding a preview button did not request repeating playback.");
    }

    private static void SelfTestDiskActivitySliderKeys()
    {
        const uint keyDownMessage = 0x0100;
        using (var panel = new TableLayoutPanel { ColumnCount = 2 })
        {
            foreach (var band in new[] { "Bass", "Mid", "Treble" })
            {
                var slider = AddDiskActivitySlider(panel, band, "DiskActivity" + band);
                Require(slider.Minimum == 0 && slider.Maximum == 100 && slider.Value == 50,
                    "EQ must expose a percentage with 50 as its neutral position.");
                SendMessage(slider.Handle, keyDownMessage, (IntPtr)Keys.PageUp, IntPtr.Zero);
                Require(slider.Value == 60, "Page Up must increase the EQ position by ten points.");
                SendMessage(slider.Handle, keyDownMessage, (IntPtr)Keys.PageDown, IntPtr.Zero);
                Require(slider.Value == 50, "Page Down must decrease the EQ position by ten points.");
                SendMessage(slider.Handle, keyDownMessage, (IntPtr)Keys.Right, IntPtr.Zero);
                Require(slider.Value == 51, "Right Arrow must still increase the EQ position by one point.");
                SendMessage(slider.Handle, keyDownMessage, (IntPtr)Keys.Up, IntPtr.Zero);
                Require(slider.Value == 52, "Up Arrow must increase the EQ position by one point.");
                SendMessage(slider.Handle, keyDownMessage, (IntPtr)Keys.Down, IntPtr.Zero);
                Require(slider.Value == 51, "Down Arrow must decrease the EQ position by one point.");
                Require(slider.AccessibilityObject.Role == AccessibleRole.Slider, "An EQ control lost its slider role.");
            }
            var volume = AddDiskActivitySlider(panel, "Volume (%)", "DiskActivityVolume");
            volume.Value = 50;
            SendMessage(volume.Handle, keyDownMessage, (IntPtr)Keys.Up, IntPtr.Zero);
            Require(volume.Value == 51, "Up Arrow must raise disk volume by one point.");
            SendMessage(volume.Handle, keyDownMessage, (IntPtr)Keys.Down, IntPtr.Zero);
            Require(volume.Value == 50, "Down Arrow must lower disk volume by one point.");
            SendMessage(volume.Handle, keyDownMessage, (IntPtr)Keys.PageUp, IntPtr.Zero);
            Require(volume.Value == 60, "Page Up must raise disk volume by ten points.");
            SendMessage(volume.Handle, keyDownMessage, (IntPtr)Keys.PageDown, IntPtr.Zero);
            Require(volume.Value == 50, "Page Down must lower disk volume by ten points.");
            var pan = AddDiskActivitySlider(panel, "Pan", "DiskActivityPan", true);
            SendMessage(pan.Handle, keyDownMessage, (IntPtr)Keys.PageUp, IntPtr.Zero);
            Require(pan.Value == 40, "The pan slider's native Page Up behavior changed.");
            SendMessage(pan.Handle, keyDownMessage, (IntPtr)Keys.Up, IntPtr.Zero);
            Require(pan.Value == 39, "The pan slider's native Up behavior changed.");
        }
    }

    private static double DiskClickAmplitude(short[] pcm, double frequency)
    {
        double real = 0, imaginary = 0;
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var phase = 2 * Math.PI * i * frequency / DiskActivityAudio.SampleRate;
            real += pcm[2 * i] * Math.Cos(phase);
            imaginary += pcm[2 * i] * Math.Sin(phase);
        }
        return Math.Sqrt(real * real + imaginary * imaginary);
    }
}
