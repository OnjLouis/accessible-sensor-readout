using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private bool diagnosticsRunning;

    private sealed class DiagnosticFanRestore
    {
        public string Identifier = "";
        public string Name = "";
        public bool Manual;
        public int Percent = 50;
    }

    private void RunDiagnostics()
    {
        var diagnosticsCaption = StripMenuMnemonic(T("ui.Run diagnostics...", "Run diagnostics..."));
        if (diagnosticsRunning)
        {
            MessageBox.Show(this, T("message.diagnosticsAlreadyRunning", "Diagnostics are already running."), diagnosticsCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            T("message.diagnosticsConfirm", "Run diagnostics? Sensor Readout will create text and HTML reports, collect a debug log, briefly try writable fan controls at 100%, restore their previous automatic, manual, or fan-curve state, zip the results, and open the Reports folder."),
            diagnosticsCaption,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        diagnosticsRunning = true;
        var timerWasEnabled = timer.Enabled;
        timer.Stop();

        Task.Factory.StartNew(delegate
        {
            return SaveDiagnosticsToZip("", null, null);
        }).ContinueWith(delegate(Task<string> task)
        {
            diagnosticsRunning = false;
            if (timerWasEnabled)
            {
                ApplyTimerSettings();
            }

            if (task.IsFaulted)
            {
                var message = task.Exception == null ? "Diagnostics failed." : task.Exception.GetBaseException().Message;
                statusLabel.Text = T("status.diagnosticsFailed", "Diagnostics failed.") + " " + message;
                LogError("Diagnostics failed. " + message);
                MessageBox.Show(this, T("status.diagnosticsFailed", "Diagnostics failed.") + " " + message, diagnosticsCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var zipPath = task.Result;
            statusLabel.Text = T("status.diagnosticsSavedTo", "Diagnostics saved to") + " " + zipPath + ".";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + zipPath + "\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                try { Process.Start(GetReportsFolderPath()); } catch { }
            }

            MessageBox.Show(this, T("status.diagnosticsSavedTo", "Diagnostics saved to") + ":" + Environment.NewLine + zipPath, diagnosticsCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public string SaveDiagnosticsToZip(string requestedPath, bool? speakOverride, bool? soundOverride)
    {
        var announce = speakOverride.HasValue ? speakOverride.Value : settings.DiagnosticsSpeakProgress;
        var playSounds = soundOverride.HasValue ? soundOverride.Value : settings.DiagnosticsPlaySounds;
        var started = DateTime.Now;
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        var zipPath = ResolveDiagnosticsZipPath(requestedPath, stamp);
        var reportsFolder = System.IO.Path.GetDirectoryName(zipPath);
        if (string.IsNullOrWhiteSpace(reportsFolder))
        {
            reportsFolder = GetReportsFolderPath();
            zipPath = System.IO.Path.Combine(reportsFolder, System.IO.Path.GetFileName(zipPath));
        }

        System.IO.Directory.CreateDirectory(reportsFolder);
        var stagingFolder = System.IO.Path.Combine(reportsFolder, System.IO.Path.GetFileNameWithoutExtension(zipPath));
        if (System.IO.Directory.Exists(stagingFolder))
        {
            System.IO.Directory.Delete(stagingFolder, true);
        }
        System.IO.Directory.CreateDirectory(stagingFolder);

        var summaryLines = new List<string>();
        var totalStopwatch = Stopwatch.StartNew();
        var finalRows = new List<SensorRow>();

        try
        {
            forceDebugLogging = true;
            AnnounceDiagnosticStep(T("status.diagnosticsStarting", "Starting Sensor Readout diagnostics."), announce, true);
            PlayDiagnosticSound(settings.DiagnosticsStartSoundFile, true, playSounds);
            LogMessage("Debug", "Diagnostics started.");
            AnnounceDiagnosticStep(T("status.diagnosticsCollectingSensors", "Collecting sensor data."), announce);
            var collectStopwatch = Stopwatch.StartNew();
            var initialRows = CollectSensorRows(true);
            collectStopwatch.Stop();
            summaryLines.Add("Initial sensor collection: " + initialRows.Count + " rows in " + FormatElapsed(collectStopwatch.Elapsed) + ".");
            AddRowSummary(summaryLines, initialRows);

            AnnounceDiagnosticStep(T("status.diagnosticsTestingFans", "Testing fan controls."), announce);
            summaryLines.Add("");
            summaryLines.Add("Fan control test:");
            summaryLines.AddRange(RunFanDiagnostics(initialRows));

            AnnounceDiagnosticStep(T("status.diagnosticsCollectingFinalSensors", "Collecting final sensor data."), announce);
            var finalCollectStopwatch = Stopwatch.StartNew();
            finalRows = CollectSensorRows(true);
            finalCollectStopwatch.Stop();
            summaryLines.Add("");
            summaryLines.Add("Final sensor collection: " + finalRows.Count + " rows in " + FormatElapsed(finalCollectStopwatch.Elapsed) + ".");
            AddRowSummary(summaryLines, finalRows);

            AnnounceDiagnosticStep(T("status.diagnosticsWritingReports", "Writing reports."), announce);
            var txtReport = System.IO.Path.Combine(stagingFolder, "SensorReadout-report.txt");
            var htmlReport = System.IO.Path.Combine(stagingFolder, "SensorReadout-report.html");
            SetLatestRows(finalRows);
            SaveReportToFile(txtReport, false, false);
            SaveReportToFile(htmlReport, true, false);

            totalStopwatch.Stop();
            var summaryPath = System.IO.Path.Combine(stagingFolder, "Diagnostics-summary.txt");
            System.IO.File.WriteAllLines(summaryPath, BuildDiagnosticSummary(started, totalStopwatch.Elapsed, summaryLines).ToArray());

            AnnounceDiagnosticStep(T("status.diagnosticsCreatingZip", "Creating diagnostics zip file."), announce);
            LogMessage("Debug", "Diagnostics: Complete.");
            var logPath = GetLogFilePath();
            if (System.IO.File.Exists(logPath))
            {
                System.IO.File.Copy(logPath, System.IO.Path.Combine(stagingFolder, "SensorReadout-debug.log"), true);
            }

            if (System.IO.File.Exists(zipPath))
            {
                System.IO.File.Delete(zipPath);
            }
            System.IO.Compression.ZipFile.CreateFromDirectory(stagingFolder, zipPath, System.IO.Compression.CompressionLevel.Optimal, false);
            LogMessage("Debug", "Diagnostics zip created: " + zipPath);
            DeleteDiagnosticsStagingFolder(stagingFolder);
            AnnounceDiagnosticStep(T("status.diagnosticsCompleteShort", "Complete."), announce, true);
            PlayDiagnosticSound(settings.DiagnosticsCompleteSoundFile, false, playSounds);
            return zipPath;
        }
        finally
        {
            forceDebugLogging = false;
            DeleteDiagnosticsStagingFolder(stagingFolder);
        }
    }

    private static void PlayDiagnosticSound(string fileName, bool start, bool playSounds)
    {
        if (!playSounds)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            PlaySoundFileSync(fileName);
            return;
        }

        if (start)
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        else
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private static string ResolveDiagnosticsZipPath(string requestedPath, string stamp)
    {
        var defaultName = "SensorReadout-Diagnostics-" + SafeFileName(Environment.MachineName) + "-" + stamp + ".zip";
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return System.IO.Path.Combine(GetReportsFolderPath(), defaultName);
        }

        var path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath.Trim('"')));
        if (System.IO.Directory.Exists(path))
        {
            return System.IO.Path.Combine(path, defaultName);
        }

        if (string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return System.IO.Path.ChangeExtension(path, ".zip");
    }

    private static string SafeFileName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Computer" : value.Trim();
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(text) ? "Computer" : text;
    }

    private void DeleteDiagnosticsStagingFolder(string stagingFolder)
    {
        if (string.IsNullOrWhiteSpace(stagingFolder) || !System.IO.Directory.Exists(stagingFolder))
        {
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(stagingFolder, true);
                LogMessage("Debug", "Diagnostics staging folder removed: " + stagingFolder);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 5)
                {
                    LogError("Diagnostics could not remove staging folder " + stagingFolder + ": " + ex.Message);
                    return;
                }

                Thread.Sleep(150);
            }
        }
    }

    private List<string> RunFanDiagnostics(List<SensorRow> rows)
    {
        var lines = new List<string>();
        var controls = (rows ?? new List<SensorRow>())
            .Where(r => r.Type == "Fan Control" && !string.IsNullOrWhiteSpace(r.Identifier))
            .GroupBy(r => IdentifierFromSettingsKey(r.Identifier), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .ToList();
        if (controls.Count == 0)
        {
            lines.Add("No writable fan controls were visible.");
            LogMessage("Debug", "Diagnostics fan test skipped: no writable fan controls.");
            return lines;
        }

        var saved = LoadFanControlSettings();
        var restore = new List<DiagnosticFanRestore>();
        foreach (var control in controls)
        {
            var identifier = IdentifierFromSettingsKey(control.Identifier);
            FanControlSetting setting;
            restore.Add(new DiagnosticFanRestore
            {
                Identifier = identifier,
                Name = CleanSensorName(control.Name),
                Manual = saved.TryGetValue(identifier, out setting) && setting != null && setting.Manual,
                Percent = setting == null ? 50 : Math.Max(0, Math.Min(100, setting.Percent))
            });
        }

        var touched = new List<DiagnosticFanRestore>();
        foreach (var item in restore)
        {
            try
            {
                SetLibreHardwareMonitorControl(item.Identifier, 100, true);
                touched.Add(item);
                lines.Add("Set " + item.Name + " to 100%: OK.");
                LogMessage("Debug", "Diagnostics set fan control to 100%: " + item.Identifier);
            }
            catch (Exception ex)
            {
                lines.Add("Set " + item.Name + " to 100%: failed, " + ex.Message);
                LogError("Diagnostics could not set fan control " + item.Identifier + " to 100%: " + ex.Message);
            }
        }

        try
        {
            if (touched.Count > 0)
            {
                SuspendFanCurvesForManualControls(touched.Select(i => i.Identifier));
                Thread.Sleep(1500);
            }
        }
        finally
        {
            foreach (var item in touched)
            {
                try
                {
                    SetLibreHardwareMonitorControl(item.Identifier, item.Percent, item.Manual);
                    lines.Add("Restored " + item.Name + " to " + (item.Manual ? item.Percent + "% manual" : "automatic/fan-curve state") + ": OK.");
                    LogMessage("Debug", "Diagnostics restored fan control " + item.Identifier + " to " + (item.Manual ? item.Percent + "% manual." : "automatic/default."));
                }
                catch (Exception ex)
                {
                    lines.Add("Restored " + item.Name + ": failed, " + ex.Message);
                    LogError("Diagnostics could not restore fan control " + item.Identifier + ": " + ex.Message);
                }
            }
        }

        var manualIds = touched.Where(i => i.Manual).Select(i => i.Identifier).ToList();
        var automaticIds = touched.Where(i => !i.Manual).Select(i => i.Identifier).ToList();
        var suspended = SuspendFanCurvesForManualControls(manualIds);
        var resumed = ResumeFanCurvesForAutomaticControls(automaticIds);
        if (suspended > 0 || resumed > 0)
        {
            lines.Add("Fan curves restored: " + suspended + " kept suspended, " + resumed + " resumed.");
        }

        return lines;
    }

    private List<string> BuildDiagnosticSummary(DateTime started, TimeSpan elapsed, List<string> details)
    {
        var lines = new List<string>();
        lines.Add("Sensor Readout diagnostics");
        lines.Add("Generated: " + started.ToString("yyyy-MM-dd HH:mm:ss"));
        lines.Add("Duration: " + FormatElapsed(elapsed));
        lines.Add("App version: " + AppVersion);
        lines.Add("Machine: " + Environment.MachineName);
        lines.Add("OS: " + GetWindowsVersionText());
        lines.Add("64-bit process: " + Environment.Is64BitProcess);
        lines.Add("Administrator: " + (IsAdministrator() ? "yes" : "no"));
        lines.Add("Language file: " + (string.IsNullOrWhiteSpace(settings.LanguageFile) ? "automatic/default" : settings.LanguageFile));
        AddDiagnosticPlugInSummary(lines);
        lines.Add("Logging was temporarily set to Debug for this diagnostic run.");
        lines.Add("");
        lines.AddRange(details ?? new List<string>());
        return lines;
    }

    private void AddDiagnosticPlugInSummary(List<string> lines)
    {
        try
        {
            var plugIns = LoadPlugInPreferenceInfos(settings);
            if (plugIns.Count == 0)
            {
                lines.Add("Plug-ins: none found");
                return;
            }

            lines.Add("Plug-ins:");
            foreach (var plugIn in plugIns)
            {
                var name = string.IsNullOrWhiteSpace(plugIn.Name) ? plugIn.Id : plugIn.Name;
                var version = string.IsNullOrWhiteSpace(plugIn.Version) ? "" : " " + plugIn.Version;
                lines.Add("  " + name + version + ": " + (plugIn.Enabled ? "enabled" : "disabled"));
            }
        }
        catch (Exception ex)
        {
            lines.Add("Plug-ins: could not read plug-in state (" + ex.Message + ")");
        }
    }

    private static void AddRowSummary(List<string> lines, List<SensorRow> rows)
    {
        foreach (var group in (rows ?? new List<SensorRow>()).GroupBy(r => r.Type ?? "").OrderBy(g => g.Key))
        {
            lines.Add("  " + (string.IsNullOrWhiteSpace(group.Key) ? "Unknown" : group.Key) + ": " + group.Count() + " rows");
        }
    }

    private void AnnounceDiagnosticStep(string message, bool announce)
    {
        AnnounceDiagnosticStep(message, announce, false);
    }

    private void AnnounceDiagnosticStep(string message, bool announce, bool immediate)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
        }

        LogMessage("Debug", "Diagnostics: " + message);
        try
        {
            if (statusLabel != null && !statusLabel.IsDisposed)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate { statusLabel.Text = message; }));
                }
                else
                {
                    statusLabel.Text = message;
                }
            }
        }
        catch
        {
        }

        if (announce)
        {
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (immediate)
                        {
                            SpeakTextWithScreenReader(message, "diagnostics");
                        }
                        else
                        {
                            SpeakTextWithScreenReaderPolite(message, "diagnostics");
                        }
                    }));
                }
                else
                {
                    if (immediate)
                    {
                        SpeakTextWithScreenReader(message, "diagnostics");
                    }
                    else
                    {
                        SpeakTextWithScreenReaderPolite(message, "diagnostics");
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowsVersionText()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    var caption = Convert.ToString(item["Caption"]);
                    var version = Convert.ToString(item["Version"]);
                    var build = Convert.ToString(item["BuildNumber"]);
                    var architecture = Convert.ToString(item["OSArchitecture"]);
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        parts.Add(caption.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        parts.Add("version " + version.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(build))
                    {
                        parts.Add("build " + build.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(architecture))
                    {
                        parts.Add(architecture.Trim());
                    }

                    if (parts.Count > 0)
                    {
                        return string.Join(", ", parts.ToArray());
                    }
                }
            }
        }
        catch
        {
        }

        return Environment.OSVersion.ToString();
    }
}
