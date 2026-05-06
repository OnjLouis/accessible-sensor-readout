using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;

[assembly: System.Reflection.AssemblyTitle("Sensor Readout")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

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
    public int RefreshIntervalSeconds = 5;
    public string TemperatureUnit = "C";
    public string DecimalSeparator = "";
    public string LanguageFile = "";
    public bool LanguagePreferenceInitialized = false;
    public string ShowHideHotKey = "";
    public string SpeakTrayHotKey = "";
    public string StartupSpeechMessage = "";
    public bool TrayStatusEnabled = true;
    public bool RunAtStartup = false;
    public bool StartMinimizedToTray = false;
    public bool PrerequisitesPromptShown = false;
    public string LoggingLevel = "Off";
    public List<string> TrayItemKeys = new List<string>();
    public List<string> HiddenReadingKeys = new List<string>();
    public Dictionary<string, string> FanLabels = new Dictionary<string, string>();
}

public sealed class LanguageChoice
{
    public string FileName;
    public string DisplayName;
    public string FullPath;

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed class LanguageCatalog
{
    private readonly Dictionary<string, string> values;
    public readonly string FileName;
    public readonly string DisplayName;
    public readonly string DecimalSeparator;

    public LanguageCatalog(string fileName, string displayName, Dictionary<string, string> values)
    {
        FileName = fileName ?? "";
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "English" : displayName;
        this.values = new Dictionary<string, string>(values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        string separator;
        DecimalSeparator = this.values.TryGetValue("number.decimalSeparator", out separator) && !string.IsNullOrWhiteSpace(separator) ? separator.Trim() : "";
    }

    public string Text(string key, string fallback)
    {
        string value;
        return !string.IsNullOrWhiteSpace(key) && values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    public static LanguageCatalog English()
    {
        return new LanguageCatalog("", "English", new Dictionary<string, string>());
    }
}

public sealed class LanguageEntryChoice
{
    public string Key;
    public string Label;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Label) ? Key : Label;
    }
}

public sealed class ReadingTreeItem
{
    public string Key;
    public string Text;
    public SensorRow Row;
    public readonly List<ReadingTreeItem> Children = new List<ReadingTreeItem>();
}

public sealed class MeterProgressBar : ProgressBar
{
    public void NotifyAccessibleValueChanged()
    {
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
}

public sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private readonly SensorReadoutForm owner;

    public HotKeyWindow(SensorReadoutForm owner)
    {
        this.owner = owner;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (owner != null && owner.HandleHotKeyMessage(ref m))
        {
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
    }
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

    [JsonProperty("body")]
    public string Body;

    [JsonProperty("assets")]
    public List<GitHubReleaseAsset> Assets;
}

public sealed class GitHubReleaseAsset
{
    [JsonProperty("name")]
    public string Name;

    [JsonProperty("browser_download_url")]
    public string BrowserDownloadUrl;
}

public sealed class GlobalHotKey
{
    public uint Modifiers;
    public Keys Key;

    public bool IsValid
    {
        get { return Key != Keys.None && Modifiers != 0; }
    }
}

public sealed class PreferencesForm : Form
{
    private readonly CheckBox autoRefreshCheckBox;
    private readonly CheckBox refreshWhileFocusedCheckBox;
    private readonly CheckBox trayStatusCheckBox;
    private readonly CheckBox runAtStartupCheckBox;
    private readonly CheckBox startMinimizedCheckBox;
    private readonly NumericUpDown refreshSecondsBox;
    private readonly ComboBox temperatureUnitBox;
    private readonly ComboBox decimalSeparatorBox;
    private readonly ComboBox languageBox;
    private readonly Label languageFolderStatusLabel;
    private readonly TextBox showHideHotKeyBox;
    private readonly TextBox speakTrayHotKeyBox;
    private readonly TextBox startupSpeechMessageBox;
    private readonly ComboBox loggingLevelBox;
    private ComboBox languageEditorFileBox;
    private ListBox languageEntryList;
    private TextBox languageEntryValueBox;
    private readonly ListBox trayAvailableList;
    private readonly ListBox traySelectedList;
    private readonly Label traySelectionStatusLabel;
    private readonly CheckedListBox hiddenItemsList;
    private readonly List<SensorRow> rows;
    private readonly List<string> hiddenReadingKeys;
    private readonly AppSettings liveSettings;
    private readonly List<string> originalTrayItemKeys;
    private readonly Dictionary<object, string> originalUiText = new Dictionary<object, string>();
    private readonly Dictionary<object, string> originalAccessibleNames = new Dictionary<object, string>();
    private readonly Dictionary<object, string> originalAccessibleDescriptions = new Dictionary<object, string>();
    private bool loadingPreferences;

    public bool AutoRefreshEnabled { get { return autoRefreshCheckBox.Checked; } }
    public bool RefreshWhileFocused { get { return refreshWhileFocusedCheckBox.Checked; } }
    public bool TrayStatusEnabled { get { return trayStatusCheckBox.Checked; } }
    public bool RunAtStartup { get { return runAtStartupCheckBox.Checked; } }
    public bool StartMinimizedToTray { get { return startMinimizedCheckBox.Checked; } }
    public int RefreshIntervalSeconds { get { return Convert.ToInt32(refreshSecondsBox.Value); } }
    public string TemperatureUnit { get { return temperatureUnitBox.SelectedIndex == 1 ? "F" : "C"; } }
    public string DecimalSeparator
    {
        get
        {
            if (decimalSeparatorBox.SelectedIndex == DecimalSeparatorIndex("."))
            {
                return ".";
            }
            if (decimalSeparatorBox.SelectedIndex == DecimalSeparatorIndex(","))
            {
                return ",";
            }

            return "";
        }
    }
    public string LanguageFile
    {
        get
        {
            var choice = languageBox.SelectedItem as LanguageChoice;
            return choice == null ? "" : choice.FileName;
        }
    }
    public string ShowHideHotKey { get { return SensorReadoutForm.NormalizeHotKeyText(showHideHotKeyBox.Text); } }
    public string SpeakTrayHotKey { get { return SensorReadoutForm.NormalizeHotKeyText(speakTrayHotKeyBox.Text); } }
    public string StartupSpeechMessage
    {
        get
        {
            var text = startupSpeechMessageBox.Text.Trim();
            return string.Equals(text, SensorReadoutForm.DefaultStartupSpeechMessage(), StringComparison.Ordinal) ? "" : text;
        }
    }
    public string LoggingLevel
    {
        get
        {
            if (loggingLevelBox.SelectedIndex == 1) return "Error";
            if (loggingLevelBox.SelectedIndex == 2) return "Normal";
            if (loggingLevelBox.SelectedIndex == 3) return "Debug";
            return "Off";
        }
    }
    public List<string> TrayItemKeys { get; private set; }
    public List<string> HiddenReadingKeys { get; private set; }

