using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;

[assembly: System.Reflection.AssemblyTitle("Sensor Readout")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

public sealed class SensorRow
{
    public string Type;
    public string Hardware;
    public string Name;
    public string Identifier;
    public float? Value;
    public string DisplayValue;
    public string Source;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? Identifier : Name;
    }
}

public sealed class DeviceFilter
{
    public string Key;
    public string DisplayName;
    public string Type;
    public string Hardware;

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed class AppSettings
{
    public bool AutoRefreshEnabled = true;
    public bool RefreshWhileFocused = true;
    public int RefreshIntervalSeconds = 8;
    public bool TrayStatusEnabled = true;
    public bool PrerequisitesPromptShown = false;
    public string LoggingLevel = "Off";
    public List<string> TrayItemKeys = new List<string>();
    public List<string> HiddenReadingKeys = new List<string>();
    public Dictionary<string, string> FanLabels = new Dictionary<string, string>();
}

public sealed class ReadingTreeItem
{
    public string Key;
    public string Text;
    public SensorRow Row;
    public readonly List<ReadingTreeItem> Children = new List<ReadingTreeItem>();
}

public sealed class PreferencesForm : Form
{
    private readonly CheckBox autoRefreshCheckBox;
    private readonly CheckBox refreshWhileFocusedCheckBox;
    private readonly CheckBox trayStatusCheckBox;
    private readonly NumericUpDown refreshSecondsBox;
    private readonly ComboBox loggingLevelBox;
    private readonly CheckedListBox trayItemsList;
    private readonly CheckedListBox hiddenItemsList;
    private readonly List<SensorRow> rows;
    private readonly List<string> hiddenReadingKeys;

    public bool AutoRefreshEnabled { get { return autoRefreshCheckBox.Checked; } }
    public bool RefreshWhileFocused { get { return refreshWhileFocusedCheckBox.Checked; } }
    public bool TrayStatusEnabled { get { return trayStatusCheckBox.Checked; } }
    public int RefreshIntervalSeconds { get { return Convert.ToInt32(refreshSecondsBox.Value); } }
    public string LoggingLevel { get { return Convert.ToString(loggingLevelBox.SelectedItem) ?? "Off"; } }
    public List<string> TrayItemKeys { get; private set; }
    public List<string> HiddenReadingKeys { get; private set; }

    public PreferencesForm(AppSettings settings, List<SensorRow> latestRows)
    {
        Text = "Sensor Readout Preferences";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 520);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        hiddenReadingKeys = new List<string>(settings.HiddenReadingKeys ?? new List<string>());

        rows = latestRows
            .Where(r => r.Type == "Temperature" || r.Type == "Fan" || r.Type == "SMART" || r.Type == "Performance")
            .OrderBy(r => SensorReadoutForm.TypeSortIndex(r.Type))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var generalTab = new TabPage("General");
        var hiddenTab = new TabPage("Hidden items");

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(10)
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        autoRefreshCheckBox = new CheckBox
        {
            Text = "Auto refresh",
            Checked = settings.AutoRefreshEnabled,
            AutoSize = true,
            AccessibleName = "Auto refresh"
        };

        refreshWhileFocusedCheckBox = new CheckBox
        {
            Text = "Refresh while Sensor Readout has focus",
            Checked = settings.RefreshWhileFocused,
            AutoSize = true,
            AccessibleName = "Refresh while focused"
        };

        trayStatusCheckBox = new CheckBox
        {
            Text = "Show status in notification area",
            Checked = settings.TrayStatusEnabled,
            AutoSize = true,
            AccessibleName = "Show status in notification area"
        };

        var intervalPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        intervalPanel.Controls.Add(new Label
        {
            Text = "Refresh interval, seconds:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        refreshSecondsBox = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 300,
            Value = Math.Max(2, Math.Min(300, settings.RefreshIntervalSeconds)),
            Width = 70,
            AccessibleName = "Refresh interval in seconds"
        };
        intervalPanel.Controls.Add(refreshSecondsBox);

        var loggingPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        loggingPanel.Controls.Add(new Label
        {
            Text = "Logging level:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        loggingLevelBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            AccessibleName = "Logging level"
        };
        loggingLevelBox.Items.AddRange(new object[] { "Off", "Error", "Normal", "Debug" });
        var configuredLogging = string.IsNullOrWhiteSpace(settings.LoggingLevel) ? "Off" : settings.LoggingLevel;
        loggingLevelBox.SelectedItem = loggingLevelBox.Items.Contains(configuredLogging) ? configuredLogging : "Off";
        loggingPanel.Controls.Add(loggingLevelBox);

        var trayLabel = new Label
        {
            Text = "Notification area items. Check up to four readings for the tray tooltip. If none are checked, Sensor Readout shows the first temperature readings.",
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        trayItemsList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            AccessibleName = "Notification area readings"
        };
        var selectedKeys = settings.TrayItemKeys ?? new List<string>();
        foreach (var row in rows)
        {
            var item = new TrayItemChoice(row);
            var index = trayItemsList.Items.Add(item);
            trayItemsList.SetItemChecked(index, selectedKeys.Contains(item.Key));
        }

        hiddenItemsList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            AccessibleName = "Hidden readings"
        };
        foreach (var key in hiddenReadingKeys.OrderBy(k => k))
        {
            var index = hiddenItemsList.Items.Add(key);
            hiddenItemsList.SetItemChecked(index, true);
        }

        var hiddenLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.Controls.Add(new Label { Text = "Hidden readings and groups. Checked items are hidden. Uncheck items to show them again.", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        hiddenLayout.Controls.Add(hiddenItemsList, 0, 1);
        var hiddenButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var unhideSelectedButton = new Button { Text = "Show selected", AutoSize = true };
        unhideSelectedButton.Click += delegate
        {
            if (hiddenItemsList.SelectedIndex >= 0)
            {
                hiddenItemsList.SetItemChecked(hiddenItemsList.SelectedIndex, false);
            }
        };
        var unhideAllButton = new Button { Text = "Show all", AutoSize = true };
        unhideAllButton.Click += delegate
        {
            for (var i = 0; i < hiddenItemsList.Items.Count; i++)
            {
                hiddenItemsList.SetItemChecked(i, false);
            }
        };
        hiddenButtons.Controls.Add(unhideSelectedButton);
        hiddenButtons.Controls.Add(unhideAllButton);
        hiddenLayout.Controls.Add(hiddenButtons, 0, 2);
        hiddenTab.Controls.Add(hiddenLayout);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        okButton.Click += delegate
        {
            TrayItemKeys = trayItemsList.CheckedItems
                .Cast<TrayItemChoice>()
                .Take(4)
                .Select(i => i.Key)
                .ToList();
            HiddenReadingKeys = hiddenItemsList.CheckedItems
                .Cast<object>()
                .Select(i => Convert.ToString(i))
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .ToList();
        };

        main.Controls.Add(autoRefreshCheckBox, 0, 0);
        main.Controls.Add(refreshWhileFocusedCheckBox, 0, 1);
        main.Controls.Add(trayStatusCheckBox, 0, 2);
        main.Controls.Add(intervalPanel, 0, 3);
        main.Controls.Add(loggingPanel, 0, 4);
        main.Controls.Add(trayLabel, 0, 5);
        main.Controls.Add(trayItemsList, 0, 6);
        main.Controls.Add(buttons, 0, 7);
        generalTab.Controls.Add(main);
        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(hiddenTab);

        Controls.Add(tabs);
    }

    private sealed class TrayItemChoice
    {
        public readonly string Key;
        private readonly string label;

        public TrayItemChoice(SensorRow row)
        {
            Key = SensorReadoutForm.RowSettingsKey(row);
            label = row.Type + ", " + SensorReadoutForm.TrayChoiceLabel(row);
        }

        public override string ToString()
        {
            return label;
        }
    }
}

public sealed class SensorReadoutForm : Form
{
    private const string AppVersion = "1.0.0";
    private const string FanActionLogName = "SensorReadoutFanActions.log";
    private const long MaxLogBytes = 262144;
    private const int RefreshIntervalMs = 8000;
    private readonly AppSettings settings;
    private readonly MenuStrip menuStrip;
    private readonly ToolStripMenuItem autoRefreshMenuItem;
    private readonly ToolStripMenuItem refreshWhileFocusedMenuItem;
    private readonly ToolStripMenuItem trayStatusMenuItem;
    private readonly ListBox deviceList;
    private readonly TreeView readingTree;
    private readonly Label statusLabel;
    private readonly Button refreshButton;
    private readonly CheckBox pauseCheckBox;
    private readonly Button saveReportButton;
    private ComboBox fanControlBox;
    private TextBox fanLabelBox;
    private NumericUpDown fanPercentBox;
    private CheckBox showStoppedFansCheckBox;
    private readonly Timer timer;
    private readonly NotifyIcon trayIcon;
    private Icon trayStatusIcon;
    private readonly List<SensorRow> latestRows = new List<SensorRow>();
    private readonly object lhmLock = new object();
    private Computer lhmComputer;
    private string selectedFilterKey = "all";
    private bool updatingFanControlBox;
    private bool refreshInProgress;
    private bool minimizingToTray;
    private string lastReadingTreeSignature = "";
    private string lastReadingTreeShapeSignature = "";

    public SensorReadoutForm()
    {
        settings = LoadSettings();
        Text = "Sensor Readout " + AppVersion;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        MinimumSize = new Size(700, 420);
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                RefreshSensors();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                SaveReport();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Oemcomma)
            {
                ShowPreferences();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.L)
            {
                ShowFanControlsDialog();
                e.Handled = true;
            }
            else if (e.Control && SelectCategoryByShortcut(e.KeyCode))
            {
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F1)
            {
                ShowManual();
                e.Handled = true;
            }
        };

        menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add("Refresh now\tF5", null, delegate { RefreshSensors(); });
        fileMenu.DropDownItems.Add("Save report...\tCtrl+S", null, delegate { SaveReport(); });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("Exit", null, delegate { Close(); });

        var editMenu = new ToolStripMenuItem("&Edit");
        editMenu.DropDownItems.Add("Copy\tCtrl+C", null, delegate { CopySelectedTreeNode(); });
        editMenu.DropDownItems.Add("Rename...\tF2", null, delegate { RenameSelectedTreeNode(); });
        editMenu.DropDownItems.Add("Hide selected\tDel", null, delegate { HideSelectedTreeNode(); });

        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add("Overview\tCtrl+1", null, delegate { SelectCategoryByKey("all"); });
        viewMenu.DropDownItems.Add("Temperatures\tCtrl+2", null, delegate { SelectCategoryByKey("type|Temperature"); });
        viewMenu.DropDownItems.Add("Fans\tCtrl+3", null, delegate { SelectCategoryByKey("type|Fan"); });
        viewMenu.DropDownItems.Add("SMART\tCtrl+4", null, delegate { SelectCategoryByKey("type|SMART"); });
        viewMenu.DropDownItems.Add("Performance\tCtrl+5", null, delegate { SelectCategoryByKey("type|Performance"); });

        var optionsMenu = new ToolStripMenuItem("&Options");
        autoRefreshMenuItem = new ToolStripMenuItem("Auto refresh")
        {
            Checked = settings.AutoRefreshEnabled,
            CheckOnClick = true
        };
        autoRefreshMenuItem.CheckedChanged += delegate
        {
            settings.AutoRefreshEnabled = autoRefreshMenuItem.Checked;
            pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
            SaveSettings(settings);
            ApplyTimerSettings();
        };

        refreshWhileFocusedMenuItem = new ToolStripMenuItem("Refresh while focused")
        {
            Checked = settings.RefreshWhileFocused,
            CheckOnClick = true
        };
        refreshWhileFocusedMenuItem.CheckedChanged += delegate
        {
            settings.RefreshWhileFocused = refreshWhileFocusedMenuItem.Checked;
            SaveSettings(settings);
        };

        trayStatusMenuItem = new ToolStripMenuItem("Show tray status")
        {
            Checked = settings.TrayStatusEnabled,
            CheckOnClick = true
        };
        trayStatusMenuItem.CheckedChanged += delegate
        {
            settings.TrayStatusEnabled = trayStatusMenuItem.Checked;
            SaveSettings(settings);
            UpdateTrayStatus();
        };

        optionsMenu.DropDownItems.Add(autoRefreshMenuItem);
        optionsMenu.DropDownItems.Add(refreshWhileFocusedMenuItem);
        optionsMenu.DropDownItems.Add(trayStatusMenuItem);
        optionsMenu.DropDownItems.Add(new ToolStripSeparator());
        optionsMenu.DropDownItems.Add("Fan controls...\tCtrl+L", null, delegate { ShowFanControlsDialog(); });
        optionsMenu.DropDownItems.Add("Preferences...\tCtrl+,", null, delegate { ShowPreferences(); });

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("Manual\tF1", null, delegate { ShowManual(); });
        helpMenu.DropDownItems.Add("Install prerequisites...", null, delegate { RunPrerequisiteInstaller(); });
        helpMenu.DropDownItems.Add("About Sensor Readout", null, delegate { ShowAbout(); });

        menuStrip.Items.Add(fileMenu);
        menuStrip.Items.Add(editMenu);
        menuStrip.Items.Add(viewMenu);
        menuStrip.Items.Add(optionsMenu);
        menuStrip.Items.Add(helpMenu);
        MainMenuStrip = menuStrip;

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        refreshButton = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            AccessibleName = "Refresh sensors now, F5"
        };
        refreshButton.Click += delegate { RefreshSensors(); };

