using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static readonly object soundPreviewLock = new object();
    private static SoundPlayer soundPreviewPlayer;

    private void CheckAlarms(List<SensorRow> rows)
    {
        var alarms = ActiveAlarmsByKey();
        PruneAlarmTriggerStates(alarms);
        var now = DateTime.UtcNow;

        foreach (var item in alarms)
        {
            var alarm = item.Value;
            AlarmTriggerState state;
            if (!alarmTriggerStates.TryGetValue(item.Key, out state))
            {
                state = new AlarmTriggerState();
                alarmTriggerStates.Add(item.Key, state);
            }

            var row = rows == null ? null : rows.FirstOrDefault(r => r != null && string.Equals(RowSettingsKey(r), alarm.ReadingKey, StringComparison.OrdinalIgnoreCase));
            bool? matches = row == null || !row.Value.HasValue || double.IsNaN(row.Value.Value) || double.IsInfinity(row.Value.Value)
                ? (bool?)null : AlarmConditionMatches(row.Value.Value, alarm);
            if (!state.ShouldTrigger(matches, alarm.RepeatWhileActive, alarm.CooldownSeconds, now))
            {
                continue;
            }

            var message = BuildAlarmMessage(alarm, row);
            if (alarm.Speak)
            {
                SpeakTextWithScreenReaderPolite(message, "alarm");
            }

            if (!string.IsNullOrWhiteSpace(alarm.SoundFile))
            {
                PlaySoundFile(alarm.SoundFile);
            }

            FlashTrayIconForAlarm();
            LogMessage("Normal", "Alarm triggered: " + message);
        }
    }

    private Dictionary<string, AlarmSetting> ActiveAlarmsByKey()
    {
        var result = new Dictionary<string, AlarmSetting>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var alarm in (settings.Alarms ?? new List<AlarmSetting>()).Where(a => a != null && a.Enabled))
        {
            // Stable across preference clones and reordering; edited alarms start a new episode.
            // Identical duplicates must not consume each other's notifications.
            var definition = Newtonsoft.Json.JsonConvert.SerializeObject(alarm);
            int occurrence;
            occurrences.TryGetValue(definition, out occurrence);
            occurrences[definition] = occurrence + 1;
            result.Add(definition + "|" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture), alarm);
        }
        return result;
    }

    private void PruneAlarmTriggerStates(Dictionary<string, AlarmSetting> activeAlarms)
    {
        foreach (var key in alarmTriggerStates.Keys.Where(k => !activeAlarms.ContainsKey(k)).ToList())
        {
            alarmTriggerStates.Remove(key);
        }
    }

    private void FlashTrayIconForAlarm()
    {
        if (trayIcon == null || !settings.TrayStatusEnabled)
        {
            return;
        }

        if (alarmTrayIcon == null)
        {
            alarmTrayIcon = CreateAlarmTrayIcon();
        }

        if (trayFlashTimer == null)
        {
            trayFlashTimer = new Timer { Interval = 300 };
            trayFlashTimer.Tick += delegate { ContinueTrayIconFlash(); };
        }

        trayIcon.Visible = true;
        trayFlashTicksRemaining = 10;
        trayFlashShowingAlarm = false;
        trayFlashTimer.Stop();
        trayFlashTimer.Start();
        ContinueTrayIconFlash();
    }

    private void ContinueTrayIconFlash()
    {
        if (trayFlashTicksRemaining <= 0 || trayIcon == null)
        {
            if (trayFlashTimer != null)
            {
                trayFlashTimer.Stop();
            }
            trayFlashShowingAlarm = false;
            UpdateTrayStatus();
            return;
        }

        trayFlashTicksRemaining--;
        trayFlashShowingAlarm = !trayFlashShowingAlarm;
        if (trayFlashShowingAlarm && alarmTrayIcon != null)
        {
            trayIcon.Icon = alarmTrayIcon;
        }
        else
        {
            UpdateTrayStatus();
        }
    }

    private static Icon CreateAlarmTrayIcon()
    {
        return CreateTrayIcon(new TrayBadgeVisual { Signal = ReadingVisualSignal.Critical, Text = "!" });
    }

    private static bool AlarmConditionMatches(double value, AlarmSetting alarm)
    {
        var condition = alarm == null || string.IsNullOrWhiteSpace(alarm.Condition) ? "Above" : alarm.Condition.Trim();
        if (string.Equals(condition, "Below", StringComparison.OrdinalIgnoreCase))
        {
            return value <= alarm.Threshold;
        }

        if (string.Equals(condition, "Equal", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(value - alarm.Threshold) < 0.01;
        }

        return value >= alarm.Threshold;
    }

    private static string BuildAlarmMessage(AlarmSetting alarm, SensorRow row)
    {
        if (alarm != null && !string.IsNullOrWhiteSpace(alarm.SpokenMessage))
        {
            return alarm.SpokenMessage.Trim();
        }

        var name = alarm == null || string.IsNullOrWhiteSpace(alarm.Name) ? "Alarm" : alarm.Name.Trim();
        var reading = row == null ? "" : DefaultSpeechLabel(row.Type, row.Hardware, row.Name, true);
        var value = row == null ? "" : FormatValue(row);
        return name + ": " + reading + " is " + value + ".";
    }

    private void PlayStartupSound()
    {
        PlaySoundFile(settings.StartupSoundFile);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (remotePollTimer != null)
        {
            remotePollTimer.Stop();
        }
        StopEmbeddedRemoteServer();
        StopAudioLatencyForShutdown();
        HideTrayIconBeforeExit();
        PlaySoundFileSync(settings.ShutdownSoundFile);
        base.OnFormClosing(e);
    }

    private static void PlaySoundFile(string fileName)
    {
        var path = ResolveSoundPath(fileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Task.Run(delegate { PlaySoundPathSync(path); });
        }
        catch
        {
        }
    }

    public static void PreviewSoundFile(string fileName)
    {
        var path = ResolveSoundPath(fileName);
        lock (soundPreviewLock)
        {
            if (soundPreviewPlayer != null)
            {
                try { soundPreviewPlayer.Stop(); } catch { }
                soundPreviewPlayer.Dispose();
                soundPreviewPlayer = null;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                soundPreviewPlayer = new SoundPlayer(path);
                soundPreviewPlayer.Play();
            }
            catch
            {
                if (soundPreviewPlayer != null)
                {
                    soundPreviewPlayer.Dispose();
                    soundPreviewPlayer = null;
                }
            }
        }
    }

    private static void PlaySoundFileSync(string fileName)
    {
        var path = ResolveSoundPath(fileName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PlaySoundPathSync(path);
        }
    }

    private static void PlaySoundPathSync(string path)
    {
        try
        {
            using (var player = new SoundPlayer(path))
            {
                player.PlaySync();
            }
        }
        catch
        {
        }
    }

    public static string GetSoundsFolderPath()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds");
    }

    public static List<string> LoadSoundFileNames()
    {
        var folder = GetSoundsFolderPath();
        if (!System.IO.Directory.Exists(folder))
        {
            return new List<string>();
        }

        return System.IO.Directory.GetFiles(folder, "*.wav")
            .Select(System.IO.Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveSoundPath(string fileName)
    {
        fileName = System.IO.Path.GetFileName((fileName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        var path = System.IO.Path.Combine(GetSoundsFolderPath(), fileName);
        return System.IO.File.Exists(path) ? path : "";
    }
}