    public PreferencesForm(AppSettings settings, List<SensorRow> latestRows, List<LanguageChoice> languageChoices)
    {
        var effectiveLanguageChoices = SensorReadoutForm.LoadLanguageChoices();
        if (effectiveLanguageChoices.Count == 0)
        {
            effectiveLanguageChoices = languageChoices ?? new List<LanguageChoice>();
        }
        languageFolderStatusLabel = new Label
        {
            Text = "Languages loaded: " + effectiveLanguageChoices.Count + " from " + SensorReadoutForm.GetLanguagesFolderPath(),
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        Text = "Sensor Readout Preferences";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 620);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        liveSettings = settings;
        originalTrayItemKeys = new List<string>(settings.TrayItemKeys ?? new List<string>());
        loadingPreferences = true;
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
        var languageEditorTab = new TabPage("Language editor");

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 15,
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
        refreshSecondsBox.ValueChanged += delegate { SaveLivePreferences(); };

        var temperaturePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        temperaturePanel.Controls.Add(new Label
        {
            Text = "Temperature unit:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        temperatureUnitBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 140,
            AccessibleName = "Temperature unit"
        };
        temperatureUnitBox.Items.AddRange(new object[] { "Celsius (C)", "Fahrenheit (F)" });
        temperatureUnitBox.SelectedItem = string.Equals(settings.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase) ? "Fahrenheit (F)" : "Celsius (C)";
        temperaturePanel.Controls.Add(temperatureUnitBox);
        temperatureUnitBox.SelectedIndexChanged += delegate { SaveLivePreferences(); };

        var decimalSeparatorPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        decimalSeparatorPanel.Controls.Add(new Label
        {
            Text = "Decimal separator:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        decimalSeparatorBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            AccessibleName = "Decimal separator"
        };
        decimalSeparatorBox.Items.AddRange(new object[] { "Language default", "Period (.)", "Comma (,)" });
        decimalSeparatorBox.SelectedItem = DecimalSeparatorChoiceText(settings.DecimalSeparator);
        decimalSeparatorPanel.Controls.Add(decimalSeparatorBox);
        decimalSeparatorBox.SelectedIndexChanged += delegate { SaveLivePreferences(); };

        var languagePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        languagePanel.Controls.Add(new Label
        {
            Text = "Language:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        languageBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            AccessibleName = "Language"
        };
        foreach (var choice in UserSelectableLanguageChoices(effectiveLanguageChoices))
        {
            languageBox.Items.Add(choice);
        }
        if (languageBox.Items.Count == 0)
        {
            languageBox.Items.Add(new LanguageChoice { FileName = "English.txt", DisplayName = "English", FullPath = System.IO.Path.Combine(SensorReadoutForm.GetLanguagesFolderPath(), "English.txt") });
        }
        UpdateLanguageFolderStatus();
        var selectedLanguage = settings.LanguageFile ?? "";
        var selectedChoice = languageBox.Items.Cast<LanguageChoice>().FirstOrDefault(i => string.Equals(i.FileName, selectedLanguage, StringComparison.OrdinalIgnoreCase))
            ?? languageBox.Items.Cast<LanguageChoice>().FirstOrDefault(i => string.IsNullOrWhiteSpace(i.FileName))
            ?? (LanguageChoice)languageBox.Items[0];
        languageBox.SelectedItem = selectedChoice;
        languagePanel.Controls.Add(languageBox);
        languageBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            SensorReadoutForm.ActivateLanguage(LanguageFile);
            if (string.IsNullOrWhiteSpace(liveSettings.StartupSpeechMessage))
            {
                startupSpeechMessageBox.Text = SensorReadoutForm.DefaultStartupSpeechMessage();
            }
            ApplyLanguage();
            var owner = Owner as SensorReadoutForm;
            if (owner != null)
            {
                owner.ReloadLanguageFromSettings();
            }
        };

        var hotKeyPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3
        };
        hotKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotKeyPanel.Controls.Add(new Label { Text = "Show/hide hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        showHideHotKeyBox = CreateHotKeyBox(settings.ShowHideHotKey, "Show or hide Sensor Readout global hotkey");
        hotKeyPanel.Controls.Add(showHideHotKeyBox, 1, 0);
        var clearShowHideButton = new Button { Text = "Clear", AutoSize = true };
        clearShowHideButton.Click += delegate { showHideHotKeyBox.Text = ""; };
        hotKeyPanel.Controls.Add(clearShowHideButton, 2, 0);
        hotKeyPanel.Controls.Add(new Label { Text = "Speak tray status hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);
        speakTrayHotKeyBox = CreateHotKeyBox(settings.SpeakTrayHotKey, "Speak notification area status global hotkey");
        hotKeyPanel.Controls.Add(speakTrayHotKeyBox, 1, 1);
        var clearSpeakButton = new Button { Text = "Clear", AutoSize = true };
        clearSpeakButton.Click += delegate { speakTrayHotKeyBox.Text = ""; };
        hotKeyPanel.Controls.Add(clearSpeakButton, 2, 1);
        hotKeyPanel.Controls.Add(new Label { Text = "Startup speech:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
        startupSpeechMessageBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(settings.StartupSpeechMessage) ? SensorReadoutForm.DefaultStartupSpeechMessage() : settings.StartupSpeechMessage,
            Dock = DockStyle.Fill,
            AccessibleName = "Startup speech message",
            AccessibleDescription = "Message spoken by NVDA when Sensor Readout starts minimized to the notification area."
        };
        hotKeyPanel.Controls.Add(startupSpeechMessageBox, 1, 2);
        var resetStartupSpeechButton = new Button { Text = "Reset", AutoSize = true };
        resetStartupSpeechButton.Click += delegate { startupSpeechMessageBox.Text = SensorReadoutForm.DefaultStartupSpeechMessage(); };
        hotKeyPanel.Controls.Add(resetStartupSpeechButton, 2, 2);

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
        loggingLevelBox.SelectedIndex = LoggingLevelIndex(configuredLogging);
        loggingPanel.Controls.Add(loggingLevelBox);
        loggingLevelBox.SelectedIndexChanged += delegate { SaveLivePreferences(); };

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
            var selectedTrayChoice = trayChoices.FirstOrDefault(i => i.Key == key);
            if (selectedTrayChoice != null && !ContainsTrayChoice(traySelectedList, selectedTrayChoice.Key))
            {
                traySelectedList.Items.Add(selectedTrayChoice);
            }
            else if (selectedTrayChoice == null && !ContainsTrayChoice(traySelectedList, key))
            {
                traySelectedList.Items.Add(TrayItemChoice.Unresolved(key));
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
        UpdateTraySelectionStatus();

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
        hiddenItemsList.ItemCheck += delegate
        {
            if (loadingPreferences)
            {
                return;
            }

            if (IsHandleCreated)
            {
                BeginInvoke((MethodInvoker)SaveLivePreferences);
            }
        };

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

        var dialogButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
        dialogButtons.Controls.Add(closeButton);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        closeButton.Click += delegate
        {
            CommitPreferences();
        };

        main.Controls.Add(languagePanel, 0, 0);
        main.Controls.Add(languageFolderStatusLabel, 0, 1);
        main.Controls.Add(autoRefreshCheckBox, 0, 2);
        main.Controls.Add(refreshWhileFocusedCheckBox, 0, 3);
        main.Controls.Add(trayStatusCheckBox, 0, 4);
        main.Controls.Add(runAtStartupCheckBox, 0, 5);
        main.Controls.Add(startMinimizedCheckBox, 0, 6);
        main.Controls.Add(intervalPanel, 0, 7);
        main.Controls.Add(temperaturePanel, 0, 8);
        main.Controls.Add(decimalSeparatorPanel, 0, 9);
        main.Controls.Add(hotKeyPanel, 0, 10);
        main.Controls.Add(loggingPanel, 0, 11);
        main.Controls.Add(trayLabel, 0, 12);
        main.Controls.Add(BuildTraySelectionPanel(), 0, 13);
        main.Controls.Add(traySelectionStatusLabel, 0, 14);
        generalTab.Controls.Add(main);
        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(hiddenTab);
        languageEditorTab.Controls.Add(BuildLanguageEditorPanel(effectiveLanguageChoices));
        tabs.TabPages.Add(languageEditorTab);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 0, 0, 8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(dialogButtons, 0, 1);
        Controls.Add(root);
        autoRefreshCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        refreshWhileFocusedCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        trayStatusCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        runAtStartupCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        startMinimizedCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        showHideHotKeyBox.TextChanged += delegate { SaveLivePreferences(); };
        speakTrayHotKeyBox.TextChanged += delegate { SaveLivePreferences(); };
        startupSpeechMessageBox.TextChanged += delegate { SaveLivePreferences(); };
        Shown += delegate
        {
            loadingPreferences = false;
            SaveLivePreferences();
        };
        FormClosing += delegate
        {
            CommitPreferences();
            if (DialogResult == DialogResult.None)
            {
                DialogResult = DialogResult.OK;
            }
        };
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Text = SensorReadoutForm.L("ui.Sensor Readout Preferences", "Sensor Readout Preferences");
        ApplyLanguageToControls(Controls);
        ApplyFixedOptionLanguage();
        UpdateLanguageFolderStatus();
        UpdateTraySelectionStatus();
        if (trayAvailableList != null)
        {
            trayAvailableList.Refresh();
        }
        if (traySelectedList != null)
        {
            traySelectedList.Refresh();
        }
    }

    private void ApplyLanguageToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            ApplyLanguageToControl(control);
            var tabs = control as TabControl;
            if (tabs != null)
            {
                foreach (TabPage page in tabs.TabPages)
                {
                    ApplyLanguageToControl(page);
                    ApplyLanguageToControls(page.Controls);
                }
            }
            ApplyLanguageToControls(control.Controls);
        }
    }

    private void ApplyLanguageToControl(Control control)
    {
        if (control == null)
        {
            return;
        }

        if (!originalUiText.ContainsKey(control))
        {
            originalUiText[control] = control.Text ?? "";
        }

        var original = originalUiText[control];
        if (ShouldTranslateControlText(control) && !string.IsNullOrWhiteSpace(original))
        {
            control.Text = SensorReadoutForm.TranslateUiText(original);
        }

        if (!originalAccessibleNames.ContainsKey(control))
        {
            originalAccessibleNames[control] = control.AccessibleName ?? "";
        }
        if (!originalAccessibleDescriptions.ContainsKey(control))
        {
            originalAccessibleDescriptions[control] = control.AccessibleDescription ?? "";
        }

        var accessibleName = originalAccessibleNames[control];
        if (!string.IsNullOrWhiteSpace(accessibleName))
        {
            var translatedAccessibleName = SensorReadoutForm.L("a11y." + accessibleName, accessibleName);
            if (string.Equals(translatedAccessibleName, accessibleName, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(original) &&
                !string.Equals(control.Text ?? "", original, StringComparison.Ordinal))
            {
                translatedAccessibleName = StripMnemonic(control.Text);
            }

            control.AccessibleName = translatedAccessibleName;
        }
        var accessibleDescription = originalAccessibleDescriptions[control];
        if (!string.IsNullOrWhiteSpace(accessibleDescription))
        {
            control.AccessibleDescription = SensorReadoutForm.L("a11y." + accessibleDescription, accessibleDescription);
        }
    }

    private static string StripMnemonic(string text)
    {
        return (text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
    }

    private static bool ShouldTranslateControlText(Control control)
    {
        return !(control is ComboBox)
            && !(control is ListBox)
            && !(control is CheckedListBox)
            && !(control is TextBox)
            && !(control is NumericUpDown)
            && !(control is TreeView)
            && !(control is ProgressBar);
    }

    private void ApplyFixedOptionLanguage()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            SetComboItems(temperatureUnitBox, new[] { SensorReadoutForm.L("ui.Celsius (C)", "Celsius (C)"), SensorReadoutForm.L("ui.Fahrenheit (F)", "Fahrenheit (F)") });
            SetComboItems(decimalSeparatorBox, new[] { SensorReadoutForm.L("ui.Language default", "Language default"), SensorReadoutForm.L("ui.Period (.)", "Period (.)"), SensorReadoutForm.L("ui.Comma (,)", "Comma (,)") });
            SetComboItems(loggingLevelBox, new[] { SensorReadoutForm.L("ui.Off", "Off"), SensorReadoutForm.L("ui.Error", "Error"), SensorReadoutForm.L("ui.Normal", "Normal"), SensorReadoutForm.L("ui.Debug", "Debug") });
            decimalSeparatorBox.SelectedIndex = DecimalSeparatorIndex(DecimalSeparator);
            if (decimalSeparatorBox.SelectedIndex >= 0)
            {
                decimalSeparatorBox.Text = Convert.ToString(decimalSeparatorBox.Items[decimalSeparatorBox.SelectedIndex]);
            }
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private static void SetComboItems(ComboBox box, string[] values)
    {
        if (box == null)
        {
            return;
        }

        var selectedIndex = Math.Max(0, box.SelectedIndex);
        var newIndex = Math.Min(selectedIndex, values.Length - 1);
        box.BeginUpdate();
        try
        {
            box.SelectedIndex = -1;
            box.Text = "";
            box.Items.Clear();
            box.Items.AddRange(values.Cast<object>().ToArray());
            box.SelectedIndex = newIndex;
            if (newIndex >= 0 && newIndex < values.Length)
            {
                box.Text = values[newIndex];
            }
        }
        finally
        {
            box.EndUpdate();
        }

        box.Refresh();
    }

    private void UpdateLanguageFolderStatus()
    {
        if (languageFolderStatusLabel == null || languageBox == null)
        {
            return;
        }

        languageFolderStatusLabel.Text = SensorReadoutForm.L("ui.Languages loaded:", "Languages loaded:") + " " + languageBox.Items.Count + " (" + string.Join(", ", languageBox.Items.Cast<LanguageChoice>().Select(i => i.DisplayName).ToArray()) + ")";
    }

    private static TextBox CreateHotKeyBox(string hotKey, string accessibleName)
    {
        var box = new TextBox
        {
            Text = SensorReadoutForm.NormalizeHotKeyText(hotKey),
            ReadOnly = true,
            Dock = DockStyle.Fill,
            AccessibleName = accessibleName,
            AccessibleDescription = "Press the key combination to assign it. Use the Clear button to disable it."
        };
        box.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Escape)
            {
                box.Text = "";
                return;
            }

            var text = SensorReadoutForm.HotKeyTextFromKeyEvent(e);
            if (!string.IsNullOrWhiteSpace(text))
            {
                box.Text = text;
            }
        };
        return box;
    }

    private void SaveLivePreferences()
    {
        if (loadingPreferences || liveSettings == null)
        {
            return;
        }

        liveSettings.AutoRefreshEnabled = AutoRefreshEnabled;
        liveSettings.RefreshWhileFocused = RefreshWhileFocused;
        liveSettings.RefreshIntervalSeconds = RefreshIntervalSeconds;
        liveSettings.TemperatureUnit = TemperatureUnit;
        liveSettings.DecimalSeparator = DecimalSeparator;
        liveSettings.LanguageFile = LanguageFile;
        liveSettings.LanguagePreferenceInitialized = true;
        liveSettings.ShowHideHotKey = ShowHideHotKey;
        liveSettings.SpeakTrayHotKey = SpeakTrayHotKey;
        liveSettings.StartupSpeechMessage = StartupSpeechMessage;
        liveSettings.TrayStatusEnabled = TrayStatusEnabled;
        liveSettings.RunAtStartup = RunAtStartup;
        liveSettings.StartMinimizedToTray = StartMinimizedToTray;
        if (liveSettings.RunAtStartup || liveSettings.StartMinimizedToTray)
        {
            liveSettings.TrayStatusEnabled = true;
        }
        liveSettings.LoggingLevel = LoggingLevel;
        var currentTrayItemKeys = CurrentTrayItemKeys();
        if (currentTrayItemKeys.Count > 0 || originalTrayItemKeys.Count == 0 || rows.Count > 0)
        {
            liveSettings.TrayItemKeys = currentTrayItemKeys;
        }
        liveSettings.HiddenReadingKeys = CurrentHiddenReadingKeys();
        SensorReadoutForm.SaveSettings(liveSettings);
    }

    private void CommitPreferences()
    {
        TrayItemKeys = CurrentTrayItemKeys();
        HiddenReadingKeys = CurrentHiddenReadingKeys();
        SaveLivePreferences();
    }

    private static string DecimalSeparatorChoiceText(string value)
    {
        if (string.Equals(value, ".", StringComparison.Ordinal))
        {
            return "Period (.)";
        }
        if (string.Equals(value, ",", StringComparison.Ordinal))
        {
            return "Comma (,)";
        }

        return "Language default";
    }

    private static int DecimalSeparatorIndex(string value)
    {
        if (string.Equals(value, ".", StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(value, ",", StringComparison.Ordinal))
        {
            return 2;
        }

        return 0;
    }

    private static int LoggingLevelIndex(string value)
    {
        if (string.Equals(value, "Error", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(value, "Normal", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase)) return 3;
        return 0;
    }

    private static IEnumerable<LanguageChoice> UserSelectableLanguageChoices(IEnumerable<LanguageChoice> choices)
    {
        var seenEnglish = false;
        foreach (var choice in choices ?? new List<LanguageChoice>())
        {
            var isEnglish = string.Equals(choice.DisplayName ?? "", "English", StringComparison.OrdinalIgnoreCase);
            if (isEnglish)
            {
                if (seenEnglish)
                {
                    continue;
                }

                seenEnglish = true;
            }

            yield return choice;
        }
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

    private Control BuildLanguageEditorPanel(List<LanguageChoice> languageChoices)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        filePanel.Controls.Add(new Label { Text = "File:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        languageEditorFileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = "Language file to edit" };
        foreach (var choice in languageChoices ?? new List<LanguageChoice>())
        {
            if (!string.IsNullOrWhiteSpace(choice.FileName))
            {
                languageEditorFileBox.Items.Add(choice);
            }
        }
        filePanel.Controls.Add(languageEditorFileBox);
        var reloadButton = new Button { Text = "Reload", AutoSize = true };
        reloadButton.Click += delegate { LoadLanguageEditorEntries(); };
        filePanel.Controls.Add(reloadButton);
        var openFolderButton = new Button { Text = "Open folder", AutoSize = true };
        openFolderButton.Click += delegate { SensorReadoutForm.OpenLanguagesFolderStatic(this); };
        filePanel.Controls.Add(openFolderButton);
        var newButton = new Button { Text = "New...", AutoSize = true };
        newButton.Click += delegate { CreateNewLanguageFile(); };
        filePanel.Controls.Add(newButton);

        languageEntryList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Language entries",
            AccessibleDescription = "Select an entry, then edit its value below. Press F2 to move to the value field."
        };
        languageEntryList.SelectedIndexChanged += delegate { LoadSelectedLanguageEntryValue(); };
        languageEntryList.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2)
            {
                languageEntryValueBox.Focus();
                languageEntryValueBox.SelectAll();
                e.Handled = true;
            }
        };

        languageEntryValueBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Language entry value"
        };
        var buttonPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var saveButton = new Button { Text = "Save entry", AutoSize = true };
        saveButton.Click += delegate { SaveSelectedLanguageEntry(); };
        buttonPanel.Controls.Add(saveButton);

        layout.Controls.Add(filePanel, 0, 0);
        layout.Controls.Add(languageEntryList, 0, 1);
        layout.Controls.Add(new Label { Text = "Value:", AutoSize = true, Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(languageEntryValueBox, 0, 3);
        layout.Controls.Add(buttonPanel, 0, 4);

        languageEditorFileBox.SelectedIndexChanged += delegate { LoadLanguageEditorEntries(); };
        if (languageEditorFileBox.Items.Count > 0)
        {
            languageEditorFileBox.SelectedIndex = 0;
        }

        return layout;
    }

    private void LoadLanguageEditorEntries()
    {
        if (languageEditorFileBox == null || languageEntryList == null)
        {
            return;
        }

        languageEntryList.Items.Clear();
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        if (choice == null || string.IsNullOrWhiteSpace(choice.FullPath) || !System.IO.File.Exists(choice.FullPath))
        {
            return;
        }

        foreach (var key in SensorReadoutForm.ReadLanguageFile(choice.FullPath).Keys.OrderBy(LanguageEntrySortKey))
        {
            languageEntryList.Items.Add(new LanguageEntryChoice { Key = key, Label = FriendlyLanguageEntryLabel(key) });
        }
        if (languageEntryList.Items.Count > 0)
        {
            languageEntryList.SelectedIndex = 0;
        }
    }

    private void LoadSelectedLanguageEntryValue()
    {
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        var entry = languageEntryList.SelectedItem as LanguageEntryChoice;
        var key = entry == null ? "" : entry.Key;
        if (choice == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(choice.FullPath))
        {
            languageEntryValueBox.Text = "";
            return;
        }

        var values = SensorReadoutForm.ReadLanguageFile(choice.FullPath);
        string value;
        languageEntryValueBox.Text = values.TryGetValue(key, out value) ? value : "";
    }

    private void SaveSelectedLanguageEntry()
    {
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        var entry = languageEntryList.SelectedItem as LanguageEntryChoice;
        var key = entry == null ? "" : entry.Key;
        if (choice == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(choice.FullPath))
        {
            return;
        }

        SensorReadoutForm.UpdateLanguageFileValue(choice.FullPath, key, languageEntryValueBox.Text);
        if (key.Equals("speech.startupActive", StringComparison.OrdinalIgnoreCase))
        {
            startupSpeechMessageBox.Text = languageEntryValueBox.Text;
        }
    }

    private void CreateNewLanguageFile()
    {
        var requested = PromptForText(this, SensorReadoutForm.L("ui.New language file", "New language file"), SensorReadoutForm.L("ui.File name:", "File name:"), "MyLanguage.txt");
        var fileName = NormalizeNewLanguageFileName(requested);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var folder = SensorReadoutForm.GetLanguagesFolderPath();
        var path = System.IO.Path.Combine(folder, fileName);
        if (System.IO.File.Exists(path))
        {
            MessageBox.Show(this, SensorReadoutForm.L("message.languageFileExists", "That language file already exists."), SensorReadoutForm.L("ui.New language file", "New language file"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            var englishPath = System.IO.Path.Combine(folder, "English.txt");
            if (System.IO.File.Exists(englishPath))
            {
                System.IO.File.Copy(englishPath, path);
            }
            else
            {
                System.IO.File.WriteAllLines(path, new[] { "language.name=" + System.IO.Path.GetFileNameWithoutExtension(fileName) });
            }

            SensorReadoutForm.UpdateLanguageFileValue(path, "language.name", System.IO.Path.GetFileNameWithoutExtension(fileName));
            var choice = new LanguageChoice
            {
                FileName = fileName,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(fileName),
                FullPath = path
            };
            languageEditorFileBox.Items.Add(choice);
            languageEditorFileBox.SelectedItem = choice;
            if (!languageBox.Items.Cast<LanguageChoice>().Any(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                languageBox.Items.Add(choice);
            }

            var owner = Owner as SensorReadoutForm;
            if (owner != null)
            {
                owner.RefreshLanguagesNow();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, SensorReadoutForm.L("ui.New language file", "New language file"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FriendlyLanguageEntryLabel(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        if (key.Equals("language.name", StringComparison.OrdinalIgnoreCase))
        {
            return "Language: Display name";
        }
        if (key.Equals("manual.file", StringComparison.OrdinalIgnoreCase))
        {
            return "Manual: file name only, no folder path";
        }
        if (key.Equals("number.decimalSeparator", StringComparison.OrdinalIgnoreCase))
        {
            return "Number: Decimal separator";
        }
        if (key.Equals("speech.startupActive", StringComparison.OrdinalIgnoreCase))
        {
            return "Spoken: Startup message";
        }
        if (key.Equals("app.title", StringComparison.OrdinalIgnoreCase))
        {
            return "Interface: App title";
        }
        if (key.StartsWith("type.", StringComparison.OrdinalIgnoreCase))
        {
            return "Section: " + key.Substring("type.".Length);
        }
        if (key.StartsWith("group.", StringComparison.OrdinalIgnoreCase))
        {
            return "Group: " + key.Substring("group.".Length);
        }
        if (key.StartsWith("reading.", StringComparison.OrdinalIgnoreCase))
        {
            return "Reading: " + key.Substring("reading.".Length);
        }
        if (key.StartsWith("message.", StringComparison.OrdinalIgnoreCase))
        {
            return "Message: " + SplitCamelKey(key.Substring("message.".Length));
        }
        if (key.StartsWith("a11y.", StringComparison.OrdinalIgnoreCase))
        {
            return "Spoken: " + key.Substring("a11y.".Length);
        }
        if (key.StartsWith("ui.", StringComparison.OrdinalIgnoreCase))
        {
            return "Interface: " + key.Substring("ui.".Length);
        }

        return "Other: " + key;
    }

    private static string LanguageEntrySortKey(string key)
    {
        if (key.StartsWith("language.", StringComparison.OrdinalIgnoreCase)) return "00|" + key;
        if (key.StartsWith("manual.", StringComparison.OrdinalIgnoreCase)) return "01|" + key;
        if (key.StartsWith("speech.", StringComparison.OrdinalIgnoreCase)) return "02|" + key;
        if (key.StartsWith("type.", StringComparison.OrdinalIgnoreCase)) return "03|" + key;
        if (key.StartsWith("group.", StringComparison.OrdinalIgnoreCase)) return "04|" + key;
        if (key.StartsWith("reading.", StringComparison.OrdinalIgnoreCase)) return "05|" + key;
        if (key.StartsWith("ui.", StringComparison.OrdinalIgnoreCase)) return "06|" + key;
        if (key.StartsWith("a11y.", StringComparison.OrdinalIgnoreCase)) return "07|" + key;
        if (key.StartsWith("message.", StringComparison.OrdinalIgnoreCase)) return "08|" + key;
        if (key.StartsWith("number.", StringComparison.OrdinalIgnoreCase)) return "09|" + key;
        return "99|" + key;
    }

    private static string SplitCamelKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var result = new System.Text.StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            if (i > 0 && char.IsUpper(ch) && char.IsLower(key[i - 1]))
            {
                result.Append(' ');
            }
            result.Append(ch);
        }

        return result.ToString();
    }

    private static string NormalizeNewLanguageFileName(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "";
        }

        var fileName = System.IO.Path.GetFileName(requested.Trim());
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".txt";
        }

        return fileName;
    }

    private static string PromptForText(IWin32Window owner, string title, string label, string initialValue)
    {
        using (var dialog = new Form())
        {
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(460, 150);
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var textBox = new TextBox { Text = initialValue ?? "", Dock = DockStyle.Fill };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = SensorReadoutForm.L("ui.OK", "OK"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = SensorReadoutForm.L("ui.Cancel", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(textBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text : "";
        }
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
        SaveLivePreferences();
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
        SaveLivePreferences();
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
        SaveLivePreferences();
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
            traySelectionStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private void UpdateTraySelectionStatus()
    {
        if (traySelectionStatusLabel == null || traySelectedList == null)
        {
            return;
        }

        SetTraySelectionStatus(SensorReadoutForm.L("ui.Tray order has", "Tray order has") + " " + traySelectedList.Items.Count + " " + SensorReadoutForm.L("ui.of 4 readings.", "of 4 readings."));
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

    private List<string> CurrentTrayItemKeys()
    {
        return traySelectedList.Items
            .Cast<TrayItemChoice>()
            .Take(4)
            .Select(i => i.Key)
            .ToList();
    }

    private List<string> CurrentHiddenReadingKeys()
    {
        return hiddenItemsList.CheckedItems
            .Cast<object>()
            .Select(i => Convert.ToString(i))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
    }

    private sealed class TrayItemChoice
    {
        public readonly string Key;
        public readonly string Type;
        public readonly string Hardware;
        public readonly string Name;
        private readonly string label;
        private readonly bool unresolved;

        private TrayItemChoice(string key)
        {
            Key = key ?? "";
            var parts = Key.Split('|');
            Type = parts.Length > 0 ? parts[0] : "";
            Hardware = parts.Length > 1 ? parts[1] : "";
            Name = parts.Length > 2 ? parts[2] : "";
            unresolved = true;
            label = string.IsNullOrWhiteSpace(Hardware) ? Key : Hardware + " - " + Name;
        }

        public TrayItemChoice(SensorRow row)
        {
            Key = SensorReadoutForm.RowSettingsKey(row);
            Type = row.Type;
            Hardware = SensorReadoutForm.ShortHardwareName(row.Hardware);
            Name = SensorReadoutForm.CleanSensorName(row.Name);
            label = "";
        }

        public static TrayItemChoice Unresolved(string key)
        {
            return new TrayItemChoice(key);
        }

        public override string ToString()
        {
            return unresolved
                ? SensorReadoutForm.L("ui.Missing reading -", "Missing reading -") + " " + label
                : SensorReadoutForm.TrayChoiceLabel(Hardware, Name, Type);
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
    private const string AppVersion = "1.2.0";
    private const string ProjectUrl = "https://github.com/OnjLouis/accessible-sensor-readout";
    private const string DefaultLanguageFileName = "English.txt";
    private const long MaxLogBytes = 262144;
    private const int RefreshIntervalMs = 5000;
    private const int ShowHideHotKeyId = 2001;
    private const int SpeakTrayHotKeyId = 2002;
    private const int WmHotKey = 0x0312;
    private readonly AppSettings settings;
    private readonly MenuStrip menuStrip;
    private readonly ToolStripMenuItem autoRefreshMenuItem;
    private readonly ToolStripMenuItem refreshWhileFocusedMenuItem;
    private readonly ToolStripMenuItem trayStatusMenuItem;
    private readonly ToolStripMenuItem celsiusMenuItem;
    private readonly ToolStripMenuItem fahrenheitMenuItem;
    private readonly ToolStripMenuItem languageMenuItem;
    private readonly ListBox deviceList;
    private readonly TreeView readingTree;
    private readonly MeterProgressBar selectedMeterProgressBar;
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
    private readonly Timer languageTimer;
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
    private string lastReadingTreeFilterKey = "";
    private bool readingTreeExpansionInitialized;
    private string currentTrayStatusText = "Sensor Readout";
    private int lastSelectedMeterValue = -1;
    private string lastSelectedMeterLabel = "";
    private readonly HotKeyWindow hotKeyWindow;
    private readonly Dictionary<string, NetworkSnapshot> networkSnapshots = new Dictionary<string, NetworkSnapshot>(StringComparer.OrdinalIgnoreCase);
    private List<LanguageChoice> languageChoices = new List<LanguageChoice>();
    private string languageFolderSignature = "";
    private readonly Dictionary<object, string> originalUiText = new Dictionary<object, string>();
    private static string activeTemperatureUnit = "C";
    private static string activeDecimalSeparator = "";
    private static LanguageCatalog activeLanguage = LanguageCatalog.English();

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
                HandleShortcutKey(e);
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                SaveReport();
                HandleShortcutKey(e);
            }
            else if (e.Control && e.KeyCode == Keys.Oemcomma)
            {
                ShowPreferences();
                HandleShortcutKey(e);
            }
            else if (e.Control && e.KeyCode == Keys.L)
            {
                ShowFanControlsDialog();
                HandleShortcutKey(e);
            }
            else if (e.Control && SelectCategoryByShortcut(e.KeyCode))
            {
                HandleShortcutKey(e);
            }
            else if (e.Shift && e.KeyCode == Keys.F1)
            {
                CheckForUpdates();
                HandleShortcutKey(e);
            }
            else if (e.Control && e.KeyCode == Keys.F1)
            {
                OpenProjectPage();
                HandleShortcutKey(e);
            }
            else if (e.KeyCode == Keys.F1)
            {
                ShowManual();
                HandleShortcutKey(e);
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
        viewMenu.DropDownItems.Add("&Performance/Overview\tCtrl+0", null, delegate { SelectCategoryByKey("type|Performance"); });
        viewMenu.DropDownItems.Add("&Temperatures\tCtrl+1", null, delegate { SelectCategoryByKey("type|Temperature"); });
        viewMenu.DropDownItems.Add("&Fans\tCtrl+2", null, delegate { SelectCategoryByKey("type|Fan"); });
        viewMenu.DropDownItems.Add("&SMART\tCtrl+3", null, delegate { SelectCategoryByKey("type|SMART"); });
        viewMenu.DropDownItems.Add("&Network\tCtrl+4", null, delegate { SelectCategoryByKey("type|Network"); });

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
            Checked = !string.Equals(settings.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        fahrenheitMenuItem = new ToolStripMenuItem("&Fahrenheit (F)")
        {
            Checked = string.Equals(settings.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase),
            CheckOnClick = true
        };
        celsiusMenuItem.CheckedChanged += CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged += FahrenheitMenuItemCheckedChangedPlaceholder;
        temperatureMenu.DropDownItems.Add(celsiusMenuItem);
        temperatureMenu.DropDownItems.Add(fahrenheitMenuItem);

        languageMenuItem = new ToolStripMenuItem("&Language");
        BuildLanguageMenu();

        optionsMenu.DropDownItems.Add(autoRefreshMenuItem);
        optionsMenu.DropDownItems.Add(refreshWhileFocusedMenuItem);
        optionsMenu.DropDownItems.Add(trayStatusMenuItem);
        optionsMenu.DropDownItems.Add(temperatureMenu);
        optionsMenu.DropDownItems.Add(languageMenuItem);
        optionsMenu.DropDownItems.Add(new ToolStripSeparator());
        optionsMenu.DropDownItems.Add("&Speak tray status now", null, delegate { SpeakTrayStatus(); });
        optionsMenu.DropDownItems.Add("&Fan controls...\tCtrl+L", null, delegate { ShowFanControlsDialog(); });
        optionsMenu.DropDownItems.Add("&Preferences...\tCtrl+,", null, delegate { ShowPreferences(); });

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("&Manual\tF1", null, delegate { ShowManual(); });
        helpMenu.DropDownItems.Add("Check for &updates...\tShift+F1", null, delegate { CheckForUpdates(); });
        helpMenu.DropDownItems.Add("&Project on GitHub\tCtrl+F1", null, delegate { OpenProjectPage(); });
        helpMenu.DropDownItems.Add("&Contact", null, delegate { OpenContactPage(); });
        helpMenu.DropDownItems.Add("&Donate", null, delegate { OpenDonatePage(); });
        helpMenu.DropDownItems.Add("&Install prerequisites...", null, delegate { RunPrerequisiteInstaller(); });
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add("&About Sensor Readout", null, delegate { ShowAbout(); });

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
            if (settings.AutoRefreshEnabled && (settings.RefreshWhileFocused || !ContainsFocus))
            {
                RefreshSensors();
            }
        };
        languageTimer = new Timer { Interval = 15000 };
        languageTimer.Tick += delegate { CheckLanguageFolderChanged(); };

        Shown += delegate
        {
            ApplyTimerSettings();
            CheckPrerequisitesOnFirstRun();
            LogMessage("Normal", "Sensor Readout " + AppVersion + " started. Log file: " + GetLogFilePath());
            RefreshSensors();
            timer.Start();
            languageTimer.Start();
            RegisterGlobalHotKeys();
            BeginSilentStartupUpdateCheck();
            if (startMinimizedRequested || settings.StartMinimizedToTray)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    WindowState = FormWindowState.Minimized;
                    MinimizeToTray();
                    ScheduleStartupActiveMessage();
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

    private void SetTemperatureUnit(string unit)
    {
        settings.TemperatureUnit = string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
        activeTemperatureUnit = settings.TemperatureUnit;
        SaveSettings(settings);
        UpdateTemperatureUnitMenu();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList();
        UpdateTrayStatus();
        statusLabel.Text = "Temperature unit set to " + (settings.TemperatureUnit == "F" ? "Fahrenheit." : "Celsius.");
    }

    private void UpdateTemperatureUnitMenu()
    {
        if (celsiusMenuItem == null || fahrenheitMenuItem == null)
        {
            return;
        }

        var fahrenheit = string.Equals(settings.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase);
        celsiusMenuItem.CheckedChanged -= CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged -= FahrenheitMenuItemCheckedChangedPlaceholder;
        celsiusMenuItem.Checked = !fahrenheit;
        fahrenheitMenuItem.Checked = fahrenheit;
        celsiusMenuItem.CheckedChanged += CelsiusMenuItemCheckedChangedPlaceholder;
        fahrenheitMenuItem.CheckedChanged += FahrenheitMenuItemCheckedChangedPlaceholder;
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

    private void RefreshLanguageChoices(bool force)
    {
        var signature = BuildLanguageFolderSignature();
        if (!force && string.Equals(signature, languageFolderSignature, StringComparison.Ordinal))
        {
            return;
        }

        languageFolderSignature = signature;
        languageChoices = LoadLanguageChoices();
        LogMessage("Debug", "Loaded " + languageChoices.Count + " language file" + (languageChoices.Count == 1 ? "" : "s") + " from " + GetLanguagesFolderPath() + ": " + string.Join(", ", languageChoices.Select(c => c.FileName).ToArray()));
    }

    private void CheckLanguageFolderChanged()
    {
        var oldSignature = languageFolderSignature;
        RefreshLanguageChoices(true);
        if (!string.Equals(oldSignature, languageFolderSignature, StringComparison.Ordinal))
        {
            BuildLanguageMenu();
            LoadSelectedLanguage();
            ApplyLanguage();
        }
    }

    private void BuildLanguageMenu()
    {
        if (languageMenuItem == null)
        {
            return;
        }

        languageMenuItem.DropDownItems.Clear();
        foreach (var choice in UserSelectableLanguageChoices(languageChoices))
        {
            var item = new ToolStripMenuItem(choice.DisplayName)
            {
                Tag = choice,
                Checked = string.Equals(settings.LanguageFile ?? "", choice.FileName ?? "", StringComparison.OrdinalIgnoreCase)
            };
            item.Click += delegate(object sender, EventArgs e)
            {
                var menuItem = sender as ToolStripMenuItem;
                var selected = menuItem == null ? null : menuItem.Tag as LanguageChoice;
                if (selected != null)
                {
                    SetLanguage(selected.FileName);
                }
            };
            languageMenuItem.DropDownItems.Add(item);
        }

        languageMenuItem.DropDownItems.Add(new ToolStripSeparator());
        languageMenuItem.DropDownItems.Add("Open languages folder", null, delegate { OpenLanguagesFolder(); });
    }

    private static IEnumerable<LanguageChoice> UserSelectableLanguageChoices(IEnumerable<LanguageChoice> choices)
    {
        var seenEnglish = false;
        foreach (var choice in choices ?? new List<LanguageChoice>())
        {
            var isEnglish = string.Equals(choice.DisplayName ?? "", "English", StringComparison.OrdinalIgnoreCase);
            if (isEnglish)
            {
                if (seenEnglish)
                {
                    continue;
                }

                seenEnglish = true;
            }

            yield return choice;
        }
    }

    private void SetLanguage(string fileName)
    {
        settings.LanguageFile = SanitizeLanguageFileName(fileName);
        settings.LanguagePreferenceInitialized = true;
        SaveSettings(settings);
        LoadSelectedLanguage();
        BuildLanguageMenu();
        ApplyLanguage();
        statusLabel.Text = "Language set to " + activeLanguage.DisplayName + ".";
    }

    private void LoadSelectedLanguage()
    {
        ActivateLanguage(settings.LanguageFile);
    }

    public static void ActivateLanguage(string fileName)
    {
        activeLanguage = LoadLanguage(fileName);
    }

    public void ReloadLanguageFromSettings()
    {
        LoadSelectedLanguage();
        BuildLanguageMenu();
        ApplyLanguage();
    }

    public void RefreshLanguagesNow()
    {
        RefreshLanguageChoices(true);
        BuildLanguageMenu();
    }

    private void ApplyLanguage()
    {
        Text = T("app.title", "Sensor Readout") + " " + AppVersion;
        ApplyLanguageToControls(Controls);
        ApplyLanguageToToolStripItems(menuStrip.Items);
        if (trayIcon != null && trayIcon.ContextMenuStrip != null)
        {
            ApplyLanguageToToolStripItems(trayIcon.ContextMenuStrip.Items);
        }
        ApplyAccessibleLanguage();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateDeviceList();
        UpdateReadingList();
        UpdateTrayStatus();
    }

    private void ApplyAccessibleLanguage()
    {
        if (refreshButton != null)
        {
            refreshButton.AccessibleName = T("a11y.Refresh sensors now, F5", "Refresh sensors now, F5");
        }
        if (saveReportButton != null)
        {
            saveReportButton.AccessibleName = T("a11y.Save sensor report", "Save sensor report");
        }
        if (pauseCheckBox != null)
        {
            pauseCheckBox.AccessibleName = T("a11y.Pause automatic updates", "Pause automatic updates");
        }
        if (deviceList != null)
        {
            deviceList.AccessibleName = T("a11y.Reading section", "Reading section");
            deviceList.AccessibleDescription = T("a11y.Choose a section such as Temperatures, Fans, SMART, Performance, or Network", "Choose a section such as Temperatures, Fans, SMART, Performance, or Network");
        }
        if (readingTree != null)
        {
            readingTree.AccessibleName = T("a11y.Readings", "Readings");
            readingTree.AccessibleDescription = T("a11y.Current readings grouped by category or device", "Current readings grouped by category or device");
        }
        if (selectedMeterProgressBar != null)
        {
            selectedMeterProgressBar.AccessibleName = T("a11y.Selected meter", "Selected meter");
            selectedMeterProgressBar.AccessibleDescription = T("a11y.Selected meter value", "Selected meter value");
        }
    }

    private void ApplyLanguageToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            ApplyLanguageToControl(control);
            ApplyLanguageToControls(control.Controls);
        }
    }

    private void ApplyLanguageToControl(Control control)
    {
        if (control == null || string.IsNullOrWhiteSpace(control.Text))
        {
            return;
        }

        string original;
        if (!originalUiText.TryGetValue(control, out original))
        {
            original = control.Text;
            originalUiText[control] = original;
        }

        control.Text = TranslateUiText(original);
    }

    private void ApplyLanguageToToolStripItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            ApplyLanguageToToolStripItem(item);
            var dropDownItem = item as ToolStripDropDownItem;
            if (dropDownItem != null)
            {
                ApplyLanguageToToolStripItems(dropDownItem.DropDownItems);
            }
        }
    }

    private void ApplyLanguageToToolStripItem(ToolStripItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Text))
        {
            return;
        }

        string original;
        if (!originalUiText.TryGetValue(item, out original))
        {
            original = item.Text;
            originalUiText[item] = original;
        }

        item.Text = TranslateUiText(original);
    }

    public static string TranslateUiText(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return original;
        }

        var tab = original.IndexOf('\t');
        var label = tab >= 0 ? original.Substring(0, tab) : original;
        var shortcut = tab >= 0 ? original.Substring(tab) : "";
        var translated = T("ui." + label, label);
        if (string.Equals(translated, label, StringComparison.Ordinal) && label.IndexOf('&') >= 0)
        {
            translated = T("ui." + StripMenuMnemonic(label), label);
        }

        return translated + shortcut;
    }

    private static string StripMenuMnemonic(string text)
    {
        return (text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
    }

    public static string L(string key, string fallback)
    {
        return T(key, fallback);
    }

    private void OpenLanguagesFolder()
    {
        var folder = GetLanguagesFolderPath();
        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            Process.Start(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open languages folder: " + ex.Message, "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RegisterGlobalHotKeys()
    {
        UnregisterGlobalHotKeys();
        LogMessage("Debug", "Registering global hotkeys. Show/hide=" + (settings.ShowHideHotKey ?? "") + ", speak tray=" + (settings.SpeakTrayHotKey ?? "") + ".");
        RegisterGlobalHotKey(settings.ShowHideHotKey, ShowHideHotKeyId, "show/hide");
        RegisterGlobalHotKey(settings.SpeakTrayHotKey, SpeakTrayHotKeyId, "speak tray status");
    }

    private void RegisterGlobalHotKey(string hotKeyText, int id, string description)
    {
        var hotKey = ParseHotKey(hotKeyText);
        if (hotKey == null || !hotKey.IsValid)
        {
            if (!string.IsNullOrWhiteSpace(hotKeyText))
            {
                LogError("Invalid " + description + " hotkey setting: " + hotKeyText + ".");
            }
            return;
        }

        var hotKeyHandle = hotKeyWindow == null ? Handle : hotKeyWindow.Handle;
        if (!NativeMethods.RegisterHotKey(hotKeyHandle, id, hotKey.Modifiers, (uint)hotKey.Key))
        {
            var error = Marshal.GetLastWin32Error();
            var message = "Could not register " + description + " hotkey " + NormalizeHotKeyText(hotKeyText) + ". Windows error " + error + ". It may already be in use.";
            statusLabel.Text = message;
            LogError(message);
            return;
        }

        LogMessage("Normal", "Registered " + description + " hotkey " + NormalizeHotKeyText(hotKeyText) + ".");
    }

    private void UnregisterGlobalHotKeys()
    {
        var hotKeyHandle = hotKeyWindow == null ? (IsHandleCreated ? Handle : IntPtr.Zero) : hotKeyWindow.Handle;
        if (hotKeyHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(hotKeyHandle, ShowHideHotKeyId);
        NativeMethods.UnregisterHotKey(hotKeyHandle, SpeakTrayHotKeyId);
    }

    public bool HandleHotKeyMessage(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            var id = m.WParam.ToInt32();
            if (id == ShowHideHotKeyId)
            {
                LogMessage("Debug", "Show/hide hotkey pressed.");
                ToggleShowHide();
                return true;
            }

            if (id == SpeakTrayHotKeyId)
            {
                LogMessage("Debug", "Speak tray status hotkey pressed.");
                SpeakTrayStatus();
                return true;
            }
        }

        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (HandleHotKeyMessage(ref m))
        {
            return;
        }

        base.WndProc(ref m);
    }

    private void ToggleShowHide()
    {
        if (Visible && WindowState != FormWindowState.Minimized && ShowInTaskbar)
        {
            LogMessage("Normal", "Hiding Sensor Readout from global hotkey.");
            Hide();
            ShowInTaskbar = false;
            return;
        }

        LogMessage("Normal", "Showing Sensor Readout from global hotkey.");
        RestoreFromTray();
    }

    private void SpeakTrayStatus()
    {
        var text = BuildCurrentTrayStatusText();
        string error;
        if (NvdaController.TrySpeak(text, out error))
        {
            if (statusLabel != null)
            {
                statusLabel.Text = "Spoke tray status with NVDA.";
            }
            LogMessage("Normal", "Spoke tray status with NVDA: " + text);
            return;
        }

        if (statusLabel != null)
        {
            statusLabel.Text = "Could not speak with NVDA. " + error;
        }
        LogError("Could not speak with NVDA. " + error);
        System.Media.SystemSounds.Beep.Play();
    }

    private void ScheduleStartupActiveMessage()
    {
        if (!settings.StartMinimizedToTray && !startMinimizedRequested)
        {
            return;
        }

        var startupSpeechTimer = new Timer { Interval = 1500 };
        startupSpeechTimer.Tick += delegate
        {
            startupSpeechTimer.Stop();
            startupSpeechTimer.Dispose();
            SpeakStartupActiveMessage();
        };
        startupSpeechTimer.Start();
    }

    private void SpeakStartupActiveMessage()
    {
        var message = string.IsNullOrWhiteSpace(settings.StartupSpeechMessage) ? DefaultStartupSpeechMessage() : settings.StartupSpeechMessage.Trim();
        string error;
        if (NvdaController.TrySpeak(message, out error))
        {
            LogMessage("Normal", "Spoke startup active message with NVDA: " + message);
        }
        else
        {
            LogMessage("Debug", "Startup active message was not spoken. " + error);
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
        UpdateDeviceList();
        UpdateReadingList();
        UpdateFanControlBox();
        Activate();
    }

    private bool IsMinimizedOrHidden()
    {
        return !Visible || WindowState == FormWindowState.Minimized || !ShowInTaskbar;
    }

    private void ShowManual()
    {
        var manualPath = ResolveManualPath(ManualFileName());
        if (!System.IO.File.Exists(manualPath))
        {
            manualPath = ResolveManualPath("README-en.html");
        }
        if (!System.IO.File.Exists(manualPath))
        {
            manualPath = ResolveManualPath("README.md");
        }

        if (!System.IO.File.Exists(manualPath))
        {
            MessageBox.Show(this, T("message.manualNotFound", "The manual could not be found beside Sensor Readout."), T("ui.Sensor Readout manual", "Sensor Readout manual"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = manualPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not open manual", "Could not open manual"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ManualFileName()
    {
        var configured = T("manual.file", "");
        if (!string.IsNullOrWhiteSpace(SanitizeManualFileName(configured)))
        {
            return configured;
        }

        var languageFile = activeLanguage == null ? "" : activeLanguage.FileName;
        languageFile = System.IO.Path.GetFileNameWithoutExtension(languageFile);
        return string.IsNullOrWhiteSpace(languageFile) ? "README-en.html" : languageFile + ".html";
    }

    private static string ResolveManualPath(string fileName)
    {
        fileName = SanitizeManualFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "README-en.html";
        }

        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", fileName);
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "docs", fileName));
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", fileName));
    }

    private static string SanitizeManualFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        fileName = System.IO.Path.GetFileName(fileName.Trim());
        return fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : "";
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
                    "LibreHardwareMonitorLib, Newtonsoft.Json, PawnIO, HidSharp, DiskInfoToolkit, RAMSPDToolkit, BlackSharp.Core, NVDA Controller Client, and Microsoft .NET Framework support libraries."
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

    private void OpenDonatePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://www.paypal.me/AndreLouis", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not open donate page", "Could not open donate page"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenContactPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://onj.me/contact", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not open contact page", "Could not open contact page"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CheckForUpdates()
    {
        CheckForUpdates(true, true);
    }

    private void CheckForUpdates(bool showUpToDate, bool showErrors)
    {
        try
        {
            var release = FetchLatestRelease();
            var latest = (release == null ? "" : release.TagName) ?? "";
            var latestVersion = latest.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                if (showErrors)
                {
                    MessageBox.Show(this, T("message.couldNotReadLatestVersion", "Could not read the latest release version."), T("ui.Check for updates...", "Check for updates..."), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            Version current;
            Version remote;
            if (Version.TryParse(AppVersion, out current) && Version.TryParse(latestVersion, out remote) && remote > current)
            {
                ShowUpdateAvailableDialog(release, latest);
                CheckPawnIoFromManualUpdateCheck(showUpToDate, showErrors);
                return;
            }

            if (showUpToDate)
            {
                MessageBox.Show(this, string.Format(T("message.upToDate", "Sensor Readout is up to date. Current version: {0}."), AppVersion), T("ui.Check for updates...", "Check for updates..."), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            CheckPawnIoFromManualUpdateCheck(showUpToDate, showErrors);
        }
        catch (WebException ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, T("message.couldNotCheckUpdates", "Could not check for updates. GitHub releases may not exist yet, or the network request failed.") + Environment.NewLine + Environment.NewLine + ex.Message, T("ui.Check for updates...", "Check for updates..."), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, T("ui.Check for updates...", "Check for updates..."), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void CheckPawnIoFromManualUpdateCheck(bool showUpToDate, bool showErrors)
    {
        if (!showUpToDate || !showErrors || IsPawnIoInstalled())
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            T("message.pawnIoMissingAfterUpdateCheck", "PawnIO is not installed. Motherboard sensors and fan controls may be missing without it.") + Environment.NewLine + Environment.NewLine +
            T("message.installPawnIoNow", "Do you want to install PawnIO now using winget?"),
            T("ui.Check for updates...", "Check for updates..."),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            RunPrerequisiteInstaller();
        }
    }

    private void BeginSilentStartupUpdateCheck()
    {
        Task.Run(delegate
        {
            try
            {
                var release = FetchLatestRelease();
                var latest = (release == null ? "" : release.TagName) ?? "";
                var latestVersion = latest.Trim().TrimStart('v', 'V');
                Version current;
                Version remote;
                if (!Version.TryParse(AppVersion, out current) || !Version.TryParse(latestVersion, out remote) || remote <= current)
                {
                    return;
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed)
                    {
                        ShowUpdateAvailableDialog(release, latest);
                    }
                });
            }
            catch
            {
                // Startup checks are intentionally silent unless an update is available.
            }
        });
    }

    private static GitHubReleaseInfo FetchLatestRelease()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        using (var client = new WebClient())
        {
            client.Headers.Add("User-Agent", "Sensor Readout " + AppVersion);
            var json = client.DownloadString(ProjectUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
            return JsonConvert.DeserializeObject<GitHubReleaseInfo>(json);
        }
    }

    private void ShowUpdateAvailableDialog(GitHubReleaseInfo release, string latest)
    {
        var releaseUrl = release == null || string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl;
        var zipAsset = FindPortableZipAsset(release);

        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Update available", "Update available");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Width = 720;
            dialog.Height = 520;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowIcon = false;
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Text = string.Format(T("message.updateAvailableHeader", "Sensor Readout {0} is available."), latest),
                Padding = new Padding(0, 0, 0, 8)
            };

            var notes = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = string.IsNullOrWhiteSpace(release == null ? "" : release.Body)
                    ? T("message.noReleaseNotes", "No release notes were provided for this update.")
                    : release.Body,
                AccessibleName = T("a11y.Release notes", "Release notes")
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };

            var laterButton = new Button { Text = T("ui.Later", "Later"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var releaseButton = new Button { Text = T("ui.Open release page", "Open release page"), AutoSize = true };
            releaseButton.Click += delegate
            {
                Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true });
            };
            buttons.Controls.Add(laterButton);
            buttons.Controls.Add(releaseButton);

            if (zipAsset != null)
            {
                var installButton = new Button { Text = T("ui.Download and install", "Download and install"), AutoSize = true };
                installButton.Click += delegate
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    StartSelfUpdate(zipAsset.BrowserDownloadUrl);
                };
                buttons.Controls.Add(installButton);
                dialog.AcceptButton = installButton;
            }

            dialog.CancelButton = laterButton;
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(notes, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
    }

    private static GitHubReleaseAsset FindPortableZipAsset(GitHubReleaseInfo release)
    {
        if (release == null || release.Assets == null)
        {
            return null;
        }

        return release.Assets
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) && !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Name.IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0)
            .ThenByDescending(a => a.Name.IndexOf("sensor", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
    }

    private void StartSelfUpdate(string zipUrl)
    {
        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            MessageBox.Show(this, T("message.noUpdatePackage", "This GitHub release does not include a downloadable ZIP package. Please open the release page instead."), T("ui.Check for updates...", "Check for updates..."), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            T("message.updateRestartRequired", "Sensor Readout will close, download the update, replace the files in this folder, and restart. Your per-computer settings and logs will be kept."),
            T("ui.Download and install", "Download and install"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var exePath = Application.ExecutablePath;
            var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SensorReadoutUpdater-" + Guid.NewGuid().ToString("N") + ".ps1");
            System.IO.File.WriteAllText(scriptPath, BuildUpdaterScript(zipUrl, appDir, exePath, Process.GetCurrentProcess().Id));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not start updater", "Could not start updater"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string BuildUpdaterScript(string zipUrl, string targetDir, string exePath, int processId)
    {
        return
            "$ErrorActionPreference = 'Stop'\r\n" +
            "Add-Type -AssemblyName System.Windows.Forms\r\n" +
            "$zipUrl = " + PowerShellQuote(zipUrl) + "\r\n" +
            "$target = " + PowerShellQuote(targetDir) + "\r\n" +
            "$exe = " + PowerShellQuote(exePath) + "\r\n" +
            "$pidToWait = " + processId.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            "try {\r\n" +
            "  $root = Join-Path $env:TEMP ('SensorReadoutUpdate_' + [guid]::NewGuid().ToString('N'))\r\n" +
            "  $zip = Join-Path $root 'update.zip'\r\n" +
            "  $stage = Join-Path $root 'stage'\r\n" +
            "  New-Item -ItemType Directory -Force -Path $root, $stage | Out-Null\r\n" +
            "  Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing\r\n" +
            "  Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force\r\n" +
            "  $source = $stage\r\n" +
            "  if (-not (Test-Path -LiteralPath (Join-Path $source 'Sensor Readout.exe'))) {\r\n" +
            "    $candidate = Get-ChildItem -LiteralPath $stage -Recurse -Filter 'Sensor Readout.exe' -File | Select-Object -First 1\r\n" +
            "    if ($candidate) { $source = $candidate.DirectoryName }\r\n" +
            "  }\r\n" +
            "  if (-not (Test-Path -LiteralPath (Join-Path $source 'Sensor Readout.exe'))) { throw 'The downloaded ZIP does not contain Sensor Readout.exe.' }\r\n" +
            "  Get-Process -Id $pidToWait -ErrorAction SilentlyContinue | Wait-Process\r\n" +
            "  Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Recurse -Force }\r\n" +
            "  Remove-Item -LiteralPath (Join-Path $target 'README.md') -Force -ErrorAction SilentlyContinue\r\n" +
            "  if (Test-Path -LiteralPath (Join-Path $target 'docs')) { Get-ChildItem -LiteralPath (Join-Path $target 'docs') -Filter '*.md' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }\r\n" +
            "  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "  Start-Process -FilePath $exe\r\n" +
            "} catch {\r\n" +
            "  [System.Windows.Forms.MessageBox]::Show('Sensor Readout update failed:' + [Environment]::NewLine + [Environment]::NewLine + $_.Exception.Message, 'Sensor Readout updater', 'OK', 'Error') | Out-Null\r\n" +
            "}\r\n" +
            "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\r\n";
    }

    private static string PowerShellQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "''") + "'";
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
        RefreshLanguageChoices(false);
        using (var dialog = new PreferencesForm(settings, latestRows, languageChoices))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            settings.AutoRefreshEnabled = dialog.AutoRefreshEnabled;
            settings.RefreshWhileFocused = dialog.RefreshWhileFocused;
            settings.RefreshIntervalSeconds = dialog.RefreshIntervalSeconds;
            settings.TemperatureUnit = dialog.TemperatureUnit;
            settings.DecimalSeparator = dialog.DecimalSeparator;
            settings.LanguageFile = dialog.LanguageFile;
            settings.LanguagePreferenceInitialized = true;
            settings.ShowHideHotKey = dialog.ShowHideHotKey;
            settings.SpeakTrayHotKey = dialog.SpeakTrayHotKey;
            settings.StartupSpeechMessage = dialog.StartupSpeechMessage;
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
            activeTemperatureUnit = settings.TemperatureUnit;
            activeDecimalSeparator = settings.DecimalSeparator;
            LoadSelectedLanguage();
            UpdateTemperatureUnitMenu();
            BuildLanguageMenu();
            ApplyLanguage();
            RegisterGlobalHotKeys();
            ApplyTimerSettings();
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
            dialog.Text = T("ui.Fan Controls", "Fan Controls");
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

            layout.Controls.Add(new Label { Text = T("ui.Fan:", "Fan:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
            layout.Controls.Add(fanControlBox, 1, 0);
            layout.Controls.Add(new Label { Text = T("ui.Label:", "Label:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);

            var labelPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            labelPanel.Controls.Add(fanLabelBox);
            var saveFanLabelButton = new Button { Text = T("ui.Save &label", "Save &label"), AutoSize = true, AccessibleName = T("a11y.Save label for selected fan control", "Save label for selected fan control") };
            saveFanLabelButton.Click += delegate { SaveSelectedFanLabel(); };
            labelPanel.Controls.Add(saveFanLabelButton);
            layout.Controls.Add(labelPanel, 1, 1);

            layout.Controls.Add(new Label { Text = T("ui.Manual percent:", "Manual percent:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
            var percentPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            percentPanel.Controls.Add(fanPercentBox);
            var applyFanButton = new Button { Text = T("ui.&Manual adjust", "&Manual adjust"), AutoSize = true, AccessibleName = T("a11y.Apply manual percentage to selected fan control", "Apply manual percentage to selected fan control") };
            applyFanButton.Click += delegate { ApplySelectedFanControl(true); };
            percentPanel.Controls.Add(applyFanButton);
            var autoSelectedFanButton = new Button { Text = T("ui.Selected &auto", "Selected &auto"), AutoSize = true, AccessibleName = T("a11y.Return selected fan control to automatic", "Return selected fan control to automatic") };
            autoSelectedFanButton.Click += delegate { ApplySelectedFanControl(false); };
            percentPanel.Controls.Add(autoSelectedFanButton);
            layout.Controls.Add(percentPanel, 1, 2);

            layout.Controls.Add(new Label { Text = T("ui.Profiles:", "Profiles:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
            var profilePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var autoFanButton = new Button { Text = T("ui.All fans &reset", "All fans &reset"), AutoSize = true, AccessibleName = T("a11y.Return all fan controls to automatic", "Return all fan controls to automatic") };
            autoFanButton.Click += delegate { ResetAllFanControls(); };
            var elevatedFanButton = new Button { Text = T("ui.All fans &75", "All fans &75"), AutoSize = true, AccessibleName = T("a11y.Set all visible fan controls to 75 percent", "Set all visible fan controls to 75 percent") };
            elevatedFanButton.Click += delegate { ApplyAllVisibleFanControls(75, "elevated"); };
            var maxFanButton = new Button { Text = T("ui.All fans ma&x", "All fans ma&x"), AutoSize = true, AccessibleName = T("a11y.Set all visible fan controls to 100 percent", "Set all visible fan controls to 100 percent") };
            maxFanButton.Click += delegate { ApplyAllVisibleFanControls(100, "max"); };
            showStoppedFansCheckBox.Text = T("ui.Show &stopped", "Show &stopped");
            showStoppedFansCheckBox.AccessibleName = T("a11y.Show stopped or unpopulated fan headers", "Show stopped or unpopulated fan headers");
            profilePanel.Controls.Add(autoFanButton);
            profilePanel.Controls.Add(elevatedFanButton);
            profilePanel.Controls.Add(maxFanButton);
            profilePanel.Controls.Add(showStoppedFansCheckBox);
            layout.Controls.Add(profilePanel, 1, 3);

            var closePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = new Button { Text = T("ui.Close", "Close"), DialogResult = DialogResult.OK, AutoSize = true };
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
            AccessibleName = T("a11y.Fan control target", "Fan control target"),
            AccessibleDescription = T("a11y.Choose which fan control to adjust", "Choose which fan control to adjust")
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
            AccessibleName = T("a11y.Fan label", "Fan label"),
            AccessibleDescription = T("a11y.Friendly name for the selected fan control", "Friendly name for the selected fan control")
        };

        fanPercentBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 5,
            Value = 50,
            Width = 70,
            AccessibleName = T("a11y.Fan manual percentage", "Fan manual percentage")
        };
        fanPercentBox.Enter += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Click += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Leave += delegate { ClampFanPercentBox(); };

        showStoppedFansCheckBox = new CheckBox
        {
            Text = T("ui.Show &stopped", "Show &stopped"),
            AutoSize = true,
            AccessibleName = T("a11y.Show stopped or unpopulated fan headers", "Show stopped or unpopulated fan headers")
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
        statusLabel.Text = T("status.refreshingSensors", "Refreshing sensors...");

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
                    if (!IsMinimizedOrHidden())
                    {
                        UpdateFanControlBox();
                        UpdateDeviceList();
                        UpdateReadingList();
                    }
                    UpdateTrayStatus();

                    if (rows.Count == 0)
                    {
                        statusLabel.Text = T("status.noSensorRows", "No sensor rows returned yet.");
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
        rows.AddRange(GetOverviewRows());
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
        var rowCountText = rows.Count + " " + (rows.Count == 1 ? T("status.sensorRowSingular", "sensor row") : T("status.sensorRowPlural", "sensor rows"));
        var status = T("status.updated", "Updated") + " " + DateTime.Now.ToString("HH:mm:ss") + " " + T("status.from", "from") + " " + sources + ". " + rowCountText + ".";
        var hasMotherboard = rows.Any(r => string.Equals(r.Hardware, "Motherboard", StringComparison.OrdinalIgnoreCase));
        var hasFanControls = rows.Any(r => r.Type == "Fan Control");
        if (!hasMotherboard || !hasFanControls)
        {
            status += " " + T("status.fanControlRequirement", "Motherboard fans or controls may require PawnIO, administrator rights, or hardware support.");
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
                RefreshSensorsAfterFanAction();
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
                RefreshSensorsAfterFanAction();
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
                RefreshSensorsAfterFanAction();
            });
    }

    private void RefreshSensorsAfterFanAction()
    {
        ForceUpdateFanControlBox();
        RefreshSensors();
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
        UpdateFanControlBox(false);
    }

    private void ForceUpdateFanControlBox()
    {
        UpdateFanControlBox(true);
    }

    private void UpdateFanControlBox(bool force)
    {
        if (fanControlBox == null || fanLabelBox == null || fanPercentBox == null || showStoppedFansCheckBox == null)
        {
            return;
        }

        if (!force && (fanControlBox.Focused || fanLabelBox.Focused || fanPercentBox.Focused))
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
            var rpmText = rpm.HasValue ? FormatNumber(Math.Round(rpm.Value, 0), "0") + " RPM" : T("value.no RPM reading", "no RPM reading");
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

    private static void HandleShortcutKey(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
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
            return FormatNumber(elapsed.TotalSeconds, "0.00") + " seconds";
        }

        return FormatNumber(elapsed.TotalMilliseconds, "0") + " ms";
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
            (identifier.IndexOf("NVApi", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("/gpu-", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("gpu-nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
            identifier.IndexOf("gpu-amd", StringComparison.OrdinalIgnoreCase) >= 0);
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

            var path = GetLogFilePath();
            EnsureDirectoryForFile(path);
            MigrateProgramDataFiles();
            RotateLogIfNeeded(path);
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + NormalizeLoggingLevel(level) + "] " + message + Environment.NewLine);
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
            var sensorType = sensor.SensorType.ToString();
            var hardwareType = hardware.HardwareType.ToString();
            var isStorage = hardware.HardwareType == HardwareType.Storage;
            var type = sensorType == "Fan" ? "Fan" : sensorType == "Control" && sensor.Control != null ? "Fan Control" : sensorType == "Temperature" ? "Temperature" : isStorage ? "SMART" : "";
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (!sensor.Value.HasValue && type != "Fan Control")
            {
                continue;
            }

            var value = sensor.Value;
            var numericValue = value.GetValueOrDefault();
            if (type == "Temperature" && numericValue <= 0)
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
                DisplayValue = type == "Temperature" ? null : type == "Fan Control" ? FormatLibreHardwareMonitorControlValue(sensor) : isStorage ? FormatLibreHardwareMonitorStorageValue(hardware.Name, sensorType, sensor.Name, numericValue) : null,
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
                DisplayValue = temperature ? null : FormatNumber(Math.Round(value, 0), "0"),
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
                        DisplayValue = FormatNumber(Math.Round(values.Average(), 1), "0.0") + "%",
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
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used", Value = (float)usedPercent, DisplayValue = FormatNumber(Math.Round(usedPercent, 1), "0.0") + "%", Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used size", DisplayValue = FormatBytes(usedKb * 1024.0), Source = "Windows WMI" });
                    rows.Add(new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory available", DisplayValue = FormatBytes(freeKb * 1024.0) + " (" + FormatNumber(Math.Round(availablePercent, 1), "0.0") + "%)", Source = "Windows WMI" });
                    break;
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetOverviewRows()
    {
        var rows = new List<SensorRow>();

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in searcher.Get())
                {
                    var bootTimeText = Convert.ToString(os["LastBootUpTime"]);
                    var bootTime = string.IsNullOrWhiteSpace(bootTimeText) ? DateTime.MinValue : ManagementDateTimeConverter.ToDateTime(bootTimeText);
                    if (bootTime > DateTime.MinValue)
                    {
                        rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "Uptime", DisplayValue = FormatUptime(DateTime.Now - bootTime), Source = "Windows WMI" });
                    }
                    break;
                }
            }
        }
        catch
        {
            rows.Add(new SensorRow { Type = "Performance", Hardware = "Overview", Name = "Uptime", DisplayValue = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount)), Source = "Windows" });
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject system in searcher.Get())
                {
                    AddOverviewTextRow(rows, "System manufacturer", Convert.ToString(system["Manufacturer"]), "Windows WMI");
                    AddOverviewTextRow(rows, "System model", Convert.ToString(system["Model"]), "Windows WMI");
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS"))
            {
                foreach (ManagementObject bios in searcher.Get())
                {
                    AddOverviewTextRow(rows, "BIOS vendor", Convert.ToString(bios["Manufacturer"]), "Windows WMI");
                    var version = Convert.ToString(bios["SMBIOSBIOSVersion"]);
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = Convert.ToString(bios["Version"]);
                    }
                    AddOverviewTextRow(rows, "BIOS version", version, "Windows WMI");
                    AddOverviewTextRow(rows, "BIOS date", FormatWmiDate(bios["ReleaseDate"]), "Windows WMI");
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            {
                foreach (ManagementObject gpu in searcher.Get())
                {
                    var name = Convert.ToString(gpu["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Display adapter";
                    }

                    AddOverviewTextRow(rows, name + " adapter RAM", FormatBytes(GetGpuAdapterMemoryBytes(name, gpu["AdapterRAM"])), "Windows");
                    AddOverviewTextRow(rows, name + " driver version", Convert.ToString(gpu["DriverVersion"]), "Windows WMI");
                    AddOverviewTextRow(rows, name + " driver date", FormatWmiDate(gpu["DriverDate"]), "Windows WMI");

                    string gpuBios;
                    string gpuBiosDate;
                    if (TryGetGpuBiosInfo(name, Convert.ToString(gpu["PNPDeviceID"]), out gpuBios, out gpuBiosDate))
                    {
                        AddOverviewTextRow(rows, name + " BIOS", gpuBios, "Windows registry");
                        AddOverviewTextRow(rows, name + " BIOS date", gpuBiosDate, "Windows registry");
                    }
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddOverviewTextRow(List<SensorRow> rows, string name, string value, string source)
    {
        if (rows == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = "Overview",
            Name = name,
            DisplayValue = value.Trim(),
            Source = source
        });
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalSeconds < 0)
        {
            uptime = TimeSpan.Zero;
        }

        var parts = new List<string>();
        if (uptime.Days > 0)
        {
            parts.Add(uptime.Days + " day" + (uptime.Days == 1 ? "" : "s"));
        }

        if (uptime.Hours > 0 || parts.Count > 0)
        {
            parts.Add(uptime.Hours + " hour" + (uptime.Hours == 1 ? "" : "s"));
        }

        parts.Add(uptime.Minutes + " minute" + (uptime.Minutes == 1 ? "" : "s"));
        return string.Join(", ", parts.ToArray());
    }

    private static string FormatWmiDate(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(text).ToString("yyyy-MM-dd");
        }
        catch
        {
            return "";
        }
    }

    private static bool TryGetGpuBiosInfo(string gpuName, string pnpDeviceId, out string bios, out string biosDate)
    {
        bios = "";
        biosDate = "";
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return false;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                bios = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosString"));
                                biosDate = RegistryValueToString(instanceKey.GetValue("HardwareInformation.BiosDate"));
                                return !string.IsNullOrWhiteSpace(bios) || !string.IsNullOrWhiteSpace(biosDate);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static object GetGpuAdapterMemoryBytes(string gpuName, object fallback)
    {
        var registryBytes = GetGpuAdapterMemoryBytesFromRegistry(gpuName);
        return registryBytes > 0 ? (object)registryBytes : fallback;
    }

    private static ulong GetGpuAdapterMemoryBytesFromRegistry(string gpuName)
    {
        try
        {
            using (var videoKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
            {
                if (videoKey == null)
                {
                    return 0;
                }

                foreach (var adapterKeyName in videoKey.GetSubKeyNames())
                {
                    using (var adapterKey = videoKey.OpenSubKey(adapterKeyName))
                    {
                        if (adapterKey == null)
                        {
                            continue;
                        }

                        foreach (var instanceName in adapterKey.GetSubKeyNames())
                        {
                            using (var instanceKey = adapterKey.OpenSubKey(instanceName))
                            {
                                if (instanceKey == null)
                                {
                                    continue;
                                }

                                var adapterString = RegistryValueToString(instanceKey.GetValue("HardwareInformation.AdapterString"));
                                var driverDesc = RegistryValueToString(instanceKey.GetValue("DriverDesc"));
                                if (!GpuRegistryEntryMatches(gpuName, adapterString, driverDesc))
                                {
                                    continue;
                                }

                                var qwMemory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.qwMemorySize"));
                                if (qwMemory > 0)
                                {
                                    return qwMemory;
                                }

                                var memory = RegistryValueToUInt64(instanceKey.GetValue("HardwareInformation.MemorySize"));
                                if (memory > 0)
                                {
                                    return memory;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static bool GpuRegistryEntryMatches(string gpuName, string adapterString, string driverDesc)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(adapterString) && gpuName.IndexOf(adapterString, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && gpuName.IndexOf(driverDesc, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(adapterString) && adapterString.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(driverDesc) && driverDesc.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string RegistryValueToString(object value)
    {
        if (value == null)
        {
            return "";
        }

        var bytes = value as byte[];
        if (bytes != null)
        {
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }

        var strings = value as string[];
        if (strings != null)
        {
            return string.Join(", ", strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
        }

        return Convert.ToString(value);
    }

    private static ulong RegistryValueToUInt64(object value)
    {
        if (value == null)
        {
            return 0;
        }

        if (value is ulong) return (ulong)value;
        if (value is long) return (ulong)Math.Max(0, (long)value);
        if (value is uint) return (uint)value;
        if (value is int) return (ulong)Math.Max(0, (int)value);

        ulong parsed;
        return ulong.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
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
                var display = usedSpace.DisplayValue + " " + T("value.usedSuffix", "used");
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

    private static string FormatLibreHardwareMonitorStorageValue(string hardwareName, string sensorType, string sensorName, float value)
    {
        if (sensorType == "Temperature")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + " C";
        }

        if (sensorType == "Load")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + "%";
        }

        if (sensorType == "Throughput")
        {
            return FormatBytesPerSecond(value);
        }

        if (sensorType == "Data")
        {
            if (IsLibreHardwareMonitorGigabyteCounter(sensorName))
            {
                return FormatStorageDataCounterGigabytes(hardwareName, sensorName, value);
            }

            return FormatNumber(Math.Round(value, 1), "0.0");
        }

        if (sensorType == "Level")
        {
            return FormatNumber(Math.Round(value, 1), "0.0") + "%";
        }

        if (sensorType == "Factor")
        {
            return FormatNumber(Math.Round(value, 0), "0");
        }

        return FormatNumber(Math.Round(value, 1), "0.0");
    }

    private static string FormatLibreHardwareMonitorControlValue(ISensor sensor)
    {
        var value = sensor.Value.HasValue ? FormatNumber(Math.Round(sensor.Value.Value, 0), "0") + "%" : T("value.unknown", "unknown");
        var mode = sensor.Control == null ? T("value.No direct control", "No direct control") : TranslateControlMode(sensor.Control.ControlMode.ToString());
        return mode + " " + value;
    }

    private static string TranslateControlMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "";
        }

        return T("value." + mode, mode);
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
        if (filters.Count > 0 && !filters.Any(f => f.Key == selectedFilterKey))
        {
            selectedFilterKey = filters[0].Key;
        }

        var currentSignature = deviceList.Items
            .Cast<DeviceFilter>()
            .Select(f => f.Key + "=" + f.DisplayName)
            .ToList();
        var newSignature = filters.Select(f => f.Key + "=" + f.DisplayName).ToList();

        if (currentSignature.SequenceEqual(newSignature))
        {
            if (deviceList.SelectedItem == null && deviceList.Items.Count > 0)
            {
                var selectedIndex = filters.FindIndex(f => f.Key == selectedFilterKey);
                deviceList.SelectedIndex = Math.Max(0, selectedIndex);
            }
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
        yield return new DeviceFilter { Key = "type|Performance", DisplayName = T("type.Performance", "Performance/Overview"), Type = "Performance" };
        yield return new DeviceFilter { Key = "type|Temperature", DisplayName = T("type.Temperature", "Temperatures"), Type = "Temperature" };
        yield return new DeviceFilter { Key = "type|Fan", DisplayName = T("type.Fan", "Fans"), Type = "Fan" };
        yield return new DeviceFilter { Key = "type|SMART", DisplayName = T("type.SMART", "SMART"), Type = "SMART" };
        yield return new DeviceFilter { Key = "type|Network", DisplayName = T("type.Network", "Network"), Type = "Network" };
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
        var filter = deviceList.SelectedItem as DeviceFilter;
        var filterKey = filter == null ? "" : filter.Key ?? "";
        var expandAll = !readingTreeExpansionInitialized || !string.Equals(lastReadingTreeFilterKey, filterKey, StringComparison.Ordinal);
        var expandedKeys = expandAll ? new HashSet<string>() : GetExpandedNodeKeys(readingTree.Nodes);
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

            ApplyExpandedNodeKeys(readingTree.Nodes, expandedKeys, expandAll);
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
            lastReadingTreeFilterKey = filterKey;
            readingTreeExpansionInitialized = true;
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
            selectedMeterProgressBar.AccessibleName = T("a11y.Selected meter", "Selected meter");
            selectedMeterProgressBar.AccessibleDescription = T("a11y.Selected reading is not a percentage meter", "Selected reading is not a percentage meter");
            selectedMeterValueLabel.Text = T("status.noMeterForSelectedReading", "No meter for selected reading.");
            lastSelectedMeterValue = -1;
            lastSelectedMeterLabel = "";
            return;
        }

        var percent = ClampPercent(ExtractPercent(row));
        var value = (int)Math.Round(percent);
        var label = MeterLabel(row);
        var changed = value != lastSelectedMeterValue || !string.Equals(label, lastSelectedMeterLabel, StringComparison.Ordinal);
        selectedMeterProgressBar.Value = value;
        selectedMeterProgressBar.AccessibleName = label + ", " + value + " percent";
        selectedMeterProgressBar.AccessibleDescription = label + ", " + value + " percent";
        selectedMeterValueLabel.Text = label + ": " + value + "%";
        if (changed)
        {
            lastSelectedMeterValue = value;
            lastSelectedMeterLabel = label;
            selectedMeterProgressBar.NotifyAccessibleValueChanged();
        }
    }

    private static string MeterLabel(SensorRow row)
    {
        var name = CleanSensorName(row.Name);
        var hardware = ShortHardwareName(row.Hardware);
        if (string.IsNullOrWhiteSpace(hardware) || hardware.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return DisplayReadingName(name);
        }

        return hardware + ", " + DisplayReadingName(name);
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

    private static void ApplyExpandedNodeKeys(TreeNodeCollection nodes, HashSet<string> expandedKeys, bool expandAll)
    {
        foreach (TreeNode node in nodes)
        {
            if (expandAll || expandedKeys.Contains(node.Name))
            {
                node.Expand();
            }
            else
            {
                node.Collapse();
            }

            ApplyExpandedNodeKeys(node.Nodes, expandedKeys, expandAll);
        }
    }

    private static List<ReadingTreeItem> BuildReadingTree(List<SensorRow> rows, DeviceFilter filter)
    {
        if (rows.Count == 0)
        {
            var loadingText = T("message.refreshingInBackground", "Readings will appear here as the background refresh completes.");
            if (filter != null && !string.IsNullOrWhiteSpace(filter.Type))
            {
                loadingText = DisplayTypeName(filter.Type) + ": " + loadingText;
            }

            return new List<ReadingTreeItem> { new ReadingTreeItem { Key = "empty", Text = loadingText } };
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

        var root = new ReadingTreeItem { Key = "readings", Text = T("type.Readings", "Readings") };
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
        var overviewRows = rows
            .Where(r => string.Equals(r.Hardware, "Overview", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overviewRows.Count > 0)
        {
            var overviewItem = new ReadingTreeItem { Key = "performance|overview", Text = T("group.Overview", "Overview") };
            AddReadingRows(overviewItem, overviewRows);
            parent.Children.Add(overviewItem);
        }

        var systemRows = rows
            .Where(r => IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (systemRows.Count > 0)
        {
            var systemItem = new ReadingTreeItem { Key = "performance|system", Text = T("group.System", "System") };
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
            .Where(r => !IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (storageRows.Count > 0)
        {
            var storageItem = new ReadingTreeItem { Key = "performance|storage", Text = T("group.Storage", "Storage") };
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

    private static bool IsOverviewHardware(string hardware)
    {
        return string.Equals(hardware, "Overview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "BIOS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "GPU", StringComparison.OrdinalIgnoreCase);
    }

    private static int OverviewHardwareSortIndex(string hardware)
    {
        if (string.Equals(hardware, "Overview", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(hardware, "BIOS", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(hardware, "GPU", StringComparison.OrdinalIgnoreCase)) return 3;
        return 10;
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
                Text = DisplayReadingName(row.Name) + ": " + FormatValue(row),
                Row = row
            });
        }
    }

    private static string DisplayReadingName(string name)
    {
        var clean = CleanSensorName(name);
        var translated = T("reading." + clean, clean);
        if (!string.Equals(translated, clean, StringComparison.Ordinal))
        {
            return translated;
        }

        if (clean.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
        {
            return T("reading.Temperature #", "Temperature #") + clean.Substring("Temperature #".Length);
        }

        return translated;
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
            return T("type.Temperature", "Temperatures");
        }

        if (type == "Fan")
        {
            return T("type.Fan", "Fans");
        }

        if (type == "SMART")
        {
            return T("type.SMART", "SMART");
        }

        if (type == "Performance")
        {
            return T("type.Performance", "Performance/Overview");
        }

        if (type == "Network")
        {
            return T("type.Network", "Network");
        }

        return string.IsNullOrWhiteSpace(type) ? T("type.Readings", "Readings") : type;
    }

    public static int ReadingSortIndex(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("Uptime", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Model", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("BIOS vendor", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("BIOS version", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS date", StringComparison.OrdinalIgnoreCase)) return 5;
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

        if (row.Type == "Temperature")
        {
            return FormatTemperature(row.Value.Value);
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            return row.DisplayValue;
        }

        if (row.Type == "Fan")
        {
            return FormatNumber(Math.Round(row.Value.Value, 0), "0") + " RPM";
        }

        if (row.Type == "SMART")
        {
            return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
        }
        return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
    }

    private static string FormatTemperature(float celsius)
    {
        if (string.Equals(activeTemperatureUnit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return FormatNumber(Math.Round((celsius * 9.0 / 5.0) + 32.0, 1), "0.0") + " F";
        }

        return FormatNumber(Math.Round(celsius, 1), "0.0") + " C";
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

        return TrayChoiceLabel(ShortHardwareName(row.Hardware), CleanSensorName(row.Name), row.Type);
    }

    public static string TrayChoiceLabel(string hardware, string name, string type)
    {
        return ShortHardwareName(hardware) + " - " + DisplayReadingName(name) + ": " + DisplayTypeName(type);
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

    private static string T(string key, string fallback)
    {
        return activeLanguage == null ? fallback : activeLanguage.Text(key, fallback);
    }

    public static string DefaultStartupSpeechMessage()
    {
        return T("speech.startupActive", "Sensor Readout active.");
    }

    private static string FormatNumber(double value, string format)
    {
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        var separator = !string.IsNullOrWhiteSpace(activeDecimalSeparator)
            ? activeDecimalSeparator
            : activeLanguage == null ? "" : activeLanguage.DecimalSeparator;
        if (!string.IsNullOrWhiteSpace(separator) && separator != ".")
        {
            text = text.Replace(".", separator);
        }

        return text;
    }

    public static List<LanguageChoice> LoadLanguageChoices()
    {
        var choices = new List<LanguageChoice>();

        try
        {
            var folder = GetLanguagesFolderPath();
            if (!System.IO.Directory.Exists(folder))
            {
                return choices;
            }

            foreach (var path in System.IO.Directory.GetFiles(folder, "*.*")
                .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => string.Equals(System.IO.Path.GetFileName(p), DefaultLanguageFileName, StringComparison.OrdinalIgnoreCase) ? "" : p))
            {
                var fileName = System.IO.Path.GetFileName(path);
                var catalog = LoadLanguage(fileName);
                if (!choices.Any(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    choices.Add(new LanguageChoice { FileName = fileName, DisplayName = catalog.DisplayName, FullPath = path });
                }
            }
        }
        catch
        {
        }

        return choices;
    }

    public static LanguageCatalog LoadLanguage(string fileName)
    {
        fileName = SanitizeLanguageFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = DefaultLanguageFileName;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = System.IO.Path.Combine(GetLanguagesFolderPath(), fileName);
            if (!System.IO.File.Exists(path))
            {
                return LoadFallbackEnglishLanguage();
            }

            values = ReadLanguageFile(path);
            if (values.Count == 0 && !string.Equals(fileName, DefaultLanguageFileName, StringComparison.OrdinalIgnoreCase))
            {
                return LoadFallbackEnglishLanguage();
            }
        }
        catch
        {
            return LoadFallbackEnglishLanguage();
        }

        string displayName;
        if (!values.TryGetValue("language.name", out displayName) || string.IsNullOrWhiteSpace(displayName))
        {
            displayName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        }

        return new LanguageCatalog(fileName, displayName, values);
    }

    private static LanguageCatalog LoadFallbackEnglishLanguage()
    {
        try
        {
            var path = System.IO.Path.Combine(GetLanguagesFolderPath(), DefaultLanguageFileName);
            var values = ReadLanguageFile(path);
            if (values.Count > 0)
            {
                string displayName;
                if (!values.TryGetValue("language.name", out displayName) || string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = "English";
                }

                return new LanguageCatalog(DefaultLanguageFileName, displayName, values);
            }
        }
        catch
        {
        }

        return LanguageCatalog.English();
    }

    private static string BuildLanguageFolderSignature()
    {
        try
        {
            var folder = GetLanguagesFolderPath();
            if (!System.IO.Directory.Exists(folder))
            {
                return "";
            }

            return string.Join("|", System.IO.Directory.GetFiles(folder, "*.*")
                .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .Select(p => System.IO.Path.GetFileName(p) + ":" + System.IO.File.GetLastWriteTimeUtc(p).Ticks + ":" + new System.IO.FileInfo(p).Length)
                .ToArray());
        }
        catch
        {
            return "";
        }
    }

    public static Dictionary<string, string> ReadLanguageFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return values;
            }

            foreach (var rawLine in System.IO.File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, equals).Trim();
                var value = line.Substring(equals + 1).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value.Replace("\\n", Environment.NewLine);
                }
            }
        }
        catch
        {
        }

        return values;
    }

    public static void UpdateLanguageFileValue(string path, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var lines = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path).ToList() : new List<string>();
        var replacement = key.Trim() + "=" + (value ?? "").Replace(Environment.NewLine, "\\n");
        var updated = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#") || line.StartsWith(";") || line.IndexOf('=') <= 0)
            {
                continue;
            }

            var existingKey = line.Substring(0, line.IndexOf('=')).Trim();
            if (existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = replacement;
                updated = true;
                break;
            }
        }

        if (!updated)
        {
            lines.Add(replacement);
        }

        System.IO.File.WriteAllLines(path, lines.ToArray());
    }

    public static void OpenLanguagesFolderStatic(IWin32Window owner)
    {
        var folder = GetLanguagesFolderPath();
        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            Process.Start(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Could not open languages folder: " + ex.Message, "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static string GetLanguagesFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "langs");
    }

    private static string SanitizeLanguageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        fileName = System.IO.Path.GetFileName(fileName.Trim());
        return fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? fileName : "";
    }

    public static string HotKeyTextFromKeyEvent(KeyEventArgs e)
    {
        if (e == null)
        {
            return "";
        }

        var key = e.KeyCode;
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
            key == Keys.LWin || key == Keys.RWin || key == Keys.None)
        {
            return "";
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        parts.Add(KeyToHotKeyPart(key));
        return parts.Count < 2 ? "" : string.Join("+", parts.ToArray());
    }

    public static string NormalizeHotKeyText(string text)
    {
        var hotKey = ParseHotKey(text);
        if (hotKey == null || !hotKey.IsValid)
        {
            return "";
        }

        var parts = new List<string>();
        if ((hotKey.Modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((hotKey.Modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((hotKey.Modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((hotKey.Modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
        parts.Add(KeyToHotKeyPart(hotKey.Key));
        return string.Join("+", parts.ToArray());
    }

    private static GlobalHotKey ParseHotKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hotKey = new GlobalHotKey();
        foreach (var rawPart in text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                hotKey.Modifiers |= NativeMethods.ModControl;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                hotKey.Modifiers |= NativeMethods.ModAlt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                hotKey.Modifiers |= NativeMethods.ModShift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                hotKey.Modifiers |= NativeMethods.ModWin;
            }
            else
            {
                Keys key;
                if (!TryParseHotKeyPart(part, out key))
                {
                    return null;
                }

                hotKey.Key = key;
            }
        }

        return hotKey;
    }

    private static bool TryParseHotKeyPart(string part, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(part))
        {
            return false;
        }

        if (part.Length == 1)
        {
            var ch = char.ToUpperInvariant(part[0]);
            if (ch >= 'A' && ch <= 'Z')
            {
                key = (Keys)ch;
                return true;
            }

            if (ch >= '0' && ch <= '9')
            {
                key = Keys.D0 + (ch - '0');
                return true;
            }
        }

        if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase))
        {
            int number;
            if (int.TryParse(part.Substring(1), out number) && number >= 1 && number <= 24)
            {
                key = Keys.F1 + (number - 1);
                return true;
            }
        }

        return Enum.TryParse(part, true, out key) && key != Keys.None;
    }

    private static string KeyToHotKeyPart(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
        {
            return ((char)('0' + (key - Keys.D0))).ToString();
        }

        if (key >= Keys.A && key <= Keys.Z)
        {
            return key.ToString();
        }

        return key.ToString();
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            MigrateProgramDataFiles();
            var path = GetConfigFilePath();
            if (System.IO.File.Exists(path))
            {
                var loaded = JsonConvert.DeserializeObject<AppSettings>(System.IO.File.ReadAllText(path));
                if (loaded != null)
                {
                    NormalizeSettings(loaded);
                    if (!loaded.LanguagePreferenceInitialized)
                    {
                        SetDefaultLanguagePreference(loaded);
                        SaveSettings(loaded);
                    }
                    return loaded;
                }
            }

            var created = new AppSettings();
            SetDefaultLanguagePreference(created);
            NormalizeSettings(created);
            SaveSettings(created);
            return created;
        }
        catch
        {
        }

        var defaults = new AppSettings();
        SetDefaultLanguagePreference(defaults);
        NormalizeSettings(defaults);
        return defaults;
    }

    private static void SetDefaultLanguagePreference(AppSettings value)
    {
        if (value == null)
        {
            return;
        }

        value.LanguageFile = DetectWindowsLanguageFile();
        value.LanguagePreferenceInitialized = true;
    }

    private static string DetectWindowsLanguageFile()
    {
        foreach (var culture in WindowsLanguageCultures())
        {
            var fileName = LanguageFileForCulture(culture);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var path = System.IO.Path.Combine(GetLanguagesFolderPath(), fileName);
            if (System.IO.File.Exists(path))
            {
                return fileName;
            }
        }

        return System.IO.File.Exists(System.IO.Path.Combine(GetLanguagesFolderPath(), DefaultLanguageFileName)) ? DefaultLanguageFileName : "";
    }

    private static IEnumerable<CultureInfo> WindowsLanguageCultures()
    {
        yield return CultureInfo.CurrentUICulture;
        yield return CultureInfo.InstalledUICulture;
        yield return CultureInfo.CurrentCulture;
    }

    private static string LanguageFileForCulture(CultureInfo culture)
    {
        if (culture == null)
        {
            return "";
        }

        var language = culture.TwoLetterISOLanguageName;
        if (string.Equals(language, "de", StringComparison.OrdinalIgnoreCase))
        {
            return "Deutsch.txt";
        }
        if (string.Equals(language, "it", StringComparison.OrdinalIgnoreCase))
        {
            return "Italiano.txt";
        }
        if (string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase))
        {
            return "Francais.txt";
        }
        if (string.Equals(language, "es", StringComparison.OrdinalIgnoreCase))
        {
            return "Espanol.txt";
        }

        return "";
    }

    public static void SaveSettings(AppSettings value)
    {
        try
        {
            NormalizeSettings(value);
            var path = GetConfigFilePath();
            EnsureDirectoryForFile(path);
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
        value.TemperatureUnit = string.Equals(value.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
        value.DecimalSeparator = string.Equals(value.DecimalSeparator, ",", StringComparison.Ordinal) || string.Equals(value.DecimalSeparator, ".", StringComparison.Ordinal)
            ? value.DecimalSeparator
            : "";
        value.LanguageFile = SanitizeLanguageFileName(value.LanguageFile);
        value.ShowHideHotKey = NormalizeHotKeyText(value.ShowHideHotKey);
        value.SpeakTrayHotKey = NormalizeHotKeyText(value.SpeakTrayHotKey);
        value.StartupSpeechMessage = value.StartupSpeechMessage ?? "";
        if (!string.IsNullOrWhiteSpace(value.ShowHideHotKey) &&
            string.Equals(value.ShowHideHotKey, value.SpeakTrayHotKey, StringComparison.OrdinalIgnoreCase))
        {
            value.SpeakTrayHotKey = "";
        }
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
        return System.IO.Path.Combine(GetConfigFolderPath(), GetConfigFileName());
    }

    private static string GetLogFilePath()
    {
        return System.IO.Path.Combine(GetLogsFolderPath(), System.IO.Path.ChangeExtension(GetConfigFileName(), ".log"));
    }

    private static string GetConfigFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
    }

    private static string GetLogsFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    }

    private static void MigrateProgramDataFiles()
    {
        try
        {
            MoveTopLevelFilesToFolder("*.json", GetConfigFolderPath());
            MoveTopLevelFilesToFolder("*.log", GetLogsFolderPath());
        }
        catch
        {
        }
    }

    private static void MoveTopLevelFilesToFolder(string pattern, string destinationFolder)
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (!System.IO.Directory.Exists(baseDirectory))
        {
            return;
        }

        var files = System.IO.Directory.GetFiles(baseDirectory, pattern, System.IO.SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(destinationFolder);
        foreach (var file in files)
        {
            var destination = System.IO.Path.Combine(destinationFolder, System.IO.Path.GetFileName(file));
            if (System.IO.File.Exists(destination))
            {
                continue;
            }

            System.IO.File.Move(file, destination);
        }
    }

    private static void EnsureDirectoryForFile(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            System.IO.Directory.CreateDirectory(folder);
        }
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

        var selectedRows = GetTrayStatusRows();
        var text = BuildTrayStatusText(selectedRows);
        currentTrayStatusText = text;
        trayIcon.Text = ShortenTrayText(text, selectedRows.Count > 1);
        SetTrayIcon(selectedRows.FirstOrDefault());
    }

    private string BuildCurrentTrayStatusText()
    {
        var rows = GetTrayStatusRows();
        var text = BuildTrayStatusText(rows);
        currentTrayStatusText = text;
        return text;
    }

    private List<SensorRow> GetTrayStatusRows()
    {
        var selectedKeys = settings.TrayItemKeys ?? new List<string>();
        var selectedRows = selectedKeys
            .Select(FindTrayRowByKey)
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

        return selectedRows;
    }

    private SensorRow FindTrayRowByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var exact = latestRows.FirstOrDefault(r => RowSettingsKey(r) == key);
        if (exact != null)
        {
            return exact;
        }

        var parts = key.Split('|');
        if (parts.Length < 3)
        {
            return null;
        }

        var type = parts[0];
        var hardware = parts[1];
        var name = parts[2];
        var identifier = parts.Length > 3 ? parts[3] : "";
        var normalizedHardware = NormalizeHardwareName(hardware);
        return latestRows.FirstOrDefault(r =>
            string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeHardwareName(r.Hardware), normalizedHardware, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(identifier) || string.Equals(r.Identifier ?? "", identifier, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(CleanSensorName(r.Name), CleanSensorName(name), StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildTrayStatusText(List<SensorRow> selectedRows)
    {
        return selectedRows == null || selectedRows.Count == 0
            ? "Sensor Readout"
            : string.Join("; ", selectedRows.Select(ShortTrayReadingText).ToArray());
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
            var value = string.Equals(activeTemperatureUnit, "F", StringComparison.OrdinalIgnoreCase)
                ? (row.Value.Value * 9.0 / 5.0) + 32.0
                : row.Value.Value;
            return FormatNumber(Math.Round(value, 0), "0");
        }

        if (row.Type == "Fan")
        {
            var rpm = row.Value.Value;
            return rpm >= 1000 ? FormatNumber(Math.Round(rpm / 1000, 1), "0.#") + "k" : FormatNumber(Math.Round(rpm, 0), "0");
        }

        var display = FormatNumber(Math.Round(row.Value.Value, 0), "0");
        return display.Length <= 3 ? display : display.Substring(0, 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && lhmComputer != null)
        {
            lhmComputer.Close();
        }

        if (disposing)
        {
            UnregisterGlobalHotKeys();
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

        if (disposing && hotKeyWindow != null)
        {
            hotKeyWindow.Dispose();
        }

        if (disposing && languageTimer != null)
        {
            languageTimer.Dispose();
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

        return FormatNumber(Math.Round(bytes, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
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

        return FormatNumber(Math.Round(bytesPerSecond, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
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

        return FormatNumber(Math.Round(value, unit == 0 ? 0 : 1), unit == 0 ? "0" : "0.0") + " " + units[unit];
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
            return FormatNumber(Math.Round(terabytes, 2), "0.##") + " TB";
        }

        var rounded = Math.Round(gigabytes, 1);
        return FormatNumber(rounded, Math.Abs(rounded % 1) < 0.05 ? "0" : "0.0") + " GB";
    }

    private static string FormatStorageDataCounterGigabytes(string hardwareName, string sensorName, double gigabytes)
    {
        if (gigabytes <= 0)
        {
            return "";
        }

        if (gigabytes > 1024 * 100)
        {
            var samsungValue = FormatSamsungStorageDataCounter(hardwareName, sensorName, gigabytes);
            if (!string.IsNullOrWhiteSpace(samsungValue))
            {
                return samsungValue;
            }

            return FormatNumber(Math.Round(gigabytes, 0), "0") + " raw";
        }

        return FormatGigabytes(gigabytes);
    }

    private static string FormatSamsungStorageDataCounter(string hardwareName, string sensorName, double rawValue)
    {
        if (string.IsNullOrWhiteSpace(hardwareName) ||
            hardwareName.IndexOf("Samsung", StringComparison.OrdinalIgnoreCase) < 0 ||
            string.IsNullOrWhiteSpace(sensorName) ||
            (sensorName.IndexOf("Data Read", StringComparison.OrdinalIgnoreCase) < 0 &&
             sensorName.IndexOf("Data Written", StringComparison.OrdinalIgnoreCase) < 0))
        {
            return "";
        }

        // Samsung NVMe drives commonly expose SMART data-unit counters where one
        // unit is 1000 blocks of 512 bytes. Older SATA-style counters are often
        // raw 512-byte LBAs, which are much larger for the same real byte count.
        var bytes = rawValue >= 1000000000.0 ? rawValue * 512.0 : rawValue * 512000.0;
        var gibibytes = bytes / 1024.0 / 1024.0 / 1024.0;
        return FormatGigabytes(gibibytes);
    }
}

internal static class NativeMethods
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}

internal static class NvdaController
{
    private delegate int NvdaTestIfRunningDelegate();
    private delegate int NvdaSpeakTextDelegate([MarshalAs(UnmanagedType.LPWStr)] string text);

    private static NvdaTestIfRunningDelegate testIfRunning;
    private static NvdaSpeakTextDelegate speakText;
    private static bool loadAttempted;
    private static string loadError = "NVDA controller client DLL not loaded.";

    public static bool TrySpeak(string text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Sensor Readout";
        }

        if (!EnsureLoaded(out error))
        {
            return false;
        }

        try
        {
            var running = testIfRunning == null ? 0 : testIfRunning();
            if (running != 0)
            {
                error = "NVDA does not appear to be running.";
                return false;
            }

            var result = speakText(text);
            if (result != 0)
            {
                error = "NVDA returned error code " + result + ".";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool EnsureLoaded(out string error)
    {
        if (speakText != null)
        {
            error = "";
            return true;
        }

        if (loadAttempted)
        {
            error = loadError;
            return false;
        }

        loadAttempted = true;
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            System.IO.Path.Combine(baseDirectory, "nvdaControllerClient.dll"),
            System.IO.Path.Combine(baseDirectory, "nvdaControllerClient64.dll")
        };

        foreach (var candidate in candidates)
        {
            if (!System.IO.File.Exists(candidate))
            {
                continue;
            }

            var module = NativeMethods.LoadLibrary(candidate);
            if (module == IntPtr.Zero)
            {
                continue;
            }

            var speakPointer = NativeMethods.GetProcAddress(module, "nvdaController_speakText");
            var testPointer = NativeMethods.GetProcAddress(module, "nvdaController_testIfRunning");
            if (speakPointer == IntPtr.Zero)
            {
                continue;
            }

            speakText = (NvdaSpeakTextDelegate)Marshal.GetDelegateForFunctionPointer(speakPointer, typeof(NvdaSpeakTextDelegate));
            if (testPointer != IntPtr.Zero)
            {
                testIfRunning = (NvdaTestIfRunningDelegate)Marshal.GetDelegateForFunctionPointer(testPointer, typeof(NvdaTestIfRunningDelegate));
            }

            error = "";
            return true;
        }

        loadError = "Place nvdaControllerClient.dll beside Sensor Readout.exe to enable NVDA speech.";
        error = loadError;
        return false;
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