        saveReportButton = new Button
        {
            Text = "Save repor&t",
            AutoSize = true,
            AccessibleName = "Save sensor report"
        };
        saveReportButton.Click += delegate { SaveReport(); };

        pauseCheckBox = new CheckBox
        {
            Text = "&Pause",
            AutoSize = true,
            AccessibleName = "Pause automatic updates"
        };
        pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
        pauseCheckBox.CheckedChanged += delegate
        {
            settings.AutoRefreshEnabled = !pauseCheckBox.Checked;
            autoRefreshMenuItem.Checked = settings.AutoRefreshEnabled;
            SaveSettings(settings);
            ApplyTimerSettings();
        };

        topPanel.Controls.Add(refreshButton);
        topPanel.Controls.Add(saveReportButton);
        topPanel.Controls.Add(pauseCheckBox);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Sensor Readout",
            Visible = settings.TrayStatusEnabled,
            ContextMenuStrip = new ContextMenuStrip()
        };
        trayIcon.ContextMenuStrip.Items.Add("Open Sensor Readout", null, delegate
        {
            RestoreFromTray();
        });
        trayIcon.ContextMenuStrip.Items.Add("Refresh now", null, delegate { RefreshSensors(); });
        trayIcon.ContextMenuStrip.Items.Add("Preferences...", null, delegate { ShowPreferences(); });
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, delegate { Close(); });
        trayIcon.DoubleClick += delegate
        {
            RestoreFromTray();
        };

        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 340,
            FixedPanel = FixedPanel.Panel1,
            TabStop = false
        };
        splitContainer.Panel1.TabStop = false;
        splitContainer.Panel2.TabStop = false;

        deviceList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Category",
            AccessibleDescription = "Choose which readings to show"
        };
        deviceList.SelectedIndexChanged += delegate
        {
            var filter = deviceList.SelectedItem as DeviceFilter;
            if (filter != null)
            {
                selectedFilterKey = filter.Key;
                UpdateReadingList();
            }
        };
        readingTree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            FullRowSelect = true,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            AccessibleName = "Readings",
            AccessibleDescription = "Current readings grouped by category or device"
        };
        readingTree.ContextMenuStrip = new ContextMenuStrip();
        readingTree.ContextMenuStrip.Items.Add("Copy", null, delegate { CopySelectedTreeNode(); });
        readingTree.ContextMenuStrip.Items.Add("Rename...", null, delegate { RenameSelectedTreeNode(); });
        readingTree.ContextMenuStrip.Items.Add("Hide selected", null, delegate { HideSelectedTreeNode(); });
        readingTree.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedTreeNode();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F2)
            {
                RenameSelectedTreeNode();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                HideSelectedTreeNode();
                e.Handled = true;
            }
        };
        readingTree.Nodes.Add(new TreeNode("Please wait, collecting sensor data") { Name = "startup" });

        splitContainer.Panel1.Controls.Add(deviceList);
        splitContainer.Panel2.Controls.Add(readingTree);
        deviceList.Items.Add(new DeviceFilter { Key = "loading", DisplayName = "Please wait, collecting sensor data" });
        deviceList.SelectedIndex = 0;

        statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(8, 5, 8, 0),
            Text = "Please wait, collecting sensor data..."
        };

        Controls.Add(splitContainer);
        Controls.Add(statusLabel);
        Controls.Add(topPanel);
        Controls.Add(menuStrip);

        timer = new Timer { Interval = RefreshIntervalMs };
        timer.Tick += delegate
        {
            if (settings.AutoRefreshEnabled && (settings.RefreshWhileFocused || !ContainsFocus))
            {
                RefreshSensors();
            }
        };

        Shown += delegate
        {
            ApplyTimerSettings();
            CheckPrerequisitesOnFirstRun();
            RefreshSensors();
            timer.Start();
        };

        Resize += delegate
        {
            if (WindowState == FormWindowState.Minimized && settings.TrayStatusEnabled && !minimizingToTray)
            {
                MinimizeToTray();
            }
        };
    }

    private void ApplyTimerSettings()
    {
        var seconds = Math.Max(2, Math.Min(300, settings.RefreshIntervalSeconds));
        settings.RefreshIntervalSeconds = seconds;
        timer.Interval = seconds * 1000;
        if (settings.AutoRefreshEnabled)
        {
            timer.Start();
        }
        else
        {
            timer.Stop();
        }
    }

    private void MinimizeToTray()
    {
        minimizingToTray = true;
        BeginInvoke((MethodInvoker)delegate
        {
            try
            {
                if (WindowState == FormWindowState.Minimized && settings.TrayStatusEnabled)
                {
                    trayIcon.Visible = true;
                    ShowInTaskbar = false;
                    Hide();
                }
            }
            finally
            {
                minimizingToTray = false;
            }
        });
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowManual()
    {
        var manualPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
        if (!System.IO.File.Exists(manualPath))
        {
            manualPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "README.md"));
        }

        if (!System.IO.File.Exists(manualPath))
        {
            MessageBox.Show(this, "The manual could not be found beside Sensor Readout.", "Sensor Readout manual", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = manualPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open manual", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            "Sensor Readout " + AppVersion + Environment.NewLine + Environment.NewLine +
            "Created by Codex." + Environment.NewLine +
            "Ideas by Andre Louis.",
            "About Sensor Readout",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void CheckPrerequisitesOnFirstRun()
    {
        if (settings.PrerequisitesPromptShown || IsPawnIoInstalled())
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "PawnIO does not appear to be installed. Sensor Readout can still open, but motherboard sensors and fan controls may be missing." + Environment.NewLine + Environment.NewLine +
            "Do you want to run the prerequisite installer now?",
            "Sensor Readout prerequisites",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        settings.PrerequisitesPromptShown = true;
        SaveSettings(settings);

        if (result == DialogResult.Yes)
        {
            RunPrerequisiteInstaller();
        }
    }

    private void RunPrerequisiteInstaller()
    {
        var installerPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install-Prerequisites.cmd");
        if (!System.IO.File.Exists(installerPath))
        {
            MessageBox.Show(this, "Install-Prerequisites.cmd could not be found beside Sensor Readout.", "Sensor Readout prerequisites", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not start prerequisite installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool SelectCategoryByShortcut(Keys keyCode)
    {
        if (keyCode == Keys.D1 || keyCode == Keys.NumPad1)
        {
            return SelectCategoryByKey("all");
        }

        if (keyCode == Keys.D2 || keyCode == Keys.NumPad2)
        {
            return SelectCategoryByKey("type|Temperature");
        }

        if (keyCode == Keys.D3 || keyCode == Keys.NumPad3)
        {
            return SelectCategoryByKey("type|Fan");
        }

        if (keyCode == Keys.D4 || keyCode == Keys.NumPad4)
        {
            return SelectCategoryByKey("type|SMART");
        }

        if (keyCode == Keys.D5 || keyCode == Keys.NumPad5)
        {
            return SelectCategoryByKey("type|Performance");
        }

        return false;
    }

    private bool SelectCategoryByKey(string key)
    {
        for (var i = 0; i < deviceList.Items.Count; i++)
        {
            var filter = deviceList.Items[i] as DeviceFilter;
            if (filter != null && filter.Key == key)
            {
                deviceList.SelectedIndex = i;
                deviceList.Focus();
                return true;
            }
        }

        return false;
    }

    private void ShowPreferences()
    {
        using (var dialog = new PreferencesForm(settings, latestRows))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            settings.AutoRefreshEnabled = dialog.AutoRefreshEnabled;
            settings.RefreshWhileFocused = dialog.RefreshWhileFocused;
            settings.RefreshIntervalSeconds = dialog.RefreshIntervalSeconds;
            settings.TrayStatusEnabled = dialog.TrayStatusEnabled;
            settings.LoggingLevel = dialog.LoggingLevel;
            settings.TrayItemKeys = dialog.TrayItemKeys;
            settings.HiddenReadingKeys = dialog.HiddenReadingKeys;
            SaveSettings(settings);

            if (settings.TrayStatusEnabled)
            {
                ShowInTaskbar = true;
            }
            else
            {
                Show();
                ShowInTaskbar = true;
                WindowState = FormWindowState.Normal;
            }

            autoRefreshMenuItem.Checked = settings.AutoRefreshEnabled;
            refreshWhileFocusedMenuItem.Checked = settings.RefreshWhileFocused;
            trayStatusMenuItem.Checked = settings.TrayStatusEnabled;
            pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
            ApplyTimerSettings();
            UpdateTrayStatus();
            lastReadingTreeSignature = "";
            lastReadingTreeShapeSignature = "";
            UpdateReadingList();
            statusLabel.Text = "Preferences saved.";
        }
    }

    private void CopySelectedTreeNode()
    {
        if (readingTree.SelectedNode == null)
        {
            return;
        }

        var lines = new List<string>();
        AddTreeNodeText(readingTree.SelectedNode, lines, 0);
        if (lines.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
            statusLabel.Text = "Copied " + lines.Count + " line" + (lines.Count == 1 ? "" : "s") + " to clipboard.";
        }
    }

    private static void AddTreeNodeText(TreeNode node, List<string> lines, int depth)
    {
        lines.Add(new string(' ', depth * 2) + node.Text);
        foreach (TreeNode child in node.Nodes)
        {
            AddTreeNodeText(child, lines, depth + 1);
        }
    }

    private void HideSelectedTreeNode()
    {
        if (readingTree.SelectedNode == null || string.IsNullOrWhiteSpace(readingTree.SelectedNode.Name))
        {
            return;
        }

        settings.HiddenReadingKeys = settings.HiddenReadingKeys ?? new List<string>();
        var fallbackKey = FindHideFallbackKey(readingTree.SelectedNode);
        if (!settings.HiddenReadingKeys.Contains(readingTree.SelectedNode.Name))
        {
            settings.HiddenReadingKeys.Add(readingTree.SelectedNode.Name);
            SaveSettings(settings);
        }

        statusLabel.Text = "Hidden " + readingTree.SelectedNode.Text + ". Use Options, Preferences, Hidden items to show it again.";
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList(fallbackKey);
    }

    private void RenameSelectedTreeNode()
    {
        var row = GetSelectedReadingRow();
        if (row == null || (row.Type != "Fan" && row.Type != "Fan Control"))
        {
            statusLabel.Text = "Select a fan reading before renaming.";
            return;
        }

        var controlIdentifier = row.Type == "Fan Control" ? row.Identifier : GuessControlIdentifier(row.Identifier);
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            statusLabel.Text = "Could not match this fan reading to a fan control.";
            return;
        }

        var labels = LoadFanLabels();
        string currentLabel;
        labels.TryGetValue(controlIdentifier, out currentLabel);
        var baseName = BaseFanReadingName(row.Name);
        var newLabel = PromptForText("Rename Fan", "Friendly name for " + baseName + ":", currentLabel ?? "");
        if (newLabel == null)
        {
            return;
        }

        newLabel = newLabel.Trim();
        if (string.IsNullOrWhiteSpace(newLabel))
        {
            labels.Remove(controlIdentifier);
            labels.Remove(GuessFanIdentifier(controlIdentifier));
        }
        else
        {
            labels[controlIdentifier] = newLabel;
            labels[GuessFanIdentifier(controlIdentifier)] = newLabel;
        }

        SaveFanLabels(labels);
        statusLabel.Text = string.IsNullOrWhiteSpace(newLabel) ? "Removed fan label for " + baseName + "." : "Renamed " + baseName + " to " + newLabel + ".";
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        RefreshSensors();
    }

    private string PromptForText(string title, string label, string initialValue)
    {
        using (var dialog = new Form())
        {
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(460, 150);
            dialog.MinimumSize = new Size(360, 140);
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var promptLabel = new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill };
            var textBox = new TextBox { Text = initialValue ?? "", Dock = DockStyle.Fill, AccessibleName = label };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            layout.Controls.Add(promptLabel, 0, 0);
            layout.Controls.Add(textBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            dialog.Shown += delegate { textBox.Focus(); textBox.SelectAll(); };

            return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text : null;
        }
    }

    private void ShowFanControlsDialog()
    {
        EnsureFanControlControls();
        UpdateFanControlBox();

        using (var dialog = new Form())
        {
            dialog.Text = "Fan Controls";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(760, 230);
            dialog.MinimumSize = new Size(620, 210);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                    e.Handled = true;
                }
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 4; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(new Label { Text = "Fan:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
            layout.Controls.Add(fanControlBox, 1, 0);
            layout.Controls.Add(new Label { Text = "Label:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);

            var labelPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            labelPanel.Controls.Add(fanLabelBox);
            var saveFanLabelButton = new Button { Text = "Save &label", AutoSize = true, AccessibleName = "Save label for selected fan control" };
            saveFanLabelButton.Click += delegate { SaveSelectedFanLabel(); };
            labelPanel.Controls.Add(saveFanLabelButton);
            layout.Controls.Add(labelPanel, 1, 1);

            layout.Controls.Add(new Label { Text = "Manual percent:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
            var percentPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            percentPanel.Controls.Add(fanPercentBox);
            var applyFanButton = new Button { Text = "&Manual adjust", AutoSize = true, AccessibleName = "Apply manual percentage to selected fan control" };
            applyFanButton.Click += delegate { ApplySelectedFanControl(true); };
            percentPanel.Controls.Add(applyFanButton);
            var autoSelectedFanButton = new Button { Text = "Selected &auto", AutoSize = true, AccessibleName = "Return selected fan control to automatic" };
            autoSelectedFanButton.Click += delegate { ApplySelectedFanControl(false); };
            percentPanel.Controls.Add(autoSelectedFanButton);
            layout.Controls.Add(percentPanel, 1, 2);

            layout.Controls.Add(new Label { Text = "Profiles:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
            var profilePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var autoFanButton = new Button { Text = "All fans &reset", AutoSize = true, AccessibleName = "Return all fan controls to automatic" };
            autoFanButton.Click += delegate { ResetAllFanControls(); };
            var elevatedFanButton = new Button { Text = "All fans &75", AutoSize = true, AccessibleName = "Set all visible fan controls to 75 percent" };
            elevatedFanButton.Click += delegate { ApplyAllVisibleFanControls(75, "elevated"); };
            var maxFanButton = new Button { Text = "All fans ma&x", AutoSize = true, AccessibleName = "Set all visible fan controls to 100 percent" };
            maxFanButton.Click += delegate { ApplyAllVisibleFanControls(100, "max"); };
            profilePanel.Controls.Add(autoFanButton);
            profilePanel.Controls.Add(elevatedFanButton);
            profilePanel.Controls.Add(maxFanButton);
            profilePanel.Controls.Add(showStoppedFansCheckBox);
            layout.Controls.Add(profilePanel, 1, 3);

            var closePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            closePanel.Controls.Add(closeButton);
            layout.Controls.Add(closePanel, 1, 4);
            dialog.AcceptButton = closeButton;
            dialog.CancelButton = closeButton;
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }

        fanControlBox = null;
        fanLabelBox = null;
        fanPercentBox = null;
        showStoppedFansCheckBox = null;
    }

    private void EnsureFanControlControls()
    {
        if (fanControlBox != null)
        {
            return;
        }

        fanControlBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 480,
            AccessibleName = "Fan control target",
            AccessibleDescription = "Choose which fan control to adjust"
        };
        fanControlBox.SelectedIndexChanged += delegate
        {
            if (updatingFanControlBox)
            {
                return;
            }

            var row = fanControlBox.SelectedItem as SensorRow;
            fanLabelBox.Text = row == null ? "" : GetFanLabel(row.Identifier, BaseFanControlName(row.Name));
        };

        fanLabelBox = new TextBox
        {
            Width = 260,
            AccessibleName = "Fan label",
            AccessibleDescription = "Friendly name for the selected fan control"
        };

        fanPercentBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 5,
            Value = 50,
            Width = 70,
            AccessibleName = "Fan manual percentage"
        };
        fanPercentBox.Enter += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Click += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Leave += delegate { ClampFanPercentBox(); };

        showStoppedFansCheckBox = new CheckBox
        {
            Text = "Show &stopped",
            AutoSize = true,
            AccessibleName = "Show stopped or unpopulated fan headers"
        };
        showStoppedFansCheckBox.CheckedChanged += delegate { UpdateFanControlBox(); };
    }

    private void RefreshSensors()
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        refreshButton.Enabled = false;
        statusLabel.Text = "Refreshing sensors...";

        Task.Factory.StartNew(new Func<List<SensorRow>>(CollectSensorRows))
            .ContinueWith(delegate(Task<List<SensorRow>> task)
            {
                try
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    if (task.IsFaulted)
                    {
                        var ex = task.Exception == null ? null : task.Exception.GetBaseException();
                        statusLabel.Text = ex == null ? "Sensor refresh failed." : ex.GetType().Name + ": " + ex.Message;
                        return;
                    }

                    var rows = task.Result;
                    latestRows.Clear();
                    latestRows.AddRange(rows);
                    UpdateFanControlBox();
                    UpdateDeviceList();
                    UpdateReadingList();
                    UpdateTrayStatus();

                    if (rows.Count == 0)
                    {
                        statusLabel.Text = "No sensor rows returned yet.";
                    }
                    else
                    {
                        var sources = string.Join(", ", rows.Select(r => r.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToArray());
                        statusLabel.Text = BuildRefreshStatus(rows, sources);
                    }
                }
                finally
                {
                    refreshButton.Enabled = true;
                    refreshInProgress = false;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private List<SensorRow> CollectSensorRows()
    {
        var rows = GetLibreHardwareMonitorSensors()
            .Concat(GetWindowsSmartRows())
            .ToList();

        rows.AddRange(GetSystemPerformanceRows());
        rows.AddRange(GetStoragePerformanceRows(rows));
        rows = ApplyFanLabelsToReadings(rows);

        return ConsolidateRelatedRows(rows
            .Where(s => s.Type == "Temperature" || s.Type == "Fan" || s.Type == "SMART" || s.Type == "Performance" || s.Type == "Fan Control")
            .GroupBy(s => SensorDeduplicationKey(s))
            .Select(g => g.First())
            .ToList())
            .OrderBy(s => TypeSortIndex(s.Type))
            .ThenBy(s => s.Hardware)
            .ThenBy(s => ReadingSortIndex(s.Name))
            .ThenBy(s => s.Name)
            .ToList();
    }

    private static string BuildRefreshStatus(List<SensorRow> rows, string sources)
    {
        var status = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " from " + sources + ". " + rows.Count + " sensor rows.";
        var hasMotherboard = rows.Any(r => string.Equals(r.Hardware, "Motherboard", StringComparison.OrdinalIgnoreCase));
        var hasFanControls = rows.Any(r => r.Type == "Fan Control");
        if (!hasMotherboard || !hasFanControls)
        {
            status += " Motherboard fans or controls may require PawnIO, administrator rights, or hardware support.";
        }

        return status;
    }

    private List<SensorRow> ApplyFanLabelsToReadings(List<SensorRow> rows)
    {
        var labels = LoadFanLabels();
        if (labels.Count == 0)
        {
            return rows;
        }

        return rows.Select(row =>
        {
            if (row.Type != "Fan")
            {
                return row;
            }

            var controlIdentifier = GuessControlIdentifier(row.Identifier);
            string label;
            if (string.IsNullOrWhiteSpace(controlIdentifier) || !labels.TryGetValue(controlIdentifier, out label) || string.IsNullOrWhiteSpace(label))
            {
                return row;
            }

            return new SensorRow
            {
                Type = row.Type,
                Hardware = row.Hardware,
                Name = label + ", " + BaseFanControlName(row.Name),
                Identifier = row.Identifier,
                Value = row.Value,
                DisplayValue = row.DisplayValue,
                Source = row.Source
            };
        }).ToList();
    }

    private void ApplySelectedFanControl(bool manual)
    {
        ClampFanPercentBox();
        var row = GetSelectedFanControlTarget();
        if (row == null || row.Type != "Fan Control")
        {
            statusLabel.Text = "Open fan controls and select a fan control target.";
            LogFanAction(statusLabel.Text);
            return;
        }

        if (!manual)
        {
            fanPercentBox.Value = 50;
        }

        var identifier = row.Identifier;
        var name = row.Name;
        var percent = (int)fanPercentBox.Value;
        RunFanAction(
            manual ? "Setting " + name + " to " + percent + "%..." : "Returning " + name + " to automatic...",
            delegate { SetLibreHardwareMonitorControl(identifier, percent, manual); },
            delegate
            {
                statusLabel.Text = "LibreHardwareMonitor: " + name + " " + (manual ? percent + "%" : "automatic/default") + ".";
                RefreshSensors();
            });
    }

    private void ClampFanPercentBox()
    {
        if (fanPercentBox == null)
        {
            return;
        }

        if (fanPercentBox.Value < fanPercentBox.Minimum)
        {
            fanPercentBox.Value = fanPercentBox.Minimum;
        }

        if (fanPercentBox.Value > fanPercentBox.Maximum)
        {
            fanPercentBox.Value = fanPercentBox.Maximum;
        }
    }

    private void ResetAllFanControls()
    {
        if (fanPercentBox == null)
        {
            statusLabel.Text = "Open fan controls before resetting fans.";
            return;
        }
        fanPercentBox.Value = 50;
        var count = 0;
        RunFanAction(
            "Returning all fan controls to automatic...",
            delegate { count = SetAllLibreHardwareMonitorControlsDefault(); },
            delegate
            {
                statusLabel.Text = "LibreHardwareMonitor: reset " + count + " fan control" + (count == 1 ? "" : "s") + " to automatic/default.";
                RefreshSensors();
            });
    }

    private void RunFanAction(string startingStatus, Action worker, Action completed)
    {
        statusLabel.Text = startingStatus;
        LogFanAction(startingStatus);
        Task.Factory.StartNew(worker).ContinueWith(delegate(Task task)
        {
            if (IsDisposed)
            {
                return;
            }

            if (task.IsFaulted)
            {
                var ex = task.Exception == null ? null : task.Exception.GetBaseException();
                statusLabel.Text = ex == null ? "Fan control action failed." : ex.GetType().Name + ": " + ex.Message;
                LogError(statusLabel.Text);
                return;
            }

            completed();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyAllVisibleFanControls(int percent, string profileName)
    {
        if (fanControlBox == null || fanPercentBox == null)
        {
            statusLabel.Text = "Open fan controls before applying a fan profile.";
            return;
        }
        fanPercentBox.Value = percent;
        var controls = fanControlBox.Items.Cast<SensorRow>().ToList();
        if (controls.Count == 0)
        {
            statusLabel.Text = "No visible fan controls to adjust.";
            LogFanAction(statusLabel.Text);
            return;
        }

        RunFanAction(
            "Applying " + profileName + " profile to " + controls.Count + " fan controls...",
            delegate
            {
                foreach (var control in controls)
                {
                    SetLibreHardwareMonitorControl(control.Identifier, percent, true);
                }
            },
            delegate
            {
                statusLabel.Text = "LibreHardwareMonitor: " + profileName + " profile, " + percent + "% on " + controls.Count + " controls.";
                RefreshSensors();
            });
    }

    private SensorRow GetSelectedFanControlTarget()
    {
        if (fanControlBox == null)
        {
            return null;
        }

        var selectedControl = fanControlBox.SelectedItem as SensorRow;
        if (selectedControl != null)
        {
            return selectedControl;
        }

        var row = GetSelectedReadingRow();
        if (row == null)
        {
            return null;
        }

        if (row.Type == "Fan Control")
        {
            return row;
        }

        if (row.Type != "Fan")
        {
            return null;
        }

        var controlRows = latestRows.Where(r => r.Type == "Fan Control").ToList();
        var byName = controlRows.FirstOrDefault(r => string.Equals(CleanControlName(r.Name), CleanControlName(row.Name), StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            return byName;
        }

        var guessedIdentifier = GuessControlIdentifier(row.Identifier);
        if (!string.IsNullOrWhiteSpace(guessedIdentifier))
        {
            var byIdentifier = controlRows.FirstOrDefault(r => string.Equals(r.Identifier, guessedIdentifier, StringComparison.OrdinalIgnoreCase));
            if (byIdentifier != null)
            {
                return byIdentifier;
            }
        }

        return null;
    }

    private void UpdateFanControlBox()
    {
        if (fanControlBox == null || fanLabelBox == null || fanPercentBox == null || showStoppedFansCheckBox == null)
        {
            return;
        }

        if (fanControlBox.Focused || fanLabelBox.Focused || fanPercentBox.Focused)
        {
            return;
        }

        var labels = LoadFanLabels();
        var controls = latestRows
            .Where(r => r.Type == "Fan Control")
            .OrderBy(r => ControlSortKey(r.Identifier))
            .ToList();
        controls = controls
            .Select(c => EnrichFanControlRow(c, labels))
            .Where(c => showStoppedFansCheckBox.Checked || ShouldShowFanControl(c))
            .OrderBy(c => ControlSortKey(c.Identifier))
            .ToList();

        var selectedIdentifier = (fanControlBox.SelectedItem as SensorRow) == null
            ? ""
            : ((SensorRow)fanControlBox.SelectedItem).Identifier;

        var currentSignature = string.Join("|", fanControlBox.Items.Cast<SensorRow>().Select(r => r.Identifier + "=" + r.Name).ToArray());
        var newSignature = string.Join("|", controls.Select(r => r.Identifier + "=" + r.Name).ToArray());
        if (currentSignature == newSignature)
        {
            return;
        }

        updatingFanControlBox = true;
        fanControlBox.BeginUpdate();
        try
        {
            fanControlBox.Items.Clear();
            foreach (var control in controls)
            {
                fanControlBox.Items.Add(control);
            }

            if (fanControlBox.Items.Count > 0)
            {
                var selectedIndex = 0;
                for (var i = 0; i < fanControlBox.Items.Count; i++)
                {
                    var item = (SensorRow)fanControlBox.Items[i];
                    if (string.Equals(item.Identifier, selectedIdentifier, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }

                fanControlBox.SelectedIndex = selectedIndex;
                var row = fanControlBox.SelectedItem as SensorRow;
                fanLabelBox.Text = row == null ? "" : GetFanLabel(row.Identifier, BaseFanControlName(row.Name));
            }
            else
            {
                fanLabelBox.Text = "";
            }
        }
        finally
        {
            fanControlBox.EndUpdate();
            updatingFanControlBox = false;
        }
    }

    private SensorRow EnrichFanControlRow(SensorRow control, Dictionary<string, string> labels)
    {
        var baseName = BaseFanControlName(control.Name);
        var label = labels.ContainsKey(control.Identifier) ? labels[control.Identifier] : baseName;
        var rpm = GetFanRpmForControl(control.Identifier);
        var rpmText = rpm.HasValue ? Math.Round(rpm.Value, 0).ToString("0") + " RPM" : "no RPM reading";
        var state = control.DisplayValue ?? "";

        return new SensorRow
        {
            Type = control.Type,
            Hardware = control.Hardware,
            Name = label + ", " + baseName + ", " + rpmText + ", " + state,
            Identifier = control.Identifier,
            Value = control.Value,
            DisplayValue = control.DisplayValue,
            Source = control.Source
        };
    }

    private bool ShouldShowFanControl(SensorRow control)
    {
        if (IsGpuControl(control.Identifier))
        {
            return true;
        }

        var rpm = GetFanRpmForControl(control.Identifier);
        return rpm.HasValue && rpm.Value > 0;
    }

    private float? GetFanRpmForControl(string controlIdentifier)
    {
        var fanIdentifier = GuessFanIdentifier(controlIdentifier);
        var row = latestRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(r.Identifier, fanIdentifier, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            return row.Value;
        }

        var baseName = BaseFanControlName(controlIdentifier);
        row = latestRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(BaseFanControlName(r.Name), baseName, StringComparison.OrdinalIgnoreCase));
        return row == null ? (float?)null : row.Value;
    }

    private void SaveSelectedFanLabel()
    {
        if (fanControlBox == null || fanLabelBox == null)
        {
            statusLabel.Text = "Open fan controls before saving a fan label.";
            return;
        }

        var row = fanControlBox.SelectedItem as SensorRow;
        if (row == null)
        {
            statusLabel.Text = "Select a fan control before saving a label.";
            return;
        }

        var labels = LoadFanLabels();
        var label = fanLabelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            labels.Remove(row.Identifier);
            labels.Remove(GuessFanIdentifier(row.Identifier));
        }
        else
        {
            labels[row.Identifier] = label;
            labels[GuessFanIdentifier(row.Identifier)] = label;
        }

        SaveFanLabels(labels);
        statusLabel.Text = "Saved fan label for " + BaseFanControlName(row.Name) + ".";
        UpdateFanControlBox();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList();
    }

    private void SaveReport()
    {
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "Save Sensor Report";
            dialog.Filter = "Text report (*.txt)|*.txt|Formatted HTML report (*.html)|*.html";
            dialog.FileName = "SensorReadout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            dialog.DefaultExt = "txt";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var html = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            if (html && !dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                dialog.FileName = System.IO.Path.ChangeExtension(dialog.FileName, ".html");
            }

            var stopwatch = Stopwatch.StartNew();
            var report = html ? BuildHtmlReport() : BuildTextReport();
            System.IO.File.WriteAllText(dialog.FileName, report);
            stopwatch.Stop();
            statusLabel.Text = "Saved report to " + dialog.FileName + " in " + FormatElapsed(stopwatch.Elapsed) + ".";
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds >= 1)
        {
            return elapsed.TotalSeconds.ToString("0.00") + " seconds";
        }

        return elapsed.TotalMilliseconds.ToString("0") + " ms";
    }

    private string BuildTextReport()
    {
        var lines = new List<string>();
        lines.Add("Sensor Readout report");
        lines.Add("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        lines.Add("");

        foreach (var typeGroup in latestRows
            .Where(r => r.Type != "Fan Control")
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => r.Type)
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .GroupBy(r => r.Type))
        {
            lines.Add(typeGroup.Key);
            foreach (var hardwareGroup in typeGroup.GroupBy(r => ShortHardwareName(r.Hardware)))
            {
                lines.Add("  " + hardwareGroup.Key);
                foreach (var row in hardwareGroup)
                {
                    lines.Add("    " + CleanSensorName(row.Name) + ": " + FormatValue(row));
                }
            }

            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private string BuildHtmlReport()
    {
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\"><title>Sensor Readout report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.35} table{border-collapse:collapse;margin:0 0 1.2em 0} th,td{border:1px solid #888;padding:4px 8px;text-align:left} h2{margin-top:1.4em} h3{margin-bottom:.4em}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>Sensor Readout report</h1>");
        html.AppendLine("<p>Generated " + HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "</p>");
        foreach (var typeGroup in latestRows
            .Where(r => r.Type != "Fan Control")
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => r.Type)
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .GroupBy(r => r.Type))
        {
            html.AppendLine("<h2>" + HtmlEncode(typeGroup.Key) + "</h2>");
            foreach (var hardwareGroup in typeGroup.GroupBy(r => ShortHardwareName(r.Hardware)))
            {
                html.AppendLine("<h3>" + HtmlEncode(hardwareGroup.Key) + "</h3>");
                html.AppendLine("<table><thead><tr><th>Sensor</th><th>Value</th><th>Source</th></tr></thead><tbody>");
                foreach (var row in hardwareGroup)
                {
                    html.AppendLine("<tr><td>" + HtmlEncode(CleanSensorName(row.Name)) + "</td><td>" + HtmlEncode(FormatValue(row)) + "</td><td>" + HtmlEncode(row.Source) + "</td></tr>");
                }

                html.AppendLine("</tbody></table>");
            }
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private SensorRow GetSelectedReadingRow()
    {
        return readingTree.SelectedNode == null ? null : readingTree.SelectedNode.Tag as SensorRow;
    }

    private void SetLibreHardwareMonitorControl(string controlIdentifier, int percent, bool manual)
    {
        lock (lhmLock)
        {
            EnsureLibreHardwareMonitorComputerOpen();
            var sensor = FindControlSensor(lhmComputer, controlIdentifier);
            if (sensor == null || sensor.Control == null)
            {
                throw new InvalidOperationException("Could not find direct LibreHardwareMonitor control: " + controlIdentifier);
            }

            if (manual)
            {
                sensor.Control.SetSoftware(Math.Max(0, Math.Min(100, percent)));
            }
            else
            {
                sensor.Control.SetDefault();
            }

            foreach (var hardware in lhmComputer.Hardware)
            {
                UpdateHardware(hardware);
            }
        }
    }

    private int SetAllLibreHardwareMonitorControlsDefault()
    {
        lock (lhmLock)
        {
            EnsureLibreHardwareMonitorComputerOpen();
            var sensors = GetAllSensors(lhmComputer.Hardware)
                .Where(s => s.SensorType.ToString() == "Control" && s.Control != null)
                .ToList();
            foreach (var sensor in sensors)
            {
                sensor.Control.SetDefault();
            }

            foreach (var hardware in lhmComputer.Hardware)
            {
                UpdateHardware(hardware);
            }

            return sensors.Count;
        }
    }

    private static Computer CreateLibreHardwareMonitorComputer()
    {
        return new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true
        };
    }

    private static ISensor FindControlSensor(Computer computer, string identifier)
    {
        foreach (var hardware in computer.Hardware)
        {
            UpdateHardware(hardware);
        }

        return GetAllSensors(computer.Hardware)
            .FirstOrDefault(s => s.Control != null && string.Equals(s.Identifier == null ? "" : s.Identifier.ToString(), identifier, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ISensor> GetAllSensors(IEnumerable<IHardware> hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            UpdateHardware(hardware);
            foreach (var sensor in hardware.Sensors)
            {
                yield return sensor;
            }

            foreach (var sensor in GetAllSensors(hardware.SubHardware))
            {
                yield return sensor;
            }
        }
    }

    private static string CleanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name
            .Replace("Control 1 - NVIDIA GeForce RTX 4060", "Fan 1 - NVIDIA GeForce RTX 4060")
            .Trim();
    }

    private static string BaseFanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var comma = name.IndexOf(',');
        if (comma > -1)
        {
            name = name.Substring(0, comma);
        }

        return CleanControlName(name);
    }

    private static string BaseFanReadingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var comma = name.IndexOf(',');
        if (comma > -1 && comma + 1 < name.Length)
        {
            var suffix = name.Substring(comma + 1).Trim();
            if (suffix.StartsWith("Fan", StringComparison.OrdinalIgnoreCase))
            {
                return CleanControlName(suffix);
            }
        }

        return BaseFanControlName(name);
    }

    private static bool IsGpuControl(string identifier)
    {
        return !string.IsNullOrWhiteSpace(identifier) &&
            identifier.IndexOf("NVApi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ControlSortKey(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return 100000;
        }

        var marker = "/control/";
        var index = identifier.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            int value;
            if (int.TryParse(identifier.Substring(index + marker.Length), out value))
            {
                return value;
            }
        }

        return IsGpuControl(identifier) ? 90000 : 80000;
    }

    private static string GuessControlIdentifier(string fanIdentifier)
    {
        if (string.IsNullOrWhiteSpace(fanIdentifier))
        {
            return "";
        }

        return fanIdentifier
            .Replace("/fan/", "/control/")
            .Replace("/fan/", "/control/")
            .Replace("-A/fan/", "-A/control/");
    }

    private static string GuessFanIdentifier(string controlIdentifier)
    {
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            return "";
        }

        return controlIdentifier
            .Replace("/control/", "/fan/")
            .Replace("-A/control/", "-A/fan/");
    }

    private string GetFanLabel(string identifier, string fallback)
    {
        var labels = LoadFanLabels();
        return labels.ContainsKey(identifier) ? labels[identifier] : fallback;
    }

    private Dictionary<string, string> LoadFanLabels()
    {
        return new Dictionary<string, string>(settings.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    private void SaveFanLabels(Dictionary<string, string> labels)
    {
        settings.FanLabels = new Dictionary<string, string>(labels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        SaveSettings(settings);
    }

    private void LogFanAction(string message)
    {
        LogMessage("Normal", message);
    }

    private void LogError(string message)
    {
        LogMessage("Error", message);
    }

    private void LogMessage(string level, string message)
    {
        try
        {
            if (!ShouldLog(level))
            {
                return;
            }

            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FanActionLogName);
            RotateLogIfNeeded(path);
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private bool ShouldLog(string level)
    {
        var configured = string.IsNullOrWhiteSpace(settings.LoggingLevel) ? "Off" : settings.LoggingLevel;
        var configuredRank = LoggingRank(configured);
        var levelRank = LoggingRank(level);
        return configuredRank > 0 && levelRank <= configuredRank;
    }

    private static int LoggingRank(string level)
    {
        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(level, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(level, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 0;
    }

    private static void RotateLogIfNeeded(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return;
            }

            var info = new System.IO.FileInfo(path);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            var oldPath = path + ".old";
            if (System.IO.File.Exists(oldPath))
            {
                System.IO.File.Delete(oldPath);
            }

            System.IO.File.Move(path, oldPath);
        }
        catch
        {
        }
    }

    private IEnumerable<SensorRow> GetLibreHardwareMonitorSensors()
    {
        try
        {
            lock (lhmLock)
            {
                EnsureLibreHardwareMonitorComputerOpen();

                foreach (var hardware in lhmComputer.Hardware)
                {
                    UpdateHardware(hardware);
                }

                return lhmComputer.Hardware.SelectMany(ReadLibreHardwareMonitorSensors).ToList();
            }
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private void EnsureLibreHardwareMonitorComputerOpen()
    {
        if (lhmComputer != null)
        {
            return;
        }

        lhmComputer = CreateLibreHardwareMonitorComputer();
        lhmComputer.Open();
    }

    private static void UpdateHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateHardware(subHardware);
        }
    }

    private static IEnumerable<SensorRow> ReadLibreHardwareMonitorSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            var sensorType = sensor.SensorType.ToString();
            var hardwareType = hardware.HardwareType.ToString();
            var isStorage = hardware.HardwareType == HardwareType.Storage;
            var type = sensorType == "Fan" ? "Fan" : sensorType == "Control" && sensor.Control != null ? "Fan Control" : sensorType == "Temperature" ? "Temperature" : isStorage ? "SMART" : "";
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var value = sensor.Value.Value;
            if (type == "Temperature" && value <= 0)
            {
                continue;
            }

            yield return new SensorRow
            {
                Type = type,
                Hardware = GetLibreHardwareMonitorRowHardware(hardware, type, isStorage),
                Name = isStorage ? CleanStorageSensorName(sensor.Name, sensorType) : sensor.Name,
                Identifier = sensor.Identifier == null ? "" : sensor.Identifier.ToString(),
                Value = value,
                DisplayValue = type == "Fan Control" ? FormatLibreHardwareMonitorControlValue(sensor) : isStorage ? FormatLibreHardwareMonitorStorageValue(sensorType, sensor.Name, value) : null,
                Source = "LibreHardwareMonitor"
            };
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var row in ReadLibreHardwareMonitorSensors(subHardware))
            {
                yield return row;
            }
        }
    }

    private static string GetLibreHardwareMonitorRowHardware(IHardware hardware, string type, bool isStorage)
    {
        if (type == "Fan Control")
        {
            return "Fan controls";
        }

        if (isStorage)
        {
            return NormalizeStorageHardwareName(hardware.Name ?? "");
        }

        var hardwareName = hardware.Name ?? "";
        if (hardware.HardwareType.ToString().Equals("SuperIO", StringComparison.OrdinalIgnoreCase) &&
            hardwareName.IndexOf("Nuvoton NCT6795D", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Motherboard";
        }

        return NormalizeHardwareName(hardwareName);
    }

    private static IEnumerable<SensorRow> GetWindowsSmartRows()
    {
        foreach (var row in GetPhysicalDiskRows())
        {
            yield return row;
        }

        foreach (var row in GetStorageReliabilityRows())
        {
            yield return row;
        }
    }

    private static IEnumerable<SensorRow> GetPhysicalDiskRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, HealthStatus, MediaType, OperationalStatus, Size FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    var name = Convert.ToString(disk["FriendlyName"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Physical disk";
                    }

                    name = NormalizeStorageHardwareName(name);
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Health", DisplayValue = DecodeHealthStatus(disk["HealthStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Media type", DisplayValue = DecodeMediaType(disk["MediaType"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Operational status", DisplayValue = DecodeOperationalStatus(disk["OperationalStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Size", DisplayValue = FormatBytes(disk["Size"]), Source = "Windows Storage" });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetStorageReliabilityRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
            {
                foreach (ManagementObject counter in searcher.Get())
                {
                    var name = Convert.ToString(counter["DeviceId"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Storage device";
                    }

                    name = NormalizeStorageHardwareName(name);
                    AddSmartCounter(rows, name, counter, "Temperature", "Temperature", true);
                    AddSmartCounter(rows, name, counter, "TemperatureMax", "Maximum temperature", true);
                    AddSmartCounter(rows, name, counter, "PowerOnHours", "Power on hours", false);
                    AddSmartCounter(rows, name, counter, "ReadErrorsTotal", "Read errors total", false);
                    AddSmartCounter(rows, name, counter, "WriteErrorsTotal", "Write errors total", false);
                    AddSmartCounter(rows, name, counter, "Wear", "Wear", false);
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddSmartCounter(List<SensorRow> rows, string hardware, ManagementBaseObject counter, string property, string name, bool temperature)
    {
        try
        {
            var raw = counter[property];
            if (raw == null)
            {
                return;
            }

            double value;
            if (!double.TryParse(Convert.ToString(raw), out value))
            {
                return;
            }

            if (value <= 0 && temperature)
            {
                return;
            }

            rows.Add(new SensorRow
            {
                Type = temperature ? "Temperature" : "SMART",
                Hardware = hardware,
                Name = name,
                Value = (float)value,
                DisplayValue = temperature ? Math.Round(value, 1).ToString("0.0") + " C" : Math.Round(value, 0).ToString("0"),
                Source = "Windows Storage"
            });
        }
        catch
        {
        }
    }

    private static IEnumerable<SensorRow> GetSystemPerformanceRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
            {
                var values = searcher.Get().Cast<ManagementObject>()
                    .Select(cpu => Convert.ToDouble(cpu["LoadPercentage"] ?? 0))
                    .ToList();
                if (values.Count > 0)
                {
                    rows.Add(new SensorRow
                    {
                        Type = "Performance",
                        Hardware = "CPU",
                        Name = "CPU usage",
                        Value = (float)values.Average(),
                        DisplayValue = Math.Round(values.Average(), 1).ToString("0.0") + "%",
                        Source = "Windows WMI"
                    });
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    var totalKb = Convert.ToDouble(os["TotalVisibleMemorySize"] ?? 0);
                    var freeKb = Convert.ToDouble(os["FreePhysicalMemory"] ?? 0);
                    if (totalKb <= 0)
                    {
                        continue;
                    }

                    var usedKb = Math.Max(0, totalKb - freeKb);
                    var usedPercent = usedKb / totalKb * 100.0;
                    var availablePercent = freeKb / totalKb * 100.0;
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used", Value = (float)usedPercent, DisplayValue = Math.Round(usedPercent, 1).ToString("0.0") + "%", Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used size", DisplayValue = FormatBytes(usedKb * 1024.0), Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory available", DisplayValue = FormatBytes(freeKb * 1024.0) + " (" + Math.Round(availablePercent, 1).ToString("0.0") + "%)", Source = "Windows WMI" });
                    break;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetStoragePerformanceRows(IEnumerable<SensorRow> sourceRows)
    {
        var wantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Data read",
            "Data written",
            "Read rate",
            "Write rate",
            "Read activity",
            "Write activity",
            "Total activity",
            "Used space",
            "Free space"
        };

        return sourceRows
            .Where(r => r.Type == "SMART" && wantedNames.Contains(CleanSensorName(r.Name)))
            .Select(r => new SensorRow
            {
                Type = "Performance",
                Hardware = NormalizeStorageHardwareName(r.Hardware),
                Name = CleanSensorName(r.Name),
                Identifier = r.Identifier,
                Value = r.Value,
                DisplayValue = r.DisplayValue,
                Source = r.Source
            })
            .ToList();
    }

    private static string SensorDeduplicationKey(SensorRow row)
    {
        var type = row.Type ?? "";
        var hardware = NormalizeHardwareName(row.Hardware);
        var name = CleanSensorName(row.Name);

        if (type == "Performance" && IsStoragePerformanceName(name))
        {
            return (type + "|" + hardware + "|" + name).ToLowerInvariant();
        }

        if (type == "SMART" && (name.Equals("Data read", StringComparison.OrdinalIgnoreCase) || name.Equals("Data written", StringComparison.OrdinalIgnoreCase)))
        {
            return (type + "|" + hardware + "|" + name).ToLowerInvariant();
        }

        return (type + "|" + hardware + "|" + name + "|" + (row.Identifier ?? "")).ToLowerInvariant();
    }

    private static List<SensorRow> ConsolidateRelatedRows(List<SensorRow> rows)
    {
        var output = new List<SensorRow>();
        foreach (var group in rows.GroupBy(r => (r.Type ?? "") + "|" + NormalizeHardwareName(r.Hardware ?? "")))
        {
            var groupRows = group.ToList();
            var usedPercent = TakeRow(groupRows, "Memory used");
            var usedSize = TakeRow(groupRows, "Memory used size");
            if (usedPercent != null && usedSize != null)
            {
                output.Add(new SensorRow
                {
                    Type = usedPercent.Type,
                    Hardware = usedPercent.Hardware,
                    Name = "Memory used",
                    Value = usedPercent.Value,
                    DisplayValue = usedSize.DisplayValue + " (" + usedPercent.DisplayValue + ")",
                    Source = MergeSources(usedPercent.Source, usedSize.Source)
                });
            }
            else
            {
                if (usedPercent != null) output.Add(usedPercent);
                if (usedSize != null) output.Add(usedSize);
            }

            var freeSpace = TakeRow(groupRows, "Free space");
            var usedSpace = TakeRow(groupRows, "Used space");
            if (freeSpace != null && usedSpace != null)
            {
                var display = usedSpace.DisplayValue + " used";
                double spaceUsedPercent;
                double freeBytes;
                if (TryParsePercent(usedSpace.DisplayValue, out spaceUsedPercent) && TryParseFormattedBytes(freeSpace.DisplayValue, out freeBytes) && spaceUsedPercent > 0 && spaceUsedPercent < 100)
                {
                    var totalBytes = freeBytes / (1.0 - (spaceUsedPercent / 100.0));
                    var usedBytes = Math.Max(0, totalBytes - freeBytes);
                    display = FormatBytes(usedBytes) + " (" + usedSpace.DisplayValue + ")";
                }

                output.Add(new SensorRow
                {
                    Type = usedSpace.Type,
                    Hardware = usedSpace.Hardware,
                    Name = "Space used",
                    Value = usedSpace.Value,
                    DisplayValue = display,
                    Source = MergeSources(usedSpace.Source, freeSpace.Source)
                });
            }
            else
            {
                if (freeSpace != null) output.Add(freeSpace);
                if (usedSpace != null) output.Add(usedSpace);
            }

            output.AddRange(groupRows);
        }

        return output;
    }

    private static SensorRow TakeRow(List<SensorRow> rows, string name)
    {
        var index = rows.FindIndex(r => CleanSensorName(r.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var row = rows[index];
        rows.RemoveAt(index);
        return row;
    }

    private static string MergeSources(string first, string second)
    {
        return string.Join(", ", new[] { first, second }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToArray());
    }

    private static bool TryParsePercent(string value, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return double.TryParse(value.Replace("%", "").Trim(), out percent);
    }

    private static bool TryParseFormattedBytes(string value, out double bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        double number;
        if (!double.TryParse(parts[0], out number))
        {
            return false;
        }

        var unit = parts[1].ToUpperInvariant();
        var multiplier = 1.0;
        if (unit == "KB") multiplier = 1024.0;
        else if (unit == "MB") multiplier = 1024.0 * 1024.0;
        else if (unit == "GB") multiplier = 1024.0 * 1024.0 * 1024.0;
        else if (unit == "TB") multiplier = 1024.0 * 1024.0 * 1024.0 * 1024.0;
        bytes = number * multiplier;
        return true;
    }

    private static bool IsStoragePerformanceName(string name)
    {
        return name.Equals("Data read", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Data written", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Read activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Write activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Total activity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Free space", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Used space", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatLibreHardwareMonitorStorageValue(string sensorType, string sensorName, float value)
    {
        if (sensorType == "Temperature")
        {
            return Math.Round(value, 1).ToString("0.0") + " C";
        }

        if (sensorType == "Load")
        {
            return Math.Round(value, 1).ToString("0.0") + "%";
        }

        if (sensorType == "Throughput")
        {
            return FormatBytesPerSecond(value);
        }

        if (sensorType == "Data")
        {
            if (IsLibreHardwareMonitorGigabyteCounter(sensorName))
            {
                return FormatStorageDataCounterGigabytes(value);
            }

            return Math.Round(value, 1).ToString("0.0");
        }

        if (sensorType == "Level")
        {
            return Math.Round(value, 1).ToString("0.0") + "%";
        }

        if (sensorType == "Factor")
        {
            return Math.Round(value, 0).ToString("0");
        }

        return Math.Round(value, 1).ToString("0.0");
    }

    private static string FormatLibreHardwareMonitorControlValue(ISensor sensor)
    {
        var value = sensor.Value.HasValue ? Math.Round(sensor.Value.Value, 0).ToString("0") + "%" : "unknown";
        var mode = sensor.Control == null ? "No direct control" : sensor.Control.ControlMode.ToString();
        return mode + " " + value;
    }

    private static string CleanStorageSensorName(string sensorName, string sensorType)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return sensorType == "Temperature" ? "Temperature" : "Storage reading";
        }

        if (sensorType == "Level" && sensorName.Equals("Life", StringComparison.OrdinalIgnoreCase))
        {
            return "Life remaining";
        }

        if (sensorName.Equals("Data Read", StringComparison.OrdinalIgnoreCase))
        {
            return "Data read";
        }

        if (sensorName.Equals("Data Written", StringComparison.OrdinalIgnoreCase))
        {
            return "Data written";
        }

        if (sensorName.Equals("Free Space", StringComparison.OrdinalIgnoreCase))
        {
            return "Free space";
        }

        if (sensorName.Equals("Total Space", StringComparison.OrdinalIgnoreCase))
        {
            return "Total space";
        }

        if (sensorName.Equals("Power On Count", StringComparison.OrdinalIgnoreCase))
        {
            return "Power on count";
        }

        if (sensorName.Equals("Power On Hours", StringComparison.OrdinalIgnoreCase))
        {
            return "Power on hours";
        }

        return sensorName;
    }

    private static bool IsLibreHardwareMonitorGigabyteCounter(string sensorName)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return false;
        }

        return sensorName.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0
            || sensorName.Equals("Data Read", StringComparison.OrdinalIgnoreCase)
            || sensorName.Equals("Data Written", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDeviceList()
    {
        var filters = BuildFilters(latestRows).ToList();
        var currentKeys = deviceList.Items
            .Cast<DeviceFilter>()
            .Select(f => f.Key)
            .ToList();
        var newKeys = filters.Select(f => f.Key).ToList();

        if (currentKeys.SequenceEqual(newKeys))
        {
            return;
        }

        deviceList.BeginUpdate();
        try
        {
            deviceList.Items.Clear();
            foreach (var filter in filters)
            {
                deviceList.Items.Add(filter);
            }

            var selectedIndex = filters.FindIndex(f => f.Key == selectedFilterKey);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                selectedFilterKey = filters.Count > 0 ? filters[0].Key : "all";
            }

            if (filters.Count > 0)
            {
                deviceList.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            deviceList.EndUpdate();
        }
    }

    private static IEnumerable<DeviceFilter> BuildFilters(List<SensorRow> rows)
    {
        yield return new DeviceFilter { Key = "all", DisplayName = "Overview" };
        yield return new DeviceFilter { Key = "type|Temperature", DisplayName = "Temperatures", Type = "Temperature" };
        yield return new DeviceFilter { Key = "type|Fan", DisplayName = "Fans", Type = "Fan" };
        yield return new DeviceFilter { Key = "type|SMART", DisplayName = "SMART", Type = "SMART" };
        yield return new DeviceFilter { Key = "type|Performance", DisplayName = "Performance", Type = "Performance" };
    }

    private void UpdateReadingList()
    {
        UpdateReadingList(null);
    }

    private void UpdateReadingList(string preferredFallbackKey)
    {
        if (readingTree == null)
        {
            return;
        }

        var selectedKey = readingTree.SelectedNode == null ? "" : readingTree.SelectedNode.Name;
        var expandedKeys = GetExpandedNodeKeys(readingTree.Nodes);
        var filter = deviceList.SelectedItem as DeviceFilter;
        var rows = ApplyFilter(latestRows, filter)
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .ToList();

        var items = BuildReadingTree(rows, filter);
        items = FilterHiddenReadingItems(items);
        var signature = TreeSignature(items);
        var shapeSignature = TreeShapeSignature(items);
        if (string.Equals(lastReadingTreeSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(lastReadingTreeShapeSignature, shapeSignature, StringComparison.Ordinal))
        {
            UpdateTreeNodeText(readingTree.Nodes, BuildTreeTextMap(items));
            lastReadingTreeSignature = signature;
            return;
        }

        readingTree.BeginUpdate();
        try
        {
            readingTree.Nodes.Clear();
            foreach (var item in items)
            {
                readingTree.Nodes.Add(CreateTreeNode(item));
            }

            ApplyExpandedNodeKeys(readingTree.Nodes, expandedKeys);
            var selectedNode = FindTreeNode(readingTree.Nodes, selectedKey);
            if (selectedNode == null && !string.IsNullOrWhiteSpace(preferredFallbackKey))
            {
                selectedNode = FindTreeNode(readingTree.Nodes, preferredFallbackKey);
            }

            if (selectedNode == null && readingTree.Nodes.Count > 0)
            {
                selectedNode = readingTree.Nodes[0];
            }

            if (selectedNode != null)
            {
                readingTree.SelectedNode = selectedNode;
            }

            lastReadingTreeSignature = signature;
            lastReadingTreeShapeSignature = shapeSignature;
        }
        finally
        {
            readingTree.EndUpdate();
        }
    }

    private static string FindHideFallbackKey(TreeNode node)
    {
        if (node == null)
        {
            return "";
        }

        var next = NextVisibleTreeNode(node);
        if (next != null && !string.IsNullOrWhiteSpace(next.Name))
        {
            return next.Name;
        }

        var previous = PreviousVisibleTreeNode(node);
        if (previous != null && !string.IsNullOrWhiteSpace(previous.Name))
        {
            return previous.Name;
        }

        return node.Parent == null ? "" : node.Parent.Name;
    }

    private static TreeNode NextVisibleTreeNode(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        if (node.Nodes.Count > 0)
        {
            return node.Nodes[0];
        }

        var current = node;
        while (current != null)
        {
            var sibling = NextSibling(current);
            if (sibling != null)
            {
                return sibling;
            }

            current = current.Parent;
        }

        return null;
    }

    private static TreeNode PreviousVisibleTreeNode(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var sibling = PreviousSibling(node);
        if (sibling != null)
        {
            while (sibling.IsExpanded && sibling.Nodes.Count > 0)
            {
                sibling = sibling.Nodes[sibling.Nodes.Count - 1];
            }

            return sibling;
        }

        return node.Parent;
    }

    private static TreeNode NextSibling(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var siblings = node.Parent == null ? node.TreeView.Nodes : node.Parent.Nodes;
        var index = node.Index + 1;
        return index >= 0 && index < siblings.Count ? siblings[index] : null;
    }

    private static TreeNode PreviousSibling(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var siblings = node.Parent == null ? node.TreeView.Nodes : node.Parent.Nodes;
        var index = node.Index - 1;
        return index >= 0 && index < siblings.Count ? siblings[index] : null;
    }

    private static Dictionary<string, string> BuildTreeTextMap(IEnumerable<ReadingTreeItem> items)
    {
        var map = new Dictionary<string, string>();
        AddTreeTextMap(items, map);
        return map;
    }

    private static void AddTreeTextMap(IEnumerable<ReadingTreeItem> items, Dictionary<string, string> map)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                map[item.Key] = item.Text;
            }

            AddTreeTextMap(item.Children, map);
        }
    }

    private static void UpdateTreeNodeText(TreeNodeCollection nodes, Dictionary<string, string> textByKey)
    {
        foreach (TreeNode node in nodes)
        {
            string text;
            if (!string.IsNullOrWhiteSpace(node.Name) && textByKey.TryGetValue(node.Name, out text) && node.Text != text)
            {
                node.Text = text;
            }

            UpdateTreeNodeText(node.Nodes, textByKey);
        }
    }

    private static HashSet<string> GetExpandedNodeKeys(TreeNodeCollection nodes)
    {
        var keys = new HashSet<string>();
        AddExpandedNodeKeys(nodes, keys);
        return keys;
    }

    private static void AddExpandedNodeKeys(TreeNodeCollection nodes, HashSet<string> keys)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && !string.IsNullOrWhiteSpace(node.Name))
            {
                keys.Add(node.Name);
            }

            AddExpandedNodeKeys(node.Nodes, keys);
        }
    }

    private static void ApplyExpandedNodeKeys(TreeNodeCollection nodes, HashSet<string> expandedKeys)
    {
        foreach (TreeNode node in nodes)
        {
            if (expandedKeys.Count == 0 || expandedKeys.Contains(node.Name))
            {
                node.Expand();
            }
            else
            {
                node.Collapse();
            }

            ApplyExpandedNodeKeys(node.Nodes, expandedKeys);
        }
    }

    private static List<ReadingTreeItem> BuildReadingTree(List<SensorRow> rows, DeviceFilter filter)
    {
        if (rows.Count == 0)
        {
            return new List<ReadingTreeItem> { new ReadingTreeItem { Key = "empty", Text = "No readings available." } };
        }

        if (filter == null || filter.Key == "all")
        {
            return rows
                .GroupBy(r => r.Type)
                .OrderBy(g => TypeSortIndex(g.Key))
                .ThenBy(g => g.Key)
                .Select(typeGroup =>
                {
                    var typeItem = new ReadingTreeItem { Key = "type|" + typeGroup.Key, Text = DisplayTypeName(typeGroup.Key) };
                    AddHardwareGroups(typeItem, typeGroup);
                    return typeItem;
                })
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var typeItem = new ReadingTreeItem { Key = "type|" + filter.Type, Text = DisplayTypeName(filter.Type) };
            AddHardwareGroups(typeItem, rows);
            return typeItem.Children;
        }

        if (!string.IsNullOrWhiteSpace(filter.Hardware))
        {
            var hardwareItem = new ReadingTreeItem { Key = "hardware|" + filter.Hardware, Text = ShortHardwareName(filter.Hardware) };
            AddReadingRows(hardwareItem, rows);
            return new List<ReadingTreeItem> { hardwareItem };
        }

        var root = new ReadingTreeItem { Key = "readings", Text = "Readings" };
        AddReadingRows(root, rows);
        return new List<ReadingTreeItem> { root };
    }

    private List<ReadingTreeItem> FilterHiddenReadingItems(IEnumerable<ReadingTreeItem> items)
    {
        var hidden = new HashSet<string>(settings.HiddenReadingKeys ?? new List<string>());
        return items
            .Select(item => FilterHiddenReadingItem(item, hidden))
            .Where(item => item != null)
            .ToList();
    }

    private static ReadingTreeItem FilterHiddenReadingItem(ReadingTreeItem item, HashSet<string> hidden)
    {
        if (hidden.Contains(item.Key))
        {
            return null;
        }

        var copy = new ReadingTreeItem { Key = item.Key, Text = item.Text, Row = item.Row };
        foreach (var child in item.Children)
        {
            var filtered = FilterHiddenReadingItem(child, hidden);
            if (filtered != null)
            {
                copy.Children.Add(filtered);
            }
        }

        if (copy.Row == null && copy.Children.Count == 0)
        {
            return null;
        }

        return copy;
    }

    private static void AddHardwareGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var hardwareGroup in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => g.Key))
        {
            var hardwareItem = new ReadingTreeItem
            {
                Key = "hardware|" + parent.Key + "|" + hardwareGroup.Key,
                Text = hardwareGroup.Key
            };
            AddReadingRows(hardwareItem, hardwareGroup);
            parent.Children.Add(hardwareItem);
        }
    }

    private static void AddReadingRows(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var row in rows
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ReadingSortIndex(r.Name))
            .ThenBy(r => CleanSensorName(r.Name)))
        {
            parent.Children.Add(new ReadingTreeItem
            {
                Key = "row|" + RowSettingsKey(row),
                Text = CleanSensorName(row.Name) + ": " + FormatValue(row),
                Row = row
            });
        }
    }

    private static TreeNode CreateTreeNode(ReadingTreeItem item)
    {
        var node = new TreeNode(item.Text) { Name = item.Key, Tag = item.Row };
        foreach (var child in item.Children)
        {
            node.Nodes.Add(CreateTreeNode(child));
        }

        return node;
    }

    private static TreeNode FindTreeNode(TreeNodeCollection nodes, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (TreeNode node in nodes)
        {
            if (string.Equals(node.Name, key, StringComparison.Ordinal))
            {
                return node;
            }

            var found = FindTreeNode(node.Nodes, key);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static string TreeSignature(IEnumerable<ReadingTreeItem> items)
    {
        return string.Join("\n", items.Select(TreeSignature).ToArray());
    }

    private static string TreeSignature(ReadingTreeItem item)
    {
        return item.Key + "=" + item.Text + "\n" + string.Join("\n", item.Children.Select(TreeSignature).ToArray());
    }

    private static string TreeShapeSignature(IEnumerable<ReadingTreeItem> items)
    {
        return string.Join("\n", items.Select(TreeShapeSignature).ToArray());
    }

    private static string TreeShapeSignature(ReadingTreeItem item)
    {
        return item.Key + "\n" + string.Join("\n", item.Children.Select(TreeShapeSignature).ToArray());
    }

    private static IEnumerable<SensorRow> ApplyFilter(IEnumerable<SensorRow> rows, DeviceFilter filter)
    {
        if (filter == null || filter.Key == "all")
        {
            return rows.Where(r => r.Type != "Fan Control");
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            return rows.Where(r => r.Type == filter.Type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Hardware))
        {
            return rows.Where(r => r.Type != "Fan Control" && r.Type != "Performance" && r.Hardware == filter.Hardware);
        }

        return rows;
    }

    public static int TypeSortIndex(string type)
    {
        if (type == "Temperature")
        {
            return 0;
        }

        if (type == "Fan")
        {
            return 1;
        }

        if (type == "SMART")
        {
            return 2;
        }

        if (type == "Performance")
        {
            return 3;
        }

        return 4;
    }

    private static string DisplayTypeName(string type)
    {
        if (type == "Temperature")
        {
            return "Temperatures";
        }

        if (type == "Fan")
        {
            return "Fans";
        }

        if (type == "SMART")
        {
            return "SMART";
        }

        if (type == "Performance")
        {
            return "Performance";
        }

        return string.IsNullOrWhiteSpace(type) ? "Overview" : type;
    }

    private static int ReadingSortIndex(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("Health", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU usage", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("Memory used", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("Memory used size", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("Memory available", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Data read", StringComparison.OrdinalIgnoreCase)) return 10;
        if (clean.Equals("Data written", StringComparison.OrdinalIgnoreCase)) return 11;
        if (clean.Equals("Read rate", StringComparison.OrdinalIgnoreCase)) return 12;
        if (clean.Equals("Write rate", StringComparison.OrdinalIgnoreCase)) return 13;
        if (clean.Equals("Read activity", StringComparison.OrdinalIgnoreCase)) return 14;
        if (clean.Equals("Write activity", StringComparison.OrdinalIgnoreCase)) return 15;
        if (clean.Equals("Total activity", StringComparison.OrdinalIgnoreCase)) return 16;
        if (clean.Equals("Space used", StringComparison.OrdinalIgnoreCase)) return 20;
        if (clean.Equals("Free space", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Used space", StringComparison.OrdinalIgnoreCase)) return 22;
        if (clean.Equals("Size", StringComparison.OrdinalIgnoreCase)) return 22;
        return 100;
    }

    private static string ShortHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeHardwareName(hardware);
    }

    private static string NormalizeHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeStorageHardwareName(hardware.Trim());
    }

    private static string NormalizeStorageHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        var value = hardware.Trim();
        if (value.StartsWith("USB ", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4).Trim();
        }

        value = ReplaceIgnoreCase(value, "SCSI Disk Device", "").Trim();
        while (value.IndexOf("  ", StringComparison.Ordinal) >= 0)
        {
            value = value.Replace("  ", " ");
        }

        return value;
    }

    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue))
        {
            return value;
        }

        var index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value.Substring(0, index) + newValue + value.Substring(index + oldValue.Length);
            index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string CleanSensorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed sensor";
        }

        return name
            .Replace("Core (Tctl/Tdie)", "CPU package")
            .Replace("CCD1 (Tdie)", "CCD1 Tdie");
    }

    private static string FormatValue(SensorRow row)
    {
        if (!row.Value.HasValue)
        {
            return row.DisplayValue ?? "";
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            return row.DisplayValue;
        }

        if (row.Type == "Fan")
        {
            return Math.Round(row.Value.Value, 0).ToString("0") + " RPM";
        }

        if (row.Type == "SMART")
        {
            return Math.Round(row.Value.Value, 1).ToString("0.0");
        }

        return Math.Round(row.Value.Value, 1).ToString("0.0") + " C";
    }

    private static string HtmlEncode(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    public static string RowSettingsKey(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        return (row.Type ?? "") + "|" + (row.Hardware ?? "") + "|" + (row.Name ?? "") + "|" + (row.Identifier ?? "");
    }

    public static string TrayChoiceLabel(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        return CleanSensorName(row.Name) + " on " + ShortHardwareName(row.Hardware);
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            var path = GetConfigFilePath();
            if (System.IO.File.Exists(path))
            {
                var loaded = JsonConvert.DeserializeObject<AppSettings>(System.IO.File.ReadAllText(path));
                if (loaded != null)
                {
                    NormalizeSettings(loaded);
                    return loaded;
                }
            }

            var created = new AppSettings();
            NormalizeSettings(created);
            SaveSettings(created);
            return created;
        }
        catch
        {
        }

        var defaults = new AppSettings();
        NormalizeSettings(defaults);
        return defaults;
    }

    private static void SaveSettings(AppSettings value)
    {
        try
        {
            NormalizeSettings(value);
            var path = GetConfigFilePath();
            System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        catch
        {
        }
    }

    private static void NormalizeSettings(AppSettings value)
    {
        if (value == null)
        {
            return;
        }

        value.TrayItemKeys = value.TrayItemKeys ?? new List<string>();
        value.HiddenReadingKeys = value.HiddenReadingKeys ?? new List<string>();
        value.FanLabels = new Dictionary<string, string>(value.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        value.RefreshIntervalSeconds = Math.Max(2, Math.Min(300, value.RefreshIntervalSeconds));
        value.LoggingLevel = NormalizeLoggingLevel(value.LoggingLevel);
    }

    private static string NormalizeLoggingLevel(string level)
    {
        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        if (string.Equals(level, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            return "Normal";
        }

        if (string.Equals(level, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Off";
    }

    private static bool IsPawnIoInstalled()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
            {
                return key != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string GetConfigFilePath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetConfigFileName());
    }

    private static string GetConfigFileName()
    {
        var computerName = string.IsNullOrWhiteSpace(Environment.MachineName) ? "Computer" : Environment.MachineName;
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            computerName = computerName.Replace(invalid, '_');
        }

        return computerName + ".json";
    }

    private void UpdateTrayStatus()
    {
        if (trayIcon == null)
        {
            return;
        }

        trayIcon.Visible = settings.TrayStatusEnabled;
        if (!settings.TrayStatusEnabled)
        {
            return;
        }

        var selectedKeys = settings.TrayItemKeys ?? new List<string>();
        var selectedRows = latestRows
            .Where(r => selectedKeys.Contains(RowSettingsKey(r)))
            .Take(4)
            .ToList();

        if (selectedRows.Count == 0)
        {
            selectedRows = latestRows
                .Where(r => r.Type == "Temperature")
                .OrderBy(r => ShortHardwareName(r.Hardware))
                .ThenBy(r => CleanSensorName(r.Name))
                .Take(2)
                .ToList();
        }

        var text = selectedRows.Count == 0
            ? "Sensor Readout"
            : string.Join("; ", selectedRows.Select(r => CleanSensorName(r.Name) + " " + FormatValue(r)).ToArray());

        trayIcon.Text = ShortenTrayText(text, selectedRows.Count > 1);
        SetTrayIcon(selectedRows.FirstOrDefault());
    }

    private static string ShortenTrayText(string text, bool showEllipsis)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Sensor Readout";
        }

        if (text.Length <= 63)
        {
            return text;
        }

        return showEllipsis ? text.Substring(0, 60) + "..." : text.Substring(0, 63);
    }

    private void SetTrayIcon(SensorRow row)
    {
        var oldIcon = trayStatusIcon;
        trayStatusIcon = CreateTrayIcon(row);
        trayIcon.Icon = trayStatusIcon;

        if (oldIcon != null)
        {
            oldIcon.Dispose();
        }
    }

    private static Icon CreateTrayIcon(SensorRow row)
    {
        using (var bitmap = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            var color = TrayColor(row);
            using (var background = new SolidBrush(color))
            using (var border = new Pen(Color.White))
            {
                graphics.FillEllipse(background, 0, 0, 15, 15);
                graphics.DrawEllipse(border, 0, 0, 15, 15);
            }

            var text = TrayIconText(row);
            using (var font = new Font("Segoe UI", text.Length > 2 ? 5.5f : 6.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 1, 16, 14), format);
            }

            return Icon.FromHandle(bitmap.GetHicon());
        }
    }

    private static Color TrayColor(SensorRow row)
    {
        if (row == null)
        {
            return Color.DimGray;
        }

        if (row.Type == "Temperature")
        {
            var value = row.Value.GetValueOrDefault();
            if (value >= 80) return Color.FromArgb(180, 40, 35);
            if (value >= 65) return Color.FromArgb(210, 116, 0);
            return Color.FromArgb(25, 120, 75);
        }

        if (row.Type == "Fan")
        {
            return Color.FromArgb(40, 95, 170);
        }

        if (row.Type == "SMART")
        {
            return Color.FromArgb(105, 80, 155);
        }

        return Color.DimGray;
    }

    private static string TrayIconText(SensorRow row)
    {
        if (row == null || !row.Value.HasValue)
        {
            return "SR";
        }

        if (row.Type == "Temperature")
        {
            return Math.Round(row.Value.Value, 0).ToString("0");
        }

        if (row.Type == "Fan")
        {
            var rpm = row.Value.Value;
            return rpm >= 1000 ? Math.Round(rpm / 1000, 1).ToString("0.#") + "k" : Math.Round(rpm, 0).ToString("0");
        }

        var value = Math.Round(row.Value.Value, 0).ToString("0");
        return value.Length <= 3 ? value : value.Substring(0, 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && lhmComputer != null)
        {
            lhmComputer.Close();
        }

        if (disposing && trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        if (disposing && trayStatusIcon != null)
        {
            trayStatusIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string DecodeHealthStatus(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 0) return "Healthy";
        if (code == 1) return "Warning";
        if (code == 2) return "Unhealthy";
        return "Unknown (" + code + ")";
    }

    private static string DecodeMediaType(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 3) return "HDD";
        if (code == 4) return "SSD";
        if (code == 5) return "SCM";
        return "Unspecified";
    }

    private static string DecodeOperationalStatus(object value)
    {
        var values = value as ushort[];
        if (values == null)
        {
            var single = value as ushort?;
            values = single.HasValue ? new[] { single.Value } : new ushort[0];
        }

        if (values.Length == 0)
        {
            return "Unknown";
        }

        return string.Join(", ", values.Select(v => v == 2 ? "OK" : "Status " + v).ToArray());
    }

    private static string FormatBytes(object value)
    {
        double bytes;
        if (value == null || !double.TryParse(Convert.ToString(value), out bytes) || bytes <= 0)
        {
            return "";
        }

        var units = new[] { "bytes", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return Math.Round(bytes, unit == 0 ? 0 : 1).ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            return "";
        }

        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var unit = 0;
        while (bytesPerSecond >= 1024 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }

        return Math.Round(bytesPerSecond, unit == 0 ? 0 : 1).ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatGigabytes(double gigabytes)
    {
        if (gigabytes <= 0)
        {
            return "";
        }

        if (gigabytes >= 1024)
        {
            var terabytes = gigabytes / 1024;
            return Math.Round(terabytes, 2).ToString("0.##") + " TB";
        }

        var rounded = Math.Round(gigabytes, 1);
        return rounded.ToString(Math.Abs(rounded % 1) < 0.05 ? "0" : "0.0") + " GB";
    }

    private static string FormatStorageDataCounterGigabytes(double gigabytes)
    {
        if (gigabytes <= 0)
        {
            return "";
        }

        if (gigabytes > 1024 * 100)
        {
            return Math.Round(gigabytes, 0).ToString("0") + " raw";
        }

        return FormatGigabytes(gigabytes);
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, @"Local\OnjSensorReadoutApp", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show(
                    "Sensor Readout is already running.",
                    "Sensor Readout",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SensorReadoutForm());
        }
    }
}
