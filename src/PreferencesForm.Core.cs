using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
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

        if (keyData == Keys.F3 && IsSelectedPreferencesTab("Language editor"))
        {
            ShowLanguageEntrySearch();
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
            if (IsSelectedPreferencesTab("Hotkeys") && HandleHotkeysTabAltNumberShortcut(key))
            {
                return true;
            }

            if (IsSelectedPreferencesTab("Fan profiles") && HandleFanProfilesTabAltNumberShortcut(key))
            {
                return true;
            }

            if (PerformShortcutButton(Controls, key))
            {
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleHotkeysTabAltNumberShortcut(Keys key)
    {
        if (key == Keys.D1 || key == Keys.NumPad1)
        {
            FocusShowHideHotKeyBox();
            return true;
        }

        if ((key == Keys.D2 || key == Keys.NumPad2) && spokenHotKeyList != null)
        {
            spokenHotKeyList.Focus();
            return true;
        }

        if ((key == Keys.D3 || key == Keys.NumPad3) && spokenAvailableList != null)
        {
            spokenAvailableList.Focus();
            return true;
        }

        if ((key == Keys.D4 || key == Keys.NumPad4) && spokenSelectedList != null)
        {
            spokenSelectedList.Focus();
            return true;
        }

        return false;
    }

    private bool HandleFanProfilesTabAltNumberShortcut(Keys key)
    {
        if ((key == Keys.D1 || key == Keys.NumPad1) && fanProfileList != null)
        {
            fanProfileList.Focus();
            return true;
        }

        if ((key == Keys.D2 || key == Keys.NumPad2) && fanProfileHotKeyBox != null)
        {
            fanProfileHotKeyBox.Focus();
            fanProfileHotKeyBox.SelectAll();
            return true;
        }

        if ((key == Keys.D3 || key == Keys.NumPad3) && fanProfileAvailableList != null)
        {
            fanProfileAvailableList.Focus();
            return true;
        }

        if ((key == Keys.D4 || key == Keys.NumPad4) && fanProfileSelectedList != null)
        {
            fanProfileSelectedList.Focus();
            return true;
        }

        return false;
    }

    private bool IsSelectedPreferencesTab(string tabName)
    {
        return preferencesTabs != null &&
            preferencesTabs.SelectedTab != null &&
            string.Equals(preferencesTabs.SelectedTab.Name, tabName, StringComparison.OrdinalIgnoreCase);
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
        else if (key == Keys.D0)
        {
            index = 9;
        }
        else if (key >= Keys.NumPad1 && key <= Keys.NumPad9)
        {
            index = key - Keys.NumPad1;
        }
        else if (key == Keys.NumPad0)
        {
            index = 9;
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
        if (ShouldBypassHotKeyCapture(keyData))
        {
            return false;
        }

        if (key == Keys.Back || key == Keys.Delete)
        {
            box.Text = "";
            return true;
        }

        if (SensorReadoutForm.IsModifierOnlyHotKeyData(keyData))
        {
            return true;
        }

        var text = SensorReadoutForm.HotKeyTextFromKeyData(keyData);
        if (string.IsNullOrWhiteSpace(text))
        {
            System.Media.SystemSounds.Beep.Play();
            return true;
        }

        box.Text = text;
        return true;
    }

    private static bool ShouldBypassHotKeyCapture(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Tab || key == Keys.Escape)
        {
            return true;
        }

        return (keyData & Keys.Alt) == Keys.Alt && key == Keys.F4;
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
            AccessibleDescription = "Press a key combination with at least two modifiers, such as Control Shift F1 or Control Alt F1. Unsafe Windows shortcuts are rejected. Use the Clear button to disable it."
        };
        box.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (ShouldBypassHotKeyCapture(e.KeyData))
            {
                return;
            }

            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                box.Text = "";
                return;
            }

            if (SensorReadoutForm.IsModifierOnlyHotKeyData(e.KeyData))
            {
                return;
            }

            var text = SensorReadoutForm.HotKeyTextFromKeyEvent(e);
            if (!string.IsNullOrWhiteSpace(text))
            {
                box.Text = text;
            }
            else
            {
                System.Media.SystemSounds.Beep.Play();
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
        liveSettings.CategorySpeechMode = CategorySpeechMode;
        liveSettings.FallbackCategorySpeechEnabled = FallbackCategorySpeechEnabled;
        liveSettings.VisualSpokenFeedbackEnabled = VisualSpokenFeedbackEnabled;
        liveSettings.VisualSpokenFeedbackPlacement = VisualSpokenFeedbackPlacement;
        liveSettings.VisualSpokenFeedbackTimeoutSeconds = VisualSpokenFeedbackTimeoutSeconds;
        liveSettings.TrayStatusEnabled = TrayStatusEnabled;
        liveSettings.TrayTooltipShowsPartialReadings = TrayTooltipShowsPartialReadings;
        liveSettings.TraySpeechSkipsUnavailableReadings = TraySpeechSkipsUnavailableReadings;
        liveSettings.ReadingTreeExpansionMode = ReadingTreeExpansionMode;
        liveSettings.RunAtStartup = RunAtStartup;
        liveSettings.StartMinimizedToTray = StartMinimizedToTray;
        liveSettings.CheckForUpdatesAtStartup = CheckForUpdatesAtStartup;
        liveSettings.UpdateCheckFrequency = UpdateCheckFrequency;
        liveSettings.InstallUpdatesQuietly = InstallUpdatesQuietly;
        liveSettings.ShowUpdateInstallConfirmation = ShowUpdateInstallConfirmation;
        liveSettings.ConfirmSpokenHotKeyProfileRemoval = ConfirmSpokenHotKeyProfileRemoval;
        liveSettings.ShowTipsOnStartup = ShowTipsOnStartup;
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
        TrayItemKeys = new List<string>(liveSettings.TrayItemKeys ?? new List<string>());

        var currentSpokenHotKeys = CurrentSpokenHotKeys();
        SpokenHotKeys = CloneSpokenHotKeys(currentSpokenHotKeys);
        liveSettings.SpokenHotKeys = CloneSpokenHotKeys(currentSpokenHotKeys);
        liveSettings.FanProfileStarterProfilesInitialized = fanProfileStarterProfilesInitialized;
        var currentFanProfiles = CurrentFanProfiles();
        FanProfiles = CloneFanProfiles(currentFanProfiles);
        liveSettings.FanProfiles = CloneFanProfiles(currentFanProfiles);
        liveSettings.ShowStoppedFans = fanProfileShowStoppedBox.Checked;
        var currentAlarms = CurrentAlarms();
        Alarms = CloneAlarms(currentAlarms);
        liveSettings.Alarms = CloneAlarms(currentAlarms);
        HiddenReadingKeys = CurrentHiddenReadingKeys();
        liveSettings.HiddenReadingKeys = new List<string>(HiddenReadingKeys);
        CategoryOrderKeys = CurrentCategoryOrderKeys();
        liveSettings.CategoryOrderKeys = new List<string>(CategoryOrderKeys);
        HiddenCategoryKeys = CurrentHiddenCategoryKeys();
        liveSettings.HiddenCategoryKeys = new List<string>(HiddenCategoryKeys);
        ReadingSpeechLabels = CurrentReadingSpeechLabels();
        liveSettings.ReadingSpeechLabels = new Dictionary<string, string>(ReadingSpeechLabels, StringComparer.OrdinalIgnoreCase);
        liveSettings.PlugInsEnabled = CurrentPlugInSettings();
        SensorReadoutForm.SaveSettings(liveSettings);
        var handler = LivePreferencesSaved;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
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
        CategoryOrderKeys = CurrentCategoryOrderKeys();
        HiddenCategoryKeys = CurrentHiddenCategoryKeys();
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

    private static string[] CategorySpeechModeOptions()
    {
        return new[]
        {
            SensorReadoutForm.L("ui.Full category guidance", "Full category guidance"),
            SensorReadoutForm.L("ui.Brief category and shortcut", "Brief category and shortcut"),
            SensorReadoutForm.L("ui.Off", "Off")
        };
    }

    private static int CategorySpeechModeIndex(string value)
    {
        switch (SensorReadoutForm.NormalizeCategorySpeechMode(value))
        {
            case SensorReadoutForm.CategorySpeechBrief: return 1;
            case SensorReadoutForm.CategorySpeechOff: return 2;
            default: return 0;
        }
    }

    private static string CategorySpeechModeFromIndex(int index)
    {
        switch (index)
        {
            case 1: return SensorReadoutForm.CategorySpeechBrief;
            case 2: return SensorReadoutForm.CategorySpeechOff;
            default: return SensorReadoutForm.CategorySpeechFull;
        }
    }

    private static string[] VisualSpokenFeedbackPlacementOptions()
    {
        return new[]
        {
            SensorReadoutForm.L("ui.Bottom right", "Bottom right"),
            SensorReadoutForm.L("ui.Bottom left", "Bottom left"),
            SensorReadoutForm.L("ui.Top right", "Top right"),
            SensorReadoutForm.L("ui.Top left", "Top left"),
            SensorReadoutForm.L("ui.Center", "Center")
        };
    }

    private static int VisualSpokenFeedbackPlacementIndex(string value)
    {
        switch (SensorReadoutForm.NormalizeVisualSpokenFeedbackPlacement(value))
        {
            case "BottomLeft": return 1;
            case "TopRight": return 2;
            case "TopLeft": return 3;
            case "Center": return 4;
            default: return 0;
        }
    }

    private static string VisualSpokenFeedbackPlacementFromIndex(int index)
    {
        switch (index)
        {
            case 1: return "BottomLeft";
            case 2: return "TopRight";
            case 3: return "TopLeft";
            case 4: return "Center";
            default: return "BottomRight";
        }
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
}
