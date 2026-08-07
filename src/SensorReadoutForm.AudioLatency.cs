using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class AudioLatencyRoutineStats
    {
        public long Count;
        public double TotalMicroseconds;
        public double MaximumMicroseconds;
    }

    private sealed class AudioLatencyImage
    {
        public ulong BaseAddress;
        public ulong EndAddress;
        public string Path = "";
    }

    private sealed class AudioLatencyDriverStats
    {
        public string Name = "";
        public string Path = "";
        public long Count;
        public double TotalMicroseconds;
        public double MaximumMicroseconds;
    }

    private sealed class AudioLatencySample
    {
        public DateTime SampledLocal;
        public double IntervalSeconds;
        public long DpcCount;
        public long IsrCount;
        public long HardFaultCount;
        public double MaximumDpcMicroseconds;
        public double MaximumIsrMicroseconds;
        public ulong MaximumDpcRoutine;
        public ulong MaximumIsrRoutine;
    }

    private sealed class AudioLatencyRun
    {
        public string SessionName = "";
        public DateTime StartedLocal;
        public DateTime StoppedLocal;
        public DateTime StopAtLocal;
        public string StopReason = "";
        public string Error = "";
        public string ReportPath = "";
        public bool StopRequested;
        public bool SpeakWhenStopped;
        public bool PlaySoundWhenStopped;
        public string CompletionSoundFile = "";
        public string CompletionMessage = "";
        public long DpcCount;
        public long IsrCount;
        public long HardFaultCount;
        public long EventsLost;
        public double MaximumDpcMicroseconds;
        public double MaximumIsrMicroseconds;
        public ulong MaximumDpcRoutine;
        public ulong MaximumIsrRoutine;
        public DateTime IntervalStartedLocal;
        public long IntervalDpcCount;
        public long IntervalIsrCount;
        public long IntervalHardFaultCount;
        public double IntervalMaximumDpcMicroseconds;
        public double IntervalMaximumIsrMicroseconds;
        public ulong IntervalMaximumDpcRoutine;
        public ulong IntervalMaximumIsrRoutine;
        public readonly Dictionary<ulong, AudioLatencyRoutineStats> DpcRoutines = new Dictionary<ulong, AudioLatencyRoutineStats>();
        public readonly Dictionary<ulong, AudioLatencyRoutineStats> IsrRoutines = new Dictionary<ulong, AudioLatencyRoutineStats>();
        public readonly Dictionary<string, long> HardFaultProcesses = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public readonly List<AudioLatencyImage> Images = new List<AudioLatencyImage>();
        public readonly List<AudioLatencySample> Samples = new List<AudioLatencySample>();
    }

    private readonly object audioLatencyLock = new object();
    private AudioLatencyRun activeAudioLatencyRun;
    private AudioLatencyRun latestAudioLatencyRun;
    private TraceEventSession activeAudioLatencySession;
    private Task activeAudioLatencyTask;
    private System.Windows.Forms.Timer audioLatencyTimer;

    private void ToggleAudioLatencyCommand()
    {
        if (IsAudioLatencyActive())
        {
            RequestAudioLatencyStop(T("ui.Stopped by user", "Stopped by user"));
            return;
        }

        ShowAudioLatencyDialog();
    }

    private void UpdateAudioLatencyMenuItem()
    {
        if (audioLatencyMenuItem == null)
        {
            return;
        }

        var text = IsAudioLatencyActive()
            ? T("ui.Stop audio latency &diagnostic", "Stop audio latency &diagnostic")
            : T("ui.Audio latency &diagnostic...", "Audio latency &diagnostic...");
        audioLatencyMenuItem.Text = WithShortcutText(text, "Ctrl+Shift+D");
        audioLatencyMenuItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.D;
        audioLatencyMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+D";
        audioLatencyMenuItem.ShowShortcutKeys = false;
        if (audioLatencyMonitorMenuItem != null)
        {
            lock (audioLatencyLock)
            {
                audioLatencyMonitorMenuItem.Available = activeAudioLatencyRun != null || latestAudioLatencyRun != null;
                audioLatencyMonitorMenuItem.Enabled = audioLatencyMonitorMenuItem.Available;
            }
        }
    }

    private bool IsAudioLatencyActive()
    {
        lock (audioLatencyLock)
        {
            return activeAudioLatencyRun != null && !activeAudioLatencyRun.StopRequested;
        }
    }

    private void ShowAudioLatencyDialog()
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Audio latency diagnostic", "Audio latency diagnostic");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 400);
            dialog.MinimumSize = new Size(590, 350);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 6
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Height = 82,
                Text = T("ui.Audio latency diagnostic intro", "Measure Windows DPC and interrupt service routine activity, hard page faults, and the drivers responsible. Start closes this dialog and measures in the background while you reproduce an audio problem. Sensor Readout saves an HTML report automatically when the test stops. No tracing runs until you start this diagnostic."),
                AccessibleName = T("a11y.Audio latency diagnostic explanation", "Audio latency diagnostic explanation")
            };
            root.Controls.Add(intro, 0, 0);

            var durationPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var durationLabel = new Label { Text = T("ui.Test duration", "Test &duration:"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            var durationBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 86400,
                Value = NormalizeAudioLatencyDurationSeconds(settings.AudioLatencyDurationSeconds),
                Width = 90,
                AccessibleName = T("a11y.Audio latency test duration in seconds", "Audio latency test duration in seconds")
            };
            var secondsLabel = new Label { Text = T("ui.seconds", "seconds"), AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
            var durationHint = new Label { Text = T("ui.Zero means until stopped", "0 means watch until stopped."), AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            durationPanel.Controls.Add(durationLabel);
            durationPanel.Controls.Add(durationBox);
            durationPanel.Controls.Add(secondsLabel);
            durationPanel.Controls.Add(durationHint);
            root.Controls.Add(durationPanel, 0, 1);

            var completionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var speakCheck = new CheckBox
            {
                Text = T("ui.Speak when test stops", "Spea&k when test stops"),
                AutoSize = true,
                Checked = settings.AudioLatencySpeakWhenStopped
            };
            var soundCheck = new CheckBox
            {
                Text = T("ui.Play sound when test stops", "&Play sound when test stops"),
                AutoSize = true,
                Checked = settings.AudioLatencyPlaySoundWhenStopped
            };
            var soundLabel = new Label { Text = T("ui.Sound:", "Sound:"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) };
            var soundBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 145,
                AccessibleName = T("a11y.Audio latency completion sound", "Audio latency completion sound")
            };
            PopulateProcessWatchSoundBox(soundBox, settings.AudioLatencySoundFile);
            var messageLabel = new Label { Text = T("ui.Message:", "Message:"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) };
            var messageBox = new TextBox
            {
                Width = 210,
                Text = FirstNonEmpty(settings.AudioLatencyCompletionMessage, T("ui.Audio latency test complete", "Audio latency test complete.")),
                AccessibleName = T("a11y.Audio latency completion message", "Audio latency completion message")
            };
            completionPanel.Controls.Add(speakCheck);
            completionPanel.Controls.Add(soundCheck);
            completionPanel.Controls.Add(soundLabel);
            completionPanel.Controls.Add(soundBox);
            completionPanel.Controls.Add(messageLabel);
            completionPanel.Controls.Add(messageBox);
            root.Controls.Add(completionPanel, 0, 2);

            var liveMonitorCheck = new CheckBox
            {
                Text = T("ui.Open live audio latency monitor when test starts", "Open live audio latency &monitor when test starts"),
                AutoSize = true,
                Checked = settings.AudioLatencyOpenLiveMonitor,
                AccessibleDescription = T("a11y.Live monitor can be closed without stopping the test.", "The live monitor can be closed without stopping the test. Reopen it from the Options menu.")
            };
            root.Controls.Add(liveMonitorCheck, 0, 3);

            var status = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = T("status.Ready to measure audio latency.", "Ready to measure audio latency.")
            };
            root.Controls.Add(status, 0, 4);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = CreateCloseButton();
            var startButton = new Button { Text = T("ui.Sta&rt", "&Start"), AutoSize = true };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(startButton);
            root.Controls.Add(buttons, 0, 5);

            Action updateCompletionState = delegate
            {
                soundLabel.Enabled = soundCheck.Checked;
                soundBox.Enabled = soundCheck.Checked;
                messageLabel.Enabled = speakCheck.Checked;
                messageBox.Enabled = speakCheck.Checked;
            };
            speakCheck.CheckedChanged += delegate { updateCompletionState(); };
            soundCheck.CheckedChanged += delegate { updateCompletionState(); };
            soundBox.SelectedIndexChanged += delegate
            {
                if (soundBox.Enabled)
                {
                    PreviewProcessWatchSound(soundBox);
                }
            };
            startButton.Click += delegate
            {
                settings.AudioLatencyDurationSeconds = NormalizeAudioLatencyDurationSeconds((int)durationBox.Value);
                settings.AudioLatencySpeakWhenStopped = speakCheck.Checked;
                settings.AudioLatencyPlaySoundWhenStopped = soundCheck.Checked;
                settings.AudioLatencySoundFile = Path.GetFileName(ProcessWatchSelectedSound(soundBox) ?? "");
                settings.AudioLatencyCompletionMessage = messageBox.Text ?? "";
                settings.AudioLatencyOpenLiveMonitor = liveMonitorCheck.Checked;
                SaveSettings(settings);
                StartAudioLatencyTest(
                    settings.AudioLatencyDurationSeconds,
                    speakCheck.Checked,
                    soundCheck.Checked,
                    settings.AudioLatencySoundFile,
                    messageBox.Text,
                    liveMonitorCheck.Checked);
                dialog.Close();
            };
            closeButton.Click += delegate { dialog.Close(); };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            dialog.Controls.Add(root);
            dialog.AcceptButton = startButton;
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate { intro.Focus(); };
            updateCompletionState();
            dialog.ShowDialog(this);
        }
    }

    private void StartAudioLatencyTest(int durationSeconds, bool speakWhenStopped, bool playSoundWhenStopped, string soundFile, string completionMessage, bool openLiveMonitor)
    {
        if (IsAudioLatencyActive())
        {
            statusLabel.Text = T("status.Audio latency test already active.", "An audio latency test is already active.");
            return;
        }

        var run = new AudioLatencyRun
        {
            SessionName = "SensorReadoutAudioLatency-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
            StartedLocal = DateTime.Now,
            IntervalStartedLocal = DateTime.Now,
            StopAtLocal = durationSeconds <= 0 ? DateTime.MinValue : DateTime.Now.AddSeconds(durationSeconds),
            SpeakWhenStopped = speakWhenStopped,
            PlaySoundWhenStopped = playSoundWhenStopped,
            CompletionSoundFile = Path.GetFileName(soundFile ?? ""),
            CompletionMessage = completionMessage ?? ""
        };

        lock (audioLatencyLock)
        {
            activeAudioLatencyRun = run;
            activeAudioLatencySession = null;
            activeAudioLatencyTask = Task.Factory.StartNew(
                () => RunAudioLatencyTrace(run),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        if (audioLatencyTimer == null)
        {
            audioLatencyTimer = new System.Windows.Forms.Timer { Interval = 500 };
            audioLatencyTimer.Tick += delegate { AudioLatencyTimerTick(); };
        }
        audioLatencyTimer.Start();
        statusLabel.Text = durationSeconds <= 0
            ? T("status.Audio latency test started.", "Audio latency test started. Use Control Shift D to stop it.")
            : string.Format(T("status.Audio latency timed test started.", "Audio latency test started for {0} seconds."), durationSeconds);
        SpeakTextWithScreenReader(statusLabel.Text, "audio latency");
        RefreshSensors(true, false, "audio-latency-started");
        if (openLiveMonitor)
        {
            ShowAudioLatencyLiveMonitor();
        }
    }

    private void RunAudioLatencyTrace(AudioLatencyRun run)
    {
        try
        {
            using (var session = new TraceEventSession(run.SessionName))
            {
                session.StopOnDispose = true;
                lock (audioLatencyLock)
                {
                    if (!ReferenceEquals(activeAudioLatencyRun, run))
                    {
                        return;
                    }
                    activeAudioLatencySession = session;
                }

                var keywords = KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.Interrupt |
                    KernelTraceEventParser.Keywords.ImageLoad |
                    KernelTraceEventParser.Keywords.MemoryHardFaults |
                    KernelTraceEventParser.Keywords.Process;
                session.EnableKernelProvider(keywords);

                var kernel = session.Source.Kernel;
                kernel.ImageLoadGroup += data => RecordAudioLatencyImage(run, data);
                kernel.PerfInfoDPC += data => RecordAudioLatencyRoutine(run, false, data.Routine, data.ElapsedTimeMSec * 1000.0);
                kernel.PerfInfoThreadedDPC += data => RecordAudioLatencyRoutine(run, false, data.Routine, data.ElapsedTimeMSec * 1000.0);
                kernel.PerfInfoTimerDPC += data => RecordAudioLatencyRoutine(run, false, data.Routine, data.ElapsedTimeMSec * 1000.0);
                kernel.PerfInfoISR += data => RecordAudioLatencyRoutine(run, true, data.Routine, data.ElapsedTimeMSec * 1000.0);
                kernel.MemoryHardFault += data => RecordAudioLatencyHardFault(run, data.ProcessName, data.ProcessID);

                lock (audioLatencyLock)
                {
                    if (run.StopRequested)
                    {
                        session.Source.StopProcessing();
                    }
                }
                session.Source.Process();
                run.EventsLost = Math.Max(0, session.EventsLost);
            }
        }
        catch (Exception ex)
        {
            run.Error = T(
                "status.Audio latency trace could not start.",
                "Windows tracing could not be started. Restart Sensor Readout and try again. If it still fails, use Help > Prepare support report.");
            LogMessage("Normal", "Audio latency diagnostic failed: " + ex);
        }
        finally
        {
            FinishAudioLatencyRun(run);
        }
    }

    private void RecordAudioLatencyImage(AudioLatencyRun run, ImageLoadTraceData data)
    {
        if (run == null || data == null || data.ImageBase == 0 || data.ImageSize <= 0)
        {
            return;
        }

        var end = data.ImageBase + (ulong)data.ImageSize;
        lock (audioLatencyLock)
        {
            run.Images.Add(new AudioLatencyImage
            {
                BaseAddress = data.ImageBase,
                EndAddress = end,
                Path = data.FileName ?? ""
            });
        }
    }

    private void RecordAudioLatencyRoutine(AudioLatencyRun run, bool isIsr, ulong routine, double microseconds)
    {
        if (run == null || double.IsNaN(microseconds) || double.IsInfinity(microseconds) || microseconds < 0)
        {
            return;
        }

        lock (audioLatencyLock)
        {
            var routines = isIsr ? run.IsrRoutines : run.DpcRoutines;
            AudioLatencyRoutineStats stats;
            if (!routines.TryGetValue(routine, out stats))
            {
                stats = new AudioLatencyRoutineStats();
                routines[routine] = stats;
            }
            stats.Count++;
            stats.TotalMicroseconds += microseconds;
            stats.MaximumMicroseconds = Math.Max(stats.MaximumMicroseconds, microseconds);
            if (isIsr)
            {
                run.IsrCount++;
                if (microseconds >= run.MaximumIsrMicroseconds)
                {
                    run.MaximumIsrMicroseconds = microseconds;
                    run.MaximumIsrRoutine = routine;
                }
                run.IntervalIsrCount++;
                if (microseconds >= run.IntervalMaximumIsrMicroseconds)
                {
                    run.IntervalMaximumIsrMicroseconds = microseconds;
                    run.IntervalMaximumIsrRoutine = routine;
                }
            }
            else
            {
                run.DpcCount++;
                if (microseconds >= run.MaximumDpcMicroseconds)
                {
                    run.MaximumDpcMicroseconds = microseconds;
                    run.MaximumDpcRoutine = routine;
                }
                run.IntervalDpcCount++;
                if (microseconds >= run.IntervalMaximumDpcMicroseconds)
                {
                    run.IntervalMaximumDpcMicroseconds = microseconds;
                    run.IntervalMaximumDpcRoutine = routine;
                }
            }
        }
    }

    private void RecordAudioLatencyHardFault(AudioLatencyRun run, string processName, int processId)
    {
        if (run == null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(processName)
            ? "PID " + processId.ToString(CultureInfo.InvariantCulture)
            : processName.Trim();
        lock (audioLatencyLock)
        {
            run.HardFaultCount++;
            run.IntervalHardFaultCount++;
            long count;
            run.HardFaultProcesses.TryGetValue(name, out count);
            run.HardFaultProcesses[name] = count + 1;
        }
    }

    private void AudioLatencyTimerTick()
    {
        AudioLatencyRun run;
        AudioLatencyLiveSnapshot snapshot = null;
        var monitorOpen = audioLatencyLiveForm != null && !audioLatencyLiveForm.IsDisposed;
        lock (audioLatencyLock)
        {
            run = activeAudioLatencyRun;
            if (run != null)
            {
                FinalizeAudioLatencySampleLocked(run, DateTime.Now, false);
            }
            if (monitorOpen)
            {
                snapshot = BuildAudioLatencyLiveSnapshotLocked();
            }
        }

        if (run == null)
        {
            if (audioLatencyTimer != null)
            {
                audioLatencyTimer.Stop();
            }
            return;
        }

        if (run.StopAtLocal != DateTime.MinValue && DateTime.Now >= run.StopAtLocal)
        {
            RequestAudioLatencyStop(T("ui.Duration complete", "Duration complete"));
            return;
        }

        statusLabel.Text = string.Format(
            T("status.Audio latency test running.", "Audio latency test running: {0} DPCs, {1} ISRs, {2} hard page faults."),
            run.DpcCount,
            run.IsrCount,
            run.HardFaultCount);
        UpdateAudioLatencyLiveMonitor(snapshot);
    }

    private void RequestAudioLatencyStop(string reason)
    {
        TraceEventSession session;
        lock (audioLatencyLock)
        {
            if (activeAudioLatencyRun == null || activeAudioLatencyRun.StopRequested)
            {
                return;
            }
            activeAudioLatencyRun.StopRequested = true;
            activeAudioLatencyRun.StopReason = FirstNonEmpty(reason, T("ui.Stopped by user", "Stopped by user"));
            session = activeAudioLatencySession;
        }

        statusLabel.Text = T("status.Stopping audio latency test...", "Stopping audio latency test...");
        if (session != null)
        {
            try { session.Source.StopProcessing(); } catch { }
        }
    }

    private void FinishAudioLatencyRun(AudioLatencyRun run)
    {
        run.StoppedLocal = DateTime.Now;
        if (string.IsNullOrWhiteSpace(run.StopReason))
        {
            run.StopReason = string.IsNullOrWhiteSpace(run.Error)
                ? T("ui.Duration complete", "Duration complete")
                : T("ui.Test failed", "Test failed");
        }

        lock (audioLatencyLock)
        {
            FinalizeAudioLatencySampleLocked(run, run.StoppedLocal, true);
            latestAudioLatencyRun = run;
            if (ReferenceEquals(activeAudioLatencyRun, run))
            {
                activeAudioLatencyRun = null;
                activeAudioLatencySession = null;
                activeAudioLatencyTask = null;
            }
        }

        if (IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(delegate
            {
                if (audioLatencyTimer != null)
                {
                    audioLatencyTimer.Stop();
                }
                SaveAudioLatencyReport(run);
                RefreshSensors(true, false, "audio-latency-complete");
                var message = string.IsNullOrWhiteSpace(run.Error)
                    ? FirstNonEmpty(run.CompletionMessage, T("ui.Audio latency test complete", "Audio latency test complete."))
                    : string.Format(T("status.Audio latency test failed.", "Audio latency test failed: {0}"), run.Error);
                statusLabel.Text = string.IsNullOrWhiteSpace(run.ReportPath)
                    ? message
                    : message + " " + string.Format(T("status.Report saved to.", "Report saved to {0}."), run.ReportPath);
                if (run.PlaySoundWhenStopped)
                {
                    PlaySoundFile(run.CompletionSoundFile);
                }
                if (run.SpeakWhenStopped)
                {
                    SpeakTextWithScreenReader(statusLabel.Text, "audio latency");
                }
                if (audioLatencyLiveForm != null && !audioLatencyLiveForm.IsDisposed)
                {
                    UpdateAudioLatencyLiveMonitor(BuildAudioLatencyLiveSnapshot());
                }
            }));
        }
        catch
        {
        }
    }

    private void StopAudioLatencyForShutdown()
    {
        CloseAudioLatencyLiveMonitor();
        Task task;
        lock (audioLatencyLock)
        {
            task = activeAudioLatencyTask;
        }
        RequestAudioLatencyStop(T("ui.Application closing", "Application closing"));
        if (task != null)
        {
            try { task.Wait(3000); } catch { }
        }

        AudioLatencyRun completedRun;
        lock (audioLatencyLock)
        {
            completedRun = latestAudioLatencyRun;
        }
        if (task == null || task.IsCompleted)
        {
            SaveAudioLatencyReport(completedRun);
        }
    }

    private IEnumerable<SensorRow> GetAudioLatencyRows()
    {
        lock (audioLatencyLock)
        {
            var run = activeAudioLatencyRun ?? latestAudioLatencyRun;
            var active = activeAudioLatencyRun != null;
            if (run == null)
            {
                return new[]
                {
                    AudioLatencyRow(T("reading.Status", "Status"), T("ui.Not measured", "Not measured"), "audio-latency-status", null)
                };
            }

            var dpcDrivers = BuildAudioLatencyDriverStats(run, false);
            var isrDrivers = BuildAudioLatencyDriverStats(run, true);
            var details = BuildAudioLatencyDetails(run, dpcDrivers, isrDrivers);
            if (!string.IsNullOrWhiteSpace(run.Error))
            {
                return new[] { AudioLatencyRow(T("reading.Status", "Status"), T("ui.Failed", "Failed") + ": " + run.Error, "audio-latency-status", details) };
            }

            var rows = new List<SensorRow>
            {
                AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Status", "Status"), active ? T("ui.Measuring", "Measuring") : T("ui.Complete", "Complete"), "audio-latency-status", details),
                AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Elapsed time", "Elapsed time"), FormatAudioLatencyDuration((active ? DateTime.Now : run.StoppedLocal) - run.StartedLocal), "audio-latency-duration", details),
            };
            var latestSample = run.Samples.LastOrDefault();
            if (latestSample != null)
            {
                var recentSamples = run.Samples.Where(s => s.SampledLocal >= latestSample.SampledLocal.AddSeconds(-60)).ToList();
                rows.Add(AudioLatencyGroupedRow(T("group.Current interval", "Current interval"), T("reading.Latest DPC peak", "Latest DPC peak"), FormatAudioLatencyMicroseconds(latestSample.MaximumDpcMicroseconds), "audio-latency-latest-dpc", details));
                rows.Add(AudioLatencyGroupedRow(T("group.Current interval", "Current interval"), T("reading.Latest ISR peak", "Latest ISR peak"), FormatAudioLatencyMicroseconds(latestSample.MaximumIsrMicroseconds), "audio-latency-latest-isr", details));
                rows.Add(AudioLatencyGroupedRow(T("group.Recent 60 seconds", "Recent 60 seconds"), T("reading.Recent DPC peak", "Recent 60-second DPC peak"), FormatAudioLatencyMicroseconds(recentSamples.Max(s => s.MaximumDpcMicroseconds)), "audio-latency-recent-dpc", details));
                rows.Add(AudioLatencyGroupedRow(T("group.Recent 60 seconds", "Recent 60 seconds"), T("reading.Recent ISR peak", "Recent 60-second ISR peak"), FormatAudioLatencyMicroseconds(recentSamples.Max(s => s.MaximumIsrMicroseconds)), "audio-latency-recent-isr", details));
            }
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Maximum DPC duration", "Maximum DPC duration"), FormatAudioLatencyMicroseconds(run.MaximumDpcMicroseconds), "audio-latency-max-dpc", details));
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Maximum ISR duration", "Maximum ISR duration"), FormatAudioLatencyMicroseconds(run.MaximumIsrMicroseconds), "audio-latency-max-isr", details));
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.DPC count", "DPC count"), run.DpcCount.ToString("N0", CultureInfo.CurrentCulture), "audio-latency-dpc-count", details));
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.ISR count", "ISR count"), run.IsrCount.ToString("N0", CultureInfo.CurrentCulture), "audio-latency-isr-count", details));
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Hard page faults", "Hard page faults"), run.HardFaultCount.ToString("N0", CultureInfo.CurrentCulture), "audio-latency-hard-faults", details));
            rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Events lost", "Events lost"), run.EventsLost.ToString("N0", CultureInfo.CurrentCulture), "audio-latency-events-lost", details));
            var topDpc = dpcDrivers.FirstOrDefault();
            var topIsr = isrDrivers.FirstOrDefault();
            if (topDpc != null)
            {
                rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Highest DPC driver", "Highest DPC driver"), topDpc.Name + ", " + FormatAudioLatencyMicroseconds(topDpc.MaximumMicroseconds), "audio-latency-top-dpc-driver", details));
            }
            if (topIsr != null)
            {
                rows.Add(AudioLatencyGroupedRow(T("group.Entire test", "Entire test"), T("reading.Highest ISR driver", "Highest ISR driver"), topIsr.Name + ", " + FormatAudioLatencyMicroseconds(topIsr.MaximumMicroseconds), "audio-latency-top-isr-driver", details));
            }
            return rows;
        }
    }

    private static SensorRow AudioLatencyRow(string name, string value, string identifier, Dictionary<string, string> details)
    {
        return AudioLatencyGroupedRow(T("group.Latest audio latency test", "Latest test"), name, value, identifier, details);
    }

    private static SensorRow AudioLatencyGroupedRow(string group, string name, string value, string identifier, Dictionary<string, string> details)
    {
        return new SensorRow
        {
            Type = "Audio Latency",
            Hardware = group,
            Name = name,
            Identifier = identifier,
            DisplayValue = value,
            Source = "Windows ETW",
            Details = details
        };
    }

    private Dictionary<string, string> BuildAudioLatencyDetails(AudioLatencyRun run)
    {
        return BuildAudioLatencyDetails(run, BuildAudioLatencyDriverStats(run, false), BuildAudioLatencyDriverStats(run, true));
    }

    private Dictionary<string, string> BuildAudioLatencyDetails(AudioLatencyRun run, IEnumerable<AudioLatencyDriverStats> dpcDrivers, IEnumerable<AudioLatencyDriverStats> isrDrivers)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, T("detail.Started", "Started"), run.StartedLocal == DateTime.MinValue ? "" : run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddDetail(details, T("detail.Stopped", "Stopped"), run.StoppedLocal == DateTime.MinValue ? T("ui.In progress", "In progress") : run.StoppedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddDetail(details, T("detail.Stop reason", "Stop reason"), run.StopReason);
        AddDetail(details, T("detail.Duration", "Duration"), FormatAudioLatencyDuration((run.StoppedLocal == DateTime.MinValue ? DateTime.Now : run.StoppedLocal) - run.StartedLocal));
        AddDetail(details, T("reading.Maximum DPC duration", "Maximum DPC duration"), FormatAudioLatencyMicroseconds(run.MaximumDpcMicroseconds));
        AddDetail(details, T("reading.Maximum ISR duration", "Maximum ISR duration"), FormatAudioLatencyMicroseconds(run.MaximumIsrMicroseconds));
        AddDetail(details, T("reading.DPC count", "DPC count"), run.DpcCount.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, T("reading.ISR count", "ISR count"), run.IsrCount.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, T("reading.Hard page faults", "Hard page faults"), run.HardFaultCount.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, T("reading.Events lost", "Events lost"), run.EventsLost.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, T("detail.Guidance", "Guidance"), T("message.Audio latency interpretation guidance", "Microsoft recommends keeping individual DPC routines below 100 microseconds and ISR routines below 25 microseconds. Results still need to be interpreted alongside the actual audio problem."));
        AddDetail(details, T("ui.Error", "Error"), run.Error);
        AddDetail(details, T("detail.Report path", "Report path"), run.ReportPath);

        AddAudioLatencyDriverDetails(details, dpcDrivers, "DPC");
        AddAudioLatencyDriverDetails(details, isrDrivers, "ISR");
        var processIndex = 1;
        foreach (var process in run.HardFaultProcesses.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            AddDetail(details, "Audio latency hard fault process " + processIndex + " name", process.Key);
            AddDetail(details, "Audio latency hard fault process " + processIndex + " count", process.Value.ToString(CultureInfo.InvariantCulture));
            processIndex++;
        }
        return details;
    }

    private static void AddAudioLatencyDriverDetails(Dictionary<string, string> details, IEnumerable<AudioLatencyDriverStats> drivers, string kind)
    {
        var index = 1;
        foreach (var driver in (drivers ?? Enumerable.Empty<AudioLatencyDriverStats>()).Take(20))
        {
            var prefix = "Audio latency " + kind + " driver " + index + " ";
            AddDetail(details, prefix + "name", driver.Name);
            AddDetail(details, prefix + "path", driver.Path);
            AddDetail(details, prefix + "count", driver.Count.ToString(CultureInfo.InvariantCulture));
            AddDetail(details, prefix + "maximum duration", FormatAudioLatencyMicroseconds(driver.MaximumMicroseconds));
            AddDetail(details, prefix + "total duration", FormatAudioLatencyMicroseconds(driver.TotalMicroseconds));
            index++;
        }
    }

    private List<AudioLatencyDriverStats> BuildAudioLatencyDriverStats(AudioLatencyRun run, bool isIsr)
    {
        var routines = isIsr ? run.IsrRoutines : run.DpcRoutines;
        var grouped = new Dictionary<string, AudioLatencyDriverStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in routines)
        {
            string path;
            var name = ResolveAudioLatencyDriver(run.Images, pair.Key, out path);
            AudioLatencyDriverStats driver;
            var key = string.IsNullOrWhiteSpace(path) ? name : path;
            if (!grouped.TryGetValue(key, out driver))
            {
                driver = new AudioLatencyDriverStats { Name = name, Path = path };
                grouped[key] = driver;
            }
            driver.Count += pair.Value.Count;
            driver.TotalMicroseconds += pair.Value.TotalMicroseconds;
            driver.MaximumMicroseconds = Math.Max(driver.MaximumMicroseconds, pair.Value.MaximumMicroseconds);
        }
        return grouped.Values
            .OrderByDescending(d => d.MaximumMicroseconds)
            .ThenByDescending(d => d.TotalMicroseconds)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveAudioLatencyDriver(IEnumerable<AudioLatencyImage> images, ulong routine, out string path)
    {
        var image = (images ?? Enumerable.Empty<AudioLatencyImage>())
            .Where(i => i != null && routine >= i.BaseAddress && routine < i.EndAddress)
            .OrderByDescending(i => i.BaseAddress)
            .FirstOrDefault();
        path = image == null ? "" : image.Path ?? "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        return routine == 0
            ? T("ui.Unknown driver", "Unknown driver")
            : T("ui.Routine", "Routine") + " 0x" + routine.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string FormatAudioLatencyMicroseconds(double microseconds)
    {
        if (microseconds >= 1000.0)
        {
            return (microseconds / 1000.0).ToString("0.###", CultureInfo.CurrentCulture) + " ms";
        }
        return microseconds.ToString("0.###", CultureInfo.CurrentCulture) + " " + T("ui.microseconds", "microseconds");
    }

    private static string FormatAudioLatencyDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void SaveAudioLatencyReport(AudioLatencyRun run)
    {
        if (run == null || run.StartedLocal == DateTime.MinValue || !string.IsNullOrWhiteSpace(run.ReportPath))
        {
            return;
        }
        try
        {
            var reportsFolder = GetReportsFolderPath();
            Directory.CreateDirectory(reportsFolder);
            var path = Path.Combine(reportsFolder, "SensorReadout-AudioLatency-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html");
            File.WriteAllText(path, BuildAudioLatencyHtmlReport(run), Encoding.UTF8);
            run.ReportPath = path;
        }
        catch (Exception ex)
        {
            LogMessage("Normal", "Could not save audio latency report: " + ex);
        }
    }

    private string BuildAudioLatencyHtmlReport(AudioLatencyRun run)
    {
        var html = new StringBuilder();
        var reportTitle = T("ui.Sensor Readout audio latency report", "Sensor Readout audio latency report");
        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>" + HtmlEncode(reportTitle) + "</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.35}table{border-collapse:collapse;margin:0 0 1.2em 0}th,td{border:1px solid #888;padding:4px 8px;text-align:left}caption{font-weight:bold;text-align:left;margin:.5em 0}</style></head><body>");
        html.AppendLine("<h1>" + HtmlEncode(reportTitle) + "</h1>");
        html.AppendLine("<p>" + HtmlEncode(string.Format(T("message.Generated by Sensor Readout version.", "Generated by Sensor Readout {0}."), AppVersion)) + " <a href=\"" + HtmlEncode(ProjectUrl + "/releases/latest") + "\">" + HtmlEncode(T("ui.Download Sensor Readout", "Download Sensor Readout")) + "</a>.</p>");
        html.AppendLine("<p>" + HtmlEncode(T("message.Audio latency report privacy", "This report measures Windows DPC and ISR execution, hard page faults, and related drivers during an explicitly started test. It does not contain audio, keystrokes, file contents, or network contents.")) + "</p>");
        html.AppendLine("<table><caption>" + HtmlEncode(T("ui.Summary", "Summary")) + "</caption><thead><tr><th scope=\"col\">" + HtmlEncode(T("ui.Metric", "Metric")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Value", "Value")) + "</th></tr></thead><tbody>");
        AddAudioLatencyHtmlRow(html, T("detail.Started", "Started"), run.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddAudioLatencyHtmlRow(html, T("detail.Stopped", "Stopped"), run.StoppedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddAudioLatencyHtmlRow(html, T("detail.Duration", "Duration"), FormatAudioLatencyDuration(run.StoppedLocal - run.StartedLocal));
        AddAudioLatencyHtmlRow(html, T("detail.Stop reason", "Stop reason"), run.StopReason);
        AddAudioLatencyHtmlRow(html, T("reading.Maximum DPC duration", "Maximum DPC duration"), FormatAudioLatencyMicroseconds(run.MaximumDpcMicroseconds));
        AddAudioLatencyHtmlRow(html, T("reading.Maximum ISR duration", "Maximum ISR duration"), FormatAudioLatencyMicroseconds(run.MaximumIsrMicroseconds));
        AddAudioLatencyHtmlRow(html, T("reading.DPC count", "DPC count"), run.DpcCount.ToString(CultureInfo.InvariantCulture));
        AddAudioLatencyHtmlRow(html, T("reading.ISR count", "ISR count"), run.IsrCount.ToString(CultureInfo.InvariantCulture));
        AddAudioLatencyHtmlRow(html, T("reading.Hard page faults", "Hard page faults"), run.HardFaultCount.ToString(CultureInfo.InvariantCulture));
        AddAudioLatencyHtmlRow(html, T("reading.Events lost", "Events lost"), run.EventsLost.ToString(CultureInfo.InvariantCulture));
        AddAudioLatencyHtmlRow(html, T("ui.Error", "Error"), run.Error);
        html.AppendLine("</tbody></table>");
        AddAudioLatencyDriverTable(html, T("ui.DPC drivers", "DPC drivers"), BuildAudioLatencyDriverStats(run, false));
        AddAudioLatencyDriverTable(html, T("ui.ISR drivers", "ISR drivers"), BuildAudioLatencyDriverStats(run, true));
        html.AppendLine("<table><caption>" + HtmlEncode(T("ui.Hard page faults by process", "Hard page faults by process")) + "</caption><thead><tr><th scope=\"col\">" + HtmlEncode(T("ui.Process", "Process")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Count", "Count")) + "</th></tr></thead><tbody>");
        foreach (var process in run.HardFaultProcesses.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddAudioLatencyHtmlRow(html, process.Key, process.Value.ToString(CultureInfo.InvariantCulture));
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("<h2>" + HtmlEncode(T("ui.Interpreting results", "Interpreting results")) + "</h2><p>" + HtmlEncode(T("message.Audio latency interpretation", "Microsoft recommends keeping individual DPC routines below 100 microseconds and ISR routines below 25 microseconds. These are engineering guidelines, not a guarantee that audio will or will not glitch. Reproduce the actual problem during the test and consider repeated high-duration drivers, hard page faults, and lost events together.")) + "</p>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void AddAudioLatencyHtmlRow(StringBuilder html, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        html.AppendLine("<tr><th scope=\"row\">" + HtmlEncode(label) + "</th><td>" + HtmlEncode(value) + "</td></tr>");
    }

    private void AddAudioLatencyDriverTable(StringBuilder html, string title, IEnumerable<AudioLatencyDriverStats> drivers)
    {
        html.AppendLine("<table><caption>" + HtmlEncode(title) + "</caption><thead><tr><th scope=\"col\">" + HtmlEncode(T("ui.Driver", "Driver")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Count", "Count")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Maximum duration", "Maximum duration")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Total duration", "Total duration")) + "</th><th scope=\"col\">" + HtmlEncode(T("ui.Path", "Path")) + "</th></tr></thead><tbody>");
        foreach (var driver in drivers ?? Enumerable.Empty<AudioLatencyDriverStats>())
        {
            html.AppendLine("<tr><th scope=\"row\">" + HtmlEncode(driver.Name) + "</th><td>" + driver.Count.ToString(CultureInfo.InvariantCulture) + "</td><td>" + HtmlEncode(FormatAudioLatencyMicroseconds(driver.MaximumMicroseconds)) + "</td><td>" + HtmlEncode(FormatAudioLatencyMicroseconds(driver.TotalMicroseconds)) + "</td><td>" + HtmlEncode(driver.Path) + "</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }
}
