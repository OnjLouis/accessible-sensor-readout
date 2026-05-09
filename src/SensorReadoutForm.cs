using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private const string AppVersion = "1.6.1";
    private const string ProjectUrl = "https://github.com/OnjLouis/accessible-sensor-readout";
    private const string DefaultLanguageFileName = "English.txt";
    private const long MaxLogBytes = 262144;
    private const int RefreshIntervalMs = 5000;
    private static readonly TimeSpan FocusedAutoRefreshMinimumInterval = TimeSpan.Zero;
    private const int ShowHideHotKeyId = 2001;
    private const int SpeakTrayHotKeyId = 2002;
    private const int SpokenHotKeyBaseId = 2100;
    private const int FanProfileHotKeyBaseId = 2200;
    private const int WmHotKey = 0x0312;
    private readonly AppSettings settings;
    private readonly MenuStrip menuStrip;
    private readonly ToolStripMenuItem editRenameMenuItem;
    private readonly ToolStripMenuItem treeRenameMenuItem;
    private readonly ToolStripMenuItem batteryViewMenuItem;
    private readonly ToolStripMenuItem autoRefreshMenuItem;
    private readonly ToolStripMenuItem refreshWhileFocusedMenuItem;
    private readonly ToolStripMenuItem trayStatusMenuItem;
    private readonly ToolStripMenuItem celsiusMenuItem;
    private readonly ToolStripMenuItem fahrenheitMenuItem;
    private readonly ToolStripMenuItem celsiusFahrenheitMenuItem;
    private readonly ToolStripMenuItem fahrenheitCelsiusMenuItem;
    private readonly ToolStripMenuItem languageMenuItem;
    private readonly ListBox deviceList;
    private readonly TreeView readingTree;
    private readonly MeterProgressBar selectedMeterProgressBar;
    private readonly Label selectedMeterValueLabel;
    private readonly Label statusLabel;
    private readonly CheckBox pauseCheckBox;
    private ComboBox fanControlBox;
    private TextBox fanLabelBox;
    private NumericUpDown fanPercentBox;
    private CheckBox showStoppedFansCheckBox;
    private Label fanControlStatusLabel;
    private readonly Timer timer;
    private readonly Timer languageTimer;
    private readonly Timer updateCheckTimer;
    private readonly Timer closeRequestTimer;
    private readonly NotifyIcon trayIcon;
    private Timer trayFlashTimer;
    private Icon trayStatusIcon;
    private Icon alarmTrayIcon;
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
    private string lastReadingTreeFilterKey = "";
    private bool readingTreeExpansionInitialized;
    private bool automaticUpdateCheckStartedThisRun;
    private string currentTrayStatusText = "Sensor Readout";
    private readonly Dictionary<int, SpokenHotKeySetting> registeredSpokenHotKeys = new Dictionary<int, SpokenHotKeySetting>();
    private readonly Dictionary<int, FanProfileSetting> registeredFanProfileHotKeys = new Dictionary<int, FanProfileSetting>();
    private readonly Dictionary<string, DateTime> alarmLastTriggeredUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private int trayFlashTicksRemaining;
    private bool trayFlashShowingAlarm;
    private int lastSpeechHotKeyId;
    private DateTime lastSpeechHotKeyPressedUtc;
    private int lastSelectedMeterValue = -1;
    private string lastSelectedMeterLabel = "";
    private string lastPreferencesTabName = "General";
    private bool savedFanControlsAppliedThisRun;
    private PreferencesForm openPreferencesDialog;
    private readonly HotKeyWindow hotKeyWindow;
    private readonly Dictionary<string, NetworkSnapshot> networkSnapshots = new Dictionary<string, NetworkSnapshot>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogicalDiskPerformanceCounters> logicalDiskCounters = new Dictionary<string, LogicalDiskPerformanceCounters>(StringComparer.OrdinalIgnoreCase);
    private readonly object logicalDiskCountersLock = new object();
    private readonly Dictionary<string, int> lastAppliedFanCurvePercents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastAppliedFanCurveUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private string toggledFanProfileKey = "";
    private UsbDiagnosticSnapshot lastUsbDiagnosticSnapshot = new UsbDiagnosticSnapshot();
    private bool hasLastCpuTimes;
    private ulong lastCpuIdleTime;
    private ulong lastCpuKernelTime;
    private ulong lastCpuUserTime;
    private readonly object slowRowsLock = new object();
    private List<SensorRow> cachedSlowRows = new List<SensorRow>();
    private DateTime cachedSlowRowsUtc = DateTime.MinValue;
    private readonly object lhmRowsLock = new object();
    private List<SensorRow> cachedLhmRows = new List<SensorRow>();
    private DateTime cachedLhmRowsUtc = DateTime.MinValue;
    private Timer visibleRefreshTimer;
    private bool menuInteractionActive;
    private bool visibleRefreshPending;
    private DateTime lastUserNavigationUtc = DateTime.MinValue;
    private DateTime lastFocusedAutoRefreshUtc = DateTime.MinValue;
    private List<LanguageChoice> languageChoices = new List<LanguageChoice>();
    private string languageFolderSignature = "";
    private readonly Dictionary<object, string> originalUiText = new Dictionary<object, string>();
    private static string activeTemperatureUnit = "C";
    private static string activeDecimalSeparator = "";
    private static LanguageCatalog activeLanguage = LanguageCatalog.English();
    private readonly System.Threading.EventWaitHandle closeRequestEvent;

    public SensorReadoutForm()
        : this(false)
    {
    }

    public SensorReadoutForm(bool startMinimized)
    {
        settings = LoadSettings();
        activeTemperatureUnit = settings.TemperatureUnit;
        activeDecimalSeparator = settings.DecimalSeparator;
        RefreshLanguageChoices(true);
        LoadSelectedLanguage();
        hotKeyWindow = new HotKeyWindow(this);
        closeRequestEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, Program.CloseRequestEventName);
        startMinimizedRequested = startMinimized;
        if ((startMinimizedRequested || settings.StartMinimizedToTray) && !settings.TrayStatusEnabled)
        {
            settings.TrayStatusEnabled = true;
        }
        Text = "Sensor Readout " + AppVersion;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        MinimumSize = new Size(700, 420);

        menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(CreateShortcutMenuItem("&Refresh now", Keys.F5, delegate { RefreshSensors(); }));
        fileMenu.DropDownItems.Add(CreateShortcutMenuItem("&Save report...", Keys.Control | Keys.S, delegate { SaveReport(); }));
        fileMenu.DropDownItems.Add(CreateShortcutMenuItem("&Import Plug-In from ZIP...", Keys.Control | Keys.I, delegate { ImportPlugInFromZip(); }));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("E&xit", null, delegate { Close(); });

        var editMenu = new ToolStripMenuItem("&Edit");
        editMenu.DropDownItems.Add(CreateShortcutMenuItem("&Copy", Keys.Control | Keys.C, delegate { CopySelectedTreeNode(); }));
        editMenu.DropDownItems.Add(CreateDisplayShortcutMenuItem("&Details...", "Enter", delegate { ShowSelectedReadingDetails(); }));
        editRenameMenuItem = CreateShortcutMenuItem("&Rename...", Keys.F2, delegate { RenameSelectedTreeNode(); });
        editMenu.DropDownItems.Add(editRenameMenuItem);
        editMenu.DropDownItems.Add(CreateShortcutMenuItem("&Hide selected", Keys.Delete, delegate { HideSelectedTreeNode(); }));
        editMenu.DropDownOpening += delegate { UpdateRenameMenuVisibility(); };

        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add("&Performance/Overview\tCtrl+0", null, delegate { SelectCategoryByKey("type|Performance"); });
        viewMenu.DropDownItems.Add("&Temperatures\tCtrl+1", null, delegate { SelectCategoryByKey("type|Temperature"); });
        viewMenu.DropDownItems.Add("&Fans\tCtrl+2", null, delegate { SelectCategoryByKey("type|Fan"); });
        viewMenu.DropDownItems.Add("&SMART\tCtrl+3", null, delegate { SelectCategoryByKey("type|SMART"); });
        viewMenu.DropDownItems.Add("&Network\tCtrl+4", null, delegate { SelectCategoryByKey("type|Network"); });
        viewMenu.DropDownItems.Add("&USB\tCtrl+5", null, delegate { SelectCategoryByKey("type|USB"); });
        batteryViewMenuItem = new ToolStripMenuItem("&Battery\tCtrl+6", null, delegate { SelectCategoryByKey("type|Battery"); });
        viewMenu.DropDownItems.Add(batteryViewMenuItem);
        viewMenu.DropDownOpening += delegate { UpdateViewMenuVisibility(); };

        var optionsMenu = new ToolStripMenuItem("&Options");
        autoRefreshMenuItem = new ToolStripMenuItem("&Auto refresh")
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

        refreshWhileFocusedMenuItem = new ToolStripMenuItem("&Refresh while focused")
        {
            Checked = settings.RefreshWhileFocused,
            CheckOnClick = true
        };
        refreshWhileFocusedMenuItem.CheckedChanged += delegate
        {
            settings.RefreshWhileFocused = refreshWhileFocusedMenuItem.Checked;
            SaveSettings(settings);
        };

        trayStatusMenuItem = new ToolStripMenuItem("Show &tray status")
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

        var temperatureMenu = new ToolStripMenuItem("Temperature &unit");
        celsiusMenuItem = new ToolStripMenuItem("&Celsius (C)")
        {
            Checked = string.Equals(settings.TemperatureUnit, "C", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        fahrenheitMenuItem = new ToolStripMenuItem("&Fahrenheit (F)")
        {
            Checked = string.Equals(settings.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        celsiusFahrenheitMenuItem = new ToolStripMenuItem("Celsius, then F&ahrenheit")
        {
            Checked = string.Equals(settings.TemperatureUnit, "CF", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        fahrenheitCelsiusMenuItem = new ToolStripMenuItem("Fahrenheit, then C&elsius")
        {
            Checked = string.Equals(settings.TemperatureUnit, "FC", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        celsiusMenuItem.CheckedChanged += CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged += FahrenheitMenuItemCheckedChangedPlaceholder;
        celsiusFahrenheitMenuItem.CheckedChanged += CelsiusFahrenheitMenuItemCheckedChangedPlaceholder;
        fahrenheitCelsiusMenuItem.CheckedChanged += FahrenheitCelsiusMenuItemCheckedChangedPlaceholder;
        temperatureMenu.DropDownItems.Add(celsiusMenuItem);
        temperatureMenu.DropDownItems.Add(fahrenheitMenuItem);
        temperatureMenu.DropDownItems.Add(celsiusFahrenheitMenuItem);
        temperatureMenu.DropDownItems.Add(fahrenheitCelsiusMenuItem);

        languageMenuItem = new ToolStripMenuItem("&Language");
        BuildLanguageMenu();

        optionsMenu.DropDownItems.Add(autoRefreshMenuItem);
        optionsMenu.DropDownItems.Add(refreshWhileFocusedMenuItem);
        optionsMenu.DropDownItems.Add(trayStatusMenuItem);
        optionsMenu.DropDownItems.Add(temperatureMenu);
        optionsMenu.DropDownItems.Add(languageMenuItem);
        optionsMenu.DropDownItems.Add(new ToolStripSeparator());
        optionsMenu.DropDownItems.Add("&Speak tray status now", null, delegate { SpeakTrayStatus(); });
        optionsMenu.DropDownItems.Add(CreateShortcutMenuItem("&Fan controls...", Keys.Control | Keys.L, delegate { ShowFanControlsDialog(); }));
        optionsMenu.DropDownItems.Add(CreateShortcutMenuItem("Fan c&urves...", Keys.Control | Keys.U, delegate { ShowFanCurvesDialog(); }));
        optionsMenu.DropDownItems.Add(CreateDisplayShortcutMenuItem("&Preferences...", "Ctrl+,", delegate { ShowPreferences(); }));

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Manual", Keys.F1, delegate { ShowManual(); }));
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Check for updates...", Keys.Shift | Keys.F1, delegate { CheckForUpdates(); }));
        helpMenu.DropDownItems.Add(CreateShortcutMenuItem("&Project on GitHub", Keys.Control | Keys.F1, delegate { OpenProjectPage(); }));
        helpMenu.DropDownItems.Add("Con&tact", null, delegate { OpenContactPage(); });
        helpMenu.DropDownItems.Add("&Donate", null, delegate { OpenDonatePage(); });
        helpMenu.DropDownItems.Add("Install Core Temp &support...", null, delegate { ShowCoreTempSupportOptions(); });
        helpMenu.DropDownItems.Add("&Install prerequisites...", null, delegate { RunPrerequisiteInstaller(); });
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add("&About Sensor Readout", null, delegate { ShowAbout(); });

        menuStrip.Items.Add(fileMenu);
        menuStrip.Items.Add(editMenu);
        menuStrip.Items.Add(viewMenu);
        menuStrip.Items.Add(optionsMenu);
        menuStrip.Items.Add(helpMenu);
        menuStrip.MenuActivate += delegate
        {
            menuInteractionActive = true;
            MarkUserNavigation();
        };
        menuStrip.MenuDeactivate += delegate
        {
            menuInteractionActive = false;
            ScheduleVisibleReadingUiUpdate();
        };
        MainMenuStrip = menuStrip;

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

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

        topPanel.Controls.Add(pauseCheckBox);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Sensor Readout",
            Visible = settings.TrayStatusEnabled,
            ContextMenuStrip = new ContextMenuStrip()
        };
        trayIcon.ContextMenuStrip.Items.Add("&Open Sensor Readout", null, delegate
        {
            RestoreFromTray();
        });
        trayIcon.ContextMenuStrip.Items.Add("&Refresh now", null, delegate { RefreshSensors(); });
        trayIcon.ContextMenuStrip.Items.Add("&Preferences...", null, delegate { ShowPreferences(); });
        trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        trayIcon.ContextMenuStrip.Items.Add("E&xit", null, delegate { Close(); });
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
            AccessibleDescription = "Choose a section such as Temperatures, Fans, SMART, Performance, Network, or USB"
        };
        deviceList.SelectedIndexChanged += delegate
        {
            MarkUserNavigation();
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
        readingTree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem("&Copy", Keys.Control | Keys.C, delegate { CopySelectedTreeNode(); }));
        readingTree.ContextMenuStrip.Items.Add(CreateDisplayShortcutMenuItem("&Details...", "Enter", delegate { ShowSelectedReadingDetails(); }));
        treeRenameMenuItem = CreateShortcutMenuItem("&Rename...", Keys.F2, delegate { RenameSelectedTreeNode(); });
        readingTree.ContextMenuStrip.Items.Add(treeRenameMenuItem);
        readingTree.ContextMenuStrip.Items.Add(CreateShortcutMenuItem("&Hide selected", Keys.Delete, delegate { HideSelectedTreeNode(); }));
        readingTree.ContextMenuStrip.Opening += delegate { UpdateRenameMenuVisibility(); };
        readingTree.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            MarkUserNavigation();
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
            else if (e.KeyCode == Keys.Enter)
            {
                ShowSelectedReadingDetails();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        readingTree.AfterSelect += delegate
        {
            UpdateSelectedMeterProgress();
            UpdateRenameMenuVisibility();
        };

        selectedMeterValueLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "Selected meter value",
            Padding = new Padding(0, 4, 0, 0)
        };
        selectedMeterProgressBar = new MeterProgressBar
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
        UpdateDeviceList();
        UpdateReadingList();

        statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(8, 5, 8, 0),
            Text = "Sensor Readout is open. Readings will appear as the background refresh completes."
        };

        Controls.Add(splitContainer);
        Controls.Add(statusLabel);
        Controls.Add(topPanel);
        Controls.Add(menuStrip);

        timer = new Timer { Interval = RefreshIntervalMs };
        timer.Tick += delegate
        {
            if (ShouldRunAutoRefresh())
            {
                RefreshSensors(true, false, ContainsFocus ? "auto-focused" : "auto-background");
            }
        };
        languageTimer = new Timer { Interval = 15000 };
        languageTimer.Tick += delegate { CheckLanguageFolderChanged(); };
        updateCheckTimer = new Timer { Interval = 10 * 60 * 1000 };
        updateCheckTimer.Tick += delegate { CheckAutomaticUpdateSchedule(); };
        closeRequestTimer = new Timer { Interval = 250 };
        closeRequestTimer.Tick += delegate { CheckCloseRequest(); };
        closeRequestTimer.Start();
        visibleRefreshTimer = new Timer { Interval = 300 };
        visibleRefreshTimer.Tick += delegate
        {
            if (!visibleRefreshPending)
            {
                visibleRefreshTimer.Stop();
                return;
            }

            if (ShouldDeferVisibleReadingUiUpdate())
            {
                return;
            }

            visibleRefreshTimer.Stop();
            visibleRefreshPending = false;
            UpdateVisibleReadingUi();
        };

        Shown += delegate
        {
            ApplyTimerSettings();
            CheckPrerequisitesOnFirstRun();
            LogMessage("Normal", "Sensor Readout " + AppVersion + " started. Log file: " + GetLogFilePath());
            RefreshSensors();
            timer.Start();
            languageTimer.Start();
            RegisterGlobalHotKeys();
            StartAutomaticUpdateChecks();
            if (startMinimizedRequested || settings.StartMinimizedToTray)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    WindowState = FormWindowState.Minimized;
                    MinimizeToTray();
                    PlayStartupSound();
                    ScheduleStartupActiveMessage();
                });
            }
            else
            {
                PlayStartupSound();
            }
        };

        Resize += delegate
        {
            if (WindowState == FormWindowState.Minimized && settings.TrayStatusEnabled && !minimizingToTray)
            {
                MinimizeToTray();
            }
        };

        ApplyLanguage();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleAppShortcut(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleAppShortcut(Keys keyData)
    {
        var keyCode = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;

        if (modifiers == Keys.None && keyCode == Keys.F5)
        {
            RefreshSensors();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.S)
        {
            SaveReport();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.I)
        {
            ImportPlugInFromZip();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.Oemcomma)
        {
            ShowPreferences();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.L)
        {
            ShowFanControlsDialog();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.U)
        {
            ShowFanCurvesDialog();
            return true;
        }

        if (modifiers == Keys.Control && SelectCategoryByShortcut(keyCode))
        {
            return true;
        }

        if (modifiers == Keys.Shift && keyCode == Keys.F1)
        {
            CheckForUpdates();
            return true;
        }

        if (modifiers == Keys.Control && keyCode == Keys.F1)
        {
            OpenProjectPage();
            return true;
        }

        if (modifiers == Keys.None && keyCode == Keys.F1)
        {
            ShowManual();
            return true;
        }

        return false;
    }

    private static ToolStripMenuItem CreateShortcutMenuItem(string text, Keys shortcutKeys, EventHandler handler)
    {
        var shortcutText = new KeysConverter().ConvertToString(shortcutKeys);
        return new ToolStripMenuItem(text, null, handler)
        {
            ShortcutKeys = shortcutKeys,
            ShortcutKeyDisplayString = shortcutText,
            ShowShortcutKeys = true
        };
    }

    private static ToolStripMenuItem CreateDisplayShortcutMenuItem(string text, string shortcutText, EventHandler handler)
    {
        return new ToolStripMenuItem(text, null, handler)
        {
            ShortcutKeyDisplayString = shortcutText,
            ShowShortcutKeys = true
        };
    }

    private void MarkUserNavigation()
    {
        lastUserNavigationUtc = DateTime.UtcNow;
    }

    private bool ShouldDeferVisibleReadingUiUpdate()
    {
        return menuInteractionActive || DateTime.UtcNow - lastUserNavigationUtc < TimeSpan.FromMilliseconds(500);
    }

    private void ScheduleVisibleReadingUiUpdate()
    {
        if (IsMinimizedOrHidden())
        {
            return;
        }

        visibleRefreshPending = true;
        if (visibleRefreshTimer != null && !visibleRefreshTimer.Enabled)
        {
            visibleRefreshTimer.Start();
        }
    }

    private void UpdateVisibleReadingUi()
    {
        if (IsMinimizedOrHidden())
        {
            return;
        }

        UpdateFanControlBox();
        UpdateDeviceList();
        UpdateReadingList();
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

    private bool ShouldRunAutoRefresh()
    {
        if (!settings.AutoRefreshEnabled)
        {
            return false;
        }

        if (!ContainsFocus)
        {
            return true;
        }

        if (!settings.RefreshWhileFocused)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastFocusedAutoRefreshUtc < FocusedAutoRefreshMinimumInterval)
        {
            return false;
        }

        lastFocusedAutoRefreshUtc = now;
        return true;
    }

    private void SetTemperatureUnit(string unit)
    {
        settings.TemperatureUnit = NormalizeTemperatureUnit(unit);
        activeTemperatureUnit = settings.TemperatureUnit;
        SaveSettings(settings);
        UpdateTemperatureUnitMenu();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList();
        UpdateTrayStatus();
        statusLabel.Text = "Temperature unit set to " + TemperatureUnitDisplayName(settings.TemperatureUnit) + ".";
    }

    private void UpdateTemperatureUnitMenu()
    {
        if (celsiusMenuItem == null || fahrenheitMenuItem == null || celsiusFahrenheitMenuItem == null || fahrenheitCelsiusMenuItem == null)
        {
            return;
        }

        var unit = NormalizeTemperatureUnit(settings.TemperatureUnit);
        celsiusMenuItem.CheckedChanged -= CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged -= FahrenheitMenuItemCheckedChangedPlaceholder;
        celsiusFahrenheitMenuItem.CheckedChanged -= CelsiusFahrenheitMenuItemCheckedChangedPlaceholder;
        fahrenheitCelsiusMenuItem.CheckedChanged -= FahrenheitCelsiusMenuItemCheckedChangedPlaceholder;
        celsiusMenuItem.Checked = string.Equals(unit, "C", StringComparison.OrdinalIgnoreCase);
        fahrenheitMenuItem.Checked = string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase);
        celsiusFahrenheitMenuItem.Checked = string.Equals(unit, "CF", StringComparison.OrdinalIgnoreCase);
        fahrenheitCelsiusMenuItem.Checked = string.Equals(unit, "FC", StringComparison.OrdinalIgnoreCase);
        celsiusMenuItem.CheckedChanged += CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged += FahrenheitMenuItemCheckedChangedPlaceholder;
        celsiusFahrenheitMenuItem.CheckedChanged += CelsiusFahrenheitMenuItemCheckedChangedPlaceholder;
        fahrenheitCelsiusMenuItem.CheckedChanged += FahrenheitCelsiusMenuItemCheckedChangedPlaceholder;
    }

    private void CelsiusMenuItemCheckedChangedPlaceholder(object sender, EventArgs e)
    {
        if (celsiusMenuItem.Checked)
        {
            SetTemperatureUnit("C");
        }
    }

    private void FahrenheitMenuItemCheckedChangedPlaceholder(object sender, EventArgs e)
    {
        if (fahrenheitMenuItem.Checked)
        {
            SetTemperatureUnit("F");
        }
    }

    private void CelsiusFahrenheitMenuItemCheckedChangedPlaceholder(object sender, EventArgs e)
    {
        if (celsiusFahrenheitMenuItem.Checked)
        {
            SetTemperatureUnit("CF");
        }
    }

    private void FahrenheitCelsiusMenuItemCheckedChangedPlaceholder(object sender, EventArgs e)
    {
        if (fahrenheitCelsiusMenuItem.Checked)
        {
            SetTemperatureUnit("FC");
        }
    }

}
