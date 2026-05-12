using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed class SensorRow
{
    public string Type;
    public string Hardware;
    public string Name;
    public string Identifier;
    public float? Value;
    public string DisplayValue;
    public string Source;
    public Dictionary<string, string> Details;

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
    public int HotKeyCopyDoublePressMs = -1;
    public bool StartupSpeechEnabled = true;
    public string StartupSpeechMessage = "";
    public bool SpeechIncludesDeviceNames = true;
    public bool TrayStatusEnabled = true;
    public bool RunAtStartup = false;
    public bool StartMinimizedToTray = false;
    public bool CheckForUpdatesAtStartup = true;
    public string UpdateCheckFrequency = "Startup";
    public bool InstallUpdatesQuietly = false;
    public bool ShowUpdateInstallConfirmation = true;
    public string LastAutomaticUpdateCheckUtc = "";
    public string UpdateAvailableSoundFile = "";
    public bool DiagnosticsSpeakProgress = true;
    public bool DiagnosticsPlaySounds = true;
    public string DiagnosticsStartSoundFile = "";
    public string DiagnosticsCompleteSoundFile = "";
    public bool PrerequisitesPromptShown = false;
    public string LoggingLevel = "Off";
    public List<string> TrayItemKeys = new List<string>();
    public List<SpokenHotKeySetting> SpokenHotKeys = new List<SpokenHotKeySetting>();
    public List<string> HiddenReadingKeys = new List<string>();
    public Dictionary<string, string> FanLabels = new Dictionary<string, string>();
    public Dictionary<string, FanControlSetting> FanControlSettings = new Dictionary<string, FanControlSetting>();
    public bool ShowStoppedFans = false;
    public bool FanProfileStarterProfilesInitialized = false;
    public List<FanProfileSetting> FanProfiles = new List<FanProfileSetting>();
    public List<FanCurveSetting> FanCurves = new List<FanCurveSetting>();
    public Dictionary<string, string> ReadingSpeechLabels = new Dictionary<string, string>();
    public List<AlarmSetting> Alarms = new List<AlarmSetting>();
    public string StartupSoundFile = "";
    public string ShutdownSoundFile = "";
    public Dictionary<string, bool> PlugInsEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

public sealed class SharedAppSettings
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
    public int HotKeyCopyDoublePressMs = -1;
    public bool StartupSpeechEnabled = true;
    public string StartupSpeechMessage = "";
    public bool SpeechIncludesDeviceNames = true;
    public bool TrayStatusEnabled = true;
    public bool StartMinimizedToTray = false;
    public bool CheckForUpdatesAtStartup = true;
    public string UpdateCheckFrequency = "Startup";
    public bool InstallUpdatesQuietly = false;
    public bool ShowUpdateInstallConfirmation = true;
    public string LastAutomaticUpdateCheckUtc = "";
    public string UpdateAvailableSoundFile = "";
    public bool DiagnosticsSpeakProgress = true;
    public bool DiagnosticsPlaySounds = true;
    public string DiagnosticsStartSoundFile = "";
    public string DiagnosticsCompleteSoundFile = "";
    public string StartupSoundFile = "";
    public string ShutdownSoundFile = "";
}

public sealed class MachineAppSettings
{
    public bool RunAtStartup = false;
    public bool PrerequisitesPromptShown = false;
    public string LoggingLevel = "Off";
    public List<string> TrayItemKeys = new List<string>();
    public List<SpokenHotKeySetting> SpokenHotKeys = new List<SpokenHotKeySetting>();
    public List<string> HiddenReadingKeys = new List<string>();
    public Dictionary<string, string> FanLabels = new Dictionary<string, string>();
    public Dictionary<string, FanControlSetting> FanControlSettings = new Dictionary<string, FanControlSetting>();
    public bool ShowStoppedFans = false;
    public bool FanProfileStarterProfilesInitialized = false;
    public List<FanProfileSetting> FanProfiles = new List<FanProfileSetting>();
    public List<FanCurveSetting> FanCurves = new List<FanCurveSetting>();
    public Dictionary<string, string> ReadingSpeechLabels = new Dictionary<string, string>();
    public List<AlarmSetting> Alarms = new List<AlarmSetting>();
    public Dictionary<string, bool> PlugInsEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

public sealed class SettingsTransferPackage
{
    public string Format = "SensorReadoutSettingsTransfer";
    public string AppVersion = "";
    public string MachineName = "";
    public string ExportedAtUtc = "";
    public SharedAppSettings SharedSettings;
    public MachineAppSettings MachineSettings;
}

public sealed class PlugInPreferenceInfo
{
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public string Author = "";
    public string Description = "";
    public bool Enabled;
    public string Status = "";

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? Id : Name;
        var label = string.IsNullOrWhiteSpace(Version) ? name : name + " " + Version;
        return string.IsNullOrWhiteSpace(Description) ? label : label + ": " + Description;
    }
}

public sealed class PlugInHelpLink
{
    public string PlugInId = "";
    public string PlugInName = "";
    public string Label = "";
    public string Url = "";

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Label) ? Url : Label;
    }
}

public sealed class FanControlSetting
{
    public bool Manual;
    public int Percent = 50;
}

