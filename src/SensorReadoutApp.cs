using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;

[assembly: System.Reflection.AssemblyTitle("Sensor Readout")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

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
    public bool RunAtStartup = false;
    public bool StartMinimizedToTray = false;
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

public sealed class NetworkSnapshot
{
    public long BytesReceived;
    public long BytesSent;
    public DateTime TimestampUtc;
}

public sealed class GitHubReleaseInfo
{
    [JsonProperty("tag_name")]
    public string TagName;

    [JsonProperty("html_url")]
    public string HtmlUrl;
}

public sealed class PreferencesForm : Form
{
    private readonly CheckBox autoRefreshCheckBox;
    private readonly CheckBox refreshWhileFocusedCheckBox;
    private readonly CheckBox trayStatusCheckBox;
    private readonly CheckBox runAtStartupCheckBox;
    private readonly CheckBox startMinimizedCheckBox;
    private readonly NumericUpDown refreshSecondsBox;
    private readonly ComboBox loggingLevelBox;
    private readonly ListBox trayAvailableList;
    private readonly ListBox traySelectedList;
    private readonly Label traySelectionStatusLabel;
    private readonly CheckedListBox hiddenItemsList;
    private readonly List<SensorRow> rows;
    private readonly List<string> hiddenReadingKeys;

    public bool AutoRefreshEnabled { get { return autoRefreshCheckBox.Checked; } }
    public bool RefreshWhileFocused { get { return refreshWhileFocusedCheckBox.Checked; } }
    public bool TrayStatusEnabled { get { return trayStatusCheckBox.Checked; } }
    public bool RunAtStartup { get { return runAtStartupCheckBox.Checked; } }
    public bool StartMinimizedToTray { get { return startMinimizedCheckBox.Checked; } }
    public int RefreshIntervalSeconds { get { return Convert.ToInt32(refreshSecondsBox.Value); } }
    public string LoggingLevel { get { return Convert.ToString(loggingLevelBox.SelectedItem) ?? "Off"; } }
    public List<string> TrayItemKeys { get; private set; }
    public List<string> HiddenReadingKeys { get; private set; }

    public PreferencesForm(AppSettings settings, List<SensorRow> latestRows)
    {
        Text = "Sensor Readout Preferences";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 620);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        hiddenReadingKeys = new List<string>(settings.HiddenReadingKeys ?? new List<string>());

        rows = latestRows
            .Where(r => r.Type == "Temperature" || r.Type == "Fan" || r.Type == "SMART" || r.Type == "Performance" || r.Type == "Network")
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
            RowCount = 11,
            Padding = new Padding(10)
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

        runAtStartupCheckBox = new CheckBox
        {
            Text = "Run at Windows startup",
            Checked = settings.RunAtStartup,
            AutoSize = true,
            AccessibleName = "Run at Windows startup"
        };

        startMinimizedCheckBox = new CheckBox
        {
            Text = "Start minimized to notification area",
            Checked = settings.StartMinimizedToTray,
            AutoSize = true,
            AccessibleName = "Start minimized to notification area"
        };

        runAtStartupCheckBox.CheckedChanged += delegate
        {
            if (runAtStartupCheckBox.Checked)
            {
                startMinimizedCheckBox.Checked = true;
                trayStatusCheckBox.Checked = true;
            }
        };

        startMinimizedCheckBox.CheckedChanged += delegate
        {
            if (startMinimizedCheckBox.Checked)
            {
                trayStatusCheckBox.Checked = true;
            }
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
            Text = "Notification area items. Maximum four readings. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.",
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        var selectedKeys = settings.TrayItemKeys ?? new List<string>();
        trayAvailableList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Available notification area readings",
            AccessibleDescription = "Press Control Right Arrow to add the selected reading to the notification area."
        };
        traySelectedList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Selected notification area readings in display order",
            AccessibleDescription = "Press Control Left Arrow to remove the selected reading. Press Control Up or Control Down to change the order."
        };
        trayAvailableList.KeyDown += TrayAvailableListKeyDown;
        traySelectedList.KeyDown += TraySelectedListKeyDown;
        var trayChoices = rows
            .Select(r => new TrayItemChoice(r))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => SensorReadoutForm.ReadingSortIndex(i.Name))
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Type)
            .ToList();
        foreach (var key in selectedKeys)
        {
            var selectedChoice = trayChoices.FirstOrDefault(i => i.Key == key);
            if (selectedChoice != null && !ContainsTrayChoice(traySelectedList, selectedChoice.Key))
            {
                traySelectedList.Items.Add(selectedChoice);
            }
        }
        traySelectionStatusLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            AccessibleName = "Notification area selection status"
        };

        foreach (var item in trayChoices)
        {
            if (!ContainsTrayChoice(traySelectedList, item.Key))
            {
                trayAvailableList.Items.Add(item);
            }
        }
        if (trayAvailableList.Items.Count > 0)
        {
            trayAvailableList.SelectedIndex = 0;
        }
        if (traySelectedList.Items.Count > 0)
        {
            traySelectedList.SelectedIndex = 0;
        }
        SetTraySelectionStatus("Tray order has " + traySelectedList.Items.Count + " of 4 readings.");

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
            TrayItemKeys = traySelectedList.Items
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
        main.Controls.Add(runAtStartupCheckBox, 0, 3);
        main.Controls.Add(startMinimizedCheckBox, 0, 4);
        main.Controls.Add(intervalPanel, 0, 5);
        main.Controls.Add(loggingPanel, 0, 6);
        main.Controls.Add(trayLabel, 0, 7);
        main.Controls.Add(BuildTraySelectionPanel(), 0, 8);
        main.Controls.Add(traySelectionStatusLabel, 0, 9);
        main.Controls.Add(buttons, 0, 10);
        generalTab.Controls.Add(main);
        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(hiddenTab);

        Controls.Add(tabs);
    }

    private Control BuildTraySelectionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var availablePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        availablePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        availablePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        availablePanel.Controls.Add(new Label { Text = "Available readings", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        availablePanel.Controls.Add(trayAvailableList, 0, 1);

        var selectedPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedPanel.Controls.Add(new Label { Text = "Tray order", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        selectedPanel.Controls.Add(traySelectedList, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 24, 8, 0)
        };
        var addButton = new Button { Text = "&Add", AutoSize = true, AccessibleDescription = "Add selected reading to the tray order. Shortcut Control Right Arrow." };
        addButton.Click += delegate { AddSelectedTrayChoice(); };
        var removeButton = new Button { Text = "&Remove", AutoSize = true, AccessibleDescription = "Remove selected reading from the tray order. Shortcut Control Left Arrow." };
        removeButton.Click += delegate { RemoveSelectedTrayChoice(); };
        var upButton = new Button { Text = "&Up", AutoSize = true, AccessibleDescription = "Move selected tray reading up. Shortcut Control Up Arrow." };
        upButton.Click += delegate { MoveSelectedTrayChoice(-1); };
        var downButton = new Button { Text = "&Down", AutoSize = true, AccessibleDescription = "Move selected tray reading down. Shortcut Control Down Arrow." };
        downButton.Click += delegate { MoveSelectedTrayChoice(1); };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(upButton);
        buttons.Controls.Add(downButton);

        panel.Controls.Add(availablePanel, 0, 0);
        panel.Controls.Add(buttons, 1, 0);
        panel.Controls.Add(selectedPanel, 2, 0);
        return panel;
    }

    private void AddSelectedTrayChoice()
    {
        var item = trayAvailableList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select an available reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (traySelectedList.Items.Count >= 4)
        {
            SetTraySelectionStatus("The notification area can show up to four readings.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = trayAvailableList.SelectedIndex;
        trayAvailableList.Items.Remove(item);
        traySelectedList.Items.Add(item);
        traySelectedList.SelectedItem = item;
        if (trayAvailableList.Items.Count > 0)
        {
            trayAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, trayAvailableList.Items.Count - 1));
        }
        SetTraySelectionStatus("Added " + item + " to tray order.");
    }

    private void RemoveSelectedTrayChoice()
    {
        var item = traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select a tray reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = traySelectedList.SelectedIndex;
        traySelectedList.Items.Remove(item);
        AddAvailableTrayChoiceSorted(item);
        if (traySelectedList.Items.Count > 0)
        {
            traySelectedList.SelectedIndex = Math.Max(0, Math.Min(index, traySelectedList.Items.Count - 1));
        }
        trayAvailableList.SelectedItem = item;
        SetTraySelectionStatus("Removed " + item + " from tray order.");
    }

    private void MoveSelectedTrayChoice(int direction)
    {
        var index = traySelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= traySelectedList.Items.Count)
        {
            SetTraySelectionStatus("Cannot move the selected tray reading further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = traySelectedList.Items[index];
        traySelectedList.Items.RemoveAt(index);
        traySelectedList.Items.Insert(target, item);
        traySelectedList.SelectedIndex = target;
        SetTraySelectionStatus("Moved " + item + (direction < 0 ? " up." : " down."));
    }

    private void TrayAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void TraySelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedTrayChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedTrayChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SetTraySelectionStatus(string message)
    {
        if (traySelectionStatusLabel != null)
        {
            traySelectionStatusLabel.Text = message;
        }
    }

    private void AddAvailableTrayChoiceSorted(TrayItemChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < trayAvailableList.Items.Count &&
            TrayItemChoice.Compare((TrayItemChoice)trayAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        trayAvailableList.Items.Insert(insertIndex, choice);
    }

    private static bool ContainsTrayChoice(ListBox list, string key)
    {
        return list.Items.Cast<TrayItemChoice>().Any(i => i.Key == key);
    }

    private sealed class TrayItemChoice
    {
        public readonly string Key;
        public readonly string Type;
        public readonly string Hardware;
        public readonly string Name;
        private readonly string label;

        public TrayItemChoice(SensorRow row)
        {
            Key = SensorReadoutForm.RowSettingsKey(row);
            Type = SensorReadoutForm.DisplayTypeName(row.Type);
            Hardware = SensorReadoutForm.ShortHardwareName(row.Hardware);
            Name = SensorReadoutForm.CleanSensorName(row.Name);
            label = SensorReadoutForm.TrayChoiceLabel(row);
        }

        public override string ToString()
        {
            return label;
        }

        public static int Compare(TrayItemChoice left, TrayItemChoice right)
        {
            var result = string.Compare(left.Hardware, right.Hardware, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            result = SensorReadoutForm.ReadingSortIndex(left.Name).CompareTo(SensorReadoutForm.ReadingSortIndex(right.Name));
            if (result != 0) return result;
            result = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            return string.Compare(left.Type, right.Type, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class SensorReadoutForm : Form
{
    private const string AppVersion = "1.1.0";
    private const string ProjectUrl = "https://github.com/OnjLouis/accessible-sensor-readout";
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
    private readonly ProgressBar selectedMeterProgressBar;
    private readonly Label selectedMeterValueLabel;
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
    private string selectedFilterKey = "type|Temperature";
    private bool updatingFanControlBox;
    private bool refreshInProgress;
    private bool minimizingToTray;
    private readonly bool startMinimizedRequested;
    private string lastReadingTreeSignature = "";
    private string lastReadingTreeShapeSignature = "";
    private readonly Dictionary<string, NetworkSnapshot> networkSnapshots = new Dictionary<string, NetworkSnapshot>(StringComparer.OrdinalIgnoreCase);

    public SensorReadoutForm()
        : this(false)
    {
    }

    public SensorReadoutForm(bool startMinimized)
    {
        settings = LoadSettings();
        startMinimizedRequested = startMinimized;
        if ((startMinimizedRequested || settings.StartMinimizedToTray) && !settings.TrayStatusEnabled)
        {
            settings.TrayStatusEnabled = true;
        }
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
        viewMenu.DropDownItems.Add("Performance\tCtrl+0", null, delegate { SelectCategoryByKey("type|Performance"); });
        viewMenu.DropDownItems.Add("Temperatures\tCtrl+1", null, delegate { SelectCategoryByKey("type|Temperature"); });
        viewMenu.DropDownItems.Add("Fans\tCtrl+2", null, delegate { SelectCategoryByKey("type|Fan"); });
        viewMenu.DropDownItems.Add("SMART\tCtrl+3", null, delegate { SelectCategoryByKey("type|SMART"); });
        viewMenu.DropDownItems.Add("Network\tCtrl+4", null, delegate { SelectCategoryByKey("type|Network"); });

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
        helpMenu.DropDownItems.Add("Check for updates...", null, delegate { CheckForUpdates(); });
        helpMenu.DropDownItems.Add("Project on GitHub", null, delegate { OpenProjectPage(); });
        helpMenu.DropDownItems.Add("Install prerequisites...", null, delegate { RunPrerequisiteInstaller(); });
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
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
            AccessibleName = "Reading section",
            AccessibleDescription = "Choose a section such as Temperatures, Fans, SMART, Performance, or Network"
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
        readingTree.AfterSelect += delegate { UpdateSelectedMeterProgress(); };
        readingTree.Nodes.Add(new TreeNode("Please wait, collecting sensor data") { Name = "startup" });

        selectedMeterValueLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "Selected meter value",
            Padding = new Padding(0, 4, 0, 0)
        };
        selectedMeterProgressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Minimum = 0,
            Maximum = 100,
            TabStop = true,
            AccessibleRole = AccessibleRole.ProgressBar,
            AccessibleName = "Selected meter",
            AccessibleDescription = "Selected meter value"
        };

        splitContainer.Panel1.Controls.Add(deviceList);
        splitContainer.Panel2.Controls.Add(readingTree);
        splitContainer.Panel2.Controls.Add(selectedMeterProgressBar);
        splitContainer.Panel2.Controls.Add(selectedMeterValueLabel);
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
            if (startMinimizedRequested || settings.StartMinimizedToTray)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    WindowState = FormWindowState.Minimized;
                    MinimizeToTray();
                });
            }
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
        using (var dialog = new Form())
        {
            dialog.Text = "About Sensor Readout";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.Size = new Size(560, 330);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                AccessibleName = "About Sensor Readout text",
                Text =
                    "Sensor Readout " + AppVersion + Environment.NewLine + Environment.NewLine +
                    "Project page:" + Environment.NewLine +
                    ProjectUrl + Environment.NewLine + Environment.NewLine +
                    "Created by Codex." + Environment.NewLine +
                    "Ideas by Andre Louis." + Environment.NewLine + Environment.NewLine +
                    "Bundled and referenced components:" + Environment.NewLine +
                    "LibreHardwareMonitorLib, Newtonsoft.Json, PawnIO, HidSharp, DiskInfoToolkit, RAMSPDToolkit, BlackSharp.Core, and Microsoft .NET Framework support libraries."
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var projectButton = new Button { Text = "Project page", AutoSize = true, AccessibleName = "Open project page" };
            projectButton.Click += delegate { OpenProjectPage(); };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(projectButton);

            dialog.AcceptButton = okButton;
            dialog.CancelButton = okButton;
            layout.Controls.Add(text, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
    }

    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ProjectUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open project page", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CheckForUpdates()
    {
        try
        {
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "Sensor Readout " + AppVersion);
                var json = client.DownloadString(ProjectUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
                var release = JsonConvert.DeserializeObject<GitHubReleaseInfo>(json);
                var latest = (release == null ? "" : release.TagName) ?? "";
                var latestVersion = latest.Trim().TrimStart('v', 'V');
                if (string.IsNullOrWhiteSpace(latestVersion))
                {
                    MessageBox.Show(this, "Could not read the latest release version.", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Version current;
                Version remote;
                if (Version.TryParse(AppVersion, out current) && Version.TryParse(latestVersion, out remote) && remote > current)
                {
                    var result = MessageBox.Show(
                        this,
                        "Sensor Readout " + latest + " is available." + Environment.NewLine + Environment.NewLine + "Open the release page?",
                        "Update available",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);
                    if (result == DialogResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo { FileName = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl, UseShellExecute = true });
                    }
                    return;
                }

                MessageBox.Show(this, "Sensor Readout is up to date. Current version: " + AppVersion + ".", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (WebException ex)
        {
            MessageBox.Show(this, "Could not check for updates. GitHub releases may not exist yet, or the network request failed." + Environment.NewLine + Environment.NewLine + ex.Message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    private static void SetRunAtStartup(bool enabled, bool startMinimized)
    {
        var shortcutPath = GetStartupShortcutPath();
        if (!enabled)
        {
            if (System.IO.File.Exists(shortcutPath))
            {
                System.IO.File.Delete(shortcutPath);
            }

            return;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("Windows Script Host is not available.");
        }

        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember(
            "CreateShortcut",
            System.Reflection.BindingFlags.InvokeMethod,
            null,
            shell,
            new object[] { shortcutPath });
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
        shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { startMinimized ? "--minimized" : "" });
        shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
        shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Sensor Readout" });
        shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static string GetStartupShortcutPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Sensor Readout.lnk");
    }

    private bool SelectCategoryByShortcut(Keys keyCode)
    {
        if (keyCode == Keys.D0 || keyCode == Keys.NumPad0)
        {
            return SelectCategoryByKey("type|Performance");
        }

        if (keyCode == Keys.D1 || keyCode == Keys.NumPad1)
        {
            return SelectCategoryByKey("type|Temperature");
        }

        if (keyCode == Keys.D2 || keyCode == Keys.NumPad2)
        {
            return SelectCategoryByKey("type|Fan");
        }

        if (keyCode == Keys.D3 || keyCode == Keys.NumPad3)
        {
            return SelectCategoryByKey("type|SMART");
        }

        if (keyCode == Keys.D4 || keyCode == Keys.NumPad4)
        {
            return SelectCategoryByKey("type|Network");
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
            settings.RunAtStartup = dialog.RunAtStartup;
            settings.StartMinimizedToTray = dialog.StartMinimizedToTray;
            if (settings.RunAtStartup || settings.StartMinimizedToTray)
            {
                settings.TrayStatusEnabled = true;
            }
            settings.LoggingLevel = dialog.LoggingLevel;
            settings.TrayItemKeys = dialog.TrayItemKeys;
            settings.HiddenReadingKeys = dialog.HiddenReadingKeys;
            SaveSettings(settings);
            try
            {
                SetRunAtStartup(settings.RunAtStartup, settings.StartMinimizedToTray);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not update Windows startup shortcut: " + ex.Message, "Sensor Readout startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

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
        rows.AddRange(GetNetworkRows());
        rows = ApplyFanLabelsToReadings(rows);

        return ConsolidateRelatedRows(rows
            .Where(s => s.Type == "Temperature" || s.Type == "Fan" || s.Type == "SMART" || s.Type == "Performance" || s.Type == "Network" || s.Type == "Fan Control")
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

    private IEnumerable<SensorRow> GetNetworkRows()
    {
        var rows = new List<SensorRow>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "Network adapter";
                }

                var stats = adapter.GetIPv4Statistics();
                var now = DateTime.UtcNow;
                var id = string.IsNullOrWhiteSpace(adapter.Id) ? name : adapter.Id;
                NetworkSnapshot previous;
                var receiveRate = 0.0;
                var sendRate = 0.0;
                if (networkSnapshots.TryGetValue(id, out previous))
                {
                    var seconds = Math.Max(0.001, (now - previous.TimestampUtc).TotalSeconds);
                    receiveRate = Math.Max(0, stats.BytesReceived - previous.BytesReceived) / seconds;
                    sendRate = Math.Max(0, stats.BytesSent - previous.BytesSent) / seconds;
                }

                networkSnapshots[id] = new NetworkSnapshot
                {
                    BytesReceived = stats.BytesReceived,
                    BytesSent = stats.BytesSent,
                    TimestampUtc = now
                };

                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Status", DisplayValue = adapter.OperationalStatus.ToString(), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Link speed", DisplayValue = FormatBitsPerSecond(adapter.Speed), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Receive rate", Value = (float)receiveRate, DisplayValue = FormatBytesPerSecond(receiveRate), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Send rate", Value = (float)sendRate, DisplayValue = FormatBytesPerSecond(sendRate), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data received", DisplayValue = FormatBytes(stats.BytesReceived), Source = "Windows Network" });
                rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "Data sent", DisplayValue = FormatBytes(stats.BytesSent), Source = "Windows Network" });

                var addresses = adapter.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address != null && a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct()
                    .ToList();
                if (addresses.Count > 0)
                {
                    rows.Add(new SensorRow { Type = "Network", Hardware = name, Name = "IP address", DisplayValue = string.Join(", ", addresses.ToArray()), Source = "Windows Network" });
                }
            }
            catch
            {
            }
        }

        return rows;
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
                selectedFilterKey = filters.Count > 0 ? filters[0].Key : "type|Temperature";
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
        yield return new DeviceFilter { Key = "type|Performance", DisplayName = "Performance", Type = "Performance" };
        yield return new DeviceFilter { Key = "type|Temperature", DisplayName = "Temperatures", Type = "Temperature" };
        yield return new DeviceFilter { Key = "type|Fan", DisplayName = "Fans", Type = "Fan" };
        yield return new DeviceFilter { Key = "type|SMART", DisplayName = "SMART", Type = "SMART" };
        yield return new DeviceFilter { Key = "type|Network", DisplayName = "Network", Type = "Network" };
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
            UpdateSelectedMeterProgress();
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

    private void UpdateSelectedMeterProgress()
    {
        if (selectedMeterProgressBar == null || selectedMeterValueLabel == null)
        {
            return;
        }

        var row = GetSelectedReadingRow();
        if (row == null || !IsMeterRow(row))
        {
            selectedMeterProgressBar.Value = 0;
            selectedMeterProgressBar.AccessibleName = "Selected meter";
            selectedMeterProgressBar.AccessibleDescription = "Selected reading is not a percentage meter";
            selectedMeterValueLabel.Text = "No meter for selected reading.";
            return;
        }

        var percent = ClampPercent(ExtractPercent(row));
        var value = (int)Math.Round(percent);
        var label = MeterLabel(row);
        selectedMeterProgressBar.Value = value;
        selectedMeterProgressBar.AccessibleName = label;
        selectedMeterProgressBar.AccessibleDescription = label + ", " + value + " percent";
        selectedMeterValueLabel.Text = label + ": " + value + "%";
    }

    private static string MeterLabel(SensorRow row)
    {
        var name = CleanSensorName(row.Name);
        var hardware = ShortHardwareName(row.Hardware);
        if (string.IsNullOrWhiteSpace(hardware) || hardware.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return hardware + ", " + name;
    }

    private static bool IsMeterRow(SensorRow row)
    {
        if (row == null || row.Type == "Fan Control")
        {
            return false;
        }

        var percent = ExtractPercent(row);
        if (!percent.HasValue || percent.Value < 0 || percent.Value > 100)
        {
            return false;
        }

        var name = CleanSensorName(row.Name);
        return name.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("activity", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("space used", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("used space", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("life remaining", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("load", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float ClampPercent(float? value)
    {
        if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100, value.Value));
    }

    private static float? ExtractPercent(SensorRow row)
    {
        if (row == null)
        {
            return null;
        }

        var text = row.DisplayValue ?? "";
        var parsed = ExtractPercent(text);
        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        if (row.Value.HasValue)
        {
            return row.Value.Value;
        }

        return null;
    }

    private static float? ExtractPercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var percentIndex = text.IndexOf('%');
        if (percentIndex < 0)
        {
            return null;
        }

        var start = percentIndex - 1;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.' || text[start] == '-'))
        {
            start--;
        }

        var number = text.Substring(start + 1, percentIndex - start - 1);
        float value;
        return float.TryParse(number, out value) ? value : (float?)null;
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

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var typeItem = new ReadingTreeItem { Key = "type|" + filter.Type, Text = DisplayTypeName(filter.Type) };
            if (filter.Type == "Performance")
            {
                AddPerformanceGroups(typeItem, rows);
                return typeItem.Children;
            }

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

    private static void AddPerformanceGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var systemRows = rows
            .Where(r => IsSystemPerformanceHardware(r.Hardware))
            .ToList();
        if (systemRows.Count > 0)
        {
            var systemItem = new ReadingTreeItem { Key = "performance|system", Text = "System" };
            foreach (var hardwareGroup in systemRows
                .GroupBy(r => ShortHardwareName(r.Hardware))
                .OrderBy(g => PerformanceHardwareSortIndex(g.Key))
                .ThenBy(g => g.Key))
            {
                var hardwareItem = new ReadingTreeItem
                {
                    Key = "hardware|performance|system|" + hardwareGroup.Key,
                    Text = hardwareGroup.Key
                };
                AddReadingRows(hardwareItem, hardwareGroup);
                systemItem.Children.Add(hardwareItem);
            }

            parent.Children.Add(systemItem);
        }

        var storageRows = rows
            .Where(r => !IsSystemPerformanceHardware(r.Hardware))
            .ToList();
        if (storageRows.Count > 0)
        {
            var storageItem = new ReadingTreeItem { Key = "performance|storage", Text = "Storage" };
            AddHardwareGroups(storageItem, storageRows);
            parent.Children.Add(storageItem);
        }
    }

    private static bool IsSystemPerformanceHardware(string hardware)
    {
        return string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase);
    }

    private static int PerformanceHardwareSortIndex(string hardware)
    {
        if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
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
        if (filter == null)
        {
            return rows.Where(r => r.Type == "Temperature");
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
        if (type == "Performance")
        {
            return 0;
        }

        if (type == "Temperature")
        {
            return 1;
        }

        if (type == "Fan")
        {
            return 2;
        }

        if (type == "SMART")
        {
            return 3;
        }

        if (type == "Network")
        {
            return 4;
        }

        return 5;
    }

    public static string DisplayTypeName(string type)
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

        if (type == "Network")
        {
            return "Network";
        }

        return string.IsNullOrWhiteSpace(type) ? "Readings" : type;
    }

    public static int ReadingSortIndex(string name)
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
        if (clean.Equals("Status", StringComparison.OrdinalIgnoreCase)) return 30;
        if (clean.Equals("IP address", StringComparison.OrdinalIgnoreCase)) return 31;
        if (clean.Equals("Link speed", StringComparison.OrdinalIgnoreCase)) return 32;
        if (clean.Equals("Receive rate", StringComparison.OrdinalIgnoreCase)) return 33;
        if (clean.Equals("Send rate", StringComparison.OrdinalIgnoreCase)) return 34;
        if (clean.Equals("Data received", StringComparison.OrdinalIgnoreCase)) return 35;
        if (clean.Equals("Data sent", StringComparison.OrdinalIgnoreCase)) return 36;
        return 100;
    }

    public static string ShortHardwareName(string hardware)
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

    public static string CleanSensorName(string name)
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

        return ShortHardwareName(row.Hardware) + " - " + ShortTrayName(row.Name) + ": " + DisplayTypeName(row.Type);
    }

    private static string ShortTrayReadingText(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        var hardware = ShortTrayHardware(row.Hardware);
        var name = ShortTrayName(row.Name);
        if (row.Type == "Temperature")
        {
            return name + " " + FormatValue(row);
        }

        if (row.Type == "Network")
        {
            return hardware + " " + name + " " + FormatValue(row);
        }

        if (row.Type == "Performance" || row.Type == "SMART")
        {
            if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase))
            {
                return name + " " + FormatValue(row);
            }

            return hardware + " " + name + " " + FormatValue(row);
        }

        return name + " " + FormatValue(row);
    }

    private static string ShortTrayHardware(string hardware)
    {
        var text = ShortHardwareName(hardware);
        if (text.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Eth";
        }

        if (text.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "WiFi";
        }

        if (text.StartsWith("VMware Network Adapter ", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("VMware Network Adapter ", "VM");
        }

        if (text.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        return text;
    }

    private static string ShortTrayName(string name)
    {
        var text = CleanSensorName(name);
        if (text.Equals("CPU package", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (text.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        if (text.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
        {
            return "T" + text.Substring("Temperature #".Length);
        }

        if (text.Equals("Receive rate", StringComparison.OrdinalIgnoreCase))
        {
            return "Rx";
        }

        if (text.Equals("Send rate", StringComparison.OrdinalIgnoreCase))
        {
            return "Tx";
        }

        if (text.Equals("Data received", StringComparison.OrdinalIgnoreCase))
        {
            return "Rx total";
        }

        if (text.Equals("Data sent", StringComparison.OrdinalIgnoreCase))
        {
            return "Tx total";
        }

        if (text.Equals("CPU usage", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (text.Equals("Memory used", StringComparison.OrdinalIgnoreCase))
        {
            return "Mem";
        }

        return text.Replace(" Activity", "").Replace(" activity", "");
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
        if (value.RunAtStartup)
        {
            value.StartMinimizedToTray = true;
            value.TrayStatusEnabled = true;
        }
        else if (value.StartMinimizedToTray)
        {
            value.TrayStatusEnabled = true;
        }
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
        var selectedRows = selectedKeys
            .Select(key => latestRows.FirstOrDefault(r => RowSettingsKey(r) == key))
            .Where(r => r != null)
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
            : string.Join("; ", selectedRows.Select(ShortTrayReadingText).ToArray());

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

    private static string FormatBitsPerSecond(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "0 bps";
        }

        var value = (double)bitsPerSecond;
        var units = new[] { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return Math.Round(value, unit == 0 ? 0 : 1).ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
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
    public static void Main(string[] args)
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
            Application.Run(new SensorReadoutForm(ShouldStartMinimized(args)));
        }
    }

    private static bool ShouldStartMinimized(string[] args)
    {
        if (args == null)
        {
            return false;
        }

        return args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));
    }
}
