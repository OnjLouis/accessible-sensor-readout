using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Media;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm
{
    private const int DiskTopologySettleMilliseconds = 400;
    private CancellationTokenSource diskAudioStop;
    private DiskActivityAudio diskAudioEngine;
    private Dictionary<string, int> diskAudioIndexes;
    private IntPtr diskAudioNotification;
    private int diskAudioTopologyRevision;
    private Task diskAudioWorker;
    private bool diskAudioClosing;
    private bool diskActivityHotKeyRegistered;
    private event Action DiskAudioStatusChanged;
    private string diskAudioStatusKey = "diskAudio.off";
    private string diskAudioStatusText = "Disk activity sounds off.";

    private static Dictionary<string, DiskSoundSetting> CloneDiskActivitySounds(Dictionary<string, DiskSoundSetting> settings)
    {
        var result = new Dictionary<string, DiskSoundSetting>(StringComparer.OrdinalIgnoreCase);
        if (settings != null)
        {
            foreach (var pair in settings)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                {
                    result[pair.Key] = pair.Value.Copy();
                }
            }
        }
        return result;
    }

    private static PhysicalStorageDevice[] FindDiskActivityDevices()
    {
        return GetPhysicalStorageDevices()
            .Where(d => !string.IsNullOrWhiteSpace(d.PnpDeviceId))
            .OrderBy(d => d.Index).ToArray();
    }

    private static Dictionary<int, SortedSet<string>> FindDiskActivityDriveLetters()
    {
        var letters = new Dictionary<int, SortedSet<string>>();
        var partitions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var query = new ManagementObjectSearcher("SELECT DiskIndex, DeviceID FROM Win32_DiskPartition"))
            {
                foreach (var partition in ExecuteWmiQuery(query, "WMI"))
                {
                    int index;
                    var relativePath = partition.Path.RelativePath;
                    if (!string.IsNullOrEmpty(relativePath) && int.TryParse(GetWmiPropertyText(partition, "DiskIndex"), out index))
                        partitions[relativePath] = index;
                }
            }
            using (var query = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (var association in ExecuteWmiQuery(query, "WMI"))
                {
                    var partition = new ManagementPath(GetWmiPropertyText(association, "Antecedent"));
                    int index;
                    if (!partitions.TryGetValue(partition.RelativePath, out index)) continue;
                    var volume = new ManagementPath(GetWmiPropertyText(association, "Dependent"));
                    var match = Regex.Match(volume.RelativePath, "^Win32_LogicalDisk\\.DeviceID=\"([A-Z]:)\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    SortedSet<string> set;
                    if (!letters.TryGetValue(index, out set)) letters[index] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(match.Groups[1].Value.ToUpperInvariant());
                }
            }
        }
        catch (ManagementException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (UnauthorizedAccessException) { }
        return letters;
    }

    private static string DiskActivityDriveLabel(PhysicalStorageDevice disk, Dictionary<int, SortedSet<string>> letters)
    {
        SortedSet<string> names;
        return letters.TryGetValue(disk.Index, out names) && names.Count != 0
            ? string.Format(T("diskAudio.driveLabel", "{0} ({1})"), string.Join(", ", names), disk.Model)
            : string.Format(T("diskAudio.disk", "Disk {0}: {1}"), disk.Index, disk.Model);
    }

    private static Dictionary<int, DiskSoundSetting> ResolveDiskActivityDevices(
        IEnumerable<PhysicalStorageDevice> devices, Dictionary<string, DiskSoundSetting> configured)
    {
        var result = new Dictionary<int, DiskSoundSetting>();
        foreach (var disk in devices)
        {
            DiskSoundSetting mix;
            if (configured.TryGetValue(DiskActivityDeviceKey(disk), out mix) && mix.Enabled)
            {
                result[disk.Index] = mix;
            }
        }
        return result;
    }

    private static string DiskActivityDeviceKey(PhysicalStorageDevice disk)
    {
        return Sha256Hex(disk.PnpDeviceId.Trim().ToUpperInvariant());
    }

    private void ToggleDiskActivityAudio()
    {
        if (diskAudioStop == null)
        {
            StartDiskActivityAudio(true);
        }
        else if (!diskAudioStop.IsCancellationRequested)
        {
            DisableDiskActivityAudio();
        }
    }

    private async void StartDiskActivityAudio(bool notifyToggle)
    {
        if (diskAudioStop != null || diskAudioClosing)
        {
            return;
        }
        if (Environment.OSVersion.Version < new Version(6, 2))
        {
            SetDiskActivityStatus("diskAudio.unsupported", "Disk activity sounds require Windows 8 or later.", notifyToggle);
            return;
        }

        var configured = CloneDiskActivitySounds(settings.DiskActivitySounds);
        if (!configured.Values.Any(s => s.Enabled))
        {
            ReportNoDiskActivityDevices(notifyToggle);
            return;
        }

        var stop = new CancellationTokenSource();
        var topologyAtStart = diskAudioTopologyRevision;
        diskAudioStop = stop;
        SetDiskActivityStatus("diskAudio.starting", "Starting disk activity sounds...", false);
        var notifyStarted = new Progress<Tuple<DiskActivityAudio, PhysicalStorageDevice[]>>(started =>
        {
            if (ReferenceEquals(diskAudioStop, stop) && !stop.IsCancellationRequested && !diskAudioClosing)
            {
                diskAudioEngine = started.Item1;
                if (topologyAtStart == diskAudioTopologyRevision)
                    ApplyDiskActivityDeviceMap(started.Item2);
                else
                    RefreshDiskActivityDeviceMap();
                RememberDiskActivityListening(true);
                SetDiskActivityStatus("diskAudio.on", "Disk activity sounds on.", false);
                if (notifyToggle) NotifyDiskActivityToggle();
            }
        });
        var hasDisks = false;
        try
        {
            diskAudioWorker = Task.Factory.StartNew(() =>
            {
                var devices = FindDiskActivityDevices();
                var disks = ResolveDiskActivityDevices(devices, configured);
                hasDisks = disks.Count != 0;
                if (hasDisks)
                {
                    // Wait for the UI to confirm that the inventory has not been invalidated by hotplug.
                    var engine = new DiskActivityAudio(new Dictionary<int, DiskSoundSetting>());
                    engine.Run(stop.Token, () => ((IProgress<Tuple<DiskActivityAudio, PhysicalStorageDevice[]>>)notifyStarted).Report(Tuple.Create(engine, devices)));
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await diskAudioWorker;
            if (!hasDisks && !stop.IsCancellationRequested && !diskAudioClosing)
            {
                ReportNoDiskActivityDevices(notifyToggle);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!diskAudioClosing && !stop.IsCancellationRequested)
            {
                LogError("Disk activity sounds failed: " + ex.Message);
                SetDiskActivityStatus("diskAudio.failed", "Disk activity sounds could not start or stopped unexpectedly. Check the audio output and try again.", notifyToggle);
            }
        }
        finally
        {
            diskAudioWorker = null;
            diskAudioStop = null;
            diskAudioEngine = null;
            diskAudioIndexes = null;
            var cancelled = stop.IsCancellationRequested;
            stop.Dispose();
            if (!diskAudioClosing)
            {
                UpdateDiskActivityDialog();
                if (!hasDisks && !cancelled && topologyAtStart != diskAudioTopologyRevision && settings.DiskActivityListening)
                    RefreshDiskActivityDeviceMap();
            }
        }
    }

    private void ReportNoDiskActivityDevices(bool speak)
    {
        SetDiskActivityStatus("diskAudio.noEnabled", "No connected physical disk is enabled.", speak);
    }

    private void SetDiskActivityStatus(string key, string fallback, bool speak)
    {
        diskAudioStatusKey = key;
        diskAudioStatusText = fallback;
        var message = T(key, fallback);
        statusLabel.Text = message;
        if (speak)
        {
            SpeakTextWithScreenReader(message, "disk activity sounds");
        }
        UpdateDiskActivityDialog();
    }

    private void UpdateDiskActivityDialog()
    {
        var changed = DiskAudioStatusChanged;
        if (changed != null) changed();
    }

    private void RememberDiskActivityListening(bool enabled)
    {
        if (settings.DiskActivityListening != enabled)
        {
            settings.DiskActivityListening = enabled;
            SaveSettings(settings);
        }
    }

    private void NotifyDiskActivityToggle()
    {
        if (string.IsNullOrEmpty(ResolveSoundPath(settings.DiskActivityToggleSoundFile)))
        {
            SpeakTextWithScreenReader(T(diskAudioStatusKey, diskAudioStatusText), "disk activity sounds");
        }
        else
        {
            PlaySoundFile(settings.DiskActivityToggleSoundFile);
        }
    }

    private void DisableDiskActivityAudio()
    {
        RememberDiskActivityListening(false);
        if (diskAudioStop != null) diskAudioStop.Cancel();
        SetDiskActivityStatus("diskAudio.off", "Disk activity sounds off.", false);
        NotifyDiskActivityToggle();
    }

    private void RegisterDiskActivityDeviceNotifications()
    {
        diskAudioNotification = DiskInterfaceNotifications.Register(Handle);
        if (diskAudioNotification == IntPtr.Zero)
            LogMessage("Debug", "Disk interface notifications unavailable; using device-change fallback.");
    }

    private void CheckDiskActivityDeviceChange(Message message)
    {
        if (!diskAudioClosing && settings != null && (diskAudioStop != null || settings.DiskActivityListening) &&
            DiskInterfaceNotifications.IsDiskChange(message, diskAudioNotification != IntPtr.Zero))
        {
            // Leave the trace running, but silence the old indexes before Windows can reuse them.
            diskAudioIndexes = null;
            if (diskAudioEngine != null)
                diskAudioEngine.UpdateMix(new Dictionary<int, DiskSoundSetting>());
            RefreshDiskActivityDeviceMap();
        }
    }

    private void ApplyDiskActivityDeviceMap(PhysicalStorageDevice[] devices)
    {
        diskAudioIndexes = devices.GroupBy(DiskActivityDeviceKey)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        diskAudioEngine.UpdateMix(ResolveDiskActivityDevices(devices, settings.DiskActivitySounds));
    }

    private async void RefreshDiskActivityDeviceMap()
    {
        var revision = ++diskAudioTopologyRevision;
        await Task.Delay(DiskTopologySettleMilliseconds);
        if (diskAudioClosing || diskAudioTopologyRevision != revision) return;
        if (diskAudioStop == null)
        {
            if (settings.DiskActivityListening) StartDiskActivityAudio(false);
            return;
        }

        var engine = diskAudioEngine;
        var session = diskAudioStop;
        if (engine == null || session.IsCancellationRequested) return;
        var devices = await Task.Run(() => FindDiskActivityDevices());
        if (!diskAudioClosing && diskAudioTopologyRevision == revision &&
            ReferenceEquals(diskAudioStop, session) && !session.IsCancellationRequested)
            ApplyDiskActivityDeviceMap(devices);
    }

    private void StopDiskActivityForShutdown()
    {
        diskAudioClosing = true;
        if (diskAudioNotification != IntPtr.Zero)
        {
            DiskInterfaceNotifications.UnregisterDeviceNotification(diskAudioNotification);
            diskAudioNotification = IntPtr.Zero;
        }
        if (diskAudioStop == null)
        {
            return;
        }
        diskAudioStop.Cancel();
        if (diskAudioWorker != null)
        {
            try { diskAudioWorker.Wait(1000); }
            catch (AggregateException) { }
        }
    }

    private bool TryChangeDiskActivityHotKey(string desired)
    {
        var parsed = ParseHotKey(desired);
        if (parsed != null && parsed.Key == Keys.K && parsed.Modifiers == (NativeMethods.ModControl | NativeMethods.ModShift))
        {
            return false;
        }
        if (string.Equals(desired, settings.DiskActivityHotKey, StringComparison.OrdinalIgnoreCase) &&
            (desired.Length == 0 || diskActivityHotKeyRegistered))
        {
            return true;
        }
        var window = hotKeyWindow == null ? Handle : hotKeyWindow.Handle;
        NativeMethods.UnregisterHotKey(window, DiskActivityHotKeyId);
        diskActivityHotKeyRegistered = desired.Length != 0 &&
            RegisterGlobalHotKey(desired, DiskActivityHotKeyId, "disk activity sounds");
        if (desired.Length != 0 && !diskActivityHotKeyRegistered)
        {
            if (!string.Equals(desired, settings.DiskActivityHotKey, StringComparison.OrdinalIgnoreCase))
            {
                diskActivityHotKeyRegistered = RegisterGlobalHotKey(settings.DiskActivityHotKey, DiskActivityHotKeyId, "disk activity sounds");
            }
            return false;
        }
        settings.DiskActivityHotKey = desired;
        return true;
    }

    private void ShowDiskActivityDialog()
    {
        var title = T("diskAudio.title", "Disk activity sounds");
        using (var preview = new SoundPlayer())
        using (var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            ClientSize = new Size(610, 525), MinimumSize = new Size(490, 550), AutoScaleMode = AutoScaleMode.Font
        })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            dialog.Controls.Add(layout);
            var diskLabel = T("diskAudio.disks", "Physical disks");
            var diskList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Height = 105, AccessibleName = diskLabel };
            ControlHelpCatalog.Bind(diskList, "DiskActivityList", diskLabel);
            AddDiskActivityRow(layout, new Label { Text = diskLabel, AutoSize = true });
            AddDiskActivityRow(layout, diskList);
            var enabled = new CheckBox { Text = T("diskAudio.enableDisk", "Enable this disk"), AutoSize = true };
            ControlHelpCatalog.Bind(enabled, "DiskActivityEnable", enabled.Text);
            AddDiskActivityRow(layout, enabled);
            var volume = AddDiskActivitySlider(layout, T("diskAudio.volume", "Volume (%)"), "DiskActivityVolume");
            var pan = AddDiskActivitySlider(layout, T("diskAudio.pan", "Pan"), "DiskActivityPan", true);
            var readPitch = AddDiskActivitySlider(layout, T("diskAudio.readPitch", "Re&ad pitch (%)"), "DiskActivityReadPitch",
                minimum: DiskSoundSetting.MinimumPitchPercent, maximum: DiskSoundSetting.MaximumPitchPercent, initialValue: DiskSoundSetting.DefaultPitchPercent);
            var writePitch = AddDiskActivitySlider(layout, T("diskAudio.writePitch", "Wr&ite pitch (%)"), "DiskActivityWritePitch",
                minimum: DiskSoundSetting.MinimumPitchPercent, maximum: DiskSoundSetting.MaximumPitchPercent, initialValue: DiskSoundSetting.DefaultPitchPercent);
            var bass = AddDiskActivitySlider(layout, T("diskAudio.bass", "Bass"), "DiskActivityBass");
            var mid = AddDiskActivitySlider(layout, T("diskAudio.mid", "Midrange"), "DiskActivityMid");
            var treble = AddDiskActivitySlider(layout, T("diskAudio.treble", "Treble"), "DiskActivityTreble");
            var hotkeyLabel = T("diskAudio.hotkey", "Global toggle hotkey");
            var hotkey = PreferencesForm.CreateHotKeyBox(settings.DiskActivityHotKey, StripMenuMnemonic(hotkeyLabel));
            ControlHelpCatalog.Bind(hotkey, "DiskActivityHotkey", hotkey.AccessibleName);
            AddDiskActivityControl(layout, hotkeyLabel, hotkey);
            var feedbackLabel = T("diskAudio.feedback", "Start/stop &feedback");
            var feedback = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
                AccessibleName = StripMenuMnemonic(feedbackLabel)
            };
            PopulateProcessWatchSoundBox(feedback, settings.DiskActivityToggleSoundFile, T("diskAudio.spokenFeedback", "Spoken"));
            ControlHelpCatalog.Bind(feedback, "DiskActivityFeedback", feedback.AccessibleName);
            AddDiskActivityControl(layout, feedbackLabel, feedback);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var previewRead = new DiskPreviewButton { Text = T("diskAudio.previewRead", "Preview read"), AutoSize = true };
            var previewWrite = new DiskPreviewButton { Text = T("diskAudio.previewWrite", "Preview write"), AutoSize = true };
            var start = new Button { Text = T("diskAudio.start", "Start"), AutoSize = true };
            var stop = new Button { Text = T("diskAudio.stop", "Stop"), AutoSize = true };
            var reset = new Button { Text = T("diskAudio.resetMix", "Reset mi&x to defaults"), AutoSize = true };
            var close = CreateCloseButton();
            buttons.Controls.AddRange(new Control[] { previewRead, previewWrite, reset, start, stop, close });
            ControlHelpCatalog.Bind(previewRead, "DiskActivityPreviewRead", previewRead.Text);
            ControlHelpCatalog.Bind(previewWrite, "DiskActivityPreviewWrite", previewWrite.Text);
            ControlHelpCatalog.Bind(reset, "DiskActivityReset", reset.Text);
            ControlHelpCatalog.Bind(start, "DiskActivityStart", start.Text);
            ControlHelpCatalog.Bind(stop, "DiskActivityStop", stop.Text);
            ControlHelpCatalog.Bind(close, "DiskActivityClose", close.Text);
            AddDiskActivityRow(layout, buttons);
            var status = new Label { AutoSize = true, Dock = DockStyle.Fill };
            AddDiskActivityRow(layout, status);
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            close.DialogResult = DialogResult.OK;
            UiMnemonics.ForControls(dialog.Controls);

            var devices = new PhysicalStorageDevice[0];
            var scanning = true;
            var loading = false;
            var changed = false;
            var revertingHotkey = false;
            DiskSoundSetting selected = null;
            string selectedKey = null;
            Action update = () =>
            {
                var running = diskAudioStop != null && !diskAudioStop.IsCancellationRequested;
                start.Enabled = !scanning && diskAudioStop == null && devices.Length != 0;
                stop.Enabled = running || settings.DiskActivityListening;
                status.Text = scanning ? T("diskAudio.scanning", "Finding physical disks...") :
                    devices.Length == 0 ? T("diskAudio.noDisks", "No physical disks available.") :
                    T(diskAudioStatusKey, diskAudioStatusText);
            };
            Action readMix = () =>
            {
                if (selected == null || loading)
                {
                    return;
                }
                selected.Enabled = enabled.Checked;
                selected.Volume = (int)volume.Value;
                selected.ReadPitchPercent = readPitch.Value;
                selected.WritePitchPercent = writePitch.Value;
                settings.DiskActivitySounds[selectedKey] = selected.Copy();
                int diskNumber;
                if (diskAudioIndexes != null && !diskAudioStop.IsCancellationRequested &&
                    diskAudioIndexes.TryGetValue(selectedKey, out diskNumber))
                {
                    diskAudioEngine.UpdateDiskMix(diskNumber, selected);
                }
                changed = true;
            };
            Action showMix = () =>
            {
                loading = true;
                var values = selected ?? new DiskSoundSetting();
                enabled.Checked = values.Enabled;
                volume.Value = values.Volume;
                pan.Value = (values.Pan + 100) / 2;
                readPitch.Value = values.ReadPitchPercent;
                writePitch.Value = values.WritePitchPercent;
                bass.Value = DiskSoundSetting.EqPosition(values.Bass);
                mid.Value = DiskSoundSetting.EqPosition(values.Mid);
                treble.Value = DiskSoundSetting.EqPosition(values.Treble);
                foreach (var control in new Control[] { enabled, volume, pan, readPitch, writePitch, bass, mid, treble, previewRead, previewWrite, reset })
                {
                    control.Enabled = selected != null;
                }
                loading = false;
            };
            diskList.SelectedIndexChanged += delegate
            {
                selected = null;
                selectedKey = null;
                if (diskList.SelectedIndex >= 0)
                {
                    selectedKey = DiskActivityDeviceKey(devices[diskList.SelectedIndex]);
                    DiskSoundSetting saved;
                    selected = settings.DiskActivitySounds.TryGetValue(selectedKey, out saved) ? saved.Copy() : new DiskSoundSetting();
                }
                showMix();
            };
            enabled.CheckedChanged += delegate { readMix(); };
            readPitch.ValueChanged += delegate { readMix(); };
            writePitch.ValueChanged += delegate { readMix(); };
            pan.ValueChanged += delegate
            {
                if (!loading && selected != null)
                {
                    selected.Pan = pan.Value * 2 - 100;
                    readMix();
                }
            };
            foreach (var slider in new[] { volume, bass, mid, treble })
            {
                slider.ValueChanged += delegate(object sender, EventArgs args)
                {
                    if (loading || selected == null) return;
                    // Keep exact legacy dB values until the corresponding band is moved.
                    if (sender == bass) selected.Bass = DiskSoundSetting.EqDecibels(bass.Value);
                    if (sender == mid) selected.Mid = DiskSoundSetting.EqDecibels(mid.Value);
                    if (sender == treble) selected.Treble = DiskSoundSetting.EqDecibels(treble.Value);
                    readMix();
                };
            }
            Action<bool, bool> play = (write, repeat) =>
            {
                if (selected == null) return;
                try
                {
                    preview.Stop();
                    if (preview.Stream != null) preview.Stream.Dispose();
                    preview.Stream = CreateDiskActivityPreview(selected, write);
                    if (repeat) preview.PlayLooping();
                    else preview.Play();
                }
                catch (Exception ex)
                {
                    LogError("Disk activity preview failed: " + ex.Message);
                    status.Text = T("diskAudio.previewFailed", "The preview could not play.");
                }
            };
            previewRead.PreviewRequested += repeat => play(false, repeat);
            previewWrite.PreviewRequested += repeat => play(true, repeat);
            previewRead.PreviewReleased += preview.Stop;
            previewWrite.PreviewReleased += preview.Stop;
            reset.Click += delegate
            {
                if (selected == null) return;
                selected.ResetMix();
                showMix();
                readMix();
                SpeakTextWithScreenReader(T("diskAudio.mixReset", "Selected disk mix reset to defaults."), "disk activity sounds");
            };
            stop.Click += delegate { DisableDiskActivityAudio(); };
            start.Click += delegate
            {
                if (ResolveDiskActivityDevices(devices, settings.DiskActivitySounds).Count == 0)
                {
                    MessageBox.Show(dialog, T("diskAudio.noEnabled", "No connected physical disk is enabled."), title);
                    enabled.Focus();
                    return;
                }
                StartDiskActivityAudio(true);
            };
            hotkey.TextChanged += delegate
            {
                if (revertingHotkey) return;
                if (!TryChangeDiskActivityHotKey(NormalizeHotKeyText(hotkey.Text)))
                {
                    revertingHotkey = true;
                    hotkey.Text = settings.DiskActivityHotKey;
                    revertingHotkey = false;
                    MessageBox.Show(dialog, T("diskAudio.hotkeyInUse", "That global hotkey is already in use. Choose another combination."), title);
                    hotkey.Focus();
                    return;
                }
                changed = true;
            };
            feedback.SelectedIndexChanged += delegate
            {
                settings.DiskActivityToggleSoundFile = ProcessWatchSelectedSound(feedback);
                changed = true;
                if (feedback.Focused) PreviewProcessWatchSound(feedback);
            };
            update();
            showMix();
            dialog.Shown += async delegate
            {
                var found = await Task.Run(() => Tuple.Create(FindDiskActivityDevices(), FindDiskActivityDriveLetters()));
                if (dialog.IsDisposed || !dialog.Visible) return;
                devices = found.Item1.OrderBy(d => !found.Item2.ContainsKey(d.Index))
                    .ThenBy(d => found.Item2.ContainsKey(d.Index) ? found.Item2[d.Index].Min : "")
                    .ThenBy(d => d.Index).ToArray();
                foreach (var disk in devices)
                {
                    diskList.Items.Add(DiskActivityDriveLabel(disk, found.Item2));
                }
                if (diskList.Items.Count > 0) diskList.SelectedIndex = 0;
                scanning = false;
                update();
            };
            DiskAudioStatusChanged += update;
            try
            {
                dialog.ShowDialog(this);
            }
            finally
            {
                DiskAudioStatusChanged -= update;
                preview.Stop();
                if (preview.Stream != null) preview.Stream.Dispose();
                if (changed) SaveSettings(settings);
            }
        }
    }

    private static void AddDiskActivityControl(TableLayoutPanel panel, string text, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = text, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void AddDiskActivityRow(TableLayoutPanel panel, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private sealed class DiskLevelTrackBar : TrackBar
    {
        private const int KeyDownMessage = 0x0100;
        private const int KeyUpMessage = 0x0101;

        protected override void WndProc(ref Message message)
        {
            // Native horizontal sliders treat Up like Left. Audio levels increase on Up.
            if (message.Msg == KeyDownMessage || message.Msg == KeyUpMessage)
            {
                switch ((Keys)message.WParam.ToInt32())
                {
                    case Keys.Up: message.WParam = (IntPtr)Keys.Down; break;
                    case Keys.Down: message.WParam = (IntPtr)Keys.Up; break;
                    case Keys.PageUp: message.WParam = (IntPtr)Keys.PageDown; break;
                    case Keys.PageDown: message.WParam = (IntPtr)Keys.PageUp; break;
                }
            }
            base.WndProc(ref message);
        }
    }

    private static TrackBar AddDiskActivitySlider(TableLayoutPanel panel, string name, string helpTopic,
        bool pan = false, int minimum = 0, int maximum = 100, int initialValue = 50)
    {
        var accessibleName = StripMenuMnemonic(name);
        var slider = pan ? new TrackBar() : new DiskLevelTrackBar();
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.AccessibleName = accessibleName;
        slider.Value = initialValue;
        slider.SmallChange = 1;
        slider.LargeChange = 10;
        slider.TickStyle = TickStyle.None;
        slider.Dock = DockStyle.Fill;
        slider.AutoSize = false;
        slider.Height = 28;
        var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(100, 0) };
        Action update = () => value.Text = pan ? FormatDiskActivityPan(slider.Value) : slider.Value.ToString() + "%";
        slider.ValueChanged += delegate { update(); };
        update();
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(slider, 0, 0);
        row.Controls.Add(value, 1, 0);
        ControlHelpCatalog.Bind(slider, helpTopic, accessibleName);
        AddDiskActivityControl(panel, name, row);
        return slider;
    }

    private static string FormatDiskActivityPan(int value)
    {
        if (value == 0) return T("diskAudio.hardLeft", "Hard left");
        if (value == 50) return T("diskAudio.center", "Center");
        if (value == 100) return T("diskAudio.hardRight", "Hard right");
        var text = value < 50 ? T("diskAudio.panLeft", "Left {0}%") : T("diskAudio.panRight", "Right {0}%");
        return string.Format(text, Math.Abs(value - 50) * 2);
    }

    private const int DiskPreviewClicks = 10;
    private const int DiskPreviewIntervalMilliseconds = 120;

    private static MemoryStream CreateDiskActivityPreview(DiskSoundSetting setting, bool write)
    {
        var samples = DiskActivityAudio.Synthesize(setting, write);
        var spacing = DiskActivityAudio.SampleRate * DiskPreviewIntervalMilliseconds / 1000 * 2;
        var gap = new byte[(spacing - samples.Length) * sizeof(short)];
        var bytes = spacing * DiskPreviewClicks * sizeof(short);
        var wave = new MemoryStream(44 + bytes);
        using (var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + bytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(DiskActivityAudio.SampleRate);
            writer.Write(DiskActivityAudio.SampleRate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(bytes);
            for (var i = 0; i < DiskPreviewClicks; i++)
            {
                foreach (var sample in samples) writer.Write(sample);
                writer.Write(gap);
            }
        }
        wave.Position = 0;
        return wave;
    }
}
