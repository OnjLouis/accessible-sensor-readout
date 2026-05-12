using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed class PreferencesForm : Form
{
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
            Minimum = 2,
            Maximum = 300,
            Value = Math.Max(2, Math.Min(300, settings.RefreshIntervalSeconds)),
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
        alarmEnabledCheckBox.CheckedChanged += delegate { SaveSelectedAlarm(); };
        alarmNameBox.TextChanged += delegate { SaveSelectedAlarm(false); };
        alarmReadingBox.SelectedIndexChanged += delegate
        {
            if (loadingPreferences)
            {
                return;
            }

            var readingChanged = !string.Equals(lastAlarmReadingKey, SelectedAlarmReadingKey(), StringComparison.OrdinalIgnoreCase);
            RefreshAlarmThresholdUnitChoices(readingChanged);
            SaveSelectedAlarm();
            lastAlarmReadingKey = SelectedAlarmReadingKey();
        };
        alarmConditionBox.SelectedIndexChanged += delegate { SaveSelectedAlarm(); };
        alarmThresholdBox.ValueChanged += delegate { SaveSelectedAlarm(false); };
        alarmThresholdUnitBox.SelectedIndexChanged += delegate
        {
            if (loadingPreferences)
            {
                return;
            }

            RefreshAlarmThresholdForSelectedUnit();
            SaveSelectedAlarm();
        };
        alarmCooldownBox.ValueChanged += delegate { SaveSelectedAlarm(false); };
        alarmSpeakCheckBox.CheckedChanged += delegate { SaveSelectedAlarm(); };
        alarmSoundBox.SelectedIndexChanged += delegate
        {
            SaveSelectedAlarm();
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
        foreach (var profile in spokenHotKeys)
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
            RowCount = 4,
            Padding = new Padding(10)
        };
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hiddenLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hiddenLayout.Controls.Add(new Label { Text = "Hidden readings and groups. Checked items are hidden. Uncheck items to show them again.", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        hiddenLayout.Controls.Add(showUpdateInstallConfirmationCheckBox, 0, 1);
        hiddenLayout.Controls.Add(hiddenItemsList, 0, 2);
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
        hiddenLayout.Controls.Add(hiddenButtons, 0, 3);
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

    private void SelectInitialTab(string tabName)
    {
        if (preferencesTabs == null || string.IsNullOrWhiteSpace(tabName))
        {
            return;
        }

        foreach (TabPage page in preferencesTabs.TabPages)
        {
            if (string.Equals(page.Name, tabName, StringComparison.OrdinalIgnoreCase))
            {
                preferencesTabs.SelectedTab = page;
                return;
            }
        }
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
        if (spokenHotKeyList != null)
        {
            spokenHotKeyList.Refresh();
        }
        if (spokenAvailableList != null)
        {
            spokenAvailableList.Refresh();
        }
        if (spokenSelectedList != null)
        {
            spokenSelectedList.Refresh();
        }
    }

    private bool ShouldPreviewSpeechWithDeviceNames()
    {
        return speechIncludesDeviceNamesCheckBox == null || speechIncludesDeviceNamesCheckBox.Checked;
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

    protected override bool ProcessDialogChar(char charCode)
    {
        if ((ModifierKeys & Keys.Alt) == 0 && FocusedControlShouldKeepPlainCharacters(Controls))
        {
            return false;
        }

        return base.ProcessDialogChar(charCode);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Tab && (keyData & Keys.Shift) == Keys.Shift && alarmEnabledCheckBox != null && alarmEnabledCheckBox.Focused)
        {
            alarmList.Focus();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var hotKeyBox = FocusedHotKeyBox();
        if (hotKeyBox != null && HandleHotKeyBoxCommandKey(hotKeyBox, keyData))
        {
            return true;
        }

        if (keyData == Keys.Enter)
        {
            if (alarmList != null && alarmList.Focused)
            {
                FocusAlarmThreshold();
                return true;
            }

            if (spokenHotKeyList != null && spokenHotKeyList.Focused)
            {
                spokenHotKeyBox.Focus();
                spokenHotKeyBox.SelectAll();
                return true;
            }
        }

        if ((keyData & Keys.Control) == Keys.Control && (keyData & Keys.Alt) == 0 && (keyData & Keys.Shift) == 0)
        {
            var key = keyData & Keys.KeyCode;
            if (SelectPreferencesTabByShortcut(key))
            {
                return true;
            }
        }

        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            var key = keyData & Keys.KeyCode;
            if (PerformShortcutButton(Controls, key))
            {
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool SelectPreferencesTabByShortcut(Keys key)
    {
        if (preferencesTabs == null)
        {
            return false;
        }

        var index = -1;
        if (key >= Keys.D1 && key <= Keys.D9)
        {
            index = key - Keys.D1;
        }
        else if (key >= Keys.NumPad1 && key <= Keys.NumPad9)
        {
            index = key - Keys.NumPad1;
        }

        if (index < 0 || index >= preferencesTabs.TabPages.Count)
        {
            return false;
        }

        preferencesTabs.SelectedIndex = index;
        return true;
    }

    private void PromptForShowHideHotKeyIfNeeded()
    {
        if (loadingPreferences || showHideHotKeyBox == null || !string.IsNullOrWhiteSpace(ShowHideHotKey))
        {
            return;
        }

        var message = SensorReadoutForm.L(
            "message.noShowHideHotKeyForMinimizedStart",
            "You do not have a show/hide hotkey set yet. If Windows hides the notification area icon, a show/hide hotkey is the fastest way back to Sensor Readout. Configure it now?");
        var title = SensorReadoutForm.L("ui.Show/hide hotkey", "Show/hide hotkey");
        if (MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        FocusShowHideHotKeyBox();
    }

    private void FocusShowHideHotKeyBox()
    {
        if (preferencesTabs != null)
        {
            foreach (TabPage page in preferencesTabs.TabPages)
            {
                if (string.Equals(page.Name, "Hotkeys", StringComparison.OrdinalIgnoreCase))
                {
                    preferencesTabs.SelectedTab = page;
                    break;
                }
            }
        }

        if (showHideHotKeyBox != null)
        {
            showHideHotKeyBox.Focus();
            showHideHotKeyBox.SelectAll();
        }
    }

    private static bool PerformShortcutButton(Control.ControlCollection controls, Keys key)
    {
        foreach (Control control in controls)
        {
            if (!control.Visible || !control.Enabled)
            {
                continue;
            }

            var button = control as ShortcutButton;
            if (button != null && button.ShortcutKeys == key && button.CanSelect)
            {
                button.PerformClick();
                return true;
            }

            if (control.HasChildren && PerformShortcutButton(control.Controls, key))
            {
                return true;
            }
        }

        return false;
    }

    private TextBox FocusedHotKeyBox()
    {
        if (showHideHotKeyBox != null && showHideHotKeyBox.Focused)
        {
            return showHideHotKeyBox;
        }

        if (speakTrayHotKeyBox != null && speakTrayHotKeyBox.Focused)
        {
            return speakTrayHotKeyBox;
        }

        if (spokenHotKeyBox != null && spokenHotKeyBox.Focused)
        {
            return spokenHotKeyBox;
        }

        return null;
    }

    private static bool HandleHotKeyBoxCommandKey(TextBox box, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Back || key == Keys.Delete || key == Keys.Escape)
        {
            box.Text = "";
            return true;
        }

        var text = SensorReadoutForm.HotKeyTextFromKeyData(keyData);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        box.Text = text;
        return true;
    }

    private static bool FocusedControlShouldKeepPlainCharacters(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (control.Focused && (control is TextBoxBase || control is ListBox || control is ComboBox))
            {
                return true;
            }

            if (control.ContainsFocus && FocusedControlShouldKeepPlainCharacters(control.Controls))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyFixedOptionLanguage()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            SetComboItems(temperatureUnitBox, new[] { SensorReadoutForm.L("ui.Celsius (C)", "Celsius (C)"), SensorReadoutForm.L("ui.Fahrenheit (F)", "Fahrenheit (F)"), SensorReadoutForm.L("ui.Celsius, then Fahrenheit", "Celsius, then Fahrenheit"), SensorReadoutForm.L("ui.Fahrenheit, then Celsius", "Fahrenheit, then Celsius") });
            SetComboItems(decimalSeparatorBox, new[] { SensorReadoutForm.L("ui.Language default", "Language default"), SensorReadoutForm.L("ui.Period (.)", "Period (.)"), SensorReadoutForm.L("ui.Comma (,)", "Comma (,)") });
            SetComboItems(loggingLevelBox, new[] { SensorReadoutForm.L("ui.Off", "Off"), SensorReadoutForm.L("ui.Error", "Error"), SensorReadoutForm.L("ui.Normal", "Normal"), SensorReadoutForm.L("ui.Debug", "Debug") });
            SetComboItems(hotKeyCopyDoublePressBox, HotKeyCopyDoublePressOptions());
            SetComboItems(updateCheckFrequencyBox, UpdateCheckFrequencyOptions());
            SetComboItems(alarmConditionBox, new[] { SensorReadoutForm.L("ui.Above or equal", "Above or equal"), SensorReadoutForm.L("ui.Below or equal", "Below or equal"), SensorReadoutForm.L("ui.Equal", "Equal") });
            SetComboItems(fanProfileActionBox, new[] { SensorReadoutForm.L("ui.Manual", "Manual"), SensorReadoutForm.L("ui.Auto", "Auto") });
            PopulateSoundCombo(updateAvailableSoundBox, UpdateAvailableSoundFile);
            PopulateSoundCombo(diagnosticsStartSoundBox, DiagnosticsStartSoundFile);
            PopulateSoundCombo(diagnosticsCompleteSoundBox, DiagnosticsCompleteSoundFile);
            PopulateSoundCombo(startupSoundBox, StartupSoundFile);
            PopulateSoundCombo(shutdownSoundBox, ShutdownSoundFile);
            PopulateSoundCombo(alarmSoundBox, SelectedSoundFile(alarmSoundBox));
            PopulateSoundCombo(fanProfileSoundBox, SelectedSoundFile(fanProfileSoundBox));
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

        values = (values ?? new string[0]).Select(StripMnemonic).ToArray();
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

    private static int SafeComboIndex(ComboBox box, int index)
    {
        if (box == null || box.Items.Count == 0)
        {
            return -1;
        }

        return Math.Max(0, Math.Min(index, box.Items.Count - 1));
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
            if (e.KeyCode == Keys.Tab)
            {
                return;
            }

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

    private static void AttachNumericAutoSelect(NumericUpDown box)
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
        liveSettings.HotKeyCopyDoublePressMs = HotKeyCopyDoublePressMs;
        liveSettings.StartupSpeechEnabled = StartupSpeechEnabled;
        liveSettings.StartupSpeechMessage = StartupSpeechMessage;
        liveSettings.SpeechIncludesDeviceNames = SpeechIncludesDeviceNames;
        liveSettings.TrayStatusEnabled = TrayStatusEnabled;
        liveSettings.TrayTooltipShowsPartialReadings = TrayTooltipShowsPartialReadings;
        liveSettings.RunAtStartup = RunAtStartup;
        liveSettings.StartMinimizedToTray = StartMinimizedToTray;
        liveSettings.CheckForUpdatesAtStartup = CheckForUpdatesAtStartup;
        liveSettings.UpdateCheckFrequency = UpdateCheckFrequency;
        liveSettings.InstallUpdatesQuietly = InstallUpdatesQuietly;
        liveSettings.ShowUpdateInstallConfirmation = ShowUpdateInstallConfirmation;
        liveSettings.UpdateAvailableSoundFile = UpdateAvailableSoundFile;
        liveSettings.DiagnosticsSpeakProgress = DiagnosticsSpeakProgress;
        liveSettings.DiagnosticsPlaySounds = DiagnosticsPlaySounds;
        liveSettings.DiagnosticsStartSoundFile = DiagnosticsStartSoundFile;
        liveSettings.DiagnosticsCompleteSoundFile = DiagnosticsCompleteSoundFile;
        if (liveSettings.RunAtStartup || liveSettings.StartMinimizedToTray)
        {
            liveSettings.TrayStatusEnabled = true;
        }
        liveSettings.LoggingLevel = LoggingLevel;
        liveSettings.StartupSoundFile = StartupSoundFile;
        liveSettings.ShutdownSoundFile = ShutdownSoundFile;
        var currentTrayItemKeys = CurrentTrayItemKeys();
        if (currentTrayItemKeys.Count > 0 || originalTrayItemKeys.Count == 0 || rows.Count > 0)
        {
            liveSettings.TrayItemKeys = currentTrayItemKeys;
        }
        liveSettings.SpokenHotKeys = CurrentSpokenHotKeys();
        liveSettings.FanProfileStarterProfilesInitialized = fanProfileStarterProfilesInitialized;
        liveSettings.FanProfiles = CurrentFanProfiles();
        liveSettings.ShowStoppedFans = fanProfileShowStoppedBox.Checked;
        liveSettings.Alarms = CurrentAlarms();
        liveSettings.HiddenReadingKeys = CurrentHiddenReadingKeys();
        liveSettings.ReadingSpeechLabels = CurrentReadingSpeechLabels();
        liveSettings.PlugInsEnabled = CurrentPlugInSettings();
        SensorReadoutForm.SaveSettings(liveSettings);
    }

    private void ApplyDesktopShortcutPreference()
    {
        if (loadingPreferences)
        {
            return;
        }

        try
        {
            SensorReadoutForm.SetDesktopShortcut(desktopShortcutCheckBox.Checked);
        }
        catch (Exception ex)
        {
            var desired = desktopShortcutCheckBox.Checked;
            desktopShortcutCheckBox.CheckedChanged -= ApplyDesktopShortcutPreferenceHandler;
            desktopShortcutCheckBox.Checked = !desired;
            desktopShortcutCheckBox.CheckedChanged += ApplyDesktopShortcutPreferenceHandler;
            MessageBox.Show(this, SensorReadoutForm.L("message.Could not update desktop shortcut:", "Could not update desktop shortcut:") + " " + ex.Message, SensorReadoutForm.L("ui.Desktop shortcut", "Desktop shortcut"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyDesktopShortcutPreferenceHandler(object sender, EventArgs e)
    {
        ApplyDesktopShortcutPreference();
    }

    private void CommitPreferences()
    {
        TrayItemKeys = CurrentTrayItemKeys();
        SpokenHotKeys = CurrentSpokenHotKeys();
        FanProfiles = CurrentFanProfiles();
        Alarms = CurrentAlarms();
        HiddenReadingKeys = CurrentHiddenReadingKeys();
        ReadingSpeechLabels = CurrentReadingSpeechLabels();
        liveSettings.PlugInsEnabled = CurrentPlugInSettings();
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

    private static string[] HotKeyCopyDoublePressOptions()
    {
        return new[]
        {
            SensorReadoutForm.L("ui.Off", "Off"),
            string.Format(SensorReadoutForm.L("ui.Windows default ({0} ms)", "Windows default ({0} ms)"), SystemInformation.DoubleClickTime),
            "250 ms",
            "500 ms",
            "750 ms",
            "1000 ms"
        };
    }

    private static int HotKeyCopyDoublePressIndex(int value)
    {
        value = SensorReadoutForm.NormalizeHotKeyCopyDoublePressMs(value);
        if (value < 0) return 1;
        if (value == 250) return 2;
        if (value == 500) return 3;
        if (value == 750) return 4;
        if (value == 1000) return 5;
        return 0;
    }

    private static int HotKeyCopyDoublePressMsFromIndex(int index)
    {
        if (index == 1) return -1;
        if (index == 2) return 250;
        if (index == 3) return 500;
        if (index == 4) return 750;
        if (index == 5) return 1000;
        return 0;
    }

    private static string[] UpdateCheckFrequencyOptions()
    {
        return new[]
        {
            SensorReadoutForm.L("ui.At startup", "At startup"),
            SensorReadoutForm.L("ui.Every hour", "Every hour"),
            SensorReadoutForm.L("ui.Every 6 hours", "Every 6 hours"),
            SensorReadoutForm.L("ui.Every 12 hours", "Every 12 hours"),
            SensorReadoutForm.L("ui.Once a day", "Once a day"),
            SensorReadoutForm.L("ui.Once a week", "Once a week"),
            SensorReadoutForm.L("ui.Never", "Never")
        };
    }

    private static int UpdateCheckFrequencyIndex(string value)
    {
        switch (SensorReadoutForm.NormalizeUpdateCheckFrequency(value))
        {
            case "Hourly": return 1;
            case "6Hours": return 2;
            case "12Hours": return 3;
            case "Daily": return 4;
            case "Weekly": return 5;
            case "Never": return 6;
            default: return 0;
        }
    }

    private static string UpdateCheckFrequencyFromIndex(int index)
    {
        switch (index)
        {
            case 1: return "Hourly";
            case 2: return "6Hours";
            case 3: return "12Hours";
            case 4: return "Daily";
            case 5: return "Weekly";
            case 6: return "Never";
            default: return "Startup";
        }
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
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.AccessibleDescription = "Add selected reading to the tray order. Shortcut Control Right Arrow.";
        addButton.Click += delegate { AddSelectedTrayChoice(); };
        var removeButton = CreateShortcutButton("&Remove", "Alt+R", Keys.R);
        removeButton.AccessibleDescription = "Remove selected reading from the tray order. Shortcut Control Left Arrow.";
        removeButton.Click += delegate { RemoveSelectedTrayChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.AccessibleDescription = "Move selected tray reading up. Shortcut Control Up Arrow.";
        upButton.Click += delegate { MoveSelectedTrayChoice(-1); };
        var downButton = CreateShortcutButton("&Down", "Alt+D", Keys.D);
        downButton.AccessibleDescription = "Move selected tray reading down. Shortcut Control Down Arrow.";
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

    private static ShortcutButton CreateShortcutButton(string text, string shortcut, Keys shortcutKeys)
    {
        return new ShortcutButton
        {
            Text = text,
            ShortcutText = shortcut,
            ShortcutKeys = shortcutKeys,
            AutoSize = true
        };
    }

    private Control BuildSpokenHotKeysPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        var profilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.Controls.Add(new Label { Text = "Spoken hotkeys", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        profilePanel.Controls.Add(spokenHotKeyList, 0, 1);
        var profileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addProfileButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        addProfileButton.Click += delegate { AddSpokenHotKeyProfile(); };
        var importProfileButton = CreateShortcutButton("&Import...", "Alt+I", Keys.I);
        importProfileButton.Click += delegate { ImportSpokenHotKeysFromConfig(); };
        var removeProfileButton = CreateShortcutButton("Remove &profile", "Alt+P", Keys.P);
        removeProfileButton.Click += delegate { RemoveSelectedSpokenHotKeyProfile(); };
        profileButtons.Controls.Add(addProfileButton);
        profileButtons.Controls.Add(importProfileButton);
        profileButtons.Controls.Add(removeProfileButton);
        profilePanel.Controls.Add(profileButtons, 0, 2);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namePanel.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        namePanel.Controls.Add(spokenHotKeyNameBox, 1, 0);

        var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        keyPanel.Controls.Add(spokenHotKeyBox, 1, 0);
        var clearKeyButton = CreateShortcutButton("&Clear", "Alt+C", Keys.C);
        clearKeyButton.Click += delegate { spokenHotKeyBox.Text = ""; };
        keyPanel.Controls.Add(clearKeyButton, 2, 0);

        editor.Controls.Add(namePanel, 0, 0);
        editor.Controls.Add(keyPanel, 0, 1);
        editor.Controls.Add(new Label { Text = "Choose the readings spoken by this hotkey. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.", AutoSize = true, Dock = DockStyle.Fill }, 0, 2);
        editor.Controls.Add(BuildSpokenSelectionPanel(), 0, 3);
        editor.Controls.Add(spokenSelectionStatusLabel, 0, 4);

        layout.Controls.Add(profilePanel, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        return layout;
    }

    private Control BuildFanProfilesPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        var profilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.Controls.Add(new Label { Text = "Fan profiles", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        profilePanel.Controls.Add(fanProfileList, 0, 1);
        var profileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addProfileButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        addProfileButton.Click += delegate { AddFanProfile(); };
        var removeProfileButton = CreateShortcutButton("Remove &profile", "Alt+P", Keys.P);
        removeProfileButton.Click += delegate { RemoveSelectedFanProfile(); };
        var applyProfileButton = CreateShortcutButton("Appl&y now", "Alt+Y", Keys.Y);
        applyProfileButton.Click += delegate { ApplySelectedFanProfileFromPreferences(); };
        profileButtons.Controls.Add(addProfileButton);
        profileButtons.Controls.Add(removeProfileButton);
        profileButtons.Controls.Add(applyProfileButton);
        profilePanel.Controls.Add(profileButtons, 0, 2);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11 };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namePanel.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        namePanel.Controls.Add(fanProfileNameBox, 1, 0);

        var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        keyPanel.Controls.Add(fanProfileHotKeyBox, 1, 0);
        var clearKeyButton = CreateShortcutButton("&Clear", "Alt+C", Keys.C);
        clearKeyButton.Click += delegate { fanProfileHotKeyBox.Text = ""; };
        keyPanel.Controls.Add(clearKeyButton, 2, 0);

        var soundPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        soundPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        soundPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        soundPanel.Controls.Add(new Label { Text = "Sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        soundPanel.Controls.Add(fanProfileSoundBox, 1, 0);

        var speechPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        speechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        speechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        speechPanel.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Speech message:", "Speech message:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        speechPanel.Controls.Add(fanProfileSpeechMessageBox, 1, 0);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actionPanel.Controls.Add(new Label { Text = "Action for selected fan:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        actionPanel.Controls.Add(fanProfileActionBox);
        actionPanel.Controls.Add(new Label { Text = "Percent:", AutoSize = true, Padding = new Padding(12, 6, 8, 0) });
        actionPanel.Controls.Add(fanProfilePercentBox);

        editor.Controls.Add(namePanel, 0, 0);
        editor.Controls.Add(keyPanel, 0, 1);
        editor.Controls.Add(fanProfileToggleBox, 0, 2);
        editor.Controls.Add(fanProfileSpeakBox, 0, 3);
        editor.Controls.Add(speechPanel, 0, 4);
        editor.Controls.Add(soundPanel, 0, 5);
        editor.Controls.Add(fanProfileShowStoppedBox, 0, 6);
        editor.Controls.Add(actionPanel, 0, 7);
        editor.Controls.Add(new Label { Text = "Choose the fan controls changed by this profile. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.", AutoSize = true, Dock = DockStyle.Fill }, 0, 8);
        editor.Controls.Add(BuildFanProfileSelectionPanel(), 0, 9);
        editor.Controls.Add(fanProfileStatusLabel, 0, 10);

        layout.Controls.Add(profilePanel, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        return layout;
    }

    private Control BuildFanProfileSelectionPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Available fan controls", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Profile fan actions", AutoSize = true }, 2, 0);
        layout.Controls.Add(fanProfileAvailableList, 0, 1);
        layout.Controls.Add(fanProfileSelectedList, 2, 1);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 4, ColumnCount = 1 };
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.Click += delegate { AddSelectedFanProfileChoice(); };
        var removeButton = CreateShortcutButton("&Remove", "Alt+R", Keys.R);
        removeButton.Click += delegate { RemoveSelectedFanProfileChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.Click += delegate { MoveSelectedFanProfileChoice(-1); };
        var downButton = CreateShortcutButton("&Down", "Alt+D", Keys.D);
        downButton.Click += delegate { MoveSelectedFanProfileChoice(1); };
        buttons.Controls.Add(addButton, 0, 0);
        buttons.Controls.Add(removeButton, 0, 1);
        buttons.Controls.Add(upButton, 0, 2);
        buttons.Controls.Add(downButton, 0, 3);
        layout.Controls.Add(buttons, 1, 1);
        return layout;
    }

    private Control BuildSoundPickerPanel(string labelText, string selectedFile, out ComboBox combo)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = labelText, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        PopulateSoundCombo(combo, selectedFile);
        panel.Controls.Add(combo, 1, 0);
        return panel;
    }

    private void PopulateSoundCombo(ComboBox combo, string selectedFile)
    {
        combo.Items.Clear();
        combo.Items.Add(SensorReadoutForm.L("ui.(None)", "(None)"));
        foreach (var sound in soundFiles)
        {
            combo.Items.Add(sound);
        }

        var selected = System.IO.Path.GetFileName(selectedFile ?? "");
        combo.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            for (var i = 1; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i].ToString(), selected, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static string SelectedSoundFile(ComboBox combo)
    {
        if (combo == null || combo.SelectedIndex <= 0 || combo.SelectedItem == null)
        {
            return "";
        }

        return System.IO.Path.GetFileName(combo.SelectedItem.ToString());
    }

    private void PreviewSelectedSound(ComboBox combo)
    {
        if (loadingPreferences)
        {
            return;
        }

        var fileName = SelectedSoundFile(combo);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            SensorReadoutForm.PreviewSoundFile(fileName);
        }
    }

    private void AlarmListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            FocusAlarmName();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            FocusAlarmThreshold();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedAlarm();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private Control BuildAlarmsPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var alarmButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var newButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        var removeButton = CreateShortcutButton("Re&move", "Alt+M", Keys.M);
        newButton.TabIndex = 0;
        removeButton.TabIndex = 1;
        newButton.Click += delegate { AddAlarm(); };
        removeButton.Click += delegate { RemoveSelectedAlarm(); };
        alarmButtons.Controls.Add(newButton);
        alarmButtons.Controls.Add(removeButton);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
        editor.TabIndex = 1;
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        alarmEnabledCheckBox.TabIndex = 0;
        alarmNameBox.TabIndex = 1;
        alarmReadingBox.TabIndex = 2;
        alarmConditionBox.TabIndex = 3;
        alarmThresholdBox.TabIndex = 4;
        alarmThresholdUnitBox.TabIndex = 5;
        alarmCooldownBox.TabIndex = 6;
        alarmSpeakCheckBox.TabIndex = 7;
        alarmSoundBox.TabIndex = 8;
        editor.Controls.Add(alarmEnabledCheckBox, 1, 0);
        editor.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);
        editor.Controls.Add(alarmNameBox, 1, 1);
        editor.Controls.Add(new Label { Text = "Reading:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
        editor.Controls.Add(alarmReadingBox, 1, 2);
        editor.Controls.Add(new Label { Text = "Condition:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
        editor.Controls.Add(alarmConditionBox, 1, 3);
        editor.Controls.Add(new Label { Text = "Threshold:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 4);
        var thresholdPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        thresholdPanel.Controls.Add(alarmThresholdBox);
        thresholdPanel.Controls.Add(alarmThresholdUnitBox);
        editor.Controls.Add(thresholdPanel, 1, 4);
        editor.Controls.Add(new Label { Text = "Cooldown seconds:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 5);
        editor.Controls.Add(alarmCooldownBox, 1, 5);
        editor.Controls.Add(alarmSpeakCheckBox, 1, 6);
        editor.Controls.Add(new Label { Text = "Sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 7);
        editor.Controls.Add(alarmSoundBox, 1, 7);

        alarmList.TabIndex = 0;
        alarmButtons.TabIndex = 2;
        alarmStatusLabel.TabStop = false;
        layout.Controls.Add(alarmList, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        layout.Controls.Add(alarmButtons, 0, 1);
        layout.Controls.Add(alarmStatusLabel, 1, 1);

        PopulateAlarmReadings();
        foreach (var alarm in alarms)
        {
            alarmList.Items.Add(new AlarmChoice(alarm, RowForKey, FormatAlarmThresholdForList));
        }

        if (alarmList.Items.Count > 0)
        {
            alarmList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedAlarm();
        }

        return layout;
    }

    private Control BuildSpokenSelectionPanel()
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
        availablePanel.Controls.Add(spokenAvailableList, 0, 1);

        var selectedPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.Controls.Add(new Label { Text = "Spoken readings", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        selectedPanel.Controls.Add(spokenSelectedList, 0, 1);
        var spokenLabelButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var renameButton = CreateShortcutButton("&Rename...", "Alt+R", Keys.R);
        renameButton.Click += delegate { RenameSelectedSpokenChoice(); };
        var resetButton = CreateShortcutButton("Reset &default", "Alt+D", Keys.D);
        resetButton.Click += delegate { ResetSelectedSpokenChoiceLabel(); };
        spokenLabelButtons.Controls.Add(renameButton);
        spokenLabelButtons.Controls.Add(resetButton);
        selectedPanel.Controls.Add(spokenLabelButtons, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 24, 8, 0)
        };
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.Click += delegate { AddSelectedSpokenChoice(); };
        var removeButton = CreateShortcutButton("Re&move", "Alt+M", Keys.M);
        removeButton.Click += delegate { RemoveSelectedSpokenChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.Click += delegate { MoveSelectedSpokenChoice(-1); };
        var downButton = CreateShortcutButton("Do&wn", "Alt+W", Keys.W);
        downButton.Click += delegate { MoveSelectedSpokenChoice(1); };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(upButton);
        buttons.Controls.Add(downButton);

        panel.Controls.Add(availablePanel, 0, 0);
        panel.Controls.Add(buttons, 1, 0);
        panel.Controls.Add(selectedPanel, 2, 0);
        return panel;
    }

    private Control BuildPlugInsPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = SensorReadoutForm.L("ui.Enable only the Plug-Ins you need for this machine. Each item includes its purpose. Disabled Plug-Ins are not loaded.", "Enable only the Plug-Ins you need for this machine. Each item includes its purpose. Disabled Plug-Ins are not loaded."),
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 0);

        RefreshPlugInList();
        layout.Controls.Add(plugInList, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var importButton = CreateShortcutButton(SensorReadoutForm.L("ui.&Import from ZIP...", "&Import from ZIP..."), "Alt+I", Keys.I);
        importButton.AccessibleDescription = SensorReadoutForm.L("a11y.Import a Plug-In ZIP file into the Plug-Ins folder. Imported Plug-Ins stay disabled until you enable them.", "Import a Plug-In ZIP file into the Plug-Ins folder. Imported Plug-Ins stay disabled until you enable them.");
        importButton.Click += delegate { ImportPlugInFromZip(); };
        buttons.Controls.Add(importButton);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(plugInDetailsLabel, 0, 3);
        UpdatePlugInDetails();
        return layout;
    }

    private void RefreshPlugInList()
    {
        if (plugInList == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            plugIns.Clear();
            plugIns.AddRange(SensorReadoutForm.LoadPlugInPreferenceInfos(liveSettings));
            plugInList.Items.Clear();
            foreach (var plugIn in plugIns)
            {
                plugInList.Items.Add(plugIn, plugIn.Enabled);
            }

            plugInList.SelectedIndex = plugInList.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdatePlugInDetails();
    }

    private void UpdatePlugInDetails()
    {
        var plugIn = plugInList == null ? null : plugInList.SelectedItem as PlugInPreferenceInfo;
        if (plugIn == null || plugInList.SelectedIndex < 0)
        {
            plugInDetailsLabel.Text = SensorReadoutForm.L("ui.No Plug-Ins found.", "No Plug-Ins found.");
            return;
        }

        plugInDetailsLabel.Text =
            (plugInList.GetItemChecked(plugInList.SelectedIndex) ? SensorReadoutForm.L("ui.Enabled. ", "Enabled. ") : SensorReadoutForm.L("ui.Disabled. ", "Disabled. ")) +
            (string.IsNullOrWhiteSpace(plugIn.Description) ? "" : plugIn.Description + " ") +
            "ID: " + plugIn.Id +
            "; " + SensorReadoutForm.L("ui.author:", "author:") + " " + (string.IsNullOrWhiteSpace(plugIn.Author) ? SensorReadoutForm.L("ui.unknown", "unknown") : plugIn.Author) +
            "; " + SensorReadoutForm.L("ui.status:", "status:") + " " + (string.IsNullOrWhiteSpace(plugIn.Status) ? SensorReadoutForm.L("ui.available", "available") : plugIn.Status) + ".";
    }

    private void ImportPlugInFromZip()
    {
        if (PlugInZipImporter.PromptAndImport(this, liveSettings))
        {
            RefreshPlugInList();
            UpdatePlugInDetails();
        }
    }

    private Dictionary<string, bool> CurrentPlugInSettings()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (plugInList == null)
        {
            return result;
        }

        for (var i = 0; i < plugInList.Items.Count; i++)
        {
            var plugIn = plugInList.Items[i] as PlugInPreferenceInfo;
            if (plugIn == null || string.IsNullOrWhiteSpace(plugIn.Id))
            {
                continue;
            }

            result[plugIn.Id] = plugInList.GetItemChecked(i);
        }

        return result;
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
        var reloadButton = new Button { Text = "&Reload", AutoSize = true };
        reloadButton.Click += delegate { LoadLanguageEditorEntries(); };
        filePanel.Controls.Add(reloadButton);
        var openFolderButton = new Button { Text = "&Open folder", AutoSize = true };
        openFolderButton.Click += delegate { SensorReadoutForm.OpenLanguagesFolderStatic(this); };
        filePanel.Controls.Add(openFolderButton);
        var newButton = new Button { Text = "&New...", AutoSize = true };
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
        var saveButton = new Button { Text = "&Save entry", AutoSize = true };
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
            return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text : null;
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

        if (traySelectedList.Items.Count >= SensorReadoutForm.MaxTrayStatusReadings)
        {
            SetTraySelectionStatus("The notification area can show up to eight readings.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = trayAvailableList.SelectedIndex;
        trayAvailableList.Items.Remove(item);
        item.ShowSpeechPreview = true;
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
        item.ShowSpeechPreview = false;
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
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(trayAvailableList, SensorReadoutForm.L("ui.Find reading", "Find reading"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void TraySelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            RenameSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
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

    private void RenameSelectedTrayChoice()
    {
        var item = traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select a tray reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        RenameSpeechLabel(item, SetTraySelectionStatus);
    }

    private void ShowPreferenceListSearch(ListBox list, string title)
    {
        if (list == null || list.Items.Count == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var choices = list.Items.Cast<object>().ToList();
        var selected = SensorReadoutForm.ShowSearchDialog(
            this,
            title,
            SensorReadoutForm.L("ui.Search:", "Search:"),
            choices,
            delegate(object item) { return item == null ? "" : item.ToString(); },
            delegate(object item) { return PreferenceListSearchText(item); });
        if (selected == null)
        {
            list.Focus();
            return;
        }

        var index = list.Items.IndexOf(selected);
        if (index >= 0)
        {
            list.SelectedIndex = index;
            list.TopIndex = Math.Max(0, index - 2);
        }

        list.Focus();
    }

    private static string PreferenceListSearchText(object item)
    {
        var trayChoice = item as TrayItemChoice;
        if (trayChoice != null)
        {
            return trayChoice.Type + " " + trayChoice.Hardware + " " + trayChoice.Name + " " + trayChoice.Key;
        }

        var fanChoice = item as FanControlChoice;
        if (fanChoice != null)
        {
            return fanChoice.Hardware + " " + fanChoice.Name + " " + fanChoice.Action + " " + fanChoice.Key;
        }

        return item == null ? "" : item.ToString();
    }

    private void AttachIncrementalListSearch(ListBox list)
    {
        if (list == null)
        {
            return;
        }

        listSearchStates[list] = new ListSearchState();
        list.KeyDown += IncrementalListSearchKeyDown;
    }

    private void IncrementalListSearchKeyDown(object sender, KeyEventArgs e)
    {
        var list = sender as ListBox;
        if (list == null || e.Control || e.Alt || list.Items.Count == 0)
        {
            return;
        }

        var searchChar = SearchCharFromKeyEvent(e);
        if (searchChar == '\0')
        {
            return;
        }

        ListSearchState state;
        if (!listSearchStates.TryGetValue(list, out state))
        {
            state = new ListSearchState();
            listSearchStates[list] = state;
        }

        var now = DateTime.UtcNow;
        if ((now - state.LastKey).TotalMilliseconds > 1000)
        {
            state.Text = "";
        }

        state.LastKey = now;
        state.Text += searchChar;
        if (!SelectListSearchMatch(list, state.Text) && state.Text.Length > 1)
        {
            state.Text = searchChar.ToString();
            SelectListSearchMatch(list, state.Text);
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private static char SearchCharFromKeyEvent(KeyEventArgs e)
    {
        if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
        {
            return char.ToLowerInvariant((char)('a' + (e.KeyCode - Keys.A)));
        }

        if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
        {
            return (char)('0' + (e.KeyCode - Keys.D0));
        }

        if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
        {
            return (char)('0' + (e.KeyCode - Keys.NumPad0));
        }

        if (e.KeyCode == Keys.Space)
        {
            return ' ';
        }

        return '\0';
    }

    private static bool SelectListSearchMatch(ListBox list, string searchText)
    {
        if (list == null || string.IsNullOrWhiteSpace(searchText))
        {
            return false;
        }

        var start = Math.Max(0, list.SelectedIndex);
        var normalizedSearch = NormalizeListSearchText(searchText);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var offset = pass == 0 ? 1 : 0; offset <= list.Items.Count; offset++)
            {
                var index = (start + offset) % list.Items.Count;
                var text = NormalizeListSearchText(Convert.ToString(list.Items[index]));
                if (text.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.SelectedIndex = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeListSearchText(string text)
    {
        return (text ?? "")
            .Replace(" - ", " ")
            .Replace(": ", " ")
            .Replace(":", " ")
            .Trim();
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

        SetTraySelectionStatus(SensorReadoutForm.L("ui.Tray order has", "Tray order has") + " " + traySelectedList.Items.Count + " " + SensorReadoutForm.L("ui.of 8 readings.", "of 8 readings."));
    }

    public void UpdateSensorRows(List<SensorRow> latestRows)
    {
        if (latestRows == null)
        {
            return;
        }

        var newRows = latestRows
            .Where(SensorReadoutForm.IsSelectableReadoutRow)
            .OrderBy(r => SensorReadoutForm.TypeSortIndex(r.Type))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();
        var newSignature = BuildRowsSignature(newRows);
        latestSensorRows.Clear();
        latestSensorRows.AddRange(latestRows.Where(r => r != null));
        var newFanControlRows = BuildFanProfileFanControlRows(latestRows);
        var newFanControlSignature = BuildRowsSignature(newFanControlRows);
        if (string.Equals(newSignature, rowsSignature, StringComparison.Ordinal) &&
            string.Equals(newFanControlSignature, fanControlRowsSignature, StringComparison.Ordinal))
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            rows.Clear();
            rows.AddRange(newRows);
            rowsSignature = newSignature;
            fanControlRows.Clear();
            fanControlRows.AddRange(newFanControlRows);
            fanControlRowsSignature = newFanControlSignature;

            PopulateTrayReadingLists(CurrentTrayItemKeys());
            PopulateSpokenReadingLists(SelectedSpokenHotKey());
            PopulateFanProfileLists(SelectedFanProfile());
            PopulateAlarmReadings();
            UpdateTraySelectionStatus();
            UpdateSpokenSelectionStatus();
            UpdateFanProfileStatus();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private static string BuildRowsSignature(IEnumerable<SensorRow> sourceRows)
    {
        return string.Join("|", (sourceRows ?? Enumerable.Empty<SensorRow>())
            .Select(r => SensorReadoutForm.RowSettingsKey(r))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderBy(k => k)
            .ToArray());
    }

    private void PopulateTrayReadingLists(List<string> selectedKeys)
    {
        if (trayAvailableList == null || traySelectedList == null)
        {
            return;
        }

        var selectedAvailableKey = SelectedTrayChoiceKey(trayAvailableList);
        var selectedTrayKey = SelectedTrayChoiceKey(traySelectedList);
        trayAvailableList.Items.Clear();
        traySelectedList.Items.Clear();
        var keys = selectedKeys ?? new List<string>();
        var choices = rows
            .Select(r => new TrayItemChoice(r, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => SensorReadoutForm.ReadingSortIndex(i.Name))
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Type)
            .ToList();

        foreach (var key in keys)
        {
            var selectedTrayChoice = choices.FirstOrDefault(i => i.Key == key);
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

        foreach (var item in choices)
        {
            if (!ContainsTrayChoice(traySelectedList, item.Key))
            {
                trayAvailableList.Items.Add(item);
            }
        }

        if (trayAvailableList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(trayAvailableList, selectedAvailableKey);
        }
        if (traySelectedList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(traySelectedList, selectedTrayKey);
        }
    }

    private static string SelectedTrayChoiceKey(ListBox list)
    {
        var choice = list == null ? null : list.SelectedItem as TrayItemChoice;
        return choice == null ? "" : choice.Key;
    }

    private static void SelectTrayChoiceByKey(ListBox list, string key)
    {
        if (list == null || list.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            var choice = list.Items[i] as TrayItemChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }

        list.SelectedIndex = 0;
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
            .Take(SensorReadoutForm.MaxTrayStatusReadings)
            .Select(i => i.Key)
            .ToList();
    }

    private void AddSpokenHotKeyProfile()
    {
        var profile = new SpokenHotKeySetting
        {
            Name = "New spoken hotkey",
            HotKey = "",
            ReadingKeys = new List<string>()
        };
        spokenHotKeys.Add(profile);
        spokenHotKeyList.Items.Add(profile);
        spokenHotKeyList.SelectedItem = profile;
        spokenHotKeyNameBox.Focus();
        spokenHotKeyNameBox.SelectAll();
        UpdateSpokenSelectionStatus("Created new spoken hotkey.");
        SaveLivePreferences();
    }

    private void SaveShowStoppedFansPreference()
    {
        if (loadingPreferences)
        {
            return;
        }

        liveSettings.ShowStoppedFans = fanProfileShowStoppedBox.Checked;
        RebuildFanProfileFanControlRows();
        SaveLivePreferences();
    }

    private void RebuildFanProfileFanControlRows()
    {
        var newFanControlRows = BuildFanProfileFanControlRows(latestSensorRows);
        fanControlRows.Clear();
        fanControlRows.AddRange(newFanControlRows);
        fanControlRowsSignature = BuildRowsSignature(newFanControlRows);
        PopulateFanProfileLists(SelectedFanProfile());
        UpdateFanProfileStatus();
    }

    private void RemoveSelectedSpokenHotKeyProfile()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken hotkey first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = spokenHotKeyList.SelectedIndex;
        spokenHotKeys.Remove(profile);
        spokenHotKeyList.Items.Remove(profile);
        if (spokenHotKeyList.Items.Count > 0)
        {
            spokenHotKeyList.SelectedIndex = Math.Max(0, Math.Min(index, spokenHotKeyList.Items.Count - 1));
        }
        else
        {
            UpdateSpokenHotKeyEditor();
        }

        UpdateSpokenSelectionStatus("Removed spoken hotkey.");
        SaveLivePreferences();
    }

    private void ImportSpokenHotKeysFromConfig()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Import spoken hotkeys";
            dialog.Filter = "Sensor Readout config (*.json)|*.json|All files (*.*)|*.*";
            dialog.InitialDirectory = SensorReadoutForm.GetConfigFolderPath();
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var importedSettings = JsonConvert.DeserializeObject<MachineAppSettings>(File.ReadAllText(dialog.FileName));
                var importedProfiles = importedSettings == null ? new List<SpokenHotKeySetting>() : importedSettings.SpokenHotKeys ?? new List<SpokenHotKeySetting>();
                var added = 0;
                var skippedProfiles = 0;
                var skippedReadings = 0;
                foreach (var imported in importedProfiles.Where(p => p != null))
                {
                    var resolvedKeys = new List<string>();
                    foreach (var key in imported.ReadingKeys ?? new List<string>())
                    {
                        var resolved = ResolveImportedSpokenReadingKey(key);
                        if (string.IsNullOrWhiteSpace(resolved))
                        {
                            skippedReadings++;
                            continue;
                        }

                        if (!resolvedKeys.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                        {
                            resolvedKeys.Add(resolved);
                        }
                    }

                    if (resolvedKeys.Count == 0)
                    {
                        skippedProfiles++;
                        continue;
                    }

                    var profile = new SpokenHotKeySetting
                    {
                        Name = UniqueSpokenHotKeyName(imported.Name),
                        HotKey = "",
                        ReadingKeys = resolvedKeys
                    };
                    spokenHotKeys.Add(profile);
                    spokenHotKeyList.Items.Add(profile);
                    added++;
                }

                if (added > 0)
                {
                    spokenHotKeyList.SelectedIndex = spokenHotKeyList.Items.Count - 1;
                    SaveLivePreferences();
                }

                UpdateSpokenSelectionStatus("Imported " + added + " spoken hotkey" + (added == 1 ? "" : "s") + ". " + skippedProfiles + " profile" + (skippedProfiles == 1 ? "" : "s") + " and " + skippedReadings + " reading" + (skippedReadings == 1 ? "" : "s") + " skipped.");
            }
            catch (Exception ex)
            {
                UpdateSpokenSelectionStatus("Could not import spoken hotkeys: " + ex.Message);
                System.Media.SystemSounds.Beep.Play();
            }
        }
    }

    private string ResolveImportedSpokenReadingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var exact = rows.FirstOrDefault(r => string.Equals(SensorReadoutForm.RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return SensorReadoutForm.RowSettingsKey(exact);
        }

        var parts = key.Split('|');
        if (parts.Length < 3)
        {
            return "";
        }

        var type = parts[0];
        var hardware = parts[1];
        var name = SensorReadoutForm.CleanSensorName(parts[2]);
        var portableHardware = IsPortableImportedHardware(hardware);
        var matches = rows
            .Where(r =>
                string.Equals(r.Type ?? "", type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(SensorReadoutForm.CleanSensorName(r.Name), name, StringComparison.OrdinalIgnoreCase) &&
                (portableHardware || string.Equals(r.Hardware ?? "", hardware, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count == 1 ? SensorReadoutForm.RowSettingsKey(matches[0]) : "";
    }

    private static bool IsPortableImportedHardware(string hardware)
    {
        return string.Equals(hardware ?? "", "CPU", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Memory", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Battery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware ?? "", "Overview", StringComparison.OrdinalIgnoreCase);
    }

    private string UniqueSpokenHotKeyName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Imported spoken hotkey" : name.Trim();
        var candidate = baseName;
        var suffix = 2;
        while (spokenHotKeys.Any(p => p != null && string.Equals(p.Name ?? "", candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + " " + suffix;
            suffix++;
        }

        return candidate;
    }

    private SpokenHotKeySetting SelectedSpokenHotKey()
    {
        return spokenHotKeyList == null ? null : spokenHotKeyList.SelectedItem as SpokenHotKeySetting;
    }

    private void LoadSelectedSpokenHotKey()
    {
        UpdateSpokenHotKeyEditor();
    }

    private void UpdateSpokenHotKeyEditor()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var profile = SelectedSpokenHotKey();
            var enabled = profile != null;
            spokenHotKeyNameBox.Enabled = enabled;
            spokenHotKeyBox.Enabled = enabled;
            spokenAvailableList.Enabled = enabled;
            spokenSelectedList.Enabled = enabled;
            spokenHotKeyNameBox.Text = profile == null ? "" : profile.Name ?? "";
            spokenHotKeyBox.Text = profile == null ? "" : SensorReadoutForm.NormalizeHotKeyText(profile.HotKey);
            PopulateSpokenReadingLists(profile);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdateSpokenSelectionStatus();
    }

    private void PopulateSpokenReadingLists(SpokenHotKeySetting profile)
    {
        var selectedAvailableKey = SelectedTrayChoiceKey(spokenAvailableList);
        var selectedSpokenKey = SelectedTrayChoiceKey(spokenSelectedList);
        spokenAvailableList.Items.Clear();
        spokenSelectedList.Items.Clear();
        var selectedKeys = profile == null || profile.ReadingKeys == null ? new List<string>() : profile.ReadingKeys;
        var choices = rows
            .Select(r => new TrayItemChoice(r, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => SensorReadoutForm.ReadingSortIndex(i.Name))
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Type)
            .ToList();

        foreach (var key in selectedKeys)
        {
            var selectedChoice = choices.FirstOrDefault(i => i.Key == key);
            if (selectedChoice != null && !ContainsTrayChoice(spokenSelectedList, selectedChoice.Key))
            {
                selectedChoice.ShowSpeechPreview = true;
                spokenSelectedList.Items.Add(selectedChoice);
            }
            else if (selectedChoice == null && !ContainsTrayChoice(spokenSelectedList, key))
            {
                var unresolved = TrayItemChoice.Unresolved(key, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames);
                unresolved.ShowSpeechPreview = true;
                spokenSelectedList.Items.Add(unresolved);
            }
        }

        foreach (var item in choices)
        {
            if (!ContainsTrayChoice(spokenSelectedList, item.Key))
            {
                spokenAvailableList.Items.Add(item);
            }
        }

        if (spokenAvailableList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(spokenAvailableList, selectedAvailableKey);
        }
        if (spokenSelectedList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(spokenSelectedList, selectedSpokenKey);
        }
    }

    private void SaveSelectedSpokenHotKeyHeader()
    {
        if (loadingPreferences)
        {
            return;
        }

        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            return;
        }

        profile.Name = spokenHotKeyNameBox.Text.Trim();
        profile.HotKey = SensorReadoutForm.NormalizeHotKeyText(spokenHotKeyBox.Text);
        RefreshSelectedSpokenHotKeyListItem();
        SaveLivePreferences();
    }

    private void RefreshSelectedSpokenHotKeyListItem()
    {
        if (spokenHotKeyList == null)
        {
            return;
        }

        spokenHotKeyList.Refresh();
    }

    private void AddSelectedSpokenChoice()
    {
        var profile = SelectedSpokenHotKey();
        var item = spokenAvailableList.SelectedItem as TrayItemChoice;
        if (profile == null || item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken hotkey and an available reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = spokenAvailableList.SelectedIndex;
        spokenAvailableList.Items.Remove(item);
        item.ShowSpeechPreview = true;
        spokenSelectedList.Items.Add(item);
        spokenSelectedList.SelectedItem = item;
        if (spokenAvailableList.Items.Count > 0)
        {
            spokenAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, spokenAvailableList.Items.Count - 1));
        }
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Added " + item + " to spoken hotkey.");
    }

    private void RemoveSelectedSpokenChoice()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = spokenSelectedList.SelectedIndex;
        spokenSelectedList.Items.Remove(item);
        item.ShowSpeechPreview = false;
        AddAvailableSpokenChoiceSorted(item);
        if (spokenSelectedList.Items.Count > 0)
        {
            spokenSelectedList.SelectedIndex = Math.Max(0, Math.Min(index, spokenSelectedList.Items.Count - 1));
        }
        spokenAvailableList.SelectedItem = item;
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Removed " + item + " from spoken hotkey.");
    }

    private void MoveSelectedSpokenChoice(int direction)
    {
        var index = spokenSelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= spokenSelectedList.Items.Count)
        {
            UpdateSpokenSelectionStatus("Cannot move the selected spoken reading further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = spokenSelectedList.Items[index];
        spokenSelectedList.Items.RemoveAt(index);
        spokenSelectedList.Items.Insert(target, item);
        spokenSelectedList.SelectedIndex = target;
        SaveSelectedSpokenReadingKeys();
        UpdateSpokenSelectionStatus("Moved " + item + (direction < 0 ? " up." : " down."));
    }

    private void RenameSelectedSpokenChoice()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        RenameSpeechLabel(item, UpdateSpokenSelectionStatus);
    }

    private void RenameSpeechLabel(TrayItemChoice item, Action<string> updateStatus)
    {
        if (item == null)
        {
            return;
        }

        var current = SpeechLabelForChoice(item);
        var value = PromptForText(this, SensorReadoutForm.L("ui.Rename...", "Rename..."), SensorReadoutForm.L("ui.Spoken label:", "Spoken label:"), current);
        if (value == null)
        {
            return;
        }

        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, DefaultSpeechLabelForChoice(item), StringComparison.OrdinalIgnoreCase))
        {
            readingSpeechLabels.Remove(item.Key);
            updateStatus(SensorReadoutForm.L("ui.Reset spoken label for", "Reset spoken label for") + " " + item + ".");
        }
        else
        {
            readingSpeechLabels[item.Key] = value;
            updateStatus(SensorReadoutForm.L("ui.Renamed spoken label to", "Renamed spoken label to") + " " + value + ".");
        }

        RefreshSpeechPreviewLists();
        SaveLivePreferences();
    }

    private void ResetSelectedSpokenChoiceLabel()
    {
        var item = spokenSelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            UpdateSpokenSelectionStatus("Select a spoken reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        readingSpeechLabels.Remove(item.Key);
        RefreshSpeechPreviewLists();
        SaveLivePreferences();
        UpdateSpokenSelectionStatus(SensorReadoutForm.L("ui.Reset spoken label for", "Reset spoken label for") + " " + item + ".");
    }

    private void RefreshSpeechPreviewLists()
    {
        if (traySelectedList != null) traySelectedList.Refresh();
        if (spokenSelectedList != null) spokenSelectedList.Refresh();
        if (spokenAvailableList != null) spokenAvailableList.Refresh();
        if (trayAvailableList != null) trayAvailableList.Refresh();
    }

    private string SpeechLabelForChoice(TrayItemChoice choice)
    {
        if (choice == null)
        {
            return "";
        }

        string custom;
        return readingSpeechLabels.TryGetValue(choice.Key, out custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : DefaultSpeechLabelForChoice(choice);
    }

    private string DefaultSpeechLabelForChoice(TrayItemChoice choice)
    {
        return choice == null ? "" : SensorReadoutForm.DefaultSpeechLabel(choice.Type, choice.Hardware, choice.Name, ShouldPreviewSpeechWithDeviceNames());
    }

    private void SaveSelectedSpokenReadingKeys()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            return;
        }

        profile.ReadingKeys = spokenSelectedList.Items
            .Cast<TrayItemChoice>()
            .Select(i => i.Key)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
        RefreshSelectedSpokenHotKeyListItem();
        SaveLivePreferences();
    }

    private void AddAvailableSpokenChoiceSorted(TrayItemChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < spokenAvailableList.Items.Count &&
            TrayItemChoice.Compare((TrayItemChoice)spokenAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        spokenAvailableList.Items.Insert(insertIndex, choice);
    }

    private void SpokenAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(spokenAvailableList, SensorReadoutForm.L("ui.Find reading", "Find reading"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SpokenHotKeyListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            spokenHotKeyNameBox.Focus();
            spokenHotKeyNameBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            spokenHotKeyBox.Focus();
            spokenHotKeyBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedSpokenHotKeyProfile();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SpokenSelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            RenameSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedSpokenChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedSpokenChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedSpokenChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateSpokenSelectionStatus()
    {
        var profile = SelectedSpokenHotKey();
        if (profile == null)
        {
            UpdateSpokenSelectionStatus("No spoken hotkey selected.");
            return;
        }

        var count = spokenSelectedList == null ? 0 : spokenSelectedList.Items.Count;
        UpdateSpokenSelectionStatus(count + " reading" + (count == 1 ? "" : "s") + " selected for this spoken hotkey.");
    }

    private void UpdateSpokenSelectionStatus(string message)
    {
        if (spokenSelectionStatusLabel != null)
        {
            spokenSelectionStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private void AddFanProfile()
    {
        var profile = new FanProfileSetting
        {
            Name = "New fan profile",
            HotKey = "",
            Actions = new List<FanProfileActionSetting>()
        };
        fanProfiles.Add(profile);
        fanProfileList.Items.Add(profile);
        fanProfileList.SelectedItem = profile;
        fanProfileNameBox.Focus();
        fanProfileNameBox.SelectAll();
        UpdateFanProfileStatus("Created new fan profile.");
        SaveLivePreferences();
    }

    private void RemoveSelectedFanProfile()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("Select a fan profile first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileList.SelectedIndex;
        fanProfiles.Remove(profile);
        fanProfileList.Items.Remove(profile);
        if (fanProfileList.Items.Count > 0)
        {
            fanProfileList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileList.Items.Count - 1));
        }
        else
        {
            UpdateFanProfileEditor();
        }

        UpdateFanProfileStatus("Removed fan profile.");
        SaveLivePreferences();
    }

    private FanProfileSetting SelectedFanProfile()
    {
        return fanProfileList == null ? null : fanProfileList.SelectedItem as FanProfileSetting;
    }

    private void LoadSelectedFanProfile()
    {
        UpdateFanProfileEditor();
    }

    private void UpdateFanProfileEditor()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var profile = SelectedFanProfile();
            var enabled = profile != null;
            fanProfileNameBox.Enabled = enabled;
            fanProfileHotKeyBox.Enabled = enabled;
            fanProfileAvailableList.Enabled = enabled;
            fanProfileSelectedList.Enabled = enabled;
            fanProfileActionBox.Enabled = enabled;
            fanProfilePercentBox.Enabled = enabled;
            fanProfileToggleBox.Enabled = enabled;
            fanProfileSpeakBox.Enabled = enabled;
            fanProfileSpeechMessageBox.Enabled = enabled;
            fanProfileSoundBox.Enabled = enabled;
            fanProfileNameBox.Text = profile == null ? "" : profile.Name ?? "";
            fanProfileHotKeyBox.Text = profile == null ? "" : SensorReadoutForm.NormalizeHotKeyText(profile.HotKey);
            fanProfileToggleBox.Checked = profile != null && profile.ToggleAutomatic;
            fanProfileSpeakBox.Checked = profile == null || profile.Speak;
            fanProfileSpeechMessageBox.Text = profile == null ? "" : profile.SpeechMessage ?? "";
            PopulateSoundCombo(fanProfileSoundBox, profile == null ? "" : profile.SoundFile ?? "");
            PopulateFanProfileLists(profile);
            LoadSelectedFanProfileAction();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdateFanProfileStatus();
    }

    private void PopulateFanProfileLists(FanProfileSetting profile)
    {
        var selectedAvailableKey = SelectedFanControlChoiceKey(fanProfileAvailableList);
        var selectedProfileKey = SelectedFanControlChoiceKey(fanProfileSelectedList);
        fanProfileAvailableList.Items.Clear();
        fanProfileSelectedList.Items.Clear();
        var actions = profile == null || profile.Actions == null ? new List<FanProfileActionSetting>() : profile.Actions;
        var choices = fanControlRows
            .Select(r => new FanControlChoice(r, FanProfileFanControlDisplayName(r)))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => i.Name)
            .ToList();

        foreach (var action in actions)
        {
            action.FanControlKey = SensorReadoutForm.IdentifierFromSettingsKey(action.FanControlKey);
            var selectedChoice = choices.FirstOrDefault(i => string.Equals(i.Key, action.FanControlKey, StringComparison.OrdinalIgnoreCase));
            if (selectedChoice != null && !ContainsFanControlChoice(fanProfileSelectedList, selectedChoice.Key))
            {
                selectedChoice.Action = CloneFanProfileAction(action);
                fanProfileSelectedList.Items.Add(selectedChoice);
            }
            else if (selectedChoice == null && !ContainsFanControlChoice(fanProfileSelectedList, action.FanControlKey))
            {
                fanProfileSelectedList.Items.Add(FanControlChoice.Unresolved(action));
            }
        }

        foreach (var item in choices)
        {
            if (!ContainsFanControlChoice(fanProfileSelectedList, item.Key))
            {
                fanProfileAvailableList.Items.Add(item);
            }
        }

        if (fanProfileAvailableList.Items.Count > 0)
        {
            SelectFanControlChoiceByKey(fanProfileAvailableList, selectedAvailableKey);
        }
        if (fanProfileSelectedList.Items.Count > 0)
        {
            SelectFanControlChoiceByKey(fanProfileSelectedList, selectedProfileKey);
        }
    }

    private void SaveSelectedFanProfileHeader()
    {
        if (loadingPreferences)
        {
            return;
        }

        var profile = SelectedFanProfile();
        if (profile == null)
        {
            return;
        }

        profile.Name = fanProfileNameBox.Text.Trim();
        profile.HotKey = SensorReadoutForm.NormalizeHotKeyText(fanProfileHotKeyBox.Text);
        profile.ToggleAutomatic = fanProfileToggleBox.Checked;
        profile.Speak = fanProfileSpeakBox.Checked;
        profile.SpeechMessage = fanProfileSpeechMessageBox.Text.Trim();
        profile.SoundFile = SelectedSoundFile(fanProfileSoundBox);
        RefreshSelectedFanProfileListItem();
        SaveLivePreferences();
    }

    private void RefreshSelectedFanProfileListItem()
    {
        if (fanProfileList != null)
        {
            fanProfileList.Refresh();
        }
    }

    private void AddSelectedFanProfileChoice()
    {
        var profile = SelectedFanProfile();
        var item = fanProfileAvailableList.SelectedItem as FanControlChoice;
        if (profile == null || item == null)
        {
            UpdateFanProfileStatus("Select a fan profile and an available fan control first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileAvailableList.SelectedIndex;
        fanProfileAvailableList.Items.Remove(item);
        item.Action = new FanProfileActionSetting { FanControlKey = item.Key, Manual = fanProfileActionBox.SelectedIndex != 1, Percent = (int)fanProfilePercentBox.Value };
        fanProfileSelectedList.Items.Add(item);
        fanProfileSelectedList.SelectedItem = item;
        if (fanProfileAvailableList.Items.Count > 0)
        {
            fanProfileAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileAvailableList.Items.Count - 1));
        }
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void RemoveSelectedFanProfileChoice()
    {
        var item = fanProfileSelectedList.SelectedItem as FanControlChoice;
        if (item == null)
        {
            UpdateFanProfileStatus("Select a fan action first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = fanProfileSelectedList.SelectedIndex;
        fanProfileSelectedList.Items.Remove(item);
        AddAvailableFanControlChoiceSorted(item);
        if (fanProfileSelectedList.Items.Count > 0)
        {
            fanProfileSelectedList.SelectedIndex = Math.Max(0, Math.Min(index, fanProfileSelectedList.Items.Count - 1));
        }
        fanProfileAvailableList.SelectedItem = item;
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void MoveSelectedFanProfileChoice(int direction)
    {
        var index = fanProfileSelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= fanProfileSelectedList.Items.Count)
        {
            UpdateFanProfileStatus("Cannot move the selected fan action further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = fanProfileSelectedList.Items[index];
        fanProfileSelectedList.Items.RemoveAt(index);
        fanProfileSelectedList.Items.Insert(target, item);
        fanProfileSelectedList.SelectedIndex = target;
        SaveSelectedFanProfileActions();
        UpdateFanProfileStatus("Fan profile updated.");
    }

    private void LoadSelectedFanProfileAction()
    {
        if (loadingPreferences)
        {
            return;
        }

        var item = fanProfileSelectedList == null ? null : fanProfileSelectedList.SelectedItem as FanControlChoice;
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var action = item == null ? null : item.Action;
            fanProfileActionBox.SelectedIndex = action != null && !action.Manual ? 1 : 0;
            fanProfilePercentBox.Value = Math.Max(fanProfilePercentBox.Minimum, Math.Min(fanProfilePercentBox.Maximum, action == null ? 50 : action.Percent));
            fanProfilePercentBox.Enabled = item != null && fanProfileActionBox.SelectedIndex == 0;
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void SaveSelectedFanProfileAction()
    {
        if (loadingPreferences)
        {
            return;
        }

        var item = fanProfileSelectedList == null ? null : fanProfileSelectedList.SelectedItem as FanControlChoice;
        if (item == null)
        {
            fanProfilePercentBox.Enabled = fanProfileActionBox.SelectedIndex == 0;
            return;
        }

        item.Action = new FanProfileActionSetting
        {
            FanControlKey = item.Key,
            Manual = fanProfileActionBox.SelectedIndex != 1,
            Percent = (int)fanProfilePercentBox.Value
        };
        fanProfilePercentBox.Enabled = item.Action.Manual;
        fanProfileSelectedList.Refresh();
        SaveSelectedFanProfileActions();
    }

    private void SaveSelectedFanProfileActions()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            return;
        }

        profile.Actions = fanProfileSelectedList.Items
            .Cast<FanControlChoice>()
            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Key))
            .Select(i => CloneFanProfileAction(i.Action ?? new FanProfileActionSetting { FanControlKey = i.Key, Manual = true, Percent = 50 }))
            .ToList();
        RefreshSelectedFanProfileListItem();
        SaveLivePreferences();
        UpdateFanProfileStatus();
    }

    private void AddAvailableFanControlChoiceSorted(FanControlChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < fanProfileAvailableList.Items.Count &&
            FanControlChoice.Compare((FanControlChoice)fanProfileAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        fanProfileAvailableList.Items.Insert(insertIndex, choice);
    }

    private void ApplySelectedFanProfileFromPreferences()
    {
        CommitPreferences();
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("Select a fan profile first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var handler = ApplyFanProfileRequested;
        if (handler != null)
        {
            handler(CloneFanProfile(profile));
        }
        UpdateFanProfileStatus("Applied fan profile.");
    }

    public event Action<FanProfileSetting> ApplyFanProfileRequested;
    public event Action InstallToLocalAppDataRequested;
    public event Action UninstallLocalAppDataRequested;

    private void FanProfileAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(fanProfileAvailableList, SensorReadoutForm.L("ui.Find fan control", "Find fan control"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedFanProfileChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void FanProfileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            fanProfileNameBox.Focus();
            fanProfileNameBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            fanProfileHotKeyBox.Focus();
            fanProfileHotKeyBox.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedFanProfile();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void FanProfileSelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete || e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedFanProfileChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedFanProfileChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedFanProfileChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateFanProfileStatus()
    {
        var profile = SelectedFanProfile();
        if (profile == null)
        {
            UpdateFanProfileStatus("No fan profile selected.");
            return;
        }

        var count = fanProfileSelectedList == null ? 0 : fanProfileSelectedList.Items.Count;
        var key = count == 1
            ? "ui.{0} fan action selected for this fan profile."
            : "ui.{0} fan actions selected for this fan profile.";
        var fallback = count == 1
            ? "{0} fan action selected for this fan profile."
            : "{0} fan actions selected for this fan profile.";
        UpdateFanProfileStatus(string.Format(SensorReadoutForm.L(key, fallback), count));
    }

    private void UpdateFanProfileStatus(string message)
    {
        if (fanProfileStatusLabel != null)
        {
            fanProfileStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private void PopulateAlarmReadings()
    {
        if (alarmReadingBox == null)
        {
            return;
        }

        var selectedKey = SelectedAlarmReadingKey();
        alarmReadingBox.Items.Clear();
        foreach (var row in rows.Where(r => r.Value.HasValue))
        {
            alarmReadingBox.Items.Add(new TrayItemChoice(row, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames));
        }

        SelectAlarmReading(selectedKey);
    }

    private void AddAlarm()
    {
        var firstChoice = alarmReadingBox.Items.Count > 0 ? alarmReadingBox.Items[0] as TrayItemChoice : null;
        var alarm = new AlarmSetting
        {
            Name = "New alarm",
            ReadingKey = firstChoice == null ? "" : firstChoice.Key,
            Condition = "Above",
            Threshold = 80,
            ThresholdUnit = AlarmThresholdUnits(firstChoice == null ? null : RowForKey(firstChoice.Key)).FirstOrDefault() ?? "",
            Enabled = true,
            Speak = true,
            SoundFile = "",
            CooldownSeconds = 60
        };
        alarms.Add(alarm);
        var choice = new AlarmChoice(alarm, RowForKey, FormatAlarmThresholdForList);
        alarmList.Items.Add(choice);
        alarmList.SelectedItem = choice;
        alarmNameBox.Focus();
        alarmNameBox.SelectAll();
        UpdateAlarmStatus("Created new alarm.");
        SaveLivePreferences();
    }

    private void RemoveSelectedAlarm()
    {
        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            UpdateAlarmStatus("Select an alarm first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = alarmList.SelectedIndex;
        alarms.Remove(alarm);
        var selectedItem = alarmList.SelectedItem;
        alarmList.Items.Remove(selectedItem);
        if (alarmList.Items.Count > 0)
        {
            alarmList.SelectedIndex = Math.Max(0, Math.Min(index, alarmList.Items.Count - 1));
        }
        else
        {
            LoadSelectedAlarm();
        }

        UpdateAlarmStatus("Removed alarm.");
        SaveLivePreferences();
    }

    private AlarmSetting SelectedAlarm()
    {
        var choice = alarmList == null ? null : alarmList.SelectedItem as AlarmChoice;
        return choice == null ? null : choice.Alarm;
    }

    private void LoadSelectedAlarm()
    {
        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var alarm = SelectedAlarm();
            var enabled = alarm != null;
            alarmEnabledCheckBox.Enabled = enabled;
            alarmNameBox.Enabled = enabled;
            alarmReadingBox.Enabled = enabled;
            alarmConditionBox.Enabled = enabled;
            alarmThresholdBox.Enabled = enabled;
            alarmThresholdUnitBox.Enabled = enabled;
            alarmCooldownBox.Enabled = enabled;
            alarmSpeakCheckBox.Enabled = enabled;
            alarmSoundBox.Enabled = enabled;

            alarmEnabledCheckBox.Checked = alarm == null || alarm.Enabled;
            alarmNameBox.Text = alarm == null ? "" : alarm.Name ?? "";
            SelectAlarmReading(alarm == null ? "" : alarm.ReadingKey);
            RefreshAlarmThresholdUnitChoices(false);
            SelectAlarmThresholdUnit(alarm == null ? "" : alarm.ThresholdUnit);
            alarmConditionBox.SelectedIndex = AlarmConditionIndex(alarm == null ? "" : alarm.Condition);
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(alarm == null ? 80 : alarm.Threshold, SelectedAlarmThresholdUnit(), SelectedAlarmRow()), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
            alarmCooldownBox.Value = ClampDecimal(alarm == null ? 60 : alarm.CooldownSeconds, alarmCooldownBox.Minimum, alarmCooldownBox.Maximum);
            alarmSpeakCheckBox.Checked = alarm == null || alarm.Speak;
            PopulateSoundCombo(alarmSoundBox, alarm == null ? "" : alarm.SoundFile);
            lastAlarmReadingKey = SelectedAlarmReadingKey();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void SaveSelectedAlarm(bool refreshListItem = true)
    {
        if (loadingPreferences)
        {
            return;
        }

        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            return;
        }

        alarm.Enabled = alarmEnabledCheckBox.Checked;
        alarm.Name = alarmNameBox.Text.Trim();
        alarm.ReadingKey = SelectedAlarmReadingKey();
        alarm.Condition = AlarmConditionFromIndex(alarmConditionBox.SelectedIndex);
        alarm.ThresholdUnit = SelectedAlarmThresholdUnit();
        alarm.Threshold = AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), alarm.ThresholdUnit, SelectedAlarmRow());
        alarm.CooldownSeconds = Convert.ToInt32(alarmCooldownBox.Value);
        alarm.Speak = alarmSpeakCheckBox.Checked;
        alarm.SoundFile = SelectedSoundFile(alarmSoundBox);
        if (refreshListItem)
        {
            RefreshSelectedAlarmListItem();
        }
        SaveLivePreferences();
    }

    private void FocusAlarmName()
    {
        if (alarmNameBox == null || !alarmNameBox.Enabled)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        alarmNameBox.Focus();
        alarmNameBox.SelectAll();
    }

    private void FocusAlarmThreshold()
    {
        if (alarmThresholdBox == null || !alarmThresholdBox.Enabled)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        alarmThresholdBox.Focus();
        alarmThresholdBox.Select(0, alarmThresholdBox.Text.Length);
    }

    private void RefreshSelectedAlarmListItem()
    {
        if (alarmList == null)
        {
            return;
        }

        var index = alarmList.SelectedIndex;
        if (index < 0 || index >= alarmList.Items.Count)
        {
            return;
        }

        var choice = alarmList.Items[index] as AlarmChoice;
        if (choice == null || choice.Alarm == null)
        {
            alarmList.Refresh();
            return;
        }

        loadingPreferences = true;
        try
        {
            alarmList.Items[index] = new AlarmChoice(choice.Alarm, RowForKey, FormatAlarmThresholdForList);
            alarmList.SelectedIndex = index;
        }
        finally
        {
            loadingPreferences = false;
        }

        alarmList.Refresh();
        var notifyingList = alarmList as NotifyingListBox;
        if (notifyingList != null)
        {
            notifyingList.NotifyItemChanged(index);
        }
    }

    private string SelectedAlarmReadingKey()
    {
        var choice = alarmReadingBox == null ? null : alarmReadingBox.SelectedItem as TrayItemChoice;
        return choice == null ? "" : choice.Key;
    }

    private void SelectAlarmReading(string key)
    {
        if (alarmReadingBox == null || alarmReadingBox.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < alarmReadingBox.Items.Count; i++)
        {
            var choice = alarmReadingBox.Items[i] as TrayItemChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                alarmReadingBox.SelectedIndex = i;
                return;
            }
        }

        alarmReadingBox.SelectedIndex = 0;
    }

    private SensorRow SelectedAlarmRow()
    {
        var key = SelectedAlarmReadingKey();
        return RowForKey(key);
    }

    private SensorRow RowForKey(string key)
    {
        return rows.FirstOrDefault(r => string.Equals(SensorReadoutForm.RowSettingsKey(r), key, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedAlarmThresholdUnit()
    {
        return alarmThresholdUnitBox == null || alarmThresholdUnitBox.SelectedItem == null ? "" : alarmThresholdUnitBox.SelectedItem.ToString();
    }

    private void SelectAlarmThresholdUnit(string unit)
    {
        if (alarmThresholdUnitBox == null || alarmThresholdUnitBox.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < alarmThresholdUnitBox.Items.Count; i++)
        {
            if (string.Equals(alarmThresholdUnitBox.Items[i].ToString(), unit, StringComparison.OrdinalIgnoreCase))
            {
                alarmThresholdUnitBox.SelectedIndex = i;
                return;
            }
        }

        alarmThresholdUnitBox.SelectedIndex = 0;
    }

    private void RefreshAlarmThresholdUnitChoices(bool preserveBaseThreshold)
    {
        if (alarmThresholdUnitBox == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            var row = SelectedAlarmRow();
            var alarm = SelectedAlarm();
            var previousUnit = alarm == null ? "" : alarm.ThresholdUnit;
            var units = AlarmThresholdUnits(row);
            var canKeepUnit = units.Any(u => string.Equals(u, previousUnit, StringComparison.OrdinalIgnoreCase));
            var readingChanged = !string.Equals(lastAlarmReadingKey, SelectedAlarmReadingKey(), StringComparison.OrdinalIgnoreCase);
            var shouldResetForNewReading = preserveBaseThreshold && alarm != null && (!canKeepUnit || readingChanged);
            var baseThreshold = preserveBaseThreshold && alarm != null && canKeepUnit && !readingChanged
                ? alarm.Threshold
                : AlarmThresholdInputToBase(Convert.ToDouble(alarmThresholdBox.Value), SelectedAlarmThresholdUnit(), row);
            if (shouldResetForNewReading)
            {
                baseThreshold = DefaultAlarmThresholdForRow(row);
                alarm.Threshold = baseThreshold;
            }
            alarmThresholdUnitBox.Items.Clear();
            alarmThresholdUnitBox.Items.AddRange(units.Cast<object>().ToArray());
            SelectAlarmThresholdUnit(string.IsNullOrWhiteSpace(previousUnit) ? units.FirstOrDefault() : previousUnit);
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(baseThreshold, SelectedAlarmThresholdUnit(), row), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private void RefreshAlarmThresholdForSelectedUnit()
    {
        if (loadingPreferences)
        {
            return;
        }

        var alarm = SelectedAlarm();
        if (alarm == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            alarmThresholdBox.Value = ClampDecimal(AlarmThresholdBaseToInput(alarm.Threshold, SelectedAlarmThresholdUnit(), SelectedAlarmRow()), alarmThresholdBox.Minimum, alarmThresholdBox.Maximum);
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private static string[] AlarmThresholdUnits(SensorRow row)
    {
        if (row == null)
        {
            return new[] { "" };
        }

        var name = (row.Name ?? "").ToLowerInvariant();
        var display = (row.DisplayValue ?? "").ToLowerInvariant();
        if (row.Type == "Temperature")
        {
            return new[] { "C", "F" };
        }

        if (row.Type == "Fan")
        {
            return new[] { "RPM" };
        }

        if (name.Contains("link speed"))
        {
            return new[] { "bits/s", "Kbit/s", "Mbit/s", "Gbit/s" };
        }

        if (name.Contains("rate") || display.Contains("/s"))
        {
            return new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        }

        if (display.Contains("%") || row.Type == "Load" || row.Type == "Performance" || row.Type == "Control")
        {
            return new[] { "%" };
        }

        if (display.Contains("gb") || display.Contains("mb") || display.Contains("kb") || display.Contains("tb") || name.Contains("space") || name.Contains("size") || name.Contains("memory"))
        {
            return new[] { "bytes", "KB", "MB", "GB", "TB", "%" };
        }

        return new[] { "value" };
    }

    private static double AlarmThresholdInputToBase(double value, string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim();
        if (string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return (value - 32.0) * 5.0 / 9.0;
        }

        return value * AlarmThresholdMultiplier(unit, row);
    }

    private static double AlarmThresholdBaseToInput(double value, string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim();
        if (string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return value * 9.0 / 5.0 + 32.0;
        }

        var multiplier = AlarmThresholdMultiplier(unit, row);
        return multiplier == 0 ? value : value / multiplier;
    }

    private string FormatAlarmThresholdForList(AlarmSetting alarm)
    {
        if (alarm == null)
        {
            return "";
        }

        var unit = alarm.ThresholdUnit ?? "";
        var row = RowForKey(alarm.ReadingKey);
        var value = AlarmThresholdBaseToInput(alarm.Threshold, unit, row);
        var suffix = string.IsNullOrWhiteSpace(unit) || unit == "value" ? "" : " " + unit;
        return Math.Round(value, 2).ToString("0.##") + suffix;
    }

    private static double AlarmThresholdMultiplier(string unit, SensorRow row)
    {
        unit = (unit ?? "").Trim().ToUpperInvariant();
        if (unit == "BITS/S") return 1.0;
        if (unit == "KBIT/S") return 1000.0;
        if (unit == "MBIT/S") return 1000.0 * 1000.0;
        if (unit == "GBIT/S") return 1000.0 * 1000.0 * 1000.0;
        if (unit == "B/S") return 1.0;
        if (unit == "KB/S") return 1000.0;
        if (unit == "MB/S") return 1000.0 * 1000.0;
        if (unit == "GB/S") return 1000.0 * 1000.0 * 1000.0;
        if (unit == "KB") return 1024.0;
        if (unit == "MB") return 1024.0 * 1024.0;
        if (unit == "GB") return 1024.0 * 1024.0 * 1024.0;
        if (unit == "TB") return 1024.0 * 1024.0 * 1024.0 * 1024.0;
        return 1.0;
    }

    private static double DefaultAlarmThresholdForRow(SensorRow row)
    {
        if (row == null)
        {
            return 80;
        }

        if (row.Type == "Temperature")
        {
            return 80;
        }

        if (row.Type == "Fan")
        {
            return 1000;
        }

        var units = AlarmThresholdUnits(row);
        return units.Any(u => string.Equals(u, "%", StringComparison.OrdinalIgnoreCase)) ? 80 : 1;
    }

    private static int AlarmConditionIndex(string condition)
    {
        condition = SensorReadoutForm.NormalizeAlarmCondition(condition);
        if (condition == "Below") return 1;
        if (condition == "Equal") return 2;
        return 0;
    }

    private static string AlarmConditionFromIndex(int index)
    {
        if (index == 1) return "Below";
        if (index == 2) return "Equal";
        return "Above";
    }

    private void UpdateAlarmStatus(string message)
    {
        if (alarmStatusLabel != null)
        {
            alarmStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return minimum;
        }

        var decimalValue = Convert.ToDecimal(value);
        if (decimalValue < minimum) return minimum;
        if (decimalValue > maximum) return maximum;
        return decimalValue;
    }

    private List<AlarmSetting> CurrentAlarms()
    {
        return CloneAlarms(alarms)
            .Where(a => !string.IsNullOrWhiteSpace(a.ReadingKey))
            .ToList();
    }

    private static List<AlarmSetting> CloneAlarms(IEnumerable<AlarmSetting> source)
    {
        return (source ?? new List<AlarmSetting>())
            .Where(a => a != null)
            .Select(a => new AlarmSetting
            {
                Name = a.Name ?? "",
                ReadingKey = a.ReadingKey ?? "",
                Condition = SensorReadoutForm.NormalizeAlarmCondition(a.Condition),
                Threshold = a.Threshold,
                ThresholdUnit = a.ThresholdUnit ?? "",
                Enabled = a.Enabled,
                Speak = a.Speak,
                SoundFile = System.IO.Path.GetFileName(a.SoundFile ?? ""),
                CooldownSeconds = Math.Max(0, Math.Min(86400, a.CooldownSeconds))
            })
            .ToList();
    }

    private List<SpokenHotKeySetting> CurrentSpokenHotKeys()
    {
        return CloneSpokenHotKeys(spokenHotKeys)
            .Where(p => !string.IsNullOrWhiteSpace(p.HotKey) || (p.ReadingKeys != null && p.ReadingKeys.Count > 0) || !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }

    private static List<SpokenHotKeySetting> CloneSpokenHotKeys(IEnumerable<SpokenHotKeySetting> source)
    {
        return (source ?? new List<SpokenHotKeySetting>())
            .Where(p => p != null)
            .Select(p => new SpokenHotKeySetting
            {
                Name = p.Name ?? "",
                HotKey = SensorReadoutForm.NormalizeHotKeyText(p.HotKey),
                ReadingKeys = (p.ReadingKeys ?? new List<string>())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();
    }

    private List<FanProfileSetting> CurrentFanProfiles()
    {
        return CloneFanProfiles(fanProfiles)
            .Where(p => !string.IsNullOrWhiteSpace(p.HotKey) || !string.IsNullOrWhiteSpace(p.SoundFile) || p.ToggleAutomatic || !p.Speak || !string.IsNullOrWhiteSpace(p.SpeechMessage) || (p.Actions != null && p.Actions.Count > 0) || !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }

    private static FanProfileSetting CloneFanProfile(FanProfileSetting profile)
    {
        if (profile == null)
        {
            return null;
        }

        return new FanProfileSetting
        {
            Name = profile.Name ?? "",
            HotKey = SensorReadoutForm.NormalizeHotKeyText(profile.HotKey),
            SoundFile = System.IO.Path.GetFileName(profile.SoundFile ?? ""),
            ToggleAutomatic = profile.ToggleAutomatic,
            Speak = profile.Speak,
            SpeechMessage = profile.SpeechMessage ?? "",
            Actions = (profile.Actions ?? new List<FanProfileActionSetting>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.FanControlKey))
                .Select(CloneFanProfileAction)
                .ToList()
        };
    }

    private static List<FanProfileSetting> CloneFanProfiles(IEnumerable<FanProfileSetting> source)
    {
        return (source ?? new List<FanProfileSetting>())
            .Where(p => p != null)
            .Select(CloneFanProfile)
            .ToList();
    }

    private static FanProfileActionSetting CloneFanProfileAction(FanProfileActionSetting action)
    {
        if (action == null)
        {
            return new FanProfileActionSetting();
        }

        return new FanProfileActionSetting
        {
            FanControlKey = SensorReadoutForm.IdentifierFromSettingsKey(action.FanControlKey),
            Manual = action.Manual,
            Percent = Math.Max(0, Math.Min(100, action.Percent))
        };
    }

    private List<string> CurrentHiddenReadingKeys()
    {
        return hiddenItemsList.CheckedItems
            .Cast<object>()
            .Select(i => Convert.ToString(i))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();
    }

    private Dictionary<string, string> CurrentReadingSpeechLabels()
    {
        return readingSpeechLabels
            .Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Value))
            .ToDictionary(i => i.Key, i => i.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string SelectedFanControlChoiceKey(ListBox list)
    {
        var choice = list == null ? null : list.SelectedItem as FanControlChoice;
        return choice == null ? "" : choice.Key;
    }

    private static bool ContainsFanControlChoice(ListBox list, string key)
    {
        if (list == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return list.Items.Cast<object>()
            .OfType<FanControlChoice>()
            .Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static void SelectFanControlChoiceByKey(ListBox list, string key)
    {
        if (list == null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            var choice = list.Items[i] as FanControlChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }
    }

    private static void AddStarterFanProfiles(List<FanProfileSetting> target)
    {
        if (target == null)
        {
            return;
        }

        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Everyday", "Everyday") });
        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Gaming/rendering", "Gaming/rendering") });
        target.Add(new FanProfileSetting { Name = SensorReadoutForm.L("ui.Reset to automatic", "Reset to automatic") });
    }

    private List<SensorRow> BuildFanProfileFanControlRows(IEnumerable<SensorRow> latestRows)
    {
        var source = (latestRows ?? Enumerable.Empty<SensorRow>()).ToList();
        var showStopped = liveSettings != null && liveSettings.ShowStoppedFans;
        var hiddenFanControlKeys = HiddenFanControlKeys(source);
        return source
            .Where(r => r.Type == "Fan Control")
            .Where(r => showStopped || ShouldShowFanProfileFanControl(r, source))
            .Where(r => showStopped || !hiddenFanControlKeys.Contains(r.Identifier ?? ""))
            .OrderBy(r => SensorReadoutForm.ControlSortKey(r.Identifier))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();
    }

    private HashSet<string> HiddenFanControlKeys(List<SensorRow> source)
    {
        var hidden = new HashSet<string>(hiddenReadingKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in (source ?? new List<SensorRow>()).Where(r => r.Type == "Fan"))
        {
            if (!hidden.Contains("row|" + SensorReadoutForm.RowSettingsKey(row)))
            {
                continue;
            }

            var controlIdentifier = SensorReadoutForm.GuessControlIdentifier(row.Identifier);
            if (!string.IsNullOrWhiteSpace(controlIdentifier))
            {
                keys.Add(controlIdentifier);
            }
        }

        foreach (var row in (source ?? new List<SensorRow>()).Where(r => r.Type == "Fan Control"))
        {
            if (hidden.Contains("row|" + SensorReadoutForm.RowSettingsKey(row)) && !string.IsNullOrWhiteSpace(row.Identifier))
            {
                keys.Add(row.Identifier);
            }
        }

        return keys;
    }

    private bool ShouldShowFanProfileFanControl(SensorRow control, List<SensorRow> source)
    {
        if (SensorReadoutForm.IsGpuControl(control == null ? "" : control.Identifier))
        {
            return true;
        }

        var rpm = GetFanRpmForControl(control == null ? "" : control.Identifier, source);
        return rpm.HasValue && rpm.Value > 0;
    }

    private float? GetFanRpmForControl(string controlIdentifier, List<SensorRow> source)
    {
        var sourceRows = source ?? new List<SensorRow>();
        var fanIdentifier = SensorReadoutForm.GuessFanIdentifier(controlIdentifier);
        var row = sourceRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(r.Identifier, fanIdentifier, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            return row.Value;
        }

        var baseName = SensorReadoutForm.BaseFanControlName(controlIdentifier);
        row = sourceRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(SensorReadoutForm.BaseFanControlName(r.Name), baseName, StringComparison.OrdinalIgnoreCase));
        return row == null ? (float?)null : row.Value;
    }

    private string FanProfileFanControlDisplayName(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        var baseName = SensorReadoutForm.BaseFanControlName(row.Name);
        string label;
        if (!fanLabels.TryGetValue(row.Identifier ?? "", out label) || string.IsNullOrWhiteSpace(label))
        {
            fanLabels.TryGetValue(SensorReadoutForm.GuessFanIdentifier(row.Identifier), out label);
        }
        label = string.IsNullOrWhiteSpace(label) ? baseName : label.Trim();

        var name = string.Equals(label, baseName, StringComparison.OrdinalIgnoreCase) ? baseName : label + ", " + baseName;
        var rpm = GetFanRpmForControl(row.Identifier, rows);
        if (rpm.HasValue)
        {
            name += ", " + Math.Round(rpm.Value, 0).ToString("0") + " RPM";
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            name += ", " + row.DisplayValue;
        }

        return name;
    }

    private sealed class FanControlChoice
    {
        public readonly string Key;
        public readonly string Hardware;
        public readonly string Name;
        public FanProfileActionSetting Action;
        private readonly bool unresolved;

        public FanControlChoice(SensorRow row, string displayName)
        {
            Key = row == null ? "" : row.Identifier ?? "";
            Hardware = row == null ? "" : row.Hardware ?? "";
            Name = string.IsNullOrWhiteSpace(displayName) ? row == null ? "" : row.Name ?? "" : displayName;
            Action = new FanProfileActionSetting { FanControlKey = Key, Manual = true, Percent = 50 };
        }

        private FanControlChoice(string key, FanProfileActionSetting action)
        {
            Key = key ?? "";
            Hardware = "";
            Name = "Missing fan control: " + Key;
            Action = CloneFanProfileAction(action);
            unresolved = true;
        }

        public static FanControlChoice Unresolved(FanProfileActionSetting action)
        {
            action = CloneFanProfileAction(action);
            return new FanControlChoice(action.FanControlKey, action);
        }

        public override string ToString()
        {
            if (unresolved)
            {
                return Name;
            }

            var actionText = Action == null || !Action.Manual ? "Auto" : Math.Max(0, Math.Min(100, Action.Percent)) + "%";
            return Name + " [" + actionText + "]";
        }

        public static int Compare(FanControlChoice left, FanControlChoice right)
        {
            var hardware = string.Compare(left == null ? "" : left.Hardware, right == null ? "" : right.Hardware, StringComparison.OrdinalIgnoreCase);
            if (hardware != 0) return hardware;
            return string.Compare(left == null ? "" : left.Name, right == null ? "" : right.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TrayItemChoice
    {
        public readonly string Key;
        public readonly string Type;
        public readonly string Hardware;
        public readonly string Name;
        private readonly Dictionary<string, string> speechLabels;
        private readonly Func<bool> includeDeviceNames;
        private readonly string label;
        private readonly bool unresolved;
        public bool ShowSpeechPreview;

        private TrayItemChoice(string key, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            Key = key ?? "";
            this.speechLabels = speechLabels ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.includeDeviceNames = includeDeviceNames ?? (delegate { return true; });
            var parts = Key.Split('|');
            Type = parts.Length > 0 ? parts[0] : "";
            Hardware = parts.Length > 1 ? parts[1] : "";
            Name = parts.Length > 2 ? parts[2] : "";
            unresolved = true;
            label = string.IsNullOrWhiteSpace(Hardware) ? Key : Hardware + " - " + Name;
        }

        public TrayItemChoice(SensorRow row, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            Key = SensorReadoutForm.RowSettingsKey(row);
            this.speechLabels = speechLabels ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.includeDeviceNames = includeDeviceNames ?? (delegate { return true; });
            Type = row.Type;
            Hardware = SensorReadoutForm.ShortHardwareName(row.Hardware);
            Name = SensorReadoutForm.CleanSensorName(row.Name);
            label = "";
        }

        public static TrayItemChoice Unresolved(string key, Dictionary<string, string> speechLabels, Func<bool> includeDeviceNames)
        {
            return new TrayItemChoice(key, speechLabels, includeDeviceNames);
        }

        public override string ToString()
        {
            var display = unresolved
                ? SensorReadoutForm.L("ui.Missing reading -", "Missing reading -") + " " + label
                : SensorReadoutForm.TrayChoiceLabel(Hardware, Name, Type);
            return ShowSpeechPreview
                ? display + " (" + SensorReadoutForm.L("ui.speaks:", "speaks:") + " " + SensorReadoutForm.SpeechPreviewLabel(Type, Hardware, Name, Key, speechLabels, includeDeviceNames()) + ")"
                : display;
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

    private sealed class AlarmChoice
    {
        private readonly Func<string, SensorRow> rowForKey;
        private readonly Func<AlarmSetting, string> thresholdText;
        public readonly AlarmSetting Alarm;

        public AlarmChoice(AlarmSetting alarm, Func<string, SensorRow> rowForKey, Func<AlarmSetting, string> thresholdText)
        {
            Alarm = alarm;
            this.rowForKey = rowForKey;
            this.thresholdText = thresholdText;
        }

        public override string ToString()
        {
            var alarm = Alarm;
            var name = alarm == null || string.IsNullOrWhiteSpace(alarm.Name) ? "Alarm" : alarm.Name.Trim();
            var condition = alarm == null || string.IsNullOrWhiteSpace(alarm.Condition) ? "Above" : SensorReadoutForm.NormalizeAlarmCondition(alarm.Condition);
            var threshold = thresholdText == null ? "" : thresholdText(alarm);
            var cooldown = alarm == null || alarm.CooldownSeconds <= 0 ? 0 : alarm.CooldownSeconds;
            return name + " (" + condition + " " + threshold + ", " + cooldown + "s cooldown)";
        }
    }

    private sealed class NotifyingListBox : ListBox
    {
        public void NotifyItemChanged(int index)
        {
            var childId = index + 1;
            AccessibilityNotifyClients(AccessibleEvents.NameChange, childId);
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, childId);
        }
    }
}