public sealed class FanProfileActionSetting
{
    public string FanControlKey = "";
    public bool Manual = true;
    public int Percent = 50;

    public override string ToString()
    {
        return Manual ? Math.Max(0, Math.Min(100, Percent)) + "%" : "Auto";
    }
}

public sealed class FanProfileSetting
{
    public string Name = "";
    public string HotKey = "";
    public string SoundFile = "";
    public bool ToggleAutomatic = false;
    public bool Speak = true;
    public string SpeechMessage = "";
    public List<FanProfileActionSetting> Actions = new List<FanProfileActionSetting>();

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "Fan profile" : Name.Trim();
        var hotKey = string.IsNullOrWhiteSpace(HotKey) ? "no hotkey" : HotKey.Trim();
        var toggle = ToggleAutomatic ? ", toggle" : "";
        var count = Actions == null ? 0 : Actions.Count;
        return name + " (" + hotKey + toggle + ", " + count + " fan" + (count == 1 ? "" : "s") + ")";
    }
}

public sealed class FanCurveSetting
{
    public string Name = "";
    public string FanControlKey = "";
    public string TemperatureReadingKey = "";
    public bool Enabled = true;
    public bool SuspendedByManualControl = false;
    public double LowTemperatureC = 35;
    public int LowPercent = 30;
    public double HighTemperatureC = 75;
    public int HighPercent = 100;
    public double EmergencyTemperatureC = 85;
    public int EmergencyPercent = 100;
    public int MinimumChangePercent = 2;

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "Fan curve" : Name.Trim();
        return name + " (" + LowTemperatureC.ToString("0.#") + " Celsius = " + LowPercent + "%, " + HighTemperatureC.ToString("0.#") + " Celsius = " + HighPercent + "%)";
    }
}

public sealed class SpokenHotKeySetting
{
    public string Name = "";
    public string HotKey = "";
    public List<string> ReadingKeys = new List<string>();

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "Spoken hotkey" : Name.Trim();
        var hotKey = string.IsNullOrWhiteSpace(HotKey) ? "no hotkey" : HotKey.Trim();
        var count = ReadingKeys == null ? 0 : ReadingKeys.Count;
        return name + " (" + hotKey + ", " + count + " reading" + (count == 1 ? "" : "s") + ")";
    }
}

public sealed class AlarmSetting
{
    public string Name = "";
    public string ReadingKey = "";
    public string Condition = "Above";
    public double Threshold = 80;
    public string ThresholdUnit = "";
    public bool Enabled = true;
    public bool Speak = true;
    public string SoundFile = "";
    public int CooldownSeconds = 60;

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "Alarm" : Name.Trim();
        var condition = string.IsNullOrWhiteSpace(Condition) ? "Above" : Condition.Trim();
        var cooldown = CooldownSeconds <= 0 ? 0 : CooldownSeconds;
        return name + " (" + condition + " " + Threshold.ToString("0.##") + ", " + cooldown + "s cooldown)";
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
public struct CoreTempSharedDataEx
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public uint[] UiLoad;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public uint[] UiTjMax;

    public uint UiCoreCnt;
    public uint UiCpuCnt;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public float[] FTemp;

    public float FVid;
    public float FCpuSpeed;
    public float FFsbSpeed;
    public float FMultiplier;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
    public string CpuName;

    public byte UcFahrenheit;
    public byte UcDeltaToTjMax;
    public byte UcTdpSupported;
    public byte UcPowerSupported;
    public uint UiStructVersion;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public uint[] UiTdp;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public float[] FPower;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public float[] FMultipliers;
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

public sealed class ListSearchState
{
    public string Text = "";
    public DateTime LastKey = DateTime.MinValue;
}

public sealed class ShortcutButton : Button
{
    public string ShortcutText { get; set; }
    public Keys ShortcutKeys { get; set; }

    public ShortcutButton()
    {
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new ShortcutButtonAccessibleObject(this);
    }

    private sealed class ShortcutButtonAccessibleObject : Control.ControlAccessibleObject
    {
        private readonly ShortcutButton owner;

        public ShortcutButtonAccessibleObject(ShortcutButton owner)
            : base(owner)
        {
            this.owner = owner;
        }

        public override string KeyboardShortcut
        {
            get
            {
                return string.IsNullOrWhiteSpace(owner.ShortcutText)
                    ? base.KeyboardShortcut
                    : owner.ShortcutText;
            }
        }
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

public sealed class UsbDiagnosticSnapshot
{
    public int HubCount;
    public int PortCount;
    public int PortMatchCount;
    public readonly List<string> Lines = new List<string>();
}

public sealed class GlobalHotKey
{
    public uint Modifiers;
    public Keys Key;

    public bool IsValid
    {
        get { return IsAllowedBaseKey(Key) && HasRequiredModifier(Modifiers); }
    }

    public static bool IsAllowedBaseKey(Keys key)
    {
        return (key >= Keys.A && key <= Keys.Z) ||
            (key >= Keys.D0 && key <= Keys.D9) ||
            (key >= Keys.NumPad0 && key <= Keys.NumPad9) ||
            (key >= Keys.F1 && key <= Keys.F24);
    }

    private static bool HasRequiredModifier(uint modifiers)
    {
        return (modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModWin)) != 0;
    }
}
