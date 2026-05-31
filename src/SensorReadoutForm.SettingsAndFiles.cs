using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    public static string HotKeyTextFromKeyEvent(KeyEventArgs e)
    {
        if (e == null)
        {
            return "";
        }

        return HotKeyTextFromKeyData(e.KeyData);
    }

    public static string HotKeyTextFromKeyData(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
            key == Keys.LWin || key == Keys.RWin || key == Keys.None)
        {
            return "";
        }

        if (!GlobalHotKey.IsAllowedBaseKey(key))
        {
            return "";
        }

        var modifiers = 0u;
        var parts = new List<string>();
        if ((keyData & Keys.Control) == Keys.Control)
        {
            modifiers |= NativeMethods.ModControl;
            parts.Add("Ctrl");
        }
        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            modifiers |= NativeMethods.ModAlt;
            parts.Add("Alt");
        }
        if ((keyData & Keys.Shift) == Keys.Shift)
        {
            modifiers |= NativeMethods.ModShift;
            parts.Add("Shift");
        }
        if ((modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModWin)) == 0)
        {
            return "";
        }

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

    public static string NormalizeReadingTreeExpansionMode(string mode)
    {
        if (string.Equals(mode, ReadingTreeExpansionCollapsed, StringComparison.OrdinalIgnoreCase))
        {
            return ReadingTreeExpansionCollapsed;
        }

        if (string.Equals(mode, ReadingTreeExpansionRemember, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Last", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "LastState", StringComparison.OrdinalIgnoreCase))
        {
            return ReadingTreeExpansionRemember;
        }

        return ReadingTreeExpansionExpanded;
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
            var machineConfigExists = System.IO.File.Exists(path);
            AppSettings loaded = null;
            if (machineConfigExists)
            {
                loaded = JsonConvert.DeserializeObject<AppSettings>(System.IO.File.ReadAllText(path));
            }

            if (loaded == null)
            {
                loaded = new AppSettings();
                SetDefaultLanguagePreference(loaded);
            }

            NormalizeSettings(loaded);
            var shared = LoadSharedSettings();
            if (shared == null)
            {
                SaveSharedSettings(ExtractSharedSettings(loaded));
            }
            else
            {
                ApplySharedSettings(loaded, shared);
                NormalizeSettings(loaded);
            }

            if (!machineConfigExists)
            {
                loaded.RunAtStartup = false;
            }

            if (!loaded.LanguagePreferenceInitialized)
            {
                SetDefaultLanguagePreference(loaded);
            }

            SaveSettings(loaded);
            return loaded;
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
        if (string.Equals(language, "pt", StringComparison.OrdinalIgnoreCase))
        {
            return "Portugues.txt";
        }

        return "";
    }

    public static void SaveSettings(AppSettings value)
    {
        try
        {
            NormalizeSettings(value);
            SaveSharedSettings(ExtractSharedSettings(value));
            var path = GetConfigFilePath();
            EnsureDirectoryForFile(path);
            System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(ExtractMachineSettings(value), Formatting.Indented));
        }
        catch
        {
        }
    }

    public static void SetLoggingLevelPreference(string level)
    {
        var normalized = NormalizeLoggingLevel(level);
        var value = LoadSettings();
        value.LoggingLevel = normalized;
        SaveSettings(value);
    }

    private static SharedAppSettings LoadSharedSettings()
    {
        var path = GetSharedConfigFilePath();
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var shared = JsonConvert.DeserializeObject<SharedAppSettings>(System.IO.File.ReadAllText(path));
        if (shared != null)
        {
            NormalizeSharedSettings(shared);
        }

        return shared;
    }

    private static void SaveSharedSettings(SharedAppSettings value)
    {
        if (value == null)
        {
            return;
        }

        NormalizeSharedSettings(value);
        var path = GetSharedConfigFilePath();
        EnsureDirectoryForFile(path);
        System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
    }

    private static SharedAppSettings ExtractSharedSettings(AppSettings value)
    {
        value = value ?? new AppSettings();
        return new SharedAppSettings
        {
            AutoRefreshEnabled = value.AutoRefreshEnabled,
            RefreshWhileFocused = value.RefreshWhileFocused,
            RefreshIntervalSeconds = value.RefreshIntervalSeconds,
            ReadingTreeExpansionMode = value.ReadingTreeExpansionMode,
            ReadingTreeLastExpanded = value.ReadingTreeLastExpanded,
            TemperatureUnit = value.TemperatureUnit,
            DecimalSeparator = value.DecimalSeparator,
            LanguageFile = value.LanguageFile,
            LanguagePreferenceInitialized = value.LanguagePreferenceInitialized,
            ShowHideHotKey = value.ShowHideHotKey,
            SpeakTrayHotKey = value.SpeakTrayHotKey,
            HotKeyCopyDoublePressMs = value.HotKeyCopyDoublePressMs,
            StartupSpeechEnabled = value.StartupSpeechEnabled,
            StartupSpeechMessage = value.StartupSpeechMessage,
            SpeechIncludesDeviceNames = value.SpeechIncludesDeviceNames,
            TrayStatusEnabled = value.TrayStatusEnabled,
            TrayTooltipShowsPartialReadings = value.TrayTooltipShowsPartialReadings,
            TraySpeechSkipsUnavailableReadings = value.TraySpeechSkipsUnavailableReadings,
            StartMinimizedToTray = value.StartMinimizedToTray,
            CheckForUpdatesAtStartup = value.CheckForUpdatesAtStartup,
            UpdateCheckFrequency = value.UpdateCheckFrequency,
            InstallUpdatesQuietly = value.InstallUpdatesQuietly,
            ShowUpdateInstallConfirmation = value.ShowUpdateInstallConfirmation,
            ConfirmSpokenHotKeyProfileRemoval = value.ConfirmSpokenHotKeyProfileRemoval,
            InitialSetupWizardDismissed = value.InitialSetupWizardDismissed,
            ShowTipsOnStartup = value.ShowTipsOnStartup,
            LastLanguageEditorFile = value.LastLanguageEditorFile,
            LastLanguageEditorKey = value.LastLanguageEditorKey,
            LastAutomaticUpdateCheckUtc = value.LastAutomaticUpdateCheckUtc,
            UpdateAvailableSoundFile = value.UpdateAvailableSoundFile,
            DiagnosticsSpeakProgress = value.DiagnosticsSpeakProgress,
            DiagnosticsPlaySounds = value.DiagnosticsPlaySounds,
            DiagnosticsStartSoundFile = value.DiagnosticsStartSoundFile,
            DiagnosticsCompleteSoundFile = value.DiagnosticsCompleteSoundFile,
            StartupSoundFile = value.StartupSoundFile,
            ShutdownSoundFile = value.ShutdownSoundFile
        };
    }

    private static MachineAppSettings ExtractMachineSettings(AppSettings value)
    {
        value = value ?? new AppSettings();
        return new MachineAppSettings
        {
            RunAtStartup = value.RunAtStartup,
            PrerequisitesPromptShown = value.PrerequisitesPromptShown,
            LoggingLevel = value.LoggingLevel,
            TrayItemKeys = new List<string>(value.TrayItemKeys ?? new List<string>()),
            SpokenHotKeys = (value.SpokenHotKeys ?? new List<SpokenHotKeySetting>())
                .Select(p => new SpokenHotKeySetting
                {
                    Name = p == null ? "" : p.Name ?? "",
                    HotKey = p == null ? "" : p.HotKey ?? "",
                    SkipUnavailableReadings = p != null && p.SkipUnavailableReadings,
                    ReadingKeys = p == null ? new List<string>() : new List<string>(p.ReadingKeys ?? new List<string>())
                })
                .ToList(),
            HiddenReadingKeys = new List<string>(value.HiddenReadingKeys ?? new List<string>()),
            FanLabels = new Dictionary<string, string>(value.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            FanControlSettings = (value.FanControlSettings ?? new Dictionary<string, FanControlSetting>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value != null)
                .ToDictionary(
                    i => i.Key,
                    i => new FanControlSetting { Manual = i.Value.Manual, Percent = i.Value.Percent },
                    StringComparer.OrdinalIgnoreCase),
            ShowStoppedFans = value.ShowStoppedFans,
            FanProfileStarterProfilesInitialized = value.FanProfileStarterProfilesInitialized,
            FanProfiles = CloneFanProfiles(value.FanProfiles),
            FanCurves = CloneFanCurves(value.FanCurves),
            ReadingSpeechLabels = new Dictionary<string, string>(value.ReadingSpeechLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            PlugInsEnabled = new Dictionary<string, bool>(value.PlugInsEnabled ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase),
            TrendLoggingEnabled = value.TrendLoggingEnabled,
            TrendLoggingKeys = new List<string>(value.TrendLoggingKeys ?? new List<string>()),
            CommunityStatsClientId = value.CommunityStatsClientId ?? "",
            Alarms = (value.Alarms ?? new List<AlarmSetting>())
                .Select(a => new AlarmSetting
                {
                    Name = a == null ? "" : a.Name ?? "",
                    ReadingKey = a == null ? "" : a.ReadingKey ?? "",
                    Condition = a == null ? "Above" : a.Condition ?? "Above",
                    Threshold = a == null ? 80 : a.Threshold,
                    ThresholdUnit = a == null ? "" : a.ThresholdUnit ?? "",
                    Enabled = a == null || a.Enabled,
                    Speak = a == null || a.Speak,
                    SoundFile = a == null ? "" : a.SoundFile ?? "",
                    CooldownSeconds = a == null ? 60 : a.CooldownSeconds
                })
                .ToList()
        };
    }

    private static void ApplySharedSettings(AppSettings target, SharedAppSettings shared)
    {
        if (target == null || shared == null)
        {
            return;
        }

        target.AutoRefreshEnabled = shared.AutoRefreshEnabled;
        target.RefreshWhileFocused = shared.RefreshWhileFocused;
        target.RefreshIntervalSeconds = shared.RefreshIntervalSeconds;
        target.ReadingTreeExpansionMode = shared.ReadingTreeExpansionMode;
        target.ReadingTreeLastExpanded = shared.ReadingTreeLastExpanded;
        target.TemperatureUnit = shared.TemperatureUnit;
        target.DecimalSeparator = shared.DecimalSeparator;
        target.LanguageFile = shared.LanguageFile;
        target.LanguagePreferenceInitialized = shared.LanguagePreferenceInitialized;
        target.ShowHideHotKey = shared.ShowHideHotKey;
        target.SpeakTrayHotKey = shared.SpeakTrayHotKey;
        target.HotKeyCopyDoublePressMs = shared.HotKeyCopyDoublePressMs;
        target.StartupSpeechEnabled = shared.StartupSpeechEnabled;
        target.StartupSpeechMessage = shared.StartupSpeechMessage;
        target.SpeechIncludesDeviceNames = shared.SpeechIncludesDeviceNames;
        target.TrayStatusEnabled = shared.TrayStatusEnabled;
        target.TrayTooltipShowsPartialReadings = shared.TrayTooltipShowsPartialReadings;
        target.TraySpeechSkipsUnavailableReadings = shared.TraySpeechSkipsUnavailableReadings;
        target.StartMinimizedToTray = shared.StartMinimizedToTray;
        target.CheckForUpdatesAtStartup = shared.CheckForUpdatesAtStartup;
        target.UpdateCheckFrequency = shared.UpdateCheckFrequency;
        target.InstallUpdatesQuietly = shared.InstallUpdatesQuietly;
        target.ShowUpdateInstallConfirmation = shared.ShowUpdateInstallConfirmation;
        target.ConfirmSpokenHotKeyProfileRemoval = shared.ConfirmSpokenHotKeyProfileRemoval;
        target.InitialSetupWizardDismissed = shared.InitialSetupWizardDismissed;
        target.ShowTipsOnStartup = shared.ShowTipsOnStartup;
        target.LastLanguageEditorFile = shared.LastLanguageEditorFile;
        target.LastLanguageEditorKey = shared.LastLanguageEditorKey;
        target.LastAutomaticUpdateCheckUtc = shared.LastAutomaticUpdateCheckUtc;
        target.UpdateAvailableSoundFile = shared.UpdateAvailableSoundFile;
        target.DiagnosticsSpeakProgress = shared.DiagnosticsSpeakProgress;
        target.DiagnosticsPlaySounds = shared.DiagnosticsPlaySounds;
        target.DiagnosticsStartSoundFile = shared.DiagnosticsStartSoundFile;
        target.DiagnosticsCompleteSoundFile = shared.DiagnosticsCompleteSoundFile;
        target.StartupSoundFile = shared.StartupSoundFile;
        target.ShutdownSoundFile = shared.ShutdownSoundFile;
    }

    private static void NormalizeSharedSettings(SharedAppSettings value)
    {
        if (value == null)
        {
            return;
        }

        value.RefreshIntervalSeconds = Math.Max(1, Math.Min(300, value.RefreshIntervalSeconds <= 0 ? 5 : value.RefreshIntervalSeconds));
        value.ReadingTreeExpansionMode = NormalizeReadingTreeExpansionMode(value.ReadingTreeExpansionMode);
        value.TemperatureUnit = NormalizeTemperatureUnit(value.TemperatureUnit);
        value.DecimalSeparator = string.Equals(value.DecimalSeparator, ",", StringComparison.Ordinal) || string.Equals(value.DecimalSeparator, ".", StringComparison.Ordinal)
            ? value.DecimalSeparator
            : "";
        value.LanguageFile = SanitizeLanguageFileName(value.LanguageFile);
        value.ShowHideHotKey = NormalizeHotKeyText(value.ShowHideHotKey);
        value.SpeakTrayHotKey = NormalizeHotKeyText(value.SpeakTrayHotKey);
        value.LastLanguageEditorFile = SanitizeLanguageFileName(value.LastLanguageEditorFile);
        value.LastLanguageEditorKey = (value.LastLanguageEditorKey ?? "").Trim();
        value.HotKeyCopyDoublePressMs = NormalizeHotKeyCopyDoublePressMs(value.HotKeyCopyDoublePressMs);
        value.UpdateCheckFrequency = NormalizeUpdateCheckFrequency(string.IsNullOrWhiteSpace(value.UpdateCheckFrequency) ? (value.CheckForUpdatesAtStartup ? "Startup" : "Never") : value.UpdateCheckFrequency);
        value.CheckForUpdatesAtStartup = !string.Equals(value.UpdateCheckFrequency, "Never", StringComparison.OrdinalIgnoreCase);
        value.LastAutomaticUpdateCheckUtc = NormalizeUtcDateString(value.LastAutomaticUpdateCheckUtc);
        value.StartupSpeechMessage = value.StartupSpeechMessage ?? "";
        value.UpdateAvailableSoundFile = System.IO.Path.GetFileName(value.UpdateAvailableSoundFile ?? "");
        value.DiagnosticsStartSoundFile = System.IO.Path.GetFileName(value.DiagnosticsStartSoundFile ?? "");
        value.DiagnosticsCompleteSoundFile = System.IO.Path.GetFileName(value.DiagnosticsCompleteSoundFile ?? "");
        value.StartupSoundFile = System.IO.Path.GetFileName(value.StartupSoundFile ?? "");
        value.ShutdownSoundFile = System.IO.Path.GetFileName(value.ShutdownSoundFile ?? "");
    }

    private static void NormalizeSettings(AppSettings value)
    {
        if (value == null)
        {
            return;
        }

        value.TrayItemKeys = value.TrayItemKeys ?? new List<string>();
        value.SpokenHotKeys = (value.SpokenHotKeys ?? new List<SpokenHotKeySetting>())
            .Where(p => p != null)
            .Select(p => new SpokenHotKeySetting
            {
                Name = p.Name ?? "",
                HotKey = NormalizeHotKeyText(p.HotKey),
                SkipUnavailableReadings = p.SkipUnavailableReadings,
                ReadingKeys = (p.ReadingKeys ?? new List<string>())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) || !string.IsNullOrWhiteSpace(p.HotKey) || p.ReadingKeys.Count > 0)
            .ToList();
        value.HiddenReadingKeys = value.HiddenReadingKeys ?? new List<string>();
        value.FanLabels = new Dictionary<string, string>(value.FanLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        value.FanControlSettings = new Dictionary<string, FanControlSetting>(value.FanControlSettings ?? new Dictionary<string, FanControlSetting>(), StringComparer.OrdinalIgnoreCase)
            .Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value != null)
            .ToDictionary(
                i => i.Key,
                i => new FanControlSetting { Manual = i.Value.Manual, Percent = Math.Max(0, Math.Min(100, i.Value.Percent)) },
                StringComparer.OrdinalIgnoreCase);
        value.FanProfiles = CloneFanProfiles(value.FanProfiles);
        value.FanCurves = CloneFanCurves(value.FanCurves);
        value.PlugInsEnabled = new Dictionary<string, bool>(value.PlugInsEnabled ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase)
            .Where(i => !string.IsNullOrWhiteSpace(i.Key))
            .ToDictionary(i => i.Key.Trim(), i => i.Value, StringComparer.OrdinalIgnoreCase);
        value.TrendLoggingKeys = (value.TrendLoggingKeys ?? new List<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        value.ReadingSpeechLabels = new Dictionary<string, string>(value.ReadingSpeechLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            .Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Value))
            .ToDictionary(i => i.Key, i => i.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        value.Alarms = (value.Alarms ?? new List<AlarmSetting>())
            .Where(a => a != null)
            .Select(a => new AlarmSetting
            {
                Name = a.Name ?? "",
                ReadingKey = a.ReadingKey ?? "",
                Condition = NormalizeAlarmCondition(a.Condition),
                Threshold = a.Threshold,
                ThresholdUnit = a.ThresholdUnit ?? "",
                Enabled = a.Enabled,
                Speak = a.Speak,
                SoundFile = System.IO.Path.GetFileName(a.SoundFile ?? ""),
                CooldownSeconds = Math.Max(0, Math.Min(86400, a.CooldownSeconds))
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) || !string.IsNullOrWhiteSpace(a.ReadingKey))
            .ToList();
        value.StartupSoundFile = System.IO.Path.GetFileName(value.StartupSoundFile ?? "");
        value.ShutdownSoundFile = System.IO.Path.GetFileName(value.ShutdownSoundFile ?? "");
        value.UpdateAvailableSoundFile = System.IO.Path.GetFileName(value.UpdateAvailableSoundFile ?? "");
        value.DiagnosticsStartSoundFile = System.IO.Path.GetFileName(value.DiagnosticsStartSoundFile ?? "");
        value.DiagnosticsCompleteSoundFile = System.IO.Path.GetFileName(value.DiagnosticsCompleteSoundFile ?? "");
        value.RefreshIntervalSeconds = Math.Max(1, Math.Min(300, value.RefreshIntervalSeconds));
        value.ReadingTreeExpansionMode = NormalizeReadingTreeExpansionMode(value.ReadingTreeExpansionMode);
        value.TemperatureUnit = NormalizeTemperatureUnit(value.TemperatureUnit);
        value.DecimalSeparator = string.Equals(value.DecimalSeparator, ",", StringComparison.Ordinal) || string.Equals(value.DecimalSeparator, ".", StringComparison.Ordinal)
            ? value.DecimalSeparator
            : "";
        value.LanguageFile = SanitizeLanguageFileName(value.LanguageFile);
        value.ShowHideHotKey = NormalizeHotKeyText(value.ShowHideHotKey);
        value.SpeakTrayHotKey = NormalizeHotKeyText(value.SpeakTrayHotKey);
        value.LastLanguageEditorFile = SanitizeLanguageFileName(value.LastLanguageEditorFile);
        value.LastLanguageEditorKey = (value.LastLanguageEditorKey ?? "").Trim();
        value.HotKeyCopyDoublePressMs = NormalizeHotKeyCopyDoublePressMs(value.HotKeyCopyDoublePressMs);
        value.UpdateCheckFrequency = NormalizeUpdateCheckFrequency(string.IsNullOrWhiteSpace(value.UpdateCheckFrequency) ? (value.CheckForUpdatesAtStartup ? "Startup" : "Never") : value.UpdateCheckFrequency);
        value.CheckForUpdatesAtStartup = !string.Equals(value.UpdateCheckFrequency, "Never", StringComparison.OrdinalIgnoreCase);
        value.LastAutomaticUpdateCheckUtc = NormalizeUtcDateString(value.LastAutomaticUpdateCheckUtc);
        value.StartupSpeechMessage = value.StartupSpeechMessage ?? "";
        value.CommunityStatsClientId = (value.CommunityStatsClientId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(value.ShowHideHotKey) &&
            string.Equals(value.ShowHideHotKey, value.SpeakTrayHotKey, StringComparison.OrdinalIgnoreCase))
        {
            value.SpeakTrayHotKey = "";
        }
        var reservedHotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(value.ShowHideHotKey)) reservedHotKeys.Add(value.ShowHideHotKey);
        if (!string.IsNullOrWhiteSpace(value.SpeakTrayHotKey)) reservedHotKeys.Add(value.SpeakTrayHotKey);
        foreach (var profile in value.SpokenHotKeys)
        {
            if (string.IsNullOrWhiteSpace(profile.HotKey))
            {
                continue;
            }

            if (reservedHotKeys.Contains(profile.HotKey))
            {
                profile.HotKey = "";
            }
            else
            {
                reservedHotKeys.Add(profile.HotKey);
            }
        }
        foreach (var profile in value.FanProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.HotKey))
            {
                continue;
            }

            if (reservedHotKeys.Contains(profile.HotKey))
            {
                profile.HotKey = "";
            }
            else
            {
                reservedHotKeys.Add(profile.HotKey);
            }
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

    private static List<FanCurveSetting> CloneFanCurves(IEnumerable<FanCurveSetting> curves)
    {
        return (curves ?? new List<FanCurveSetting>())
            .Where(c => c != null)
            .Select(c =>
            {
                var lowTemperature = Math.Max(-100, Math.Min(150, c.LowTemperatureC));
                var highTemperature = Math.Max(-100, Math.Min(150, c.HighTemperatureC));
                if (highTemperature <= lowTemperature)
                {
                    highTemperature = lowTemperature + 1;
                }

                var emergencyTemperature = Math.Max(highTemperature, Math.Min(150, c.EmergencyTemperatureC));
                return new FanCurveSetting
                {
                    Name = c.Name ?? "",
                    FanControlKey = c.FanControlKey ?? "",
                    TemperatureReadingKey = c.TemperatureReadingKey ?? "",
                    Enabled = c.Enabled,
                    SuspendedByManualControl = c.SuspendedByManualControl,
                    LowTemperatureC = lowTemperature,
                    LowPercent = Math.Max(0, Math.Min(100, c.LowPercent)),
                    HighTemperatureC = highTemperature,
                    HighPercent = Math.Max(0, Math.Min(100, c.HighPercent)),
                    EmergencyTemperatureC = emergencyTemperature,
                    EmergencyPercent = Math.Max(0, Math.Min(100, c.EmergencyPercent)),
                    MinimumChangePercent = Math.Max(0, Math.Min(25, c.MinimumChangePercent))
                };
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.FanControlKey) || !string.IsNullOrWhiteSpace(c.TemperatureReadingKey))
            .ToList();
    }

    private static List<FanProfileSetting> CloneFanProfiles(IEnumerable<FanProfileSetting> profiles)
    {
        return (profiles ?? new List<FanProfileSetting>())
            .Where(p => p != null)
            .Select(p => new FanProfileSetting
            {
                Name = p.Name ?? "",
                HotKey = NormalizeHotKeyText(p.HotKey),
                SoundFile = System.IO.Path.GetFileName(p.SoundFile ?? ""),
                ToggleAutomatic = p.ToggleAutomatic,
                Speak = p.Speak,
                SpeechMessage = p.SpeechMessage ?? "",
                Actions = (p.Actions ?? new List<FanProfileActionSetting>())
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.FanControlKey))
                    .Select(a => new FanProfileActionSetting
                    {
                        FanControlKey = IdentifierFromSettingsKey(a.FanControlKey),
                        Manual = a.Manual,
                        Percent = Math.Max(0, Math.Min(100, a.Percent))
                    })
                    .GroupBy(a => a.FanControlKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList()
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) || !string.IsNullOrWhiteSpace(p.HotKey) || !string.IsNullOrWhiteSpace(p.SoundFile) || p.ToggleAutomatic || !p.Speak || !string.IsNullOrWhiteSpace(p.SpeechMessage) || p.Actions.Count > 0)
            .ToList();
    }

    public static string NormalizeAlarmCondition(string condition)
    {
        if (string.Equals(condition, "Below", StringComparison.OrdinalIgnoreCase))
        {
            return "Below";
        }

        if (string.Equals(condition, "Equal", StringComparison.OrdinalIgnoreCase))
        {
            return "Equal";
        }

        return "Above";
    }

    public static int NormalizeHotKeyCopyDoublePressMs(int value)
    {
        if (value < 0)
        {
            return -1;
        }

        if (value == 0)
        {
            return 0;
        }

        return Math.Max(100, Math.Min(5000, value));
    }

    private static string NormalizeUtcDateString(string value)
    {
        DateTime date;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
        {
            return "";
        }

        return date.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
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

    private static string GetSharedConfigFilePath()
    {
        return System.IO.Path.Combine(GetConfigFolderPath(), "Shared.json");
    }

    private static string GetLogFilePath()
    {
        return System.IO.Path.Combine(GetLogsFolderPath(), System.IO.Path.ChangeExtension(GetConfigFileName(), ".log"));
    }

    public static string GetConfigFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
    }

    public static string GetLogsFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    }

    public static string GetReportsFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports");
    }

    private static string GetBackupsFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
    }

    private static void MigrateProgramDataFiles()
    {
        try
        {
            MoveTopLevelFilesToFolder("*.json", GetConfigFolderPath());
            MoveTopLevelFilesToFolder("*.log", GetLogsFolderPath());
            MoveLegacyUpdateBackups();
            RepairNestedUpdateFolders();
            DeleteObsoletePortableFiles();
        }
        catch
        {
        }
    }

    private static void DeleteObsoletePortableFiles()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var fileName in new[]
        {
            "nvdaControllerClient.dll",
            "nvdaControllerClient.LICENSE.txt"
        })
        {
            var path = System.IO.Path.Combine(baseDirectory, fileName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private static void RepairNestedUpdateFolders()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (!System.IO.Directory.Exists(baseDirectory))
        {
            return;
        }

        foreach (var folder in System.IO.Directory.GetDirectories(baseDirectory, "*", System.IO.SearchOption.AllDirectories)
            .OrderByDescending(p => p.Length))
        {
            RepairNestedUpdateFolder(folder);
        }
    }

    private static void RepairNestedUpdateFolder(string parent)
    {
        var folderName = System.IO.Path.GetFileName(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var nested = System.IO.Path.Combine(parent, folderName);
        if (!System.IO.Directory.Exists(nested))
        {
            return;
        }

        BackupFolderBeforeRemoval(parent, "Folder state before repairing nested update folder.");
        CopyDirectoryContents(nested, parent);
        System.IO.Directory.Delete(nested, true);
    }

    private static void MoveLegacyUpdateBackups()
    {
        var legacy = System.IO.Path.Combine(GetConfigFolderPath(), "Update Backups");
        if (!System.IO.Directory.Exists(legacy))
        {
            return;
        }

        BackupFolderBeforeRemoval(legacy, "Legacy Config\\Update Backups moved to top-level Backups.");
        System.IO.Directory.Delete(legacy, true);
    }

    private static void BackupFolderBeforeRemoval(string folder, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
            {
                return;
            }

            var backupRoot = System.IO.Path.Combine(GetBackupsFolderPath(), "Recovered");
            System.IO.Directory.CreateDirectory(backupRoot);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            var zipPath = System.IO.Path.Combine(backupRoot, stamp + "-" + name + ".zip");
            if (System.IO.File.Exists(zipPath))
            {
                zipPath = System.IO.Path.Combine(backupRoot, stamp + "-" + name + "-" + Guid.NewGuid().ToString("N") + ".zip");
            }

            System.IO.Compression.ZipFile.CreateFromDirectory(folder, zipPath, System.IO.Compression.CompressionLevel.Optimal, false);
            var notePath = System.IO.Path.ChangeExtension(zipPath, ".txt");
            System.IO.File.WriteAllText(notePath, reason + Environment.NewLine + folder);
        }
        catch
        {
        }
    }

    private static void CopyDirectoryContents(string sourceFolder, string destinationFolder)
    {
        System.IO.Directory.CreateDirectory(destinationFolder);
        foreach (var sourcePath in System.IO.Directory.GetFileSystemEntries(sourceFolder))
        {
            var destinationPath = System.IO.Path.Combine(destinationFolder, System.IO.Path.GetFileName(sourcePath));
            if (System.IO.Directory.Exists(sourcePath))
            {
                CopyDirectoryContents(sourcePath, destinationPath);
            }
            else
            {
                System.IO.File.Copy(sourcePath, destinationPath, true);
            }
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

}
