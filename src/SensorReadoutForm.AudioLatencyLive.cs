using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class AudioLatencyLiveSnapshot
    {
        public bool HasRun;
        public bool Active;
        public DateTime StartedLocal;
        public DateTime StopAtLocal;
        public TimeSpan Elapsed;
        public string Error = "";
        public string ReportPath = "";
        public long DpcCount;
        public long IsrCount;
        public long HardFaultCount;
        public long EventsLost;
        public double MaximumDpcMicroseconds;
        public double MaximumIsrMicroseconds;
        public string HighestDpcDriver = "";
        public string HighestIsrDriver = "";
        public string LatestDpcDriver = "";
        public string LatestIsrDriver = "";
        public readonly List<AudioLatencySample> Samples = new List<AudioLatencySample>();
    }

    private sealed class AudioLatencyGraphControl : Control
    {
        private List<AudioLatencySample> samples = new List<AudioLatencySample>();
        public string DpcTitle = "DPC peak";
        public string IsrTitle = "ISR peak";
        public string ThresholdText = "guidance";

        public AudioLatencyGraphControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
        }

        public void SetSamples(IEnumerable<AudioLatencySample> value)
        {
            samples = (value ?? Enumerable.Empty<AudioLatencySample>()).ToList();
            if (samples.Count > 60)
            {
                samples.RemoveRange(0, samples.Count - 60);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(SystemColors.Window);
            var bounds = ClientRectangle;
            if (bounds.Width < 80 || bounds.Height < 100)
            {
                return;
            }

            using (var border = new Pen(SystemColors.ControlDark))
            {
                e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
            }

            var half = bounds.Height / 2;
            DrawSeries(e.Graphics, new Rectangle(8, 8, bounds.Width - 16, half - 12), DpcTitle, 100.0, samples.Select(s => s.MaximumDpcMicroseconds).ToList(), SystemColors.Highlight);
            DrawSeries(e.Graphics, new Rectangle(8, half + 4, bounds.Width - 16, bounds.Height - half - 12), IsrTitle, 25.0, samples.Select(s => s.MaximumIsrMicroseconds).ToList(), Color.DarkOrange);
        }

        private void DrawSeries(Graphics graphics, Rectangle area, string title, double threshold, IList<double> values, Color lineColor)
        {
            if (area.Width < 40 || area.Height < 35)
            {
                return;
            }

            var titleHeight = TextRenderer.MeasureText(title ?? "", Font).Height;
            var plot = new Rectangle(area.Left, area.Top + titleHeight + 2, area.Width, Math.Max(12, area.Height - titleHeight - 2));
            var observedMaximum = values.Count == 0 ? 0.0 : values.Max();
            var scaleMaximum = Math.Max(threshold * 2.0, Math.Ceiling(observedMaximum / threshold) * threshold);
            if (scaleMaximum <= 0)
            {
                scaleMaximum = threshold * 2.0;
            }

            var latest = values.Count == 0 ? 0.0 : values[values.Count - 1];
            var heading = string.Format(CultureInfo.CurrentCulture, "{0}: {1:0.###} us", title, latest);
            TextRenderer.DrawText(graphics, heading, Font, new Rectangle(area.Left, area.Top, area.Width, titleHeight), ForeColor, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            using (var grid = new Pen(SystemColors.ControlLight))
            {
                for (var index = 0; index <= 4; index++)
                {
                    var y = plot.Bottom - (int)Math.Round(plot.Height * (index / 4.0));
                    graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            var thresholdY = plot.Bottom - (int)Math.Round(plot.Height * Math.Min(1.0, threshold / scaleMaximum));
            using (var thresholdPen = new Pen(SystemColors.ControlDarkDark) { DashStyle = DashStyle.Dash })
            {
                graphics.DrawLine(thresholdPen, plot.Left, thresholdY, plot.Right, thresholdY);
            }
            var thresholdLabel = string.Format(CultureInfo.CurrentCulture, "{0}: {1:0} us", ThresholdText, threshold);
            TextRenderer.DrawText(graphics, thresholdLabel, Font, new Rectangle(plot.Left + 3, Math.Max(plot.Top, thresholdY - titleHeight), plot.Width - 6, titleHeight), SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (values.Count == 0)
            {
                return;
            }

            var points = new PointF[values.Count];
            for (var index = 0; index < values.Count; index++)
            {
                var x = values.Count == 1 ? plot.Right : plot.Left + (float)(plot.Width * index / (double)(values.Count - 1));
                var ratio = Math.Min(1.0, Math.Max(0.0, values[index] / scaleMaximum));
                var y = plot.Bottom - (float)(plot.Height * ratio);
                points[index] = new PointF(x, y);
            }
            using (var line = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : lineColor, 2.0f))
            {
                if (points.Length == 1)
                {
                    graphics.DrawEllipse(line, points[0].X - 1, points[0].Y - 1, 2, 2);
                }
                else
                {
                    graphics.DrawLines(line, points);
                }
            }
        }
    }

    private Form audioLatencyLiveForm;
    private AudioLatencyGraphControl audioLatencyLiveGraph;
    private TreeView audioLatencyLiveTree;
    private Label audioLatencyLiveStatus;
    private Button audioLatencyLiveStopButton;
    private Button audioLatencyLiveOpenReportButton;
    private readonly Dictionary<string, TreeNode> audioLatencyLiveNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

    private void ShowAudioLatencyLiveMonitor()
    {
        var snapshot = BuildAudioLatencyLiveSnapshot();
        if (!snapshot.HasRun)
        {
            statusLabel.Text = T("status.No audio latency test is available.", "No audio latency test is available yet.");
            return;
        }
        if (audioLatencyLiveForm != null && !audioLatencyLiveForm.IsDisposed)
        {
            audioLatencyLiveForm.Show();
            audioLatencyLiveForm.WindowState = FormWindowState.Normal;
            audioLatencyLiveForm.BringToFront();
            audioLatencyLiveForm.Activate();
            UpdateAudioLatencyLiveMonitor(snapshot);
            return;
        }

        var form = new Form
        {
            Text = T("ui.Live audio latency monitor", "Live audio latency monitor"),
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(900, 700),
            MinimumSize = new Size(650, 480),
            ShowInTaskbar = true,
            KeyPreview = true
        };
        form.Icon = Icon == null ? LoadApplicationIcon() : (Icon)Icon.Clone();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var intro = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 54,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Text = T("ui.Live audio latency monitor intro", "Live values update once per second. Closing this window does not stop the diagnostic; reopen it from Options. Use Stop and save or Control Shift D to finish and save the report."),
            AccessibleName = T("a11y.Live audio latency monitor explanation", "Live audio latency monitor explanation")
        };
        var graph = new AudioLatencyGraphControl
        {
            Dock = DockStyle.Fill,
            AccessibleName = T("a11y.Audio latency history graph", "Audio latency history graph"),
            AccessibleDescription = T("a11y.Graph of recent DPC and ISR peaks.", "Graph of the most recent DPC and ISR duration peaks. The same values are available in the readings tree below."),
            DpcTitle = T("ui.DPC peak history", "DPC peak history"),
            IsrTitle = T("ui.ISR peak history", "ISR peak history"),
            ThresholdText = T("ui.Guidance threshold", "Guidance threshold")
        };
        var tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            FullRowSelect = true,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            AccessibleName = T("a11y.Live audio latency readings", "Live audio latency readings"),
            AccessibleDescription = T("a11y.Current interval, recent peaks, and entire test summary.", "Current interval, recent peaks, and entire test summary. Values update once per second without moving focus.")
        };
        tree.ContextMenuStrip = new ContextMenuStrip();
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.&Copy", "&Copy"), Keys.Control | Keys.C, delegate { CopyDetailsTree(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.Copy &value only", "Copy &value only"), Keys.Control | Keys.Shift | Keys.C, delegate { CopyDetailsTreeValueOnly(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.Copy &matching...", "Copy &matching..."), Keys.Control | Keys.M, delegate { CopyMatchingDetailsTreeLines(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.&Find...", "&Find..."), Keys.F3, delegate { ShowDetailsTreeSearchDialog(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.Review &text...", "Review &text..."), Keys.F4, delegate { ShowDetailsTreeTextReview(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.&Expand all", "&Expand all"), Keys.Control | Keys.Shift | Keys.Right, delegate { ExpandDetailsTree(tree); }));
        tree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem(T("ui.C&ollapse all", "C&ollapse all"), Keys.Control | Keys.Shift | Keys.Left, delegate { CollapseDetailsTree(tree); }));
        tree.KeyDown += delegate(object sender, KeyEventArgs e) { HandleDetailsTreeKey(tree, e); };

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var monitorStatus = new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var closeButton = CreateCloseButton();
        var openReportButton = new Button { Text = T("ui.&Open report", "&Open report"), AutoSize = true };
        var stopButton = new Button { Text = T("ui.&Stop and save", "&Stop and save"), AutoSize = true };
        closeButton.Click += delegate { form.Close(); };
        stopButton.Click += delegate
        {
            RequestAudioLatencyStop(T("ui.Stopped by user", "Stopped by user"));
            stopButton.Enabled = false;
        };
        openReportButton.Click += delegate { OpenAudioLatencyReport(BuildAudioLatencyLiveSnapshot().ReportPath); };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(openReportButton);
        buttons.Controls.Add(stopButton);
        footer.Controls.Add(monitorStatus, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(graph, 0, 1);
        root.Controls.Add(tree, 0, 2);
        root.Controls.Add(footer, 0, 3);
        form.Controls.Add(root);
        form.CancelButton = closeButton;
        form.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                form.Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        form.FormClosed += delegate
        {
            audioLatencyLiveForm = null;
            audioLatencyLiveGraph = null;
            audioLatencyLiveTree = null;
            audioLatencyLiveStatus = null;
            audioLatencyLiveStopButton = null;
            audioLatencyLiveOpenReportButton = null;
            audioLatencyLiveNodes.Clear();
        };
        form.Shown += delegate { tree.Focus(); };
        audioLatencyLiveForm = form;
        audioLatencyLiveGraph = graph;
        audioLatencyLiveTree = tree;
        audioLatencyLiveStatus = monitorStatus;
        audioLatencyLiveStopButton = stopButton;
        audioLatencyLiveOpenReportButton = openReportButton;
        BuildAudioLatencyLiveTree();
        UpdateAudioLatencyLiveMonitor(snapshot);
        form.Show();
    }

    private void BuildAudioLatencyLiveTree()
    {
        if (audioLatencyLiveTree == null)
        {
            return;
        }
        audioLatencyLiveTree.BeginUpdate();
        try
        {
            audioLatencyLiveTree.Nodes.Clear();
            audioLatencyLiveNodes.Clear();
            AddAudioLatencyLiveGroup("current", T("group.Current interval", "Current interval"), new[]
            {
                "latest-dpc", "latest-dpc-count", "latest-dpc-driver", "latest-isr", "latest-isr-count", "latest-isr-driver", "latest-faults"
            });
            AddAudioLatencyLiveGroup("recent", T("group.Recent 60 seconds", "Recent 60 seconds"), new[] { "recent-dpc", "recent-isr" });
            AddAudioLatencyLiveGroup("session", T("group.Entire test", "Entire test"), new[]
            {
                "status", "elapsed", "max-dpc", "max-isr", "dpc-count", "isr-count", "fault-count", "lost", "top-dpc", "top-isr"
            });
            audioLatencyLiveTree.ExpandAll();
            if (audioLatencyLiveTree.Nodes.Count > 0)
            {
                audioLatencyLiveTree.SelectedNode = audioLatencyLiveTree.Nodes[0];
            }
        }
        finally
        {
            audioLatencyLiveTree.EndUpdate();
        }
    }

    private void AddAudioLatencyLiveGroup(string key, string text, IEnumerable<string> children)
    {
        var root = new TreeNode(text) { Name = key };
        audioLatencyLiveNodes[key] = root;
        foreach (var childKey in children)
        {
            var child = new TreeNode { Name = childKey };
            root.Nodes.Add(child);
            audioLatencyLiveNodes[childKey] = child;
        }
        audioLatencyLiveTree.Nodes.Add(root);
    }

    private void UpdateAudioLatencyLiveMonitor(AudioLatencyLiveSnapshot snapshot)
    {
        if (snapshot == null || audioLatencyLiveForm == null || audioLatencyLiveForm.IsDisposed || audioLatencyLiveTree == null)
        {
            return;
        }
        if (audioLatencyLiveForm.InvokeRequired)
        {
            try { audioLatencyLiveForm.BeginInvoke(new Action(() => UpdateAudioLatencyLiveMonitor(snapshot))); } catch { }
            return;
        }

        var latest = snapshot.Samples.LastOrDefault();
        var recent = snapshot.Samples.Where(s => latest != null && s.SampledLocal >= latest.SampledLocal.AddSeconds(-60)).ToList();
        SetAudioLatencyLiveNode("latest-dpc", T("reading.Latest DPC peak", "Latest DPC peak"), FormatAudioLatencyMicroseconds(latest == null ? 0 : latest.MaximumDpcMicroseconds));
        SetAudioLatencyLiveNode("latest-dpc-count", T("reading.DPC count", "DPC count"), (latest == null ? 0 : latest.DpcCount).ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("latest-dpc-driver", T("reading.Latest DPC driver", "Latest DPC driver"), FirstNonEmpty(snapshot.LatestDpcDriver, T("ui.Not available", "Not available")));
        SetAudioLatencyLiveNode("latest-isr", T("reading.Latest ISR peak", "Latest ISR peak"), FormatAudioLatencyMicroseconds(latest == null ? 0 : latest.MaximumIsrMicroseconds));
        SetAudioLatencyLiveNode("latest-isr-count", T("reading.ISR count", "ISR count"), (latest == null ? 0 : latest.IsrCount).ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("latest-isr-driver", T("reading.Latest ISR driver", "Latest ISR driver"), FirstNonEmpty(snapshot.LatestIsrDriver, T("ui.Not available", "Not available")));
        SetAudioLatencyLiveNode("latest-faults", T("reading.Hard page faults", "Hard page faults"), (latest == null ? 0 : latest.HardFaultCount).ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("recent-dpc", T("reading.Recent DPC peak", "Recent 60-second DPC peak"), FormatAudioLatencyMicroseconds(recent.Count == 0 ? 0 : recent.Max(s => s.MaximumDpcMicroseconds)));
        SetAudioLatencyLiveNode("recent-isr", T("reading.Recent ISR peak", "Recent 60-second ISR peak"), FormatAudioLatencyMicroseconds(recent.Count == 0 ? 0 : recent.Max(s => s.MaximumIsrMicroseconds)));
        SetAudioLatencyLiveNode("status", T("reading.Status", "Status"), snapshot.Active ? T("ui.Measuring", "Measuring") : string.IsNullOrWhiteSpace(snapshot.Error) ? T("ui.Complete", "Complete") : T("ui.Failed", "Failed"));
        SetAudioLatencyLiveNode("elapsed", T("reading.Elapsed time", "Elapsed time"), FormatAudioLatencyDuration(snapshot.Elapsed));
        SetAudioLatencyLiveNode("max-dpc", T("reading.Maximum DPC duration", "Maximum DPC duration"), FormatAudioLatencyMicroseconds(snapshot.MaximumDpcMicroseconds));
        SetAudioLatencyLiveNode("max-isr", T("reading.Maximum ISR duration", "Maximum ISR duration"), FormatAudioLatencyMicroseconds(snapshot.MaximumIsrMicroseconds));
        SetAudioLatencyLiveNode("dpc-count", T("reading.DPC count", "DPC count"), snapshot.DpcCount.ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("isr-count", T("reading.ISR count", "ISR count"), snapshot.IsrCount.ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("fault-count", T("reading.Hard page faults", "Hard page faults"), snapshot.HardFaultCount.ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("lost", T("reading.Events lost", "Events lost"), snapshot.EventsLost.ToString("N0", CultureInfo.CurrentCulture));
        SetAudioLatencyLiveNode("top-dpc", T("reading.Highest DPC driver", "Highest DPC driver"), FirstNonEmpty(snapshot.HighestDpcDriver, T("ui.Not available", "Not available")));
        SetAudioLatencyLiveNode("top-isr", T("reading.Highest ISR driver", "Highest ISR driver"), FirstNonEmpty(snapshot.HighestIsrDriver, T("ui.Not available", "Not available")));
        audioLatencyLiveGraph.SetSamples(snapshot.Samples);
        audioLatencyLiveStopButton.Enabled = snapshot.Active;
        audioLatencyLiveOpenReportButton.Enabled = !string.IsNullOrWhiteSpace(snapshot.ReportPath);
        audioLatencyLiveStatus.Text = snapshot.Active
            ? (snapshot.StopAtLocal == DateTime.MinValue
                ? T("status.Audio latency test is running until stopped.", "Test is running until stopped.")
                : string.Format(T("status.Audio latency test remaining.", "Test is running; {0} remaining."), FormatAudioLatencyDuration(snapshot.StopAtLocal - DateTime.Now)))
            : string.IsNullOrWhiteSpace(snapshot.ReportPath)
                ? T("status.Audio latency test complete.", "Test complete.")
                : T("status.Audio latency test complete; report available.", "Test complete. The report is ready to open.");
    }

    private void SetAudioLatencyLiveNode(string key, string label, string value)
    {
        TreeNode node;
        if (!audioLatencyLiveNodes.TryGetValue(key, out node))
        {
            return;
        }
        var text = label + ": " + value;
        if (!string.Equals(node.Text, text, StringComparison.Ordinal))
        {
            node.Text = text;
        }
    }

    private AudioLatencyLiveSnapshot BuildAudioLatencyLiveSnapshot()
    {
        lock (audioLatencyLock)
        {
            return BuildAudioLatencyLiveSnapshotLocked();
        }
    }

    private AudioLatencyLiveSnapshot BuildAudioLatencyLiveSnapshotLocked()
    {
        var snapshot = new AudioLatencyLiveSnapshot();
        var run = activeAudioLatencyRun ?? latestAudioLatencyRun;
        if (run == null)
        {
            return snapshot;
        }
        snapshot.HasRun = true;
        snapshot.Active = activeAudioLatencyRun != null && !run.StopRequested;
        snapshot.StartedLocal = run.StartedLocal;
        snapshot.StopAtLocal = run.StopAtLocal;
        snapshot.Elapsed = (run.StoppedLocal == DateTime.MinValue ? DateTime.Now : run.StoppedLocal) - run.StartedLocal;
        snapshot.Error = run.Error ?? "";
        snapshot.ReportPath = run.ReportPath ?? "";
        snapshot.DpcCount = run.DpcCount;
        snapshot.IsrCount = run.IsrCount;
        snapshot.HardFaultCount = run.HardFaultCount;
        snapshot.EventsLost = run.EventsLost;
        snapshot.MaximumDpcMicroseconds = run.MaximumDpcMicroseconds;
        snapshot.MaximumIsrMicroseconds = run.MaximumIsrMicroseconds;
        foreach (var sample in run.Samples)
        {
            snapshot.Samples.Add(CloneAudioLatencySample(sample));
        }
        var latest = run.Samples.LastOrDefault();
        if (latest != null)
        {
            string ignored;
            snapshot.LatestDpcDriver = latest.DpcCount == 0 ? "" : ResolveAudioLatencyDriver(run.Images, latest.MaximumDpcRoutine, out ignored);
            snapshot.LatestIsrDriver = latest.IsrCount == 0 ? "" : ResolveAudioLatencyDriver(run.Images, latest.MaximumIsrRoutine, out ignored);
        }
        string ignoredPath;
        snapshot.HighestDpcDriver = run.DpcCount == 0
            ? ""
            : ResolveAudioLatencyDriver(run.Images, run.MaximumDpcRoutine, out ignoredPath) + ", " + FormatAudioLatencyMicroseconds(run.MaximumDpcMicroseconds);
        snapshot.HighestIsrDriver = run.IsrCount == 0
            ? ""
            : ResolveAudioLatencyDriver(run.Images, run.MaximumIsrRoutine, out ignoredPath) + ", " + FormatAudioLatencyMicroseconds(run.MaximumIsrMicroseconds);
        return snapshot;
    }

    private static AudioLatencySample CloneAudioLatencySample(AudioLatencySample sample)
    {
        return new AudioLatencySample
        {
            SampledLocal = sample.SampledLocal,
            IntervalSeconds = sample.IntervalSeconds,
            DpcCount = sample.DpcCount,
            IsrCount = sample.IsrCount,
            HardFaultCount = sample.HardFaultCount,
            MaximumDpcMicroseconds = sample.MaximumDpcMicroseconds,
            MaximumIsrMicroseconds = sample.MaximumIsrMicroseconds,
            MaximumDpcRoutine = sample.MaximumDpcRoutine,
            MaximumIsrRoutine = sample.MaximumIsrRoutine
        };
    }

    private static void FinalizeAudioLatencySampleLocked(AudioLatencyRun run, DateTime now, bool force)
    {
        if (run == null)
        {
            return;
        }
        if (run.IntervalStartedLocal == DateTime.MinValue)
        {
            run.IntervalStartedLocal = now;
            return;
        }
        var elapsed = now - run.IntervalStartedLocal;
        if ((!force && elapsed.TotalMilliseconds < 900) || elapsed <= TimeSpan.Zero)
        {
            return;
        }
        if (force && elapsed.TotalMilliseconds < 100 && run.IntervalDpcCount == 0 && run.IntervalIsrCount == 0 && run.IntervalHardFaultCount == 0)
        {
            return;
        }
        run.Samples.Add(new AudioLatencySample
        {
            SampledLocal = now,
            IntervalSeconds = elapsed.TotalSeconds,
            DpcCount = run.IntervalDpcCount,
            IsrCount = run.IntervalIsrCount,
            HardFaultCount = run.IntervalHardFaultCount,
            MaximumDpcMicroseconds = run.IntervalMaximumDpcMicroseconds,
            MaximumIsrMicroseconds = run.IntervalMaximumIsrMicroseconds,
            MaximumDpcRoutine = run.IntervalMaximumDpcRoutine,
            MaximumIsrRoutine = run.IntervalMaximumIsrRoutine
        });
        while (run.Samples.Count > 120)
        {
            run.Samples.RemoveAt(0);
        }
        run.IntervalStartedLocal = now;
        run.IntervalDpcCount = 0;
        run.IntervalIsrCount = 0;
        run.IntervalHardFaultCount = 0;
        run.IntervalMaximumDpcMicroseconds = 0;
        run.IntervalMaximumIsrMicroseconds = 0;
        run.IntervalMaximumDpcRoutine = 0;
        run.IntervalMaximumIsrRoutine = 0;
    }

    private void OpenAudioLatencyReport(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath) || !System.IO.File.Exists(reportPath))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(audioLatencyLiveForm ?? this, ex.Message, T("ui.Could not open report", "Could not open report"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CloseAudioLatencyLiveMonitor()
    {
        var form = audioLatencyLiveForm;
        if (form == null || form.IsDisposed)
        {
            return;
        }
        try { form.Close(); } catch { }
    }
}
