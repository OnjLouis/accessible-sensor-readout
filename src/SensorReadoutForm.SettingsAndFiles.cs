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

    public static void SetLoggingLevelPreference(string level)
    {
        var normalized = NormalizeLoggingLevel(level);
        var value = LoadSettings();
        value.LoggingLevel = normalized;
        SaveSettings(value);
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
        value.RefreshIntervalSeconds = Math.Max(2, Math.Min(300, value.RefreshIntervalSeconds));
        value.TemperatureUnit = NormalizeTemperatureUnit(value.TemperatureUnit);
        value.DecimalSeparator = string.Equals(value.DecimalSeparator, ",", StringComparison.Ordinal) || string.Equals(value.DecimalSeparator, ".", StringComparison.Ordinal)
            ? value.DecimalSeparator
            : "";
        value.LanguageFile = SanitizeLanguageFileName(value.LanguageFile);
        value.ShowHideHotKey = NormalizeHotKeyText(value.ShowHideHotKey);
        value.SpeakTrayHotKey = NormalizeHotKeyText(value.SpeakTrayHotKey);
        value.HotKeyCopyDoublePressMs = NormalizeHotKeyCopyDoublePressMs(value.HotKeyCopyDoublePressMs);
        value.StartupSpeechMessage = value.StartupSpeechMessage ?? "";
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

}
