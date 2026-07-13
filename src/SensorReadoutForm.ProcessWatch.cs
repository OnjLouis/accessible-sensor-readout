using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class ProcessWatchSample
    {
        public DateTime LocalTime;
        public double ElapsedSeconds;
        public bool ProcessRunning;
        public double CpuPercent;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
        public int ThreadCount;
        public int HandleCount;
        public double GpuUsagePercent;
        public double DedicatedGpuBytes;
        public double SharedGpuBytes;
    }

    private sealed class ProcessWatchTarget
    {
        public int ProcessId;
        public string Name;
        public string Path;
        public string WindowTitle;
        public string Role;
        public string ParentName;
        public DateTime StartedLocal;

        public override string ToString()
        {
            var name = FirstNonEmpty(Name, "Process");
            var parts = new List<string> { name };
            if (!string.IsNullOrWhiteSpace(WindowTitle))
            {
                parts.Add("window " + WindowTitle);
            }
            if (!string.IsNullOrWhiteSpace(Role))
            {
                parts.Add(Role);
            }
            if (StartedLocal != DateTime.MinValue)
            {
                parts.Add("started " + StartedLocal.ToString("HH:mm:ss"));
            }
            if (!string.IsNullOrWhiteSpace(ParentName))
            {
                parts.Add("parent " + ParentName);
            }
            return string.Join("; ", parts.ToArray());
        }
    }

    private sealed class ProcessWatchSession
    {
        public int ProcessId;
        public string ProcessName = "";
        public string ProcessPath = "";
        public DateTime StartedLocal;
        public DateTime StoppedLocal;
        public DateTime AutoStopLocal;
        public bool ProcessExited;
        public string StopReason = "";
        public string OutputFormat = "html";
        public bool SpeakWhenStopped = true;
        public bool PlaySoundWhenStopped;
        public string CompletionSoundFile = "";
        public string CompletionMessage = "";
        public bool CompletionNotified;
        public string ReportPath = "";
        public readonly List<ProcessWatchSample> Samples = new List<ProcessWatchSample>();
    }

    private Timer processWatchTimer;
    private ProcessWatchSession activeProcessWatchSession;
    private TaskProcessSnapshot activeProcessWatchPreviousSnapshot;
    private DateTime activeProcessWatchPreviousUtc = DateTime.MinValue;
    private string processWatchSearchPrefix = "";
    private DateTime processWatchSearchLastKey = DateTime.MinValue;
    private static readonly object processWatchMetadataCacheLock = new object();
    private static Dictionary<int, ProcessWatchMetadata> cachedProcessWatchMetadata = new Dictionary<int, ProcessWatchMetadata>();
    private static DateTime cachedProcessWatchMetadataUtc = DateTime.MinValue;

    private void ToggleProcessWatchCommand()
    {
        if (IsProcessWatchActive())
        {
            StopAndSaveProcessWatch(T("ui.Stopped by user", "Stopped by user"));
            return;
        }

        ShowProcessWatchDialog();
    }

    private bool CanWatchSelectedProcessFromTree()
    {
        return IsTaskProcessInventoryRow(GetSelectedReadingRow());
    }

    private bool WatchSelectedProcessFromTree()
    {
        if (IsProcessWatchActive())
        {
            statusLabel.Text = T("status.Process watch already active.", "A process watch is already active. Stop it before starting another.");
            System.Media.SystemSounds.Beep.Play();
            return false;
        }

        var target = ProcessWatchTargetFromRow(GetSelectedReadingRow());
        if (target == null)
        {
            statusLabel.Text = T("status.Select a running process row.", "Select a running process row.");
            System.Media.SystemSounds.Beep.Play();
            return false;
        }

        StartProcessWatch(
            target,
            ProcessWatchDuration(settings.ProcessWatchDurationValue, settings.ProcessWatchDurationUnit),
            settings.ProcessWatchOutputFormat,
            settings.ProcessWatchSpeakWhenStopped,
            settings.ProcessWatchPlaySoundWhenStopped,
            settings.ProcessWatchSoundFile,
            settings.ProcessWatchCompletionMessage);
        SpeakTextWithScreenReader(statusLabel.Text, "process watch");
        return true;
    }

    private void UpdateProcessWatchMenuItem()
    {
        if (processWatchMenuItem == null)
        {
            return;
        }

        processWatchMenuItem.Text = IsProcessWatchActive()
            ? T("ui.S&top process watch", "S&top process watch")
            : T("ui.&Watch process...", "&Watch process...");
    }

    private void ShowProcessWatchDialog()
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Watch process", "Watch process");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(780, 560);
            dialog.MinimumSize = new Size(560, 380);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = T("ui.Process watch intro", "Choose a running process, output format, and optional duration. Start closes this dialog and watches in the background. Use Options > Stop process watch or Ctrl+Shift+W to stop; Sensor Readout saves the report automatically in Reports. Sensor Readout records resource counters only; it does not inspect private app data, keystrokes, files, or network contents. Saved reports may include the selected process name and executable path.")
            };
            root.Controls.Add(intro, 0, 0);

            var processList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                AccessibleName = T("a11y.Running processes", "Running processes")
            };
            root.Controls.Add(processList, 0, 1);

            var durationPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var durationLabel = new Label { Text = T("ui.Watch duration", "Watch duration:"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            var durationBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1440,
                Value = NormalizeProcessWatchDurationValue(settings.ProcessWatchDurationValue),
                Width = 80,
                AccessibleName = T("a11y.Process watch duration", "Watch duration")
            };
            AttachNumericReplaceOnFocus(durationBox);
            var durationUnit = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                AccessibleName = T("a11y.Process watch duration unit", "Watch duration unit")
            };
            durationUnit.Items.Add(T("ui.Minutes", "minutes"));
            durationUnit.Items.Add(T("ui.seconds", "seconds"));
            durationUnit.Items.Add(T("ui.Hours", "hours"));
            durationUnit.SelectedIndex = ProcessWatchDurationUnitIndex(settings.ProcessWatchDurationUnit);
            var formatLabel = new Label { Text = T("ui.Report format", "Report format:"), AutoSize = true, Margin = new Padding(14, 6, 6, 0) };
            var formatBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 110,
                AccessibleName = T("a11y.Process watch report format", "Process watch report format")
            };
            formatBox.Items.Add(T("ui.HTML", "HTML"));
            formatBox.Items.Add(T("ui.CSV", "CSV"));
            formatBox.SelectedIndex = string.Equals(settings.ProcessWatchOutputFormat, "csv", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var durationHint = new Label { Text = T("ui.Zero means until stopped", "0 means watch until stopped."), AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
            durationPanel.Controls.Add(durationLabel);
            durationPanel.Controls.Add(durationBox);
            durationPanel.Controls.Add(durationUnit);
            durationPanel.Controls.Add(formatLabel);
            durationPanel.Controls.Add(formatBox);
            durationPanel.Controls.Add(durationHint);
            root.Controls.Add(durationPanel, 0, 2);

            var completionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var speakCompleteCheck = new CheckBox
            {
                Text = T("ui.Speak when watch stops", "Speak when watch stops"),
                AutoSize = true,
                Checked = settings.ProcessWatchSpeakWhenStopped,
                AccessibleName = T("a11y.Speak when process watch stops", "Speak when process watch stops")
            };
            var soundCompleteCheck = new CheckBox
            {
                Text = T("ui.Play sound when watch stops", "Play sound when watch stops"),
                AutoSize = true,
                Checked = settings.ProcessWatchPlaySoundWhenStopped,
                AccessibleName = T("a11y.Play sound when process watch stops", "Play sound when process watch stops")
            };
            var soundLabel = new Label { Text = T("ui.Sound:", "Sound:"), AutoSize = true, Margin = new Padding(14, 6, 6, 0) };
            var soundBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 150,
                AccessibleName = T("a11y.Process watch completion sound", "Process watch completion sound")
            };
            PopulateProcessWatchSoundBox(soundBox, settings.ProcessWatchSoundFile);
            var messageLabel = new Label { Text = T("ui.Watch stopped message", "Message:"), AutoSize = true, Margin = new Padding(14, 6, 6, 0) };
            var messageBox = new TextBox
            {
                Width = 220,
                Text = FirstNonEmpty(settings.ProcessWatchCompletionMessage, T("ui.Process watch complete", "Process watch complete.")),
                AccessibleName = T("a11y.Process watch stopped message", "Process watch stopped message")
            };
            completionPanel.Controls.Add(speakCompleteCheck);
            completionPanel.Controls.Add(soundCompleteCheck);
            completionPanel.Controls.Add(soundLabel);
            completionPanel.Controls.Add(soundBox);
            completionPanel.Controls.Add(messageLabel);
            completionPanel.Controls.Add(messageBox);
            root.Controls.Add(completionPanel, 0, 3);

            var status = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = T("status.Select a process to watch.", "Select a process to watch.")
            };
            root.Controls.Add(status, 0, 4);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = CreateCloseButton();
            var stopButton = new Button { Text = T("ui.S&top", "S&top"), AutoSize = true, Enabled = false };
            var startButton = new Button { Text = T("ui.&Start", "&Start"), AutoSize = true };
            var refreshButton = new Button { Text = T("ui.&Refresh list", "&Refresh list"), AutoSize = true };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(stopButton);
            buttons.Controls.Add(startButton);
            buttons.Controls.Add(refreshButton);
            root.Controls.Add(buttons, 0, 5);

            Action refreshProcesses = delegate
            {
                var selectedPid = SelectedProcessWatchTarget(processList) == null ? -1 : SelectedProcessWatchTarget(processList).ProcessId;
                processList.BeginUpdate();
                processList.Items.Clear();
                foreach (var target in EnumerateProcessWatchTargets(false, true))
                {
                    var index = processList.Items.Add(target);
                    if (target.ProcessId == selectedPid)
                    {
                        processList.SelectedIndex = index;
                    }
                }
                if (processList.SelectedIndex < 0 && processList.Items.Count > 0)
                {
                    processList.SelectedIndex = 0;
                }
                processList.EndUpdate();
                status.Text = string.Format(T("status.Process count ready.", "{0} processes available."), processList.Items.Count);
            };

            Action updateDialogState = delegate
            {
                var active = IsProcessWatchActive();
                startButton.Enabled = !active;
                stopButton.Enabled = active;
                refreshButton.Enabled = !active;
                processList.Enabled = !active;
                durationBox.Enabled = !active;
                durationUnit.Enabled = !active;
                formatBox.Enabled = !active;
                speakCompleteCheck.Enabled = !active;
                soundCompleteCheck.Enabled = !active;
                soundLabel.Enabled = !active && soundCompleteCheck.Checked;
                soundBox.Enabled = !active && soundCompleteCheck.Checked;
                messageBox.Enabled = !active && speakCompleteCheck.Checked;
                if (active)
                {
                    status.Text = ProcessWatchStatusText(activeProcessWatchSession);
                }
                else if (activeProcessWatchSession != null && activeProcessWatchSession.Samples.Count > 0)
                {
                    status.Text = string.Format(T("status.Process watch stopped.", "Stopped. {0} samples captured."), activeProcessWatchSession.Samples.Count);
                }
            };

            refreshButton.Click += delegate { refreshProcesses(); updateDialogState(); };
            startButton.Click += delegate
            {
                var target = SelectedProcessWatchTarget(processList);
                if (target == null)
                {
                    status.Text = T("status.Select a process to watch.", "Select a process to watch.");
                    return;
                }

                SaveProcessWatchDialogSettings(durationBox, durationUnit, formatBox, speakCompleteCheck, soundCompleteCheck, soundBox, messageBox);
                StartProcessWatch(target, ProcessWatchDuration(durationBox, durationUnit), ProcessWatchOutputFormat(formatBox), speakCompleteCheck.Checked, soundCompleteCheck.Checked, ProcessWatchSelectedSound(soundBox), messageBox.Text);
                dialog.Close();
            };
            stopButton.Click += delegate
            {
                StopAndSaveProcessWatch(T("ui.Stopped by user", "Stopped by user"));
                updateDialogState();
            };
            speakCompleteCheck.CheckedChanged += delegate { updateDialogState(); };
            soundCompleteCheck.CheckedChanged += delegate { updateDialogState(); };
            soundBox.SelectedIndexChanged += delegate
            {
                if (soundBox.Enabled)
                {
                    PreviewProcessWatchSound(soundBox);
                }
            };
            closeButton.Click += delegate
            {
                dialog.Close();
            };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            processList.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                if (!char.IsControl(e.KeyChar))
                {
                    SelectProcessWatchPrefix(processList, e.KeyChar);
                    e.Handled = true;
                }
            };

            dialog.Controls.Add(root);
            dialog.AcceptButton = startButton;
            dialog.CancelButton = closeButton;
            refreshProcesses();
            updateDialogState();
            dialog.Shown += delegate { processList.Focus(); };
            dialog.ShowDialog(this);
        }
    }

    private bool IsProcessWatchActive()
    {
        return processWatchTimer != null && processWatchTimer.Enabled && activeProcessWatchSession != null;
    }

    private static TimeSpan ProcessWatchDuration(NumericUpDown durationBox, ComboBox durationUnit)
    {
        var value = durationBox == null ? 0 : (double)durationBox.Value;
        return ProcessWatchDuration(value, ProcessWatchDurationUnitValue(durationUnit));
    }

    private static TimeSpan ProcessWatchDuration(int durationValue, string durationUnit)
    {
        return ProcessWatchDuration((double)NormalizeProcessWatchDurationValue(durationValue), NormalizeProcessWatchDurationUnit(durationUnit));
    }

    private static TimeSpan ProcessWatchDuration(double value, string unit)
    {
        if (value <= 0)
        {
            return TimeSpan.Zero;
        }

        if (string.Equals(unit, "seconds", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(value);
        }
        if (string.Equals(unit, "hours", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(value);
        }
        return TimeSpan.FromMinutes(value);
    }

    private static int ProcessWatchDurationUnitIndex(string unit)
    {
        unit = NormalizeProcessWatchDurationUnit(unit);
        if (string.Equals(unit, "seconds", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (string.Equals(unit, "hours", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return 0;
    }

    private static string ProcessWatchDurationUnitValue(ComboBox durationUnit)
    {
        if (durationUnit == null)
        {
            return "minutes";
        }
        if (durationUnit.SelectedIndex == 1)
        {
            return "seconds";
        }
        if (durationUnit.SelectedIndex == 2)
        {
            return "hours";
        }
        return "minutes";
    }

    private static string ProcessWatchOutputFormat(ComboBox formatBox)
    {
        return formatBox != null && formatBox.SelectedIndex == 1 ? "csv" : "html";
    }

    private static void AttachNumericReplaceOnFocus(NumericUpDown box)
    {
        if (box == null)
        {
            return;
        }

        EventHandler selectAll = delegate { box.Select(0, box.Text.Length); };
        box.Enter += selectAll;
        box.Click += selectAll;
        box.MouseUp += delegate { box.Select(0, box.Text.Length); };
    }

    private static void PopulateProcessWatchSoundBox(ComboBox soundBox, string selectedFile)
    {
        if (soundBox == null)
        {
            return;
        }

        soundBox.Items.Clear();
        soundBox.Items.Add(T("ui.(None)", "(None)"));
        foreach (var sound in LoadSoundFileNames())
        {
            soundBox.Items.Add(sound);
        }

        soundBox.SelectedIndex = 0;
        var selected = System.IO.Path.GetFileName(selectedFile ?? "");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            for (var i = 1; i < soundBox.Items.Count; i++)
            {
                if (string.Equals(Convert.ToString(soundBox.Items[i], CultureInfo.InvariantCulture), selected, StringComparison.OrdinalIgnoreCase))
                {
                    soundBox.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static string ProcessWatchSelectedSound(ComboBox soundBox)
    {
        if (soundBox == null || soundBox.SelectedItem == null || soundBox.SelectedIndex <= 0)
        {
            return "";
        }

        return soundBox.SelectedItem.ToString();
    }

    private void SaveProcessWatchDialogSettings(NumericUpDown durationBox, ComboBox durationUnit, ComboBox formatBox, CheckBox speakCompleteCheck, CheckBox soundCompleteCheck, ComboBox soundBox, TextBox messageBox)
    {
        settings.ProcessWatchDurationValue = durationBox == null ? 0 : NormalizeProcessWatchDurationValue((int)durationBox.Value);
        settings.ProcessWatchDurationUnit = ProcessWatchDurationUnitValue(durationUnit);
        settings.ProcessWatchOutputFormat = ProcessWatchOutputFormat(formatBox);
        settings.ProcessWatchSpeakWhenStopped = speakCompleteCheck == null || speakCompleteCheck.Checked;
        settings.ProcessWatchPlaySoundWhenStopped = soundCompleteCheck != null && soundCompleteCheck.Checked;
        settings.ProcessWatchSoundFile = System.IO.Path.GetFileName(ProcessWatchSelectedSound(soundBox) ?? "");
        settings.ProcessWatchCompletionMessage = messageBox == null ? "" : messageBox.Text ?? "";
        SaveSettings(settings);
    }

    private static void PreviewProcessWatchSound(ComboBox soundBox)
    {
        var fileName = ProcessWatchSelectedSound(soundBox);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            PreviewSoundFile(fileName);
        }
    }

    private void StartProcessWatch(ProcessWatchTarget target, TimeSpan duration, string outputFormat, bool speakWhenStopped, bool playSoundWhenStopped, string completionSoundFile, string completionMessage)
    {
        if (target == null)
        {
            return;
        }

        StopProcessWatch(T("ui.Replaced by new watch", "Replaced by new watch"), false);
        activeProcessWatchSession = new ProcessWatchSession
        {
            ProcessId = target.ProcessId,
            ProcessName = target.Name,
            ProcessPath = target.Path,
            StartedLocal = DateTime.Now,
            OutputFormat = string.Equals(outputFormat, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "html",
            SpeakWhenStopped = speakWhenStopped,
            PlaySoundWhenStopped = playSoundWhenStopped,
            CompletionSoundFile = System.IO.Path.GetFileName(completionSoundFile ?? ""),
            CompletionMessage = FirstNonEmpty(completionMessage, T("ui.Process watch complete", "Process watch complete."))
        };
        if (duration > TimeSpan.Zero)
        {
            activeProcessWatchSession.AutoStopLocal = activeProcessWatchSession.StartedLocal.Add(duration);
        }
        activeProcessWatchPreviousSnapshot = null;
        activeProcessWatchPreviousUtc = DateTime.MinValue;
        activeProcessWatchSession.Samples.Add(CaptureProcessWatchSample(activeProcessWatchSession.ProcessId, activeProcessWatchSession.StartedLocal, ref activeProcessWatchPreviousSnapshot, ref activeProcessWatchPreviousUtc));

        if (processWatchTimer == null)
        {
            processWatchTimer = new Timer { Interval = 1000 };
            processWatchTimer.Tick += delegate { ProcessWatchTimerTick(); };
        }
        processWatchTimer.Start();
        statusLabel.Text = ProcessWatchStartedText(target.Name, duration);
    }

    private void ProcessWatchTimerTick()
    {
        if (activeProcessWatchSession == null)
        {
            if (processWatchTimer != null)
            {
                processWatchTimer.Stop();
            }
            return;
        }

        var sample = CaptureProcessWatchSample(activeProcessWatchSession.ProcessId, activeProcessWatchSession.StartedLocal, ref activeProcessWatchPreviousSnapshot, ref activeProcessWatchPreviousUtc);
        activeProcessWatchSession.Samples.Add(sample);
        if (!sample.ProcessRunning)
        {
            activeProcessWatchSession.ProcessExited = true;
            StopAndSaveProcessWatch(T("ui.Process exited", "Process exited"));
            return;
        }

        if (activeProcessWatchSession.AutoStopLocal != DateTime.MinValue && DateTime.Now >= activeProcessWatchSession.AutoStopLocal)
        {
            StopAndSaveProcessWatch(T("ui.Duration complete", "Duration complete"));
            return;
        }

        statusLabel.Text = ProcessWatchStatusText(activeProcessWatchSession);
    }

    private string ProcessWatchStatusText(ProcessWatchSession session)
    {
        if (session == null)
        {
            return T("status.Select a process to watch.", "Select a process to watch.");
        }

        var latest = session.Samples.LastOrDefault(s => s != null && s.ProcessRunning);
        if (latest == null)
        {
            var duration = session.AutoStopLocal == DateTime.MinValue ? TimeSpan.Zero : session.AutoStopLocal - session.StartedLocal;
            return ProcessWatchStartedText(session.ProcessName, duration);
        }

        return string.Format(
            T("status.Process watch sample", "Watching {0}: CPU {1}, memory {2}, samples {3}."),
            session.ProcessName,
            FormatNumber(Math.Round(latest.CpuPercent, 1), "0.0") + "%",
            FormatBytes(latest.WorkingSetBytes),
            session.Samples.Count);
    }

    private string ProcessWatchStartedText(string processName, TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            return string.Format(
                T("status.Process watch started timed.", "Watching {0} for {1}."),
                processName,
                FormatProcessWatchDuration(duration));
        }

        return string.Format(T("status.Process watch started.", "Watching {0}. Press Stop when finished."), processName);
    }

    private static string FormatProcessWatchDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0 seconds";
        }

        if (duration.TotalHours >= 1 && Math.Abs(duration.TotalHours - Math.Round(duration.TotalHours)) < 0.001)
        {
            var hours = (int)Math.Round(duration.TotalHours);
            return hours == 1
                ? T("ui.1 hour", "1 hour")
                : string.Format(T("ui.n hours", "{0} hours"), hours.ToString(CultureInfo.InvariantCulture));
        }

        if (duration.TotalMinutes >= 1 && Math.Abs(duration.TotalMinutes - Math.Round(duration.TotalMinutes)) < 0.001)
        {
            var minutes = (int)Math.Round(duration.TotalMinutes);
            return minutes == 1
                ? T("ui.1 minute", "1 minute")
                : string.Format(T("ui.n minutes", "{0} minutes"), minutes.ToString(CultureInfo.InvariantCulture));
        }

        var seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        return seconds == 1
            ? T("ui.1 second", "1 second")
            : string.Format(T("ui.n seconds", "{0} seconds"), seconds.ToString(CultureInfo.InvariantCulture));
    }

    private void StopProcessWatch(string reason)
    {
        StopProcessWatch(reason, true);
    }

    private void StopProcessWatch(string reason, bool notify)
    {
        if (activeProcessWatchSession == null)
        {
            return;
        }

        if (processWatchTimer != null)
        {
            processWatchTimer.Stop();
        }
        activeProcessWatchSession.StoppedLocal = DateTime.Now;
        if (string.IsNullOrWhiteSpace(activeProcessWatchSession.StopReason))
        {
            activeProcessWatchSession.StopReason = FirstNonEmpty(reason, T("ui.Stopped by user", "Stopped by user"));
        }
        statusLabel.Text = string.Format(T("status.Process watch stopped.", "Stopped. {0} samples captured."), activeProcessWatchSession.Samples.Count);
        if (notify)
        {
            NotifyProcessWatchStopped(activeProcessWatchSession);
        }
    }

    private void StopAndSaveProcessWatch(string reason)
    {
        StopProcessWatch(reason);
        SaveProcessWatchReportToDefaultPath(activeProcessWatchSession);
    }

    private void SaveProcessWatchReportToDefaultPath(ProcessWatchSession session)
    {
        if (session == null || session.Samples.Count == 0)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(session.ReportPath))
        {
            statusLabel.Text = string.Format(T("status.Process watch report saved to.", "Process watch report saved to {0}."), session.ReportPath);
            return;
        }

        var reportsFolder = GetReportsFolderPath();
        System.IO.Directory.CreateDirectory(reportsFolder);
        var csv = string.Equals(session.OutputFormat, "csv", StringComparison.OrdinalIgnoreCase);
        var extension = csv ? ".csv" : ".html";
        var path = System.IO.Path.Combine(reportsFolder, "SensorReadout-ProcessWatch-" + SafeReportFileName(session.ProcessName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + extension);
        System.IO.File.WriteAllText(path, csv ? BuildProcessWatchCsv(session) : BuildProcessWatchHtmlReport(session), Encoding.UTF8);
        session.ReportPath = path;
        statusLabel.Text = string.Format(T("status.Process watch report saved to.", "Process watch report saved to {0}."), path);
    }

    private void NotifyProcessWatchStopped(ProcessWatchSession session)
    {
        if (session == null || session.CompletionNotified)
        {
            return;
        }

        session.CompletionNotified = true;
        if (session.PlaySoundWhenStopped)
        {
            if (!string.IsNullOrWhiteSpace(session.CompletionSoundFile))
            {
                PlaySoundFile(session.CompletionSoundFile);
            }
            else
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
        }
        if (session.SpeakWhenStopped)
        {
            SpeakTextWithScreenReader(FirstNonEmpty(session.CompletionMessage, T("ui.Process watch complete", "Process watch complete.")), "process watch");
        }
    }

    private void SelectProcessWatchPrefix(ListBox list, char keyChar)
    {
        if (list == null || list.Items.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        if ((now - processWatchSearchLastKey).TotalMilliseconds > 1200)
        {
            processWatchSearchPrefix = "";
        }
        processWatchSearchLastKey = now;
        processWatchSearchPrefix += keyChar.ToString();

        for (var i = 0; i < list.Items.Count; i++)
        {
            var text = list.Items[i] == null ? "" : list.Items[i].ToString();
            if (text.StartsWith(processWatchSearchPrefix, StringComparison.CurrentCultureIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }

        processWatchSearchPrefix = keyChar.ToString();
        for (var i = 0; i < list.Items.Count; i++)
        {
            var text = list.Items[i] == null ? "" : list.Items[i].ToString();
            if (text.StartsWith(processWatchSearchPrefix, StringComparison.CurrentCultureIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }
    }

    private static ProcessWatchTarget SelectedProcessWatchTarget(ListBox list)
    {
        return list == null ? null : list.SelectedItem as ProcessWatchTarget;
    }

    private static ProcessWatchTarget ProcessWatchTargetFromRow(SensorRow row)
    {
        if (!IsTaskProcessInventoryRow(row) || row.Details == null)
        {
            return null;
        }

        string processIdText;
        int processId;
        if (!row.Details.TryGetValue("Process ID", out processIdText) ||
            !int.TryParse(processIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId) ||
            processId <= 0)
        {
            return null;
        }

        return EnumerateProcessWatchTargets(false, true).FirstOrDefault(t => t.ProcessId == processId);
    }

    private static IEnumerable<ProcessWatchTarget> EnumerateProcessWatchTargets()
    {
        return EnumerateProcessWatchTargets(false);
    }

    private static IEnumerable<ProcessWatchTarget> EnumerateProcessWatchTargets(bool backgroundRefresh)
    {
        return EnumerateProcessWatchTargets(backgroundRefresh, false);
    }

    private static IEnumerable<ProcessWatchTarget> EnumerateProcessWatchTargets(bool backgroundRefresh, bool forceMetadataRefresh)
    {
        var targets = new List<ProcessWatchTarget>();
        var processInfo = LoadProcessWatchMetadata(backgroundRefresh, forceMetadataRefresh);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    ProcessWatchMetadata metadata;
                    processInfo.TryGetValue(process.Id, out metadata);
                    var target = new ProcessWatchTarget
                    {
                        ProcessId = process.Id,
                        Name = FirstNonEmpty(process.ProcessName, "Process"),
                        Path = SafeProcessMainModulePath(process),
                        WindowTitle = SafeProcessMainWindowTitle(process),
                        StartedLocal = SafeProcessStartTime(process),
                        Role = metadata == null ? "" : ProcessWatchRoleFromCommandLine(metadata.CommandLine),
                        ParentName = metadata == null ? "" : ProcessWatchParentName(metadata.ParentProcessId, processInfo)
                    };
                    targets.Add(target);
                }
                catch
                {
                }
            }
        }

        return targets
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.ProcessId)
            .ToList();
    }

    private sealed class ProcessWatchMetadata
    {
        public int ProcessId;
        public int ParentProcessId;
        public string Name = "";
        public string CommandLine = "";
    }

    private static Dictionary<int, ProcessWatchMetadata> LoadProcessWatchMetadata()
    {
        return LoadProcessWatchMetadata(false, false);
    }

    private static Dictionary<int, ProcessWatchMetadata> LoadProcessWatchMetadata(bool backgroundRefresh, bool forceRefresh)
    {
        var minimumAge = backgroundRefresh ? BackgroundProcessMetadataMinimumInterval : ForegroundProcessMetadataMinimumInterval;
        lock (processWatchMetadataCacheLock)
        {
            if (!forceRefresh && cachedProcessWatchMetadata.Count > 0 && DateTime.UtcNow - cachedProcessWatchMetadataUtc < minimumAge)
            {
                return CloneProcessWatchMetadata(cachedProcessWatchMetadata);
            }
        }

        var result = new Dictionary<int, ProcessWatchMetadata>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process"))
            using (var rows = ExecuteWmiQuery(searcher, "WMI"))
            {
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        var processId = SafeManagementInt(row, "ProcessId");
                        if (processId <= 0)
                        {
                            continue;
                        }

                        result[processId] = new ProcessWatchMetadata
                        {
                            ProcessId = processId,
                            ParentProcessId = SafeManagementInt(row, "ParentProcessId"),
                            Name = Convert.ToString(row["Name"], CultureInfo.InvariantCulture) ?? "",
                            CommandLine = Convert.ToString(row["CommandLine"], CultureInfo.InvariantCulture) ?? ""
                        };
                    }
                }
            }
        }
        catch
        {
        }

        lock (processWatchMetadataCacheLock)
        {
            cachedProcessWatchMetadata = CloneProcessWatchMetadata(result);
            cachedProcessWatchMetadataUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static Dictionary<int, ProcessWatchMetadata> CloneProcessWatchMetadata(Dictionary<int, ProcessWatchMetadata> source)
    {
        return (source ?? new Dictionary<int, ProcessWatchMetadata>())
            .ToDictionary(
                pair => pair.Key,
                pair => new ProcessWatchMetadata
                {
                    ProcessId = pair.Value == null ? pair.Key : pair.Value.ProcessId,
                    ParentProcessId = pair.Value == null ? 0 : pair.Value.ParentProcessId,
                    Name = pair.Value == null ? "" : pair.Value.Name,
                    CommandLine = pair.Value == null ? "" : pair.Value.CommandLine
                });
    }

    private static string ProcessWatchParentName(int parentProcessId, Dictionary<int, ProcessWatchMetadata> metadata)
    {
        if (parentProcessId <= 0 || metadata == null)
        {
            return "";
        }

        ProcessWatchMetadata parent;
        if (!metadata.TryGetValue(parentProcessId, out parent) || parent == null)
        {
            return "";
        }

        return FirstNonEmpty(System.IO.Path.GetFileNameWithoutExtension(parent.Name), parent.Name);
    }

    private static string ProcessWatchRoleFromCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return "";
        }

        var type = ProcessWatchCommandLineValue(commandLine, "--type=");
        if (!string.IsNullOrWhiteSpace(type))
        {
            return "role " + type.Replace('-', ' ');
        }

        if (commandLine.IndexOf("--utility-sub-type=", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var utilityType = ProcessWatchCommandLineValue(commandLine, "--utility-sub-type=");
            if (!string.IsNullOrWhiteSpace(utilityType))
            {
                return "role " + utilityType.Replace('.', ' ');
            }
        }

        return "";
    }

    private static string ProcessWatchCommandLineValue(string commandLine, string option)
    {
        var index = commandLine.IndexOf(option, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var start = index + option.Length;
        if (start >= commandLine.Length)
        {
            return "";
        }

        if (commandLine[start] == '"')
        {
            start++;
            var endQuote = commandLine.IndexOf('"', start);
            return endQuote > start ? commandLine.Substring(start, endQuote - start) : commandLine.Substring(start);
        }

        var end = commandLine.IndexOfAny(new[] { ' ', '\t' }, start);
        return end > start ? commandLine.Substring(start, end - start) : commandLine.Substring(start);
    }

    private static int SafeManagementInt(ManagementObject row, string propertyName)
    {
        try
        {
            var value = row == null ? null : row[propertyName];
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeProcessMainModulePath(Process process)
    {
        try
        {
            return process == null || process.MainModule == null ? "" : process.MainModule.FileName;
        }
        catch
        {
            return "";
        }
    }

    private static string SafeProcessMainWindowTitle(Process process)
    {
        try
        {
            return process == null ? "" : process.MainWindowTitle ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static DateTime SafeProcessStartTime(Process process)
    {
        try
        {
            return process == null ? DateTime.MinValue : process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private ProcessWatchSample CaptureProcessWatchSample(int processId, DateTime startedLocal, ref TaskProcessSnapshot previousSnapshot, ref DateTime previousUtc)
    {
        var sample = new ProcessWatchSample
        {
            LocalTime = DateTime.Now,
            ElapsedSeconds = Math.Max(0, (DateTime.Now - startedLocal).TotalSeconds)
        };

        TaskProcessSnapshot snapshot = null;
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                snapshot = new TaskProcessSnapshot
                {
                    ProcessId = process.Id,
                    Name = FirstNonEmpty(process.ProcessName, "Process"),
                    TotalProcessorTime = process.TotalProcessorTime,
                    WorkingSetBytes = SafeProcessMemoryValue(() => process.WorkingSet64),
                    PrivateMemoryBytes = SafeProcessMemoryValue(() => process.PrivateMemorySize64)
                };
                sample.ProcessRunning = true;
                sample.WorkingSetBytes = snapshot.WorkingSetBytes;
                sample.PrivateMemoryBytes = snapshot.PrivateMemoryBytes;
                sample.ThreadCount = SafeProcessIntValue(() => process.Threads.Count);
                sample.HandleCount = SafeProcessIntValue(() => process.HandleCount);
            }
        }
        catch
        {
            sample.ProcessRunning = false;
            return sample;
        }

        var nowUtc = DateTime.UtcNow;
        if (previousSnapshot != null && previousUtc != DateTime.MinValue)
        {
            var elapsedSeconds = Math.Max(0.001, (nowUtc - previousUtc).TotalSeconds);
            var cpuSeconds = Math.Max(0, (snapshot.TotalProcessorTime - previousSnapshot.TotalProcessorTime).TotalSeconds);
            sample.CpuPercent = Math.Min(100.0, cpuSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0);
        }
        previousSnapshot = snapshot;
        previousUtc = nowUtc;

        var gpuUsage = GetGpuProcessUsages(new List<TaskProcessUsage>
        {
            new TaskProcessUsage
            {
                ProcessId = processId,
                Name = snapshot.Name,
                CpuPercent = sample.CpuPercent,
                WorkingSetBytes = sample.WorkingSetBytes,
                PrivateMemoryBytes = sample.PrivateMemoryBytes
            }
        }).FirstOrDefault(g => g.ProcessId == processId);
        if (gpuUsage != null)
        {
            NormalizeGpuProcessMemory(gpuUsage);
            sample.GpuUsagePercent = gpuUsage.UsagePercent;
            sample.DedicatedGpuBytes = gpuUsage.DedicatedBytes;
            sample.SharedGpuBytes = gpuUsage.SharedBytes;
        }

        return sample;
    }

    private static int SafeProcessIntValue(Func<int> getter)
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

    private string BuildProcessWatchHtmlReport(ProcessWatchSession session)
    {
        session = session ?? new ProcessWatchSession();
        var samples = session.Samples ?? new List<ProcessWatchSample>();
        var runningSamples = samples.Where(s => s != null && s.ProcessRunning).ToList();
        var html = new StringBuilder();
        var title = "Sensor Readout process watch report";
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\"><title>" + HtmlEncode(title) + "</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.4;margin:1.5rem;}table{border-collapse:collapse;margin:0.75rem 0 1.5rem 0;min-width:34rem;}th,td{border:1px solid #777;padding:0.35rem 0.55rem;text-align:left;vertical-align:top;}th{background:#f0f0f0;}caption{font-weight:bold;text-align:left;margin-bottom:0.35rem;}</style></head><body>");
        html.AppendLine("<h1>" + HtmlEncode(title) + "</h1>");
        html.AppendLine("<p>Generated by Sensor Readout " + HtmlEncode(AppVersion) + ". <a href=\"" + HtmlEncode(ProjectUrl + "/releases/latest") + "\">Download Sensor Readout</a>.</p>");
        html.AppendLine("<p>Generated: " + HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "</p>");
        html.AppendLine("<p><strong>Privacy note:</strong> this report contains resource counters for the selected process only. It may include the selected process name and executable path. It does not include keystrokes, private app contents, file contents, registry contents, or network payloads.</p>");

        html.AppendLine("<h2>Target</h2>");
        html.AppendLine("<table><tbody>");
        AddProcessWatchHtmlRow(html, "Process name", FirstNonEmpty(session.ProcessName, "Process"));
        AddProcessWatchHtmlRow(html, "Process ID", session.ProcessId.ToString(CultureInfo.InvariantCulture));
        AddProcessWatchHtmlRow(html, "Executable path", session.ProcessPath);
        AddProcessWatchHtmlRow(html, "Started watching", session.StartedLocal == DateTime.MinValue ? "" : session.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddProcessWatchHtmlRow(html, "Stopped watching", session.StoppedLocal == DateTime.MinValue ? "" : session.StoppedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        AddProcessWatchHtmlRow(html, "Stop reason", FirstNonEmpty(session.StopReason, session.ProcessExited ? "Process exited" : "Stopped"));
        AddProcessWatchHtmlRow(html, "Samples", samples.Count.ToString(CultureInfo.InvariantCulture));
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h2>Summary</h2>");
        html.AppendLine("<table><thead><tr><th scope=\"col\">Metric</th><th scope=\"col\">Average</th><th scope=\"col\">Peak</th><th scope=\"col\">Final</th></tr></thead><tbody>");
        AddProcessWatchSummaryHtmlRow(html, "CPU", runningSamples.Select(s => s.CpuPercent), v => FormatNumber(Math.Round(v, 1), "0.0") + "%");
        AddProcessWatchSummaryHtmlRow(html, "Working set", runningSamples.Select(s => (double)s.WorkingSetBytes), v => FormatBytes(v));
        AddProcessWatchSummaryHtmlRow(html, "Private memory", runningSamples.Select(s => (double)s.PrivateMemoryBytes), v => FormatBytes(v));
        AddProcessWatchSummaryHtmlRow(html, "Dedicated GPU memory", runningSamples.Select(s => s.DedicatedGpuBytes), v => FormatBytes(v));
        AddProcessWatchSummaryHtmlRow(html, "Shared GPU memory", runningSamples.Select(s => s.SharedGpuBytes), v => FormatBytes(v));
        AddProcessWatchSummaryHtmlRow(html, "GPU usage", runningSamples.Select(s => s.GpuUsagePercent), v => FormatNumber(Math.Round(v, 1), "0.0") + "%");
        AddProcessWatchSummaryHtmlRow(html, "Threads", runningSamples.Select(s => (double)s.ThreadCount), v => FormatNumber(Math.Round(v, 0), "0"));
        AddProcessWatchSummaryHtmlRow(html, "Handles", runningSamples.Select(s => (double)s.HandleCount), v => FormatNumber(Math.Round(v, 0), "0"));
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h2>Growth</h2>");
        html.AppendLine("<table><tbody>");
        if (runningSamples.Count >= 2)
        {
            var first = runningSamples.First();
            var last = runningSamples.Last();
            AddProcessWatchHtmlRow(html, "Working set change", FormatSignedBytes(last.WorkingSetBytes - first.WorkingSetBytes));
            AddProcessWatchHtmlRow(html, "Private memory change", FormatSignedBytes(last.PrivateMemoryBytes - first.PrivateMemoryBytes));
            AddProcessWatchHtmlRow(html, "Dedicated GPU memory change", FormatSignedBytes(last.DedicatedGpuBytes - first.DedicatedGpuBytes));
            AddProcessWatchHtmlRow(html, "Shared GPU memory change", FormatSignedBytes(last.SharedGpuBytes - first.SharedGpuBytes));
        }
        else
        {
            AddProcessWatchHtmlRow(html, "Growth", "Not enough running samples to calculate growth.");
        }
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h2>Samples</h2>");
        html.AppendLine("<table><thead><tr><th scope=\"col\">Time</th><th scope=\"col\">Elapsed</th><th scope=\"col\">CPU</th><th scope=\"col\">Working set</th><th scope=\"col\">Private memory</th><th scope=\"col\">GPU</th><th scope=\"col\">Dedicated GPU memory</th><th scope=\"col\">Shared GPU memory</th><th scope=\"col\">Threads</th><th scope=\"col\">Handles</th><th scope=\"col\">Running</th></tr></thead><tbody>");
        foreach (var sample in samples)
        {
            AddProcessWatchSampleHtmlRow(html, sample);
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static void AddProcessWatchHtmlRow(StringBuilder html, string label, string value)
    {
        html.AppendLine("<tr><td>" + HtmlEncode(label) + "</td><td>" + HtmlEncode(value) + "</td></tr>");
    }

    private static void AddProcessWatchSummaryHtmlRow(StringBuilder html, string label, IEnumerable<double> values, Func<double, string> formatter)
    {
        var list = (values ?? Enumerable.Empty<double>()).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        if (list.Count == 0)
        {
            html.AppendLine("<tr><td>" + HtmlEncode(label) + "</td><td colspan=\"3\">No samples</td></tr>");
            return;
        }

        html.AppendLine("<tr><td>" + HtmlEncode(label) + "</td><td>" + HtmlEncode(formatter(list.Average())) + "</td><td>" + HtmlEncode(formatter(list.Max())) + "</td><td>" + HtmlEncode(formatter(list.Last())) + "</td></tr>");
    }

    private static void AddProcessWatchSampleHtmlRow(StringBuilder html, ProcessWatchSample sample)
    {
        if (sample == null)
        {
            return;
        }

        html.AppendLine("<tr><td>" + HtmlEncode(sample.LocalTime.ToString("HH:mm:ss")) + "</td><td>" +
            HtmlEncode(FormatNumber(Math.Round(sample.ElapsedSeconds, 0), "0") + "s") + "</td><td>" +
            HtmlEncode(FormatNumber(Math.Round(sample.CpuPercent, 1), "0.0") + "%") + "</td><td>" +
            HtmlEncode(FormatBytes(sample.WorkingSetBytes)) + "</td><td>" +
            HtmlEncode(FormatBytes(sample.PrivateMemoryBytes)) + "</td><td>" +
            HtmlEncode(FormatNumber(Math.Round(sample.GpuUsagePercent, 1), "0.0") + "%") + "</td><td>" +
            HtmlEncode(FormatBytes(sample.DedicatedGpuBytes)) + "</td><td>" +
            HtmlEncode(FormatBytes(sample.SharedGpuBytes)) + "</td><td>" +
            HtmlEncode(sample.ThreadCount.ToString(CultureInfo.InvariantCulture)) + "</td><td>" +
            HtmlEncode(sample.HandleCount.ToString(CultureInfo.InvariantCulture)) + "</td><td>" +
            HtmlEncode(sample.ProcessRunning ? "Yes" : "No") + "</td></tr>");
    }

    private static string BuildProcessWatchCsv(ProcessWatchSession session)
    {
        session = session ?? new ProcessWatchSession();
        var lines = new List<string>();
        lines.Add("Time,ElapsedSeconds,CPUPercent,WorkingSetBytes,PrivateMemoryBytes,GPUPercent,DedicatedGPUBytes,SharedGPUBytes,Threads,Handles,Running");
        foreach (var sample in session.Samples ?? new List<ProcessWatchSample>())
        {
            lines.Add(string.Join(",", new[]
            {
                ProcessWatchCsv(sample.LocalTime.ToString("yyyy-MM-dd HH:mm:ss")),
                Math.Round(sample.ElapsedSeconds, 0).ToString("0", CultureInfo.InvariantCulture),
                Math.Round(sample.CpuPercent, 3).ToString("0.###", CultureInfo.InvariantCulture),
                sample.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                sample.PrivateMemoryBytes.ToString(CultureInfo.InvariantCulture),
                Math.Round(sample.GpuUsagePercent, 3).ToString("0.###", CultureInfo.InvariantCulture),
                Math.Round(sample.DedicatedGpuBytes, 0).ToString(CultureInfo.InvariantCulture),
                Math.Round(sample.SharedGpuBytes, 0).ToString(CultureInfo.InvariantCulture),
                sample.ThreadCount.ToString(CultureInfo.InvariantCulture),
                sample.HandleCount.ToString(CultureInfo.InvariantCulture),
                sample.ProcessRunning ? "Yes" : "No"
            }));
        }
        return string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
    }

    private static string ProcessWatchCsv(string value)
    {
        value = value ?? "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatSignedBytes(double bytes)
    {
        var sign = bytes > 0 ? "+" : bytes < 0 ? "-" : "";
        return sign + FormatBytes(Math.Abs(bytes));
    }
}
