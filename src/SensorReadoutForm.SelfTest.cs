using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class SelfTestResult
    {
        public string Name = "";
        public bool Passed;
        public string Message = "";
        public long Milliseconds;
    }

    public static void RunSelfTest(string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            outputFolder = Path.Combine(GetReportsFolderPath(), "SelfTest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        outputFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(outputFolder.Trim('"')));
        Directory.CreateDirectory(outputFolder);

        var results = new List<SelfTestResult>();
        var started = DateTime.Now;
        var exitCode = 0;
        using (var form = new SensorReadoutForm(false))
        {
            form.forceDebugLogging = true;
            form.ConfigureSelfTestSettings();
            form.LogMessage("Debug", "Self-test started. Output folder: " + outputFolder);
            form.RunSelfTestStep(results, "Settings save and reload", delegate { form.SelfTestSettingsRoundTrip(); });
            form.RunSelfTestStep(results, "Sensor collection", delegate { form.SelfTestSensorCollection(); });
            form.RunSelfTestStep(results, "Category tree navigation", delegate { form.SelfTestCategoryNavigation(); });
            form.RunSelfTestStep(results, "Expand and collapse commands", delegate { form.SelfTestExpandCollapse(); });
            form.RunSelfTestStep(results, "Show/hide expansion preservation", delegate { form.SelfTestExpansionPreservation(); });
            form.RunSelfTestStep(results, "Tray tooltip modes", delegate { form.SelfTestTrayStatusText(); });
            form.RunSelfTestStep(results, "Hotkeys menu", delegate { form.SelfTestHotkeysMenu(); });
            form.RunSelfTestStep(results, "Spoken hotkey assignment persistence", delegate { form.SelfTestSpokenHotKeyAssignment(); });
            form.RunSelfTestStep(results, "Alarm and fan curve persistence", delegate { form.SelfTestAlarmAndFanCurvePersistence(); });
            form.RunSelfTestStep(results, "TXT and HTML report writing", delegate { form.SelfTestReportWriting(outputFolder); });
            form.RunSelfTestStep(results, "Report reopening and ZIP selection", delegate { form.SelfTestReportReopen(outputFolder); });
            form.RunSelfTestStep(results, "Diagnostics ZIP creation", delegate { form.SelfTestDiagnosticsZip(outputFolder); });
            form.RunSelfTestStep(results, "Language and manual files", delegate { form.SelfTestLanguageAndManualFiles(); });
            form.LogMessage("Debug", "Self-test complete.");
        }

        if (results.Any(r => !r.Passed))
        {
            exitCode = 1;
        }

        WriteSelfTestSummary(outputFolder, started, results);
        Environment.ExitCode = exitCode;
    }

    private void ConfigureSelfTestSettings()
    {
        settings.LoggingLevel = "Debug";
        settings.RunAtStartup = false;
        settings.StartMinimizedToTray = false;
        settings.TrayStatusEnabled = true;
        settings.TrayTooltipShowsPartialReadings = true;
        settings.DiagnosticsSpeakProgress = false;
        settings.DiagnosticsPlaySounds = false;
        settings.StartupSoundFile = "";
        settings.ShutdownSoundFile = "";
        settings.DiagnosticsStartSoundFile = "";
        settings.DiagnosticsCompleteSoundFile = "";
        SaveSettings(settings);
    }

    private void RunSelfTestStep(List<SelfTestResult> results, string name, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
            stopwatch.Stop();
            results.Add(new SelfTestResult { Name = name, Passed = true, Message = "OK", Milliseconds = stopwatch.ElapsedMilliseconds });
            LogMessage("Debug", "Self-test PASS: " + name + " in " + stopwatch.ElapsedMilliseconds + " ms.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            results.Add(new SelfTestResult { Name = name, Passed = false, Message = ex.GetType().Name + ": " + ex.Message, Milliseconds = stopwatch.ElapsedMilliseconds });
            LogError("Self-test FAIL: " + name + ". " + ex);
        }
    }

    private void SelfTestSettingsRoundTrip()
    {
        settings.ShowHideHotKey = "Ctrl+Alt+F12";
        settings.SpeakTrayHotKey = "Ctrl+Alt+F11";
        SaveSettings(settings);
        var reloaded = LoadSettings();
        Require(string.Equals(reloaded.ShowHideHotKey, "Ctrl+Alt+F12", StringComparison.OrdinalIgnoreCase), "Show/hide hotkey did not round-trip.");
        Require(string.Equals(reloaded.SpeakTrayHotKey, "Ctrl+Alt+F11", StringComparison.OrdinalIgnoreCase), "Speak tray hotkey did not round-trip.");
    }

    private void SelfTestSensorCollection()
    {
        var rows = CollectSensorRows(true);
        Require(rows.Count > 0, "No sensor rows were collected.");
        latestRows.Clear();
        latestRows.AddRange(rows);
        Require(rows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase)), "Performance rows missing.");
        Require(rows.Any(r => !string.IsNullOrWhiteSpace(r.Name)), "Collected rows have no names.");
    }

    private void SelfTestCategoryNavigation()
    {
        EnsureSelfTestRows();
        UpdateDeviceList();
        Require(deviceList.Items.Count > 0, "Category list is empty.");
        for (var i = 0; i < deviceList.Items.Count; i++)
        {
            deviceList.SelectedIndex = i;
            UpdateReadingList();
            Require(readingTree.Nodes.Count > 0, "Reading tree empty for category " + deviceList.Items[i] + ".");
        }
    }

    private void SelfTestExpandCollapse()
    {
        EnsureSelfTestRows();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        ExpandAllReadings();
        Require(CountExpandedNodes(readingTree.Nodes) > 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Expand all did not expand any nodes.");
        CollapseAllReadings();
        Require(CountExpandedNodes(readingTree.Nodes) == 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Collapse all left expanded nodes.");
    }

    private void SelfTestExpansionPreservation()
    {
        EnsureSelfTestRows();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        ExpandAllReadings();
        var before = CountExpandedNodes(readingTree.Nodes);
        CaptureReadingExpansionBeforeHide();
        CollapseAllReadings();
        RestoreReadingExpansionAfterShow();
        var after = CountExpandedNodes(readingTree.Nodes);
        Require(after == before, "Expanded node count changed after restore. Before=" + before + ", after=" + after + ".");
    }

    private void SelfTestTrayStatusText()
    {
        EnsureSelfTestRows();
        var keys = latestRows.Where(IsSelectableReadoutRow).Select(RowSettingsKey).Where(k => !string.IsNullOrWhiteSpace(k)).Take(MaxTrayStatusReadings).ToList();
        Require(keys.Count > 0, "No selectable rows for tray status.");
        settings.TrayItemKeys = keys;
        settings.TrayStatusEnabled = true;
        settings.TrayTooltipShowsPartialReadings = true;
        var extendedText = BuildTrayTooltipText(GetTrayStatusRows(), BuildCurrentSpeechStatusText());
        Require(extendedText.Length <= ExtendedTrayTooltipTextLimit, "Extended tray tooltip exceeds Windows limit.");
        UpdateTrayStatus();
        Require(!string.IsNullOrWhiteSpace(trayIcon.Text), "Tray tooltip is empty in partial mode.");
        Require(trayIcon.Text.Length <= WinFormsTrayTooltipTextLimit, "WinForms tray tooltip fallback exceeds Windows Forms limit.");
        settings.TrayTooltipShowsPartialReadings = false;
        UpdateTrayStatus();
        Require(!string.IsNullOrWhiteSpace(trayIcon.Text), "Tray tooltip is empty in fallback mode.");
        Require(trayIcon.Text.Length <= WinFormsTrayTooltipTextLimit, "Fallback tray tooltip exceeds Windows Forms limit.");
    }

    private void SelfTestHotkeysMenu()
    {
        EnsureSelfTestRows();
        var row = latestRows.FirstOrDefault(IsSelectableReadoutRow);
        Require(row != null, "No selectable row for hotkeys menu setup.");
        settings.ShowHideHotKey = "Ctrl+Alt+F12";
        settings.SpeakTrayHotKey = "Ctrl+Alt+F11";
        settings.SpokenHotKeys = new List<SpokenHotKeySetting>
        {
            new SpokenHotKeySetting
            {
                Name = "Self-test spoken hotkey",
                HotKey = "Ctrl+Alt+F10",
                ReadingKeys = new List<string> { RowSettingsKey(row) }
            }
        };
        settings.FanProfiles = new List<FanProfileSetting>
        {
            new FanProfileSetting
            {
                Name = "Self-test fan profile",
                HotKey = "Ctrl+Alt+F9",
                Actions = new List<FanProfileActionSetting>()
            }
        };
        BuildHotkeysMenu();
        Require(hotkeysMenu.DropDownItems.Count >= 5, "Hotkeys menu did not populate.");
        Require(ContainsToolStripText(hotkeysMenu.DropDownItems, "Ctrl+Alt+F11"), "Speak tray hotkey not shown in Hotkeys menu.");
        Require(ContainsToolStripText(hotkeysMenu.DropDownItems, "Self-test spoken hotkey"), "Spoken hotkey profile not shown in Hotkeys menu.");
        Require(ContainsToolStripText(hotkeysMenu.DropDownItems, "Self-test fan profile"), "Fan profile hotkey not shown in Hotkeys menu.");
    }

    private static bool ContainsToolStripText(ToolStripItemCollection items, string text)
    {
        foreach (ToolStripItem item in items)
        {
            if ((item.Text ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var dropDown = item as ToolStripDropDownItem;
            if (dropDown != null && ContainsToolStripText(dropDown.DropDownItems, text))
            {
                return true;
            }
        }

        return false;
    }

    private void SelfTestSpokenHotKeyAssignment()
    {
        EnsureSelfTestRows();
        var row = latestRows.FirstOrDefault(IsSelectableReadoutRow);
        Require(row != null, "No selectable row for spoken hotkey assignment.");
        var key = RowSettingsKey(row);
        settings.TrayItemKeys = new List<string>();
        settings.TrayItemKeys.Add(key);
        settings.TrayStatusEnabled = true;
        SaveSettings(settings);
        Require(LoadSettings().TrayItemKeys.Contains(key), "Tray quick assignment did not persist.");
        settings.TrayItemKeys.Remove(key);
        SaveSettings(settings);
        Require(!LoadSettings().TrayItemKeys.Contains(key), "Tray quick removal did not persist.");
        var profile = new SpokenHotKeySetting { Name = "Self-test spoken hotkey", HotKey = "Ctrl+Alt+F10", ReadingKeys = new List<string>() };
        settings.SpokenHotKeys = new List<SpokenHotKeySetting> { profile };
        profile.ReadingKeys.Add(key);
        SaveSettings(settings);
        var reloaded = LoadSettings();
        var reloadedProfile = reloaded.SpokenHotKeys.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
        Require(reloadedProfile != null && reloadedProfile.ReadingKeys.Contains(key), "Spoken hotkey assignment did not persist.");
        reloadedProfile.ReadingKeys.Remove(key);
        settings.SpokenHotKeys = reloaded.SpokenHotKeys;
        SaveSettings(settings);
        Require(!LoadSettings().SpokenHotKeys.First(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal)).ReadingKeys.Contains(key), "Spoken hotkey removal did not persist.");
    }

    private void SelfTestAlarmAndFanCurvePersistence()
    {
        EnsureSelfTestRows();
        var row = latestRows.FirstOrDefault(r => IsSelectableReadoutRow(r) && r.Value.HasValue) ?? latestRows.FirstOrDefault(IsSelectableReadoutRow);
        Require(row != null, "No row available for alarm setup.");
        settings.Alarms = new List<AlarmSetting>
        {
            new AlarmSetting
            {
                Name = "Self-test alarm",
                ReadingKey = RowSettingsKey(row),
                Condition = "Above",
                Threshold = 999999,
                Enabled = true,
                Speak = false,
                SoundFile = "",
                CooldownSeconds = 1
            }
        };
        settings.FanCurves = new List<FanCurveSetting>
        {
            new FanCurveSetting
            {
                Name = "Self-test disabled fan curve",
                Enabled = false,
                TemperatureReadingKey = RowSettingsKey(row),
                FanControlKey = "self-test-fan-control",
                LowTemperatureC = 30,
                HighTemperatureC = 70,
                LowPercent = 20,
                HighPercent = 80
            }
        };
        SaveSettings(settings);
        var reloaded = LoadSettings();
        Require(reloaded.Alarms.Any(a => string.Equals(a.Name, "Self-test alarm", StringComparison.Ordinal)), "Alarm did not persist.");
        Require(reloaded.FanCurves.Any(c => string.Equals(c.Name, "Self-test disabled fan curve", StringComparison.Ordinal)), "Fan curve did not persist.");
        CheckAlarms(latestRows);
    }

    private void SelfTestReportWriting(string outputFolder)
    {
        EnsureSelfTestRows();
        var txt = Path.Combine(outputFolder, "self-test-report.txt");
        var html = Path.Combine(outputFolder, "self-test-report.html");
        SaveReportToFile(txt, false, false);
        SaveReportToFile(html, true, false);
        Require(File.Exists(txt) && new FileInfo(txt).Length > 0, "TXT report was not written.");
        Require(File.Exists(html) && new FileInfo(html).Length > 0, "HTML report was not written.");
        var txtText = File.ReadAllText(txt);
        var htmlText = File.ReadAllText(html);
        Require(txtText.Contains("Sensor Readout"), "TXT report does not look like a Sensor Readout report.");
        Require(htmlText.Contains("Sensor Readout"), "HTML report does not look like a Sensor Readout report.");
        Require(txtText.Contains("[SensorReadoutReportData]"), "TXT report missing wrapped internal report data.");
        Require(!Regex.IsMatch(txtText, @"(?im)^\s*Printer\s+.+\s+(status|driver|port|offline|shared|jobs queued|paper size|resolution|color|duplex):"),
            "TXT report contains verbose printer prefixes instead of the grouped printer tree.");
    }

    private void SelfTestReportReopen(string outputFolder)
    {
        var html = Path.Combine(outputFolder, "self-test-report.html");
        if (!File.Exists(html))
        {
            SelfTestReportWriting(outputFolder);
        }

        LoadReportFile(html);
        Require(reportViewMode, "HTML report did not enter report view.");
        Require(latestRows.Count > 0, "Report view has no rows.");
        ReturnToLiveReadings();

        var zip = Path.Combine(outputFolder, "self-test-report.zip");
        if (File.Exists(zip))
        {
            File.Delete(zip);
        }

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            File.WriteAllText(Path.Combine(outputFolder, "self-test-summary-noise.txt"), "This file should not be selected as the report.");
            archive.CreateEntryFromFile(Path.Combine(outputFolder, "self-test-summary-noise.txt"), "00-summary.txt");
            archive.CreateEntryFromFile(html, "reports/self-test-report.html");
        }

        LoadReportFile(zip);
        Require(reportViewMode, "ZIP report did not enter report view.");
        Require(latestRows.Count > 0, "ZIP report view has no rows.");
        ReturnToLiveReadings();
    }

    private void SelfTestDiagnosticsZip(string outputFolder)
    {
        EnsureSelfTestRows();
        var staging = Path.Combine(outputFolder, "self-test-diagnostics-staging");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
        Directory.CreateDirectory(staging);

        var txt = Path.Combine(staging, "SensorReadout-report.txt");
        var html = Path.Combine(staging, "SensorReadout-report.html");
        var summary = Path.Combine(staging, "Diagnostics-summary.txt");
        SaveReportToFile(txt, false, false);
        SaveReportToFile(html, true, false);
        File.WriteAllText(summary, "Self-test diagnostics bundle. Fan-control diagnostics are intentionally skipped in automated self-test mode.");
        var logPath = GetLogFilePath();
        if (File.Exists(logPath))
        {
            File.Copy(logPath, Path.Combine(staging, "SensorReadout-debug.log"), true);
        }

        var zip = Path.Combine(outputFolder, "self-test-diagnostics.zip");
        if (File.Exists(zip))
        {
            File.Delete(zip);
        }
        ZipFile.CreateFromDirectory(staging, zip);
        Directory.Delete(staging, true);
        Require(File.Exists(zip) && new FileInfo(zip).Length > 0, "Diagnostics ZIP was not created.");
        using (var archive = ZipFile.OpenRead(zip))
        {
            Require(archive.Entries.Any(e => e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)), "Diagnostics ZIP missing HTML report.");
            Require(archive.Entries.Any(e => e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)), "Diagnostics ZIP missing text files.");
        }
    }

    private void SelfTestLanguageAndManualFiles()
    {
        Require(Directory.Exists(GetLanguagesFolderPath()), "Langs folder missing.");
        var englishLanguagePath = Path.Combine(GetLanguagesFolderPath(), DefaultLanguageFileName);
        Require(File.Exists(englishLanguagePath), "English language file missing.");
        Require(Directory.Exists(GetDocsFolderPath()), "Docs folder missing.");
        Require(File.Exists(Path.Combine(GetDocsFolderPath(), "README-en.html")), "English HTML manual missing.");
        RefreshLanguageChoices(true);
        Require(languageChoices.Count > 0, "No language choices loaded.");

        var languageFiles = Directory.GetFiles(GetLanguagesFolderPath(), "*.txt");
        Require(languageFiles.Length > 0, "No bundled language files found.");
        var englishKeys = ReadLanguageKeys(englishLanguagePath);
        Require(englishKeys.Count > 0, "English language file has no keys.");
        foreach (var languageFile in languageFiles.Where(p => !string.Equals(Path.GetFileName(p), DefaultLanguageFileName, StringComparison.OrdinalIgnoreCase)))
        {
            var keys = ReadLanguageKeys(languageFile);
            var missing = englishKeys.Except(keys).OrderBy(k => k, StringComparer.Ordinal).Take(10).ToList();
            var extra = keys.Except(englishKeys).OrderBy(k => k, StringComparer.Ordinal).Take(10).ToList();
            Require(missing.Count == 0, Path.GetFileName(languageFile) + " missing language keys: " + string.Join(", ", missing));
            Require(extra.Count == 0, Path.GetFileName(languageFile) + " has unknown language keys: " + string.Join(", ", extra));
        }
    }

    private static HashSet<string> ReadLanguageKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            keys.Add(line.Substring(0, equals).Trim());
        }

        return keys;
    }

    private void EnsureSelfTestRows()
    {
        if (latestRows.Count == 0)
        {
            SelfTestSensorCollection();
        }
    }

    private static int CountTreeNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            count++;
            count += CountTreeNodes(node.Nodes);
        }

        return count;
    }

    private static int CountExpandedNodes(TreeNodeCollection nodes)
    {
        var count = 0;
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded)
            {
                count++;
            }
            count += CountExpandedNodes(node.Nodes);
        }

        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WriteSelfTestSummary(string outputFolder, DateTime started, List<SelfTestResult> results)
    {
        var finished = DateTime.Now;
        var lines = new List<string>
        {
            "Sensor Readout self-test",
            "Started: " + started.ToString("yyyy-MM-dd HH:mm:ss"),
            "Finished: " + finished.ToString("yyyy-MM-dd HH:mm:ss"),
            "Version: " + AppVersion,
            "Executable: " + Application.ExecutablePath,
            "Base folder: " + AppDomain.CurrentDomain.BaseDirectory,
            "Result: " + (results.All(r => r.Passed) ? "PASS" : "FAIL"),
            ""
        };
        foreach (var result in results)
        {
            lines.Add((result.Passed ? "PASS" : "FAIL") + " [" + result.Milliseconds + " ms] " + result.Name + " - " + result.Message);
        }

        File.WriteAllLines(Path.Combine(outputFolder, "SelfTest-summary.txt"), lines.ToArray());
        File.WriteAllText(Path.Combine(outputFolder, "SelfTest-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
    }
}
