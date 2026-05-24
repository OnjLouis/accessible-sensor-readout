using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    public event EventHandler LivePreferencesSaved;

    private readonly CheckBox autoRefreshCheckBox;
    private readonly CheckBox refreshWhileFocusedCheckBox;
    private readonly CheckBox trayStatusCheckBox;
    private readonly CheckBox trayTooltipPartialReadingsCheckBox;
    private readonly CheckBox runAtStartupCheckBox;
    private readonly CheckBox desktopShortcutCheckBox;
    private readonly CheckBox startMinimizedCheckBox;
    private readonly ComboBox updateCheckFrequencyBox;
    private readonly CheckBox installUpdatesQuietlyCheckBox;
    private readonly CheckBox showUpdateInstallConfirmationCheckBox;
    private readonly CheckBox confirmSpokenHotKeyProfileRemovalCheckBox;
    private readonly ComboBox updateAvailableSoundBox;
    private readonly CheckBox diagnosticsSpeakProgressCheckBox;
    private readonly CheckBox diagnosticsPlaySoundsCheckBox;
    private readonly ComboBox diagnosticsStartSoundBox;
    private readonly ComboBox diagnosticsCompleteSoundBox;
    private readonly NumericUpDown refreshSecondsBox;
    private readonly ComboBox temperatureUnitBox;
    private readonly ComboBox decimalSeparatorBox;
    private readonly ComboBox languageBox;
    private readonly Label languageFolderStatusLabel;
    private readonly TextBox showHideHotKeyBox;
    private readonly TextBox speakTrayHotKeyBox;
    private readonly ComboBox hotKeyCopyDoublePressBox;
    private readonly CheckBox startupSpeechEnabledCheckBox;
    private readonly TextBox startupSpeechMessageBox;
    private readonly CheckBox speechIncludesDeviceNamesCheckBox;
    private readonly ComboBox loggingLevelBox;
    private readonly TabControl preferencesTabs;
    private ComboBox languageEditorFileBox;
    private ListBox languageEntryList;
    private TextBox languageEntryValueBox;
    private readonly ListBox trayAvailableList;
    private readonly ListBox traySelectedList;
    private readonly Label traySelectionStatusLabel;
    private readonly ListBox spokenHotKeyList;
    private readonly TextBox spokenHotKeyNameBox;
    private readonly TextBox spokenHotKeyBox;
    private readonly ListBox spokenAvailableList;
    private readonly ListBox spokenSelectedList;
    private readonly Label spokenSelectionStatusLabel;
    private readonly ListBox fanProfileList;
    private readonly TextBox fanProfileNameBox;
    private readonly TextBox fanProfileHotKeyBox;
    private readonly CheckBox fanProfileToggleBox;
    private readonly CheckBox fanProfileSpeakBox;
    private readonly TextBox fanProfileSpeechMessageBox;
    private readonly CheckBox fanProfileShowStoppedBox;
    private readonly ListBox fanProfileAvailableList;
    private readonly ListBox fanProfileSelectedList;
    private readonly ComboBox fanProfileActionBox;
    private readonly NumericUpDown fanProfilePercentBox;
    private readonly ComboBox fanProfileSoundBox;
    private readonly Label fanProfileStatusLabel;
    private readonly ListBox alarmList;
    private readonly CheckBox alarmEnabledCheckBox;
    private readonly TextBox alarmNameBox;
    private readonly ComboBox alarmReadingBox;
    private readonly ComboBox alarmConditionBox;
    private readonly NumericUpDown alarmThresholdBox;
    private readonly ComboBox alarmThresholdUnitBox;
    private readonly NumericUpDown alarmCooldownBox;
    private readonly CheckBox alarmSpeakCheckBox;
    private readonly ComboBox alarmSoundBox;
    private readonly ComboBox startupSoundBox;
    private readonly ComboBox shutdownSoundBox;
    private readonly Label alarmStatusLabel;
    private readonly CheckedListBox plugInList;
    private readonly Label plugInDetailsLabel;
    private readonly CheckedListBox hiddenItemsList;
    private readonly List<SensorRow> latestSensorRows;
    private readonly List<SensorRow> rows;
    private readonly List<SensorRow> fanControlRows;
    private string rowsSignature = "";
    private string fanControlRowsSignature = "";
    private readonly List<string> hiddenReadingKeys;
    private readonly List<SpokenHotKeySetting> spokenHotKeys;
    private readonly List<FanProfileSetting> fanProfiles;
    private readonly List<AlarmSetting> alarms;
    private readonly List<PlugInPreferenceInfo> plugIns;
    private readonly List<string> soundFiles;
    private readonly Dictionary<string, string> readingSpeechLabels;
    private readonly Dictionary<string, string> fanLabels;
    private readonly AppSettings liveSettings;
    private readonly List<string> originalTrayItemKeys;
    private readonly Dictionary<object, string> originalUiText = new Dictionary<object, string>();
    private readonly Dictionary<object, string> originalAccessibleNames = new Dictionary<object, string>();
    private readonly Dictionary<object, string> originalAccessibleDescriptions = new Dictionary<object, string>();
    private readonly Dictionary<ListBox, ListSearchState> listSearchStates = new Dictionary<ListBox, ListSearchState>();
    private bool loadingPreferences;
    private bool fanProfileStarterProfilesInitialized;
    private string lastAlarmReadingKey = "";

    public bool AutoRefreshEnabled { get { return autoRefreshCheckBox.Checked; } }
    public bool RefreshWhileFocused { get { return refreshWhileFocusedCheckBox.Checked; } }
    public bool TrayStatusEnabled { get { return trayStatusCheckBox.Checked; } }
    public bool TrayTooltipShowsPartialReadings { get { return trayTooltipPartialReadingsCheckBox == null || trayTooltipPartialReadingsCheckBox.Checked; } }
    public bool RunAtStartup { get { return runAtStartupCheckBox.Checked; } }
    public bool StartMinimizedToTray { get { return startMinimizedCheckBox.Checked; } }
    public bool CheckForUpdatesAtStartup { get { return UpdateCheckFrequency != "Never"; } }
    public string UpdateCheckFrequency { get { return UpdateCheckFrequencyFromIndex(updateCheckFrequencyBox.SelectedIndex); } }
    public bool InstallUpdatesQuietly { get { return installUpdatesQuietlyCheckBox != null && installUpdatesQuietlyCheckBox.Checked; } }
    public bool ShowUpdateInstallConfirmation { get { return showUpdateInstallConfirmationCheckBox == null || showUpdateInstallConfirmationCheckBox.Checked; } }
    public bool ConfirmSpokenHotKeyProfileRemoval { get { return confirmSpokenHotKeyProfileRemovalCheckBox == null || confirmSpokenHotKeyProfileRemovalCheckBox.Checked; } }
    public string UpdateAvailableSoundFile { get { return SelectedSoundFile(updateAvailableSoundBox); } }
    public bool DiagnosticsSpeakProgress { get { return diagnosticsSpeakProgressCheckBox == null || diagnosticsSpeakProgressCheckBox.Checked; } }
    public bool DiagnosticsPlaySounds { get { return diagnosticsPlaySoundsCheckBox == null || diagnosticsPlaySoundsCheckBox.Checked; } }
    public string DiagnosticsStartSoundFile { get { return SelectedSoundFile(diagnosticsStartSoundBox); } }
    public string DiagnosticsCompleteSoundFile { get { return SelectedSoundFile(diagnosticsCompleteSoundBox); } }
    public int RefreshIntervalSeconds { get { return Convert.ToInt32(refreshSecondsBox.Value); } }
    public string TemperatureUnit { get { return SensorReadoutForm.TemperatureUnitFromIndex(temperatureUnitBox.SelectedIndex); } }
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
    public int HotKeyCopyDoublePressMs { get { return HotKeyCopyDoublePressMsFromIndex(hotKeyCopyDoublePressBox.SelectedIndex); } }
    public bool StartupSpeechEnabled { get { return startupSpeechEnabledCheckBox.Checked; } }
    public string StartupSpeechMessage
    {
        get
        {
            var text = startupSpeechMessageBox.Text.Trim();
            return string.Equals(text, SensorReadoutForm.DefaultStartupSpeechMessage(), StringComparison.Ordinal) ? "" : text;
        }
    }
    public bool SpeechIncludesDeviceNames { get { return speechIncludesDeviceNamesCheckBox.Checked; } }
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
    public List<SpokenHotKeySetting> SpokenHotKeys { get; private set; }
    public List<FanProfileSetting> FanProfiles { get; private set; }
    public List<AlarmSetting> Alarms { get; private set; }
    public List<string> HiddenReadingKeys { get; private set; }
    public Dictionary<string, string> ReadingSpeechLabels { get; private set; }
    public Dictionary<string, bool> PlugInsEnabled { get { return CurrentPlugInSettings(); } }
    public string StartupSoundFile { get { return SelectedSoundFile(startupSoundBox); } }
    public string ShutdownSoundFile { get { return SelectedSoundFile(shutdownSoundBox); } }
    public string SelectedTabName
    {
        get
        {
            return preferencesTabs != null && preferencesTabs.SelectedTab != null
                ? preferencesTabs.SelectedTab.Name
                : "General";
        }
    }

    public PreferencesForm(AppSettings settings, List<SensorRow> latestRows, List<LanguageChoice> languageChoices)
        : this(settings, latestRows, languageChoices, "General")
    {
    }

    public PreferencesForm(AppSettings settings, List<SensorRow> latestRows, List<LanguageChoice> languageChoices, string initialTabName)
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
        spokenHotKeys = CloneSpokenHotKeys(settings.SpokenHotKeys);
        fanProfiles = CloneFanProfiles(settings.FanProfiles);
        fanProfileStarterProfilesInitialized = settings.FanProfileStarterProfilesInitialized;
        if (!fanProfileStarterProfilesInitialized)
        {
            if (fanProfiles.Count == 0)
            {
                AddStarterFanProfiles(fanProfiles);
            }
            fanProfileStarterProfilesInitialized = true;
        }
        alarms = CloneAlarms(settings.Alarms);
        plugIns = SensorReadoutForm.LoadPlugInPreferenceInfos(settings);
        soundFiles = SensorReadoutForm.LoadSoundFileNames();
        readingSpeechLabels = new Dictionary<string, string>(settings.ReadingSpeechLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        fanLabels = new Dictionary<string, string>(settings.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        latestSensorRows = (latestRows ?? new List<SensorRow>()).Where(r => r != null).ToList();

        rows = latestSensorRows
            .Where(SensorReadoutForm.IsSelectableReadoutRow)
            .OrderBy(r => SensorReadoutForm.TypeSortIndex(r.Type))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();
        rowsSignature = BuildRowsSignature(rows);
        fanControlRows = BuildFanProfileFanControlRows(latestSensorRows);
        fanControlRowsSignature = BuildRowsSignature(fanControlRows);

        preferencesTabs = new TabControl { Dock = DockStyle.Fill };
        var generalTab = new TabPage("General") { Name = "General" };
        var startupTab = new TabPage("Startup and Install") { Name = "Startup" };
        var hotKeysTab = new TabPage("Hotkeys") { Name = "Hotkeys" };
        var fanProfilesTab = new TabPage("Fan profiles") { Name = "Fan profiles" };
        var alarmsTab = new TabPage("Alarms") { Name = "Alarms" };
        var plugInsTab = new TabPage("Plug-Ins") { Name = "Plug-Ins" };
        var hiddenTab = new TabPage("Hidden items") { Name = "Hidden items" };
        var languageEditorTab = new TabPage("Language editor") { Name = "Language editor" };

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

        trayTooltipPartialReadingsCheckBox = new CheckBox
        {
            Text = "Show as many &readings as possible in notification area tooltip",
            Checked = settings.TrayTooltipShowsPartialReadings,
            AutoSize = true,
            AccessibleName = "Show as many readings as possible in notification area tooltip",
            AccessibleDescription = "When checked, a long notification area tooltip shows as many configured readings as Windows allows, followed by three dots. When unchecked, long tooltips only show Sensor Readout."
        };

        trayStatusCheckBox.CheckedChanged += delegate
        {
            trayTooltipPartialReadingsCheckBox.Enabled = trayStatusCheckBox.Checked;
        };
        trayTooltipPartialReadingsCheckBox.Enabled = trayStatusCheckBox.Checked;

        runAtStartupCheckBox = new CheckBox
        {
            Text = "Run at Windows startup",
            Checked = settings.RunAtStartup,
            AutoSize = true,
            AccessibleName = "Run at Windows startup"
        };

        desktopShortcutCheckBox = new CheckBox
        {
            Text = "Create desktop shortcut",
            Checked = SensorReadoutForm.DesktopShortcutExists(),
            AutoSize = true,
            AccessibleName = "Create desktop shortcut"
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
                desktopShortcutCheckBox.Checked = true;
            }
        };

        startMinimizedCheckBox.CheckedChanged += delegate
        {
            if (startMinimizedCheckBox.Checked)
            {
                trayStatusCheckBox.Checked = true;
                PromptForShowHideHotKeyIfNeeded();
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
            Minimum = 1,
            Maximum = 300,
            Value = Math.Max(1, Math.Min(300, settings.RefreshIntervalSeconds)),
            Width = 70,
            AccessibleName = "Refresh interval in seconds"
        };
        AttachNumericAutoSelect(refreshSecondsBox);
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
        temperatureUnitBox.Items.AddRange(new object[] { "Celsius (C)", "Fahrenheit (F)", "Celsius, then Fahrenheit", "Fahrenheit, then Celsius" });
        temperatureUnitBox.SelectedIndex = SensorReadoutForm.TemperatureUnitIndex(settings.TemperatureUnit);
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

        var updatesPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4
        };
        updatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        updatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        updatesPanel.Controls.Add(new Label { Text = "Updates", AutoSize = true, Padding = new Padding(0, 8, 0, 2), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        updatesPanel.SetColumnSpan(updatesPanel.Controls[0], 2);
        updatesPanel.Controls.Add(new Label { Text = "Check for updates:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);
        updateCheckFrequencyBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            AccessibleName = "Check for updates",
            AccessibleDescription = "Choose how often Sensor Readout checks GitHub releases. Automatic checks are silent unless a newer version is available."
        };
        updateCheckFrequencyBox.Items.AddRange(UpdateCheckFrequencyOptions().Cast<object>().ToArray());
        updateCheckFrequencyBox.SelectedIndex = UpdateCheckFrequencyIndex(settings.UpdateCheckFrequency);
        updatesPanel.Controls.Add(updateCheckFrequencyBox, 1, 1);
        installUpdatesQuietlyCheckBox = new CheckBox
        {
            Text = "Install updates &quietly",
            Checked = settings.InstallUpdatesQuietly,
            AutoSize = true,
            AccessibleName = "Install updates quietly",
            AccessibleDescription = "When checked, an available update is downloaded, installed, and reopened without first showing release notes or asking for confirmation."
        };
        updatesPanel.Controls.Add(installUpdatesQuietlyCheckBox, 1, 2);
        updatesPanel.Controls.Add(new Label { Text = "Update available sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
        updateAvailableSoundBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            AccessibleName = "Update available sound"
        };
        PopulateSoundCombo(updateAvailableSoundBox, settings.UpdateAvailableSoundFile);
        updatesPanel.Controls.Add(updateAvailableSoundBox, 1, 3);

        var diagnosticsPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5
        };
        diagnosticsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        diagnosticsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        diagnosticsPanel.Controls.Add(new Label { Text = "Diagnostics", AutoSize = true, Padding = new Padding(0, 8, 0, 2), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        diagnosticsPanel.SetColumnSpan(diagnosticsPanel.Controls[0], 2);
        diagnosticsSpeakProgressCheckBox = new CheckBox
        {
            Text = "Speak diagnostic progress",
            Checked = settings.DiagnosticsSpeakProgress,
            AutoSize = true,
            AccessibleName = "Speak diagnostic progress",
            AccessibleDescription = "When checked, diagnostic runs speak each step and say Complete when finished."
        };
        diagnosticsPanel.Controls.Add(diagnosticsSpeakProgressCheckBox, 0, 1);
        diagnosticsPanel.SetColumnSpan(diagnosticsSpeakProgressCheckBox, 2);
        diagnosticsPlaySoundsCheckBox = new CheckBox
        {
            Text = "Play diagnostic sounds",
            Checked = settings.DiagnosticsPlaySounds,
            AutoSize = true,
            AccessibleName = "Play diagnostic sounds",
            AccessibleDescription = "When checked, diagnostic runs play a sound at the start and when complete."
        };
        diagnosticsPanel.Controls.Add(diagnosticsPlaySoundsCheckBox, 0, 2);
        diagnosticsPanel.SetColumnSpan(diagnosticsPlaySoundsCheckBox, 2);
        diagnosticsPanel.Controls.Add(new Label { Text = "Start sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
        diagnosticsStartSoundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, AccessibleName = "Diagnostic start sound" };
        PopulateSoundCombo(diagnosticsStartSoundBox, settings.DiagnosticsStartSoundFile);
        diagnosticsPanel.Controls.Add(diagnosticsStartSoundBox, 1, 3);
        diagnosticsPanel.Controls.Add(new Label { Text = "Complete sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 4);
        diagnosticsCompleteSoundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, AccessibleName = "Diagnostic complete sound" };
        PopulateSoundCombo(diagnosticsCompleteSoundBox, settings.DiagnosticsCompleteSoundFile);
        diagnosticsPanel.Controls.Add(diagnosticsCompleteSoundBox, 1, 4);
        diagnosticsStartSoundBox.Enabled = diagnosticsPlaySoundsCheckBox.Checked;
        diagnosticsCompleteSoundBox.Enabled = diagnosticsPlaySoundsCheckBox.Checked;

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
        var clearShowHideButton = new Button { Text = "&Clear", AutoSize = true };
        clearShowHideButton.Click += delegate { showHideHotKeyBox.Text = ""; };
        hotKeyPanel.Controls.Add(clearShowHideButton, 2, 0);
        hotKeyPanel.Controls.Add(new Label { Text = "Speak tray status hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);
        speakTrayHotKeyBox = CreateHotKeyBox(settings.SpeakTrayHotKey, "Speak notification area status global hotkey");
        hotKeyPanel.Controls.Add(speakTrayHotKeyBox, 1, 1);
        var clearSpeakButton = new Button { Text = "&Clear", AutoSize = true };
        clearSpeakButton.Click += delegate { speakTrayHotKeyBox.Text = ""; };
        hotKeyPanel.Controls.Add(clearSpeakButton, 2, 1);
        hotKeyPanel.Controls.Add(new Label { Text = "Double-press hotkey copies:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
        hotKeyCopyDoublePressBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, AccessibleName = "Double-press hotkey copies" };
        hotKeyCopyDoublePressBox.Items.AddRange(HotKeyCopyDoublePressOptions().Cast<object>().ToArray());
        hotKeyCopyDoublePressBox.SelectedIndex = SafeComboIndex(hotKeyCopyDoublePressBox, HotKeyCopyDoublePressIndex(settings.HotKeyCopyDoublePressMs));
        hotKeyPanel.Controls.Add(hotKeyCopyDoublePressBox, 1, 2);

        var startupSpeechPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        startupSpeechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        startupSpeechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        startupSpeechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        startupSpeechEnabledCheckBox = new CheckBox
        {
            Text = "Speak startup message",
            Checked = settings.StartupSpeechEnabled,
            AutoSize = true,
            AccessibleName = "Speak startup message",
            AccessibleDescription = "When checked, Sensor Readout speaks the startup message through the active screen reader."
        };
        startupSpeechPanel.Controls.Add(new Label { Text = "Spoken message:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        startupSpeechMessageBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(settings.StartupSpeechMessage) ? SensorReadoutForm.DefaultStartupSpeechMessage() : settings.StartupSpeechMessage,
            Dock = DockStyle.Fill,
            AccessibleName = "Startup speech message",
            AccessibleDescription = "Message spoken by the screen reader when Sensor Readout starts minimized to the notification area."
        };
        startupSpeechPanel.Controls.Add(startupSpeechMessageBox, 1, 0);
        var resetStartupSpeechButton = new Button { Text = "&Reset", AutoSize = true };
        resetStartupSpeechButton.Click += delegate { startupSpeechMessageBox.Text = SensorReadoutForm.DefaultStartupSpeechMessage(); };
        startupSpeechPanel.Controls.Add(resetStartupSpeechButton, 2, 0);
        startupSpeechMessageBox.Enabled = startupSpeechEnabledCheckBox.Checked;
        resetStartupSpeechButton.Enabled = startupSpeechEnabledCheckBox.Checked;

        var startupSoundPanel = BuildSoundPickerPanel("Startup sound:", settings.StartupSoundFile, out startupSoundBox);
        var shutdownSoundPanel = BuildSoundPickerPanel("Shutdown sound:", settings.ShutdownSoundFile, out shutdownSoundBox);

        speechIncludesDeviceNamesCheckBox = new CheckBox
        {
            Text = "Include device names in spoken feedback",
            Checked = settings.SpeechIncludesDeviceNames,
            AutoSize = true,
            AccessibleName = "Include device names in spoken feedback",
            AccessibleDescription = "When checked, spoken status includes device names such as WiFi or CPU before each selected reading."
        };

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
            Text = "Notification area items. Maximum eight readings. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.",
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
            AccessibleDescription = "Press F2 to rename the spoken label. Press Control Left Arrow to remove the selected reading. Press Control Up or Control Down to change the order."
        };
        trayAvailableList.KeyDown += TrayAvailableListKeyDown;
        traySelectedList.KeyDown += TraySelectedListKeyDown;
        AttachIncrementalListSearch(trayAvailableList);
        AttachIncrementalListSearch(traySelectedList);
        var trayChoices = rows
            .Select(r => new TrayItemChoice(r, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames))
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
                selectedTrayChoice.ShowSpeechPreview = true;
                traySelectedList.Items.Add(selectedTrayChoice);
            }
            else if (selectedTrayChoice == null && !ContainsTrayChoice(traySelectedList, key))
            {
                var unresolved = TrayItemChoice.Unresolved(key, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames);
                unresolved.ShowSpeechPreview = true;
                traySelectedList.Items.Add(unresolved);
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

        spokenHotKeyList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Spoken hotkeys",
            AccessibleDescription = "Choose a spoken hotkey profile to edit."
        };
        spokenHotKeyNameBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Spoken hotkey name",
            AccessibleDescription = "Friendly name for this spoken hotkey."
        };
        spokenHotKeyBox = CreateHotKeyBox("", "Spoken hotkey key combination");
        spokenAvailableList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Available readings for spoken hotkey",
            AccessibleDescription = "Press Control Right Arrow to add the selected reading to this spoken hotkey."
        };
        spokenSelectedList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Readings spoken by this hotkey",
            AccessibleDescription = "Press F2 to rename the spoken label. Press Control Left Arrow to remove the selected reading. Press Control Up or Control Down to change the order."
        };
        spokenSelectionStatusLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            AccessibleName = "Spoken hotkey selection status"
        };
        fanProfileList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Fan profiles",
            AccessibleDescription = "Choose a fan profile to edit."
        };
        fanProfileNameBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Fan profile name",
            AccessibleDescription = "Friendly name for this fan profile."
        };
        fanProfileHotKeyBox = CreateHotKeyBox("", "Fan profile key combination");
        fanProfileToggleBox = new CheckBox
        {
            Text = "Toggle back to automatic when pressed again",
            AutoSize = true,
            AccessibleName = "Toggle fan profile back to automatic"
        };
        fanProfileSpeakBox = new CheckBox
        {
            Text = SensorReadoutForm.L("ui.Speak when profile changes", "Speak when profile changes"),
            Checked = true,
            AutoSize = true,
            AccessibleName = SensorReadoutForm.L("a11y.Speak when fan profile changes", "Speak when fan profile changes")
        };
        fanProfileSpeechMessageBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = SensorReadoutForm.L("a11y.Fan profile spoken message", "Fan profile spoken message"),
            AccessibleDescription = SensorReadoutForm.L("a11y.Optional message spoken when this fan profile is switched. Use {0} for the profile name.", "Optional message spoken when this fan profile is switched. Use {0} for the profile name.")
        };
        fanProfileShowStoppedBox = new CheckBox
        {
            Text = SensorReadoutForm.L("ui.Show stopped or hidden fan controls", "Show stopped or hidden fan controls"),
            Checked = settings.ShowStoppedFans,
            AutoSize = true,
            AccessibleName = SensorReadoutForm.L("a11y.Show stopped or hidden fan controls", "Show stopped or hidden fan controls")
        };
        fanProfileSoundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Fan profile sound" };
        PopulateSoundCombo(fanProfileSoundBox, "");
        fanProfileAvailableList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Available fan controls",
            AccessibleDescription = "Press Control Right Arrow to add the selected fan control to this fan profile."
        };
        fanProfileSelectedList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Fan controls in this profile",
            AccessibleDescription = "Press Delete or Control Left Arrow to remove. Press Control Up or Control Down to change the order."
        };
        fanProfileActionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, AccessibleName = "Fan profile action" };
        fanProfileActionBox.Items.AddRange(new object[] { "Manual", "Auto" });
        fanProfileActionBox.SelectedIndex = 0;
        fanProfilePercentBox = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 50, Width = 70, AccessibleName = "Fan profile percent" };
        AttachNumericAutoSelect(fanProfilePercentBox);
        fanProfileStatusLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            AccessibleName = "Fan profile status"
        };
        alarmList = new NotifyingListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Alarms"
        };
        alarmEnabledCheckBox = new CheckBox { Text = "Enabled", Checked = true, AutoSize = true };
        alarmNameBox = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Alarm name" };
        alarmReadingBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Alarm reading" };
        alarmConditionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Alarm condition" };
        alarmConditionBox.Items.AddRange(new object[] { "Above or equal", "Below or equal", "Equal" });
        alarmThresholdBox = new NumericUpDown { DecimalPlaces = 1, Minimum = -1000000, Maximum = 1000000, Value = 80, Dock = DockStyle.Fill, AccessibleName = "Alarm threshold" };
        AttachNumericAutoSelect(alarmThresholdBox);
        alarmThresholdUnitBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, AccessibleName = "Alarm threshold unit" };
        alarmCooldownBox = new NumericUpDown { Minimum = 0, Maximum = 86400, Value = 60, Dock = DockStyle.Fill, AccessibleName = "Alarm cooldown seconds" };
        AttachNumericAutoSelect(alarmCooldownBox);
        alarmSpeakCheckBox = new CheckBox { Text = "Speak with screen reader", Checked = true, AutoSize = true };
        alarmSoundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Alarm sound" };
        PopulateSoundCombo(alarmSoundBox, "");
        alarmStatusLabel = new Label { AutoSize = true, Dock = DockStyle.Fill };
        plugInList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            AccessibleName = SensorReadoutForm.L("a11y.Installed Plug-Ins", "Installed Plug-Ins"),
            AccessibleDescription = SensorReadoutForm.L("a11y.Each item includes the Plug-In name and what it does. Check a Plug-In to enable it. Changes apply after closing Preferences.", "Each item includes the Plug-In name and what it does. Check a Plug-In to enable it. Changes apply after closing Preferences.")
        };
        plugInDetailsLabel = new Label { AutoSize = true, Dock = DockStyle.Fill };
        plugInList.SelectedIndexChanged += delegate { UpdatePlugInDetails(); };
        plugInList.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
        {
            if (loadingPreferences)
            {
                return;
            }

            BeginInvoke((MethodInvoker)delegate
            {
                SaveLivePreferences();
                UpdatePlugInDetails();
            });
        };
        spokenHotKeyList.SelectedIndexChanged += delegate { LoadSelectedSpokenHotKey(); };
        spokenHotKeyNameBox.TextChanged += delegate { SaveSelectedSpokenHotKeyHeader(); };
        spokenHotKeyBox.TextChanged += delegate { SaveSelectedSpokenHotKeyHeader(); };
        fanProfileList.SelectedIndexChanged += delegate { LoadSelectedFanProfile(); };
        fanProfileNameBox.TextChanged += delegate { SaveSelectedFanProfileHeader(); };
        fanProfileHotKeyBox.TextChanged += delegate { SaveSelectedFanProfileHeader(); };
        fanProfileToggleBox.CheckedChanged += delegate { SaveSelectedFanProfileHeader(); };
        fanProfileSpeakBox.CheckedChanged += delegate { SaveSelectedFanProfileHeader(); };
        fanProfileSpeechMessageBox.TextChanged += delegate { SaveSelectedFanProfileHeader(); };
        fanProfileShowStoppedBox.CheckedChanged += delegate { SaveShowStoppedFansPreference(); };
        fanProfileSoundBox.SelectedIndexChanged += delegate
        {
            SaveSelectedFanProfileHeader();
            PreviewSelectedSound(fanProfileSoundBox);
        };
        fanProfileActionBox.SelectedIndexChanged += delegate { SaveSelectedFanProfileAction(); };
        fanProfilePercentBox.ValueChanged += delegate { SaveSelectedFanProfileAction(); };
        fanProfileSelectedList.SelectedIndexChanged += delegate { LoadSelectedFanProfileAction(); };
        alarmList.SelectedIndexChanged += delegate { LoadSelectedAlarm(); };
        alarmEnabledCheckBox.CheckedChanged += delegate { SaveSelectedAlarm(false); };
        alarmNameBox.TextChanged += delegate { SaveSelectedAlarm(false); };
        alarmReadingBox.SelectedIndexChanged += delegate
        {
            if (loadingPreferences)
            {
                return;
            }

            var readingChanged = !string.Equals(lastAlarmReadingKey, SelectedAlarmReadingKey(), StringComparison.OrdinalIgnoreCase);
            RefreshAlarmThresholdUnitChoices(readingChanged);
            SaveSelectedAlarm(false);
            lastAlarmReadingKey = SelectedAlarmReadingKey();
        };
        alarmConditionBox.SelectedIndexChanged += delegate { SaveSelectedAlarm(false); };
        alarmThresholdBox.ValueChanged += delegate { SaveSelectedAlarm(false); };
        alarmThresholdUnitBox.SelectedIndexChanged += delegate
        {
            if (loadingPreferences)
            {
                return;
            }

            RefreshAlarmThresholdForSelectedUnit();
            SaveSelectedAlarm(false);
        };
        alarmCooldownBox.ValueChanged += delegate { SaveSelectedAlarm(false); };
        alarmSpeakCheckBox.CheckedChanged += delegate { SaveSelectedAlarm(false); };
        alarmSoundBox.SelectedIndexChanged += delegate
        {
            SaveSelectedAlarm(false);
            PreviewSelectedSound(alarmSoundBox);
        };
        alarmList.KeyDown += AlarmListKeyDown;
        alarmList.Enter += delegate { RefreshSelectedAlarmListItem(); };
        spokenHotKeyList.KeyDown += SpokenHotKeyListKeyDown;
        spokenAvailableList.KeyDown += SpokenAvailableListKeyDown;
        spokenSelectedList.KeyDown += SpokenSelectedListKeyDown;
        fanProfileList.KeyDown += FanProfileListKeyDown;
        fanProfileAvailableList.KeyDown += FanProfileAvailableListKeyDown;
        fanProfileSelectedList.KeyDown += FanProfileSelectedListKeyDown;
        AttachIncrementalListSearch(spokenAvailableList);
        AttachIncrementalListSearch(spokenSelectedList);
        AttachIncrementalListSearch(fanProfileAvailableList);
        AttachIncrementalListSearch(fanProfileSelectedList);
        foreach (var profile in SortedSpokenHotKeyProfiles())
        {
            spokenHotKeyList.Items.Add(profile);
        }
        if (spokenHotKeyList.Items.Count > 0)
        {
            spokenHotKeyList.SelectedIndex = 0;
        }
        else
        {
            UpdateSpokenHotKeyEditor();
        }

        foreach (var profile in fanProfiles)
        {
            fanProfileList.Items.Add(profile);
        }
        if (fanProfileList.Items.Count > 0)
        {
            fanProfileList.SelectedIndex = 0;
        }
        else
        {
            UpdateFanProfileEditor();
        }

        hiddenItemsList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            AccessibleName = "Hidden readings"
        };
        showUpdateInstallConfirmationCheckBox = new CheckBox
        {
            Text = "Show update install &confirmation",
            Checked = settings.ShowUpdateInstallConfirmation,
            AutoSize = true,
            AccessibleName = "Show update install confirmation",
            AccessibleDescription = "When checked, Download and install asks before Sensor Readout closes, installs the update, and reopens."
        };
        confirmSpokenHotKeyProfileRemovalCheckBox = new CheckBox
        {
            Text = "Confirm spoken hotkey profile &removal",
            Checked = settings.ConfirmSpokenHotKeyProfileRemoval,
            AutoSize = true,
            AccessibleName = "Confirm spoken hotkey profile removal",
            AccessibleDescription = "When checked, Sensor Readout asks before removing a spoken hotkey profile."
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
            RowCount = 5,
            Padding = new Padding(10)
        };
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.Controls.Add(new Label { Text = "Hidden readings and groups. Checked items are hidden. Uncheck items to show them again.", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        hiddenLayout.Controls.Add(showUpdateInstallConfirmationCheckBox, 0, 1);
        hiddenLayout.Controls.Add(confirmSpokenHotKeyProfileRemovalCheckBox, 0, 2);
        hiddenLayout.Controls.Add(hiddenItemsList, 0, 3);
        var hiddenButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var unhideSelectedButton = new Button { Text = "&Show selected", AutoSize = true };
        unhideSelectedButton.Click += delegate
        {
            if (hiddenItemsList.SelectedIndex >= 0)
            {
                hiddenItemsList.SetItemChecked(hiddenItemsList.SelectedIndex, false);
            }
        };
        var unhideAllButton = new Button { Text = "Show &all", AutoSize = true };
        unhideAllButton.Click += delegate
        {
            for (var i = 0; i < hiddenItemsList.Items.Count; i++)
            {
                hiddenItemsList.SetItemChecked(i, false);
            }
        };
        hiddenButtons.Controls.Add(unhideSelectedButton);
        hiddenButtons.Controls.Add(unhideAllButton);
        hiddenLayout.Controls.Add(hiddenButtons, 0, 4);
        hiddenTab.Controls.Add(hiddenLayout);

        var dialogButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var closeButton = CreateShortcutButton("Close", "Esc", Keys.Escape);
        closeButton.DialogResult = DialogResult.OK;
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
        main.Controls.Add(trayTooltipPartialReadingsCheckBox, 0, 5);
        main.Controls.Add(intervalPanel, 0, 6);
        main.Controls.Add(temperaturePanel, 0, 7);
        main.Controls.Add(decimalSeparatorPanel, 0, 8);
        main.Controls.Add(updatesPanel, 0, 9);
        main.Controls.Add(diagnosticsPanel, 0, 10);
        main.Controls.Add(loggingPanel, 0, 11);
        main.Controls.Add(trayLabel, 0, 12);
        main.Controls.Add(BuildTraySelectionPanel(), 0, 13);
        main.Controls.Add(traySelectionStatusLabel, 0, 14);
        generalTab.Controls.Add(main);
        preferencesTabs.TabPages.Add(generalTab);

        var installLocationPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        installLocationPanel.Controls.Add(new Label
        {
            Text = "Install location:",
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0)
        });
        installLocationPanel.Controls.Add(new Label
        {
            Text = "Windows programs folder for this user",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
            AccessibleName = "Install location"
        });

        var runningInstalled = SensorReadoutForm.IsRunningFromLocalInstallFolder();
        var installToLocalAppDataButton = new Button
        {
            Text = runningInstalled
                ? SensorReadoutForm.L("ui.&Uninstall from this PC...", "&Uninstall from this PC...")
                : SensorReadoutForm.L("ui.&Install to this PC...", "&Install to this PC..."),
            AutoSize = true,
            AccessibleName = runningInstalled
                ? SensorReadoutForm.L("a11y.Uninstall from this PC", "Uninstall from this PC")
                : SensorReadoutForm.L("a11y.Install to this PC", "Install to this PC"),
            AccessibleDescription = runningInstalled
                ? SensorReadoutForm.L("a11y.Remove the installed Sensor Readout app files from this PC while keeping Config, Logs, and Reports.", "Remove the installed Sensor Readout app files from this PC while keeping Config, Logs, and Reports.")
                : SensorReadoutForm.L("a11y.Copies this portable Sensor Readout folder to the Windows programs folder for this user, optionally creates desktop and startup shortcuts, closes this copy, and starts the installed copy.", "Copies this portable Sensor Readout folder to the Windows programs folder for this user, optionally creates desktop and startup shortcuts, closes this copy, and starts the installed copy.")
        };
        installToLocalAppDataButton.Click += delegate
        {
            CommitPreferences();
            var handler = runningInstalled ? UninstallLocalAppDataRequested : InstallToLocalAppDataRequested;
            if (handler != null)
            {
                handler();
            }
        };

        var startupLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(10)
        };
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        startupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        startupLayout.Controls.Add(installLocationPanel, 0, 0);
        startupLayout.Controls.Add(installToLocalAppDataButton, 0, 1);
        startupLayout.Controls.Add(runAtStartupCheckBox, 0, 2);
        startupLayout.Controls.Add(desktopShortcutCheckBox, 0, 3);
        startupLayout.Controls.Add(startMinimizedCheckBox, 0, 4);
        startupLayout.Controls.Add(startupSpeechEnabledCheckBox, 0, 5);
        startupLayout.Controls.Add(startupSpeechPanel, 0, 6);
        startupLayout.Controls.Add(startupSoundPanel, 0, 7);
        startupLayout.Controls.Add(shutdownSoundPanel, 0, 8);
        startupTab.Controls.Add(startupLayout);
        preferencesTabs.TabPages.Add(startupTab);

        var hotKeysLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        hotKeysLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hotKeysLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hotKeysLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hotKeysLayout.Controls.Add(hotKeyPanel, 0, 0);
        hotKeysLayout.Controls.Add(speechIncludesDeviceNamesCheckBox, 0, 1);
        hotKeysLayout.Controls.Add(BuildSpokenHotKeysPanel(), 0, 2);
        hotKeysTab.Controls.Add(hotKeysLayout);
        preferencesTabs.TabPages.Add(hotKeysTab);
        fanProfilesTab.Controls.Add(BuildFanProfilesPanel());
        preferencesTabs.TabPages.Add(fanProfilesTab);
        alarmsTab.Controls.Add(BuildAlarmsPanel());
        preferencesTabs.TabPages.Add(alarmsTab);
        plugInsTab.Controls.Add(BuildPlugInsPanel());
        preferencesTabs.TabPages.Add(plugInsTab);
        preferencesTabs.TabPages.Add(hiddenTab);
        languageEditorTab.Controls.Add(BuildLanguageEditorPanel(effectiveLanguageChoices));
        preferencesTabs.TabPages.Add(languageEditorTab);
        SelectInitialTab(initialTabName);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 0, 0, 8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(preferencesTabs, 0, 0);
        root.Controls.Add(dialogButtons, 0, 1);
        Controls.Add(root);
        autoRefreshCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        refreshWhileFocusedCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        trayStatusCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        trayTooltipPartialReadingsCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        runAtStartupCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        desktopShortcutCheckBox.CheckedChanged += ApplyDesktopShortcutPreferenceHandler;
        startMinimizedCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        updateCheckFrequencyBox.SelectedIndexChanged += delegate { SaveLivePreferences(); };
        installUpdatesQuietlyCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        showUpdateInstallConfirmationCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        confirmSpokenHotKeyProfileRemovalCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        updateAvailableSoundBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            PreviewSelectedSound(updateAvailableSoundBox);
        };
        diagnosticsSpeakProgressCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
        diagnosticsPlaySoundsCheckBox.CheckedChanged += delegate
        {
            diagnosticsStartSoundBox.Enabled = diagnosticsPlaySoundsCheckBox.Checked;
            diagnosticsCompleteSoundBox.Enabled = diagnosticsPlaySoundsCheckBox.Checked;
            SaveLivePreferences();
        };
        diagnosticsStartSoundBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            PreviewSelectedSound(diagnosticsStartSoundBox);
        };
        diagnosticsCompleteSoundBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            PreviewSelectedSound(diagnosticsCompleteSoundBox);
        };
        showHideHotKeyBox.TextChanged += delegate { SaveLivePreferences(); };
        speakTrayHotKeyBox.TextChanged += delegate { SaveLivePreferences(); };
        hotKeyCopyDoublePressBox.SelectedIndexChanged += delegate { SaveLivePreferences(); };
        startupSpeechEnabledCheckBox.CheckedChanged += delegate
        {
            startupSpeechMessageBox.Enabled = startupSpeechEnabledCheckBox.Checked;
            resetStartupSpeechButton.Enabled = startupSpeechEnabledCheckBox.Checked;
            SaveLivePreferences();
        };
        startupSpeechMessageBox.TextChanged += delegate { SaveLivePreferences(); };
        startupSoundBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            PreviewSelectedSound(startupSoundBox);
        };
        shutdownSoundBox.SelectedIndexChanged += delegate
        {
            SaveLivePreferences();
            PreviewSelectedSound(shutdownSoundBox);
        };
        speechIncludesDeviceNamesCheckBox.CheckedChanged += delegate { SaveLivePreferences(); };
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
}
