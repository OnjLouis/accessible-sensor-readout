using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            form.RunSelfTestStep(results, "Remote monitoring encryption and identity", delegate { form.SelfTestRemoteMonitoringCrypto(); });
            form.RunSelfTestStep(results, "Remote monitoring server round-trip and privacy", delegate { form.SelfTestRemoteMonitoringServer(outputFolder); });
            form.RunSelfTestStep(results, "Embedded remote server round-trip and privacy", delegate { form.SelfTestEmbeddedRemoteMonitoringServer(outputFolder); });
            form.RunSelfTestStep(results, "Global hotkey validation", delegate { form.SelfTestGlobalHotKeyValidation(); });
            form.RunSelfTestStep(results, "Sensor collection", delegate { form.SelfTestSensorCollection(); });
            form.RunSelfTestStep(results, "Storage inventory parsers and privacy", delegate { form.SelfTestStorageInventoryParsersAndPrivacy(); });
            form.RunSelfTestStep(results, "PCIe slot summary wording", delegate { form.SelfTestPciSlotSummaryWording(); });
            form.RunSelfTestStep(results, "Wi-Fi BSS list bounds", delegate { form.SelfTestWifiBssListBounds(); });
            form.RunSelfTestStep(results, "Listening port details split", delegate { form.SelfTestListeningPortDetailsSplit(); });
            form.RunSelfTestStep(results, "Network tools privacy and shortcut", delegate { form.SelfTestNetworkToolsPrivacyAndShortcut(); });
            form.RunSelfTestStep(results, "USB SuperSpeedPlus speed decoding", delegate { form.SelfTestUsbSuperSpeedPlusSpeedDecoding(); });
            form.RunSelfTestStep(results, "Bluetooth and battery filtering", delegate { form.SelfTestBluetoothAndBatteryFiltering(); });
            form.RunSelfTestStep(results, "Performance group boundaries", delegate { form.SelfTestPerformanceGroupBoundaries(); });
            form.RunSelfTestStep(results, "Category tree navigation", delegate { form.SelfTestCategoryNavigation(); });
            form.RunSelfTestStep(results, "Category speech modes", delegate { form.SelfTestCategorySpeechModes(); });
            form.RunSelfTestStep(results, "Expand and collapse commands", delegate { form.SelfTestExpandCollapse(); });
            form.RunSelfTestStep(results, "Reading tree expansion preference", delegate { form.SelfTestReadingTreeExpansionPreference(); });
            form.RunSelfTestStep(results, "Show/hide expansion preservation", delegate { form.SelfTestExpansionPreservation(); });
            form.RunSelfTestStep(results, "Tray tooltip modes", delegate { form.SelfTestTrayStatusText(); });
            form.RunSelfTestStep(results, "Visual status badges and meters", delegate { form.SelfTestVisualStatusBadgesAndMeters(); });
            form.RunSelfTestStep(results, "Byte unit formatting modes", delegate { form.SelfTestByteUnitFormattingModes(); });
            form.RunSelfTestStep(results, "Pending refresh coalescing", delegate { form.SelfTestPendingRefreshCoalescing(); });
            form.RunSelfTestStep(results, "Background hotkey refresh cadence", delegate { form.SelfTestBackgroundHotKeyRefreshCadence(); });
            form.RunSelfTestStep(results, "Formatted row cache clearing", delegate { form.SelfTestFormattedRowCacheClearing(); });
            form.RunSelfTestStep(results, "Fragile WMI row caches", delegate { form.SelfTestFragileWmiRowCaches(); });
            form.RunSelfTestStep(results, "Spoken hotkey mirror order", delegate { form.SelfTestSpokenHotKeyMirrorOrder(); });
            form.RunSelfTestStep(results, "Task row refresh cache", delegate { form.SelfTestTaskRowRefreshCache(); });
            form.RunSelfTestStep(results, "Process watch report", delegate { form.SelfTestProcessWatchReport(); });
            form.RunSelfTestStep(results, "Audio latency aggregation and privacy", delegate { form.SelfTestAudioLatencyAggregationAndPrivacy(); });
            form.RunSelfTestStep(results, "Crash log writing", delegate { form.SelfTestCrashLogWriting(); });
            form.RunSelfTestStep(results, "Installed app registration", delegate { form.SelfTestInstalledAppRegistration(outputFolder); });
            form.RunSelfTestStep(results, "Hotkeys menu", delegate { form.SelfTestHotkeysMenu(); });
            form.RunSelfTestStep(results, "UI mnemonic uniqueness", delegate { form.SelfTestUiMnemonicUniqueness(); });
            form.RunSelfTestStep(results, "Preferences category and shortcut behavior", delegate { form.SelfTestPreferencesCategoryAndShortcutBehavior(); });
            form.RunSelfTestStep(results, "Plug-in preference identity", delegate { form.SelfTestPlugInPreferenceIdentity(); });
            form.RunSelfTestStep(results, "Windows setting target mapping", delegate { form.SelfTestWindowsSettingTargetMapping(); });
            form.RunSelfTestStep(results, "Spoken hotkey assignment persistence", delegate { form.SelfTestSpokenHotKeyAssignment(); });
            form.RunSelfTestStep(results, "Alarm and fan curve persistence", delegate { form.SelfTestAlarmAndFanCurvePersistence(); });
            form.RunSelfTestStep(results, "TXT and HTML report writing", delegate { form.SelfTestReportWriting(outputFolder); });
            form.RunSelfTestStep(results, "Report reopening and ZIP selection", delegate { form.SelfTestReportReopen(outputFolder); });
            form.RunSelfTestStep(results, "Report tools and reading history", delegate { form.SelfTestReportToolsAndHistory(outputFolder); });
            form.RunSelfTestStep(results, "Community stats payload privacy", delegate { form.SelfTestCommunityStatsPayloadPrivacy(); });
            form.RunSelfTestStep(results, "Diagnostics ZIP creation", delegate { form.SelfTestDiagnosticsZip(outputFolder); });
            form.RunSelfTestStep(results, "Client update channel and replacement safety", delegate { form.SelfTestUpdateChannelSeparation(outputFolder); });
            form.RunSelfTestStep(results, "Language and manual files", delegate { form.SelfTestLanguageAndManualFiles(); });
            form.RunSelfTestStep(results, "Bundled plug-in manifest repair", delegate { form.SelfTestBundledPlugInManifestRepair(outputFolder); });
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
        settings.CommunityStatsClientId = "self-test-client-id";
        settings.ReadingTreeExpansionMode = ReadingTreeExpansionRemember;
        settings.ReadingTreeLastExpanded = false;
        settings.CategorySpeechMode = CategorySpeechBrief;
        settings.FallbackCategorySpeechEnabled = true;
        settings.VisualSpokenFeedbackEnabled = true;
        settings.VisualSpokenFeedbackPlacement = "TopLeft";
        settings.VisualSpokenFeedbackTimeoutSeconds = 9;
        settings.RemoteMachineId = RemotePayloadCrypto.CreateRandomId();
        settings.RemoteConnections = new List<RemoteConnectionSetting>
        {
            new RemoteConnectionSetting
            {
                Id = RemotePayloadCrypto.CreateRandomId(),
                Name = "Self-test server",
                ServerUrl = "http://127.0.0.1:48673/",
                ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret("self-test-token"),
                ProtectedPassword = RemotePayloadCrypto.ProtectSecret("self-test-password"),
                PublishThisComputer = true,
                AllowRemoteFanProfiles = true,
                Enabled = true,
                PollIntervalSeconds = 7
            }
        };
        settings.RemoteHostEnabled = true;
        settings.RemoteHostPort = 49673;
        settings.RemoteHostConnectionUrl = "http://192.0.2.44:49673/";
        settings.ProtectedRemoteHostAccessToken = RemotePayloadCrypto.ProtectSecret("self-test-host-token");
        SaveSettings(settings);
        var reloaded = LoadSettings();
        Require(string.Equals(reloaded.ShowHideHotKey, "Ctrl+Alt+F12", StringComparison.OrdinalIgnoreCase), "Show/hide hotkey did not round-trip.");
        Require(string.Equals(reloaded.SpeakTrayHotKey, "Ctrl+Alt+F11", StringComparison.OrdinalIgnoreCase), "Speak tray hotkey did not round-trip.");
        Require(string.Equals(reloaded.CommunityStatsClientId, "self-test-client-id", StringComparison.Ordinal), "Community stats client ID did not round-trip.");
        Require(string.Equals(reloaded.ReadingTreeExpansionMode, ReadingTreeExpansionRemember, StringComparison.OrdinalIgnoreCase), "Reading tree expansion mode did not round-trip.");
        Require(!reloaded.ReadingTreeLastExpanded, "Reading tree last expanded state did not round-trip.");
        Require(string.Equals(reloaded.CategorySpeechMode, CategorySpeechBrief, StringComparison.Ordinal), "Category speech mode did not round-trip.");
        Require(reloaded.FallbackCategorySpeechEnabled, "Fallback category speech setting did not round-trip.");
        Require(reloaded.VisualSpokenFeedbackEnabled, "Visual spoken feedback setting did not round-trip.");
        Require(string.Equals(reloaded.VisualSpokenFeedbackPlacement, "TopLeft", StringComparison.Ordinal), "Visual spoken feedback placement did not round-trip.");
        Require(reloaded.VisualSpokenFeedbackTimeoutSeconds == 9, "Visual spoken feedback timeout did not round-trip.");
        Require(!string.IsNullOrWhiteSpace(reloaded.RemoteMachineId), "Remote machine identity did not round-trip.");
        Require(reloaded.RemoteConnections != null && reloaded.RemoteConnections.Count == 1 && reloaded.RemoteConnections[0].PollIntervalSeconds == 7, "Remote connection settings did not round-trip.");
        Require(reloaded.RemoteConnections[0].AllowRemoteFanProfiles, "Remote fan profile permission did not round-trip.");
        Require(string.Equals(RemotePayloadCrypto.UnprotectSecret(reloaded.RemoteConnections[0].ProtectedAccessToken), "self-test-token", StringComparison.Ordinal), "Protected remote server token did not round-trip.");
        Require(reloaded.RemoteHostEnabled && reloaded.RemoteHostPort == 49673, "Embedded remote server settings did not round-trip.");
        Require(string.Equals(reloaded.RemoteHostConnectionUrl, "http://192.0.2.44:49673/", StringComparison.Ordinal), "Embedded remote server connection address did not round-trip.");
        Require(string.Equals(RemotePayloadCrypto.UnprotectSecret(reloaded.ProtectedRemoteHostAccessToken), "self-test-host-token", StringComparison.Ordinal), "Protected embedded server token did not round-trip.");
        var transferPackage = BuildSettingsTransferPackage(new HashSet<string>(new[] { TransferTray }, StringComparer.OrdinalIgnoreCase));
        Require(transferPackage.MachineSettings == null || string.IsNullOrWhiteSpace(transferPackage.MachineSettings.CommunityStatsClientId), "Settings transfer exported the local community stats client ID.");
        Require(transferPackage.MachineSettings == null || string.IsNullOrWhiteSpace(transferPackage.MachineSettings.RemoteMachineId), "Settings transfer exported the remote machine identity.");
        Require(transferPackage.MachineSettings == null || transferPackage.MachineSettings.RemoteConnections == null || transferPackage.MachineSettings.RemoteConnections.Count == 0, "Settings transfer exported remote connection credentials.");
        Require(transferPackage.MachineSettings == null || (!transferPackage.MachineSettings.RemoteHostEnabled && string.IsNullOrWhiteSpace(transferPackage.MachineSettings.RemoteHostConnectionUrl) && string.IsNullOrWhiteSpace(transferPackage.MachineSettings.ProtectedRemoteHostAccessToken)), "Settings transfer exported embedded server credentials.");
    }

    private void SelfTestGlobalHotKeyValidation()
    {
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Alt+1")), "Unsafe Alt+number hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Ctrl+A")), "Unsafe single-modifier Ctrl+letter hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Shift+F1")), "Unsafe single-modifier Shift+function hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Alt+F4")), "Reserved Alt+F4 hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Ctrl+Esc")), "Reserved Ctrl+Esc hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Win+1")), "Reserved Win+number hotkey was accepted.");
        Require(string.IsNullOrWhiteSpace(NormalizeHotKeyText("Win+D")), "Reserved Windows desktop hotkey was accepted.");
        Require(string.Equals(NormalizeHotKeyText("Ctrl+Shift+F1"), "Ctrl+Shift+F1", StringComparison.OrdinalIgnoreCase), "Safe Ctrl+Shift function hotkey was rejected.");
        Require(string.Equals(NormalizeHotKeyText("Ctrl+Alt+F1"), "Ctrl+Alt+F1", StringComparison.OrdinalIgnoreCase), "Safe Ctrl+Alt function hotkey was rejected.");
        Require(string.Equals(NormalizeHotKeyText("Alt+Shift+F1"), "Alt+Shift+F1", StringComparison.OrdinalIgnoreCase), "Safe Alt+Shift function hotkey was rejected.");
        var configuredModifiers = NativeMethods.ModControl | NativeMethods.ModShift;
        var registrationModifiers = GlobalHotKeyRegistrationModifiers(configuredModifiers);
        Require((registrationModifiers & NativeMethods.ModNoRepeat) != 0, "Global hotkey registration did not suppress held-key repeat.");
        Require((registrationModifiers & configuredModifiers) == configuredModifiers, "Global hotkey registration lost configured modifiers.");
    }

    private void SelfTestInstalledAppRegistration(string outputFolder)
    {
        var testKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sensor Readout SelfTest " + Guid.NewGuid().ToString("N");
        var associationRoot = @"Software\SensorReadoutSelfTest\RemoteConnection " + Guid.NewGuid().ToString("N");
        var extensionKeyPath = associationRoot + @"\.srconnection";
        var testProgId = "SensorReadout.RemoteConnection.SelfTest";
        var progIdKeyPath = associationRoot + @"\" + testProgId;
        var installFolder = Path.Combine(outputFolder, "InstalledAppRegistration");
        Directory.CreateDirectory(installFolder);
        var exePath = Path.Combine(installFolder, "Sensor Readout.exe");
        File.WriteAllText(exePath, "self-test");

        try
        {
            RegisterInstalledAppEntry(exePath, installFolder, testKeyPath);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKeyPath))
            {
                Require(key != null, "Installed app registry key was not created.");
                Require(string.Equals(Convert.ToString(key.GetValue("DisplayName")), "Sensor Readout", StringComparison.Ordinal), "DisplayName was not registered.");
                Require(string.Equals(Convert.ToString(key.GetValue("DisplayVersion")), AppVersion, StringComparison.Ordinal), "DisplayVersion was not registered.");
                Require(string.Equals(Convert.ToString(key.GetValue("Publisher")), "Andre Louis", StringComparison.Ordinal), "Publisher was not registered.");
                Require(string.Equals(Convert.ToString(key.GetValue("InstallLocation")), installFolder, StringComparison.OrdinalIgnoreCase), "InstallLocation was not registered.");
                var uninstallString = Convert.ToString(key.GetValue("UninstallString")) ?? "";
                Require(uninstallString.IndexOf("--uninstall", StringComparison.OrdinalIgnoreCase) >= 0, "UninstallString does not call --uninstall.");
                Require(uninstallString.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0, "UninstallString does not reference the installed executable.");
                Require(Convert.ToInt32(key.GetValue("NoModify")) == 1, "NoModify was not registered.");
                Require(Convert.ToInt32(key.GetValue("NoRepair")) == 1, "NoRepair was not registered.");
                Require(Convert.ToInt32(key.GetValue("EstimatedSize")) > 0, "EstimatedSize was not registered.");
            }

            RegisterRemoteConnectionFileAssociation(exePath, extensionKeyPath, testProgId, progIdKeyPath, false);
            using (var extensionKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(extensionKeyPath))
            {
                Require(extensionKey != null, ".srconnection registry key was not created.");
                Require(string.Equals(Convert.ToString(extensionKey.GetValue("")), testProgId, StringComparison.Ordinal), ".srconnection ProgID was not registered.");
                Require(string.Equals(Convert.ToString(extensionKey.GetValue("Content Type")), RemoteConnectionContentType, StringComparison.Ordinal), ".srconnection content type was not registered.");
                using (var openWithKey = extensionKey.OpenSubKey("OpenWithProgids"))
                {
                    Require(openWithKey != null && openWithKey.GetValueNames().Any(name => string.Equals(name, testProgId, StringComparison.Ordinal)), ".srconnection OpenWithProgids entry was not registered.");
                }
            }
            using (var progIdKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(progIdKeyPath))
            {
                Require(progIdKey != null, ".srconnection ProgID key was not created.");
                using (var iconKey = progIdKey.OpenSubKey("DefaultIcon"))
                {
                    Require(iconKey != null && Convert.ToString(iconKey.GetValue("")).IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0, ".srconnection icon does not reference the installed executable.");
                }
                using (var commandKey = progIdKey.OpenSubKey(@"shell\open\command"))
                {
                    var command = commandKey == null ? "" : Convert.ToString(commandKey.GetValue("")) ?? "";
                    Require(command.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0, ".srconnection open command does not reference the installed executable.");
                    Require(command.IndexOf("--import-remote-connection", StringComparison.OrdinalIgnoreCase) >= 0, ".srconnection open command does not use the remote import path.");
                    Require(command.IndexOf("\"%1\"", StringComparison.Ordinal) >= 0, ".srconnection open command does not preserve a quoted file path.");
                }
            }
        }
        finally
        {
            UnregisterInstalledAppEntry(testKeyPath);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(associationRoot, false);
        }
    }

    private void SelfTestSensorCollection()
    {
        var rows = CollectSensorRows(true);
        Require(rows.Count > 0, "No sensor rows were collected.");
        SetLatestRows(rows);
        Require(rows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase)), "Performance rows missing.");
        Require(rows.Any(r => !string.IsNullOrWhiteSpace(r.Name)), "Collected rows have no names.");
    }

    private void SelfTestPciSlotSummaryWording()
    {
        var unknownSummary = FormatExpansionSlotSummary(5, 1, 0, 4);
        Require(unknownSummary.IndexOf("4 unknown usage", StringComparison.OrdinalIgnoreCase) >= 0, "Expansion slot summary omitted unknown usage.");
        Require(unknownSummary.IndexOf("0 empty", StringComparison.OrdinalIgnoreCase) < 0, "Expansion slot summary still says 0 empty.");
        Require(unknownSummary.IndexOf("reported empty", StringComparison.OrdinalIgnoreCase) < 0, "Expansion slot summary reported empty slots when no slot was reported empty.");

        var emptySummary = FormatExpansionSlotSummary(5, 1, 4, 0);
        Require(emptySummary.IndexOf("4 reported empty", StringComparison.OrdinalIgnoreCase) >= 0, "Expansion slot summary omitted reported empty slots.");
        Require(emptySummary.IndexOf("unknown usage", StringComparison.OrdinalIgnoreCase) < 0, "Expansion slot summary reported unknown usage when all slots were classified.");
    }

    private void SelfTestCrashLogWriting()
    {
        settings.LoggingLevel = "Off";
        SaveSettings(settings);
        var path = Program.WriteCrashLogForSelfTest();
        Require(File.Exists(path), "Crash log was not written when regular logging was off.");
        var text = File.ReadAllText(path);
        Require(text.IndexOf("Self-test crash log", StringComparison.OrdinalIgnoreCase) >= 0, "Crash log missing self-test source.");
        Require(text.IndexOf("Self-test crash log exception", StringComparison.OrdinalIgnoreCase) >= 0, "Crash log missing exception text.");
        Require(text.IndexOf("crash logs are always attempted", StringComparison.OrdinalIgnoreCase) >= 0, "Crash log missing regular-log independence note.");
        settings.LoggingLevel = "Debug";
        SaveSettings(settings);
    }

    private void SelfTestWifiBssListBounds()
    {
        var itemSize = Marshal.SizeOf(typeof(WlanBssEntry));
        Require(itemSize > 0, "WLAN BSS entry marshal size was not positive.");
        Require(SafeWlanBssEntryCount(8 + (itemSize * 2), 2, itemSize) == 2, "WLAN BSS list count did not use dwNumberOfItems.");
        Require(SafeWlanBssEntryCount(8 + (itemSize * 2), 2000, itemSize) == 2, "WLAN BSS list count was not capped by buffer size.");
        Require(SafeWlanBssEntryCount(4, 1, itemSize) == 0, "WLAN BSS list accepted a header smaller than the entry offset.");
    }

    private void SelfTestListeningPortDetailsSplit()
    {
        var tcpEndpoint = new IPEndPoint(IPAddress.Loopback, 12345);
        var udpEndpoint = new IPEndPoint(IPAddress.Loopback, 23456);
        var wildcardEndpoint = new IPEndPoint(IPAddress.Any, 34567);
        var tcp = Enumerable.Range(1, 12).Select(port => new IPEndPoint(IPAddress.Loopback, 12000 + port)).ToList();
        tcp[0] = tcpEndpoint;
        var udp = new List<IPEndPoint> { udpEndpoint };
        var tcpOwners = new Dictionary<string, List<ListeningPortOwner>>(StringComparer.OrdinalIgnoreCase);
        var udpOwners = new Dictionary<string, List<ListeningPortOwner>>(StringComparer.OrdinalIgnoreCase);
        AddListeningPortOwner(tcpOwners, new ListeningPortOwner { Protocol = "TCP", Endpoint = tcpEndpoint, ProcessId = 111, ProcessName = "tcp-test.exe" });
        AddListeningPortOwner(udpOwners, new ListeningPortOwner { Protocol = "UDP", Endpoint = udpEndpoint, ProcessId = 222, ProcessName = "udp-test.exe" });

        var tcpDetails = BuildListeningPortDetails("TCP", tcp, tcpOwners);
        var udpDetails = BuildListeningPortDetails("UDP", udp, udpOwners);
        var tcpEndpointText = FormatEndpoint(tcpEndpoint);
        var wildcardEndpointText = FormatEndpoint(wildcardEndpoint);

        Require(tcpDetails.ContainsKey("TCP listening port count"), "TCP listening details did not include TCP count.");
        Require(!tcpDetails.ContainsKey("UDP listening port count"), "TCP listening details included UDP count.");
        Require(tcpDetails.Values.Any(v => v.IndexOf("tcp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "TCP listening details did not include TCP owner.");
        Require(!tcpDetails.Values.Any(v => v.IndexOf("udp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "TCP listening details included UDP owner.");
        var orderedTcpDetailKeys = tcpDetails.Keys
            .OrderBy(key => UsbDetailSortIndex(key))
            .ThenBy(key => NaturalDetailSortKey(key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Require(orderedTcpDetailKeys.IndexOf("TCP listening endpoint 2") < orderedTcpDetailKeys.IndexOf("TCP listening endpoint 10"), "TCP endpoint details were sorted alphabetically instead of numerically.");
        Require(orderedTcpDetailKeys.IndexOf("TCP listening endpoint 10") < orderedTcpDetailKeys.IndexOf("TCP listening endpoint 11"), "TCP endpoint detail ordering was unstable above 10.");
        var tcpEndpoint2Path = GetDetailTreePath("TCP listening endpoint 2");
        var tcpEndpoint10Path = GetDetailTreePath("TCP listening endpoint 10");
        var udpEndpoint1Path = GetDetailTreePath("UDP listening endpoint 1");
        Require(tcpEndpoint2Path.Label == "2" && tcpEndpoint10Path.Label == "10" && udpEndpoint1Path.Label == "1", "Listening endpoint detail labels retained redundant protocol wording.");
        Require(tcpEndpoint2Path.SortIndex < tcpEndpoint10Path.SortIndex, "Concise listening endpoint labels did not retain numeric order.");
        Require(tcpEndpointText.StartsWith("localhost:12345", StringComparison.OrdinalIgnoreCase), "Loopback listening endpoint did not start with localhost and port.");
        Require(wildcardEndpointText.StartsWith("all IPv4 addresses:34567", StringComparison.OrdinalIgnoreCase), "Wildcard IPv4 listening endpoint did not use a friendly host label.");
        Require(udpDetails.ContainsKey("UDP listening port count"), "UDP listening details did not include UDP count.");
        Require(!udpDetails.ContainsKey("TCP listening port count"), "UDP listening details included TCP count.");
        Require(udpDetails.Values.Any(v => v.IndexOf("udp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "UDP listening details did not include UDP owner.");
        Require(!udpDetails.Values.Any(v => v.IndexOf("tcp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "UDP listening details included TCP owner.");
    }

    private void SelfTestNetworkToolsPrivacyAndShortcut()
    {
        string normalized;
        string error;
        Require(TryNormalizeNetworkToolTarget("https://example.com/path?q=1", out normalized, out error) && string.Equals(normalized, "example.com", StringComparison.OrdinalIgnoreCase), "Network Tools did not extract a host name from a URL.");
        Require(TryNormalizeNetworkToolTarget("192.168.1.25", out normalized, out error) && string.Equals(normalized, "192.168.1.25", StringComparison.Ordinal), "Network Tools did not preserve a valid IPv4 address.");
        Require(TryNormalizeNetworkToolTarget("2606:4700:4700::1111", out normalized, out error) && IPAddress.Parse(normalized).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6, "Network Tools did not preserve an explicit IPv6 address.");
        Require(!TryNormalizeNetworkToolTarget("not a valid host", out normalized, out error), "Network Tools accepted a host name containing spaces.");

        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("127.0.0.1")), "Network Tools would send a loopback address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("10.1.2.3")), "Network Tools would send a private IPv4 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("100.64.1.1")), "Network Tools would send a carrier-grade NAT address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("169.254.1.1")), "Network Tools would send a link-local IPv4 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("192.88.99.1")), "Network Tools would send a special-use IPv4 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("192.0.2.1")), "Network Tools would send a documentation IPv4 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("fc00::1")), "Network Tools would send a unique-local IPv6 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("100::1")), "Network Tools would send a non-global IPv6 address to the online provider.");
        Require(!IsPublicNetworkToolAddress(IPAddress.Parse("2001:db8::1")), "Network Tools would send a documentation IPv6 address to the online provider.");
        Require(IsPublicNetworkToolAddress(IPAddress.Parse("8.8.8.8")), "Network Tools did not recognize a public IPv4 address.");
        Require(IsPublicNetworkToolAddress(IPAddress.Parse("2606:4700:4700::1111")), "Network Tools did not recognize a public IPv6 address.");

        EnsureModernTlsCompatibility();
        Require((ServicePointManager.SecurityProtocol & (SecurityProtocolType)3072) != 0, "Network Tools did not enable TLS 1.2 for public metadata lookup.");
        Require(string.Equals(BuildPublicIpLookupUrl("8.8.8.8"), PublicIpLookupProvider + "8.8.8.8", StringComparison.Ordinal), "Public metadata lookup did not preserve a canonical IPv4 path.");
        var ipv6LookupUrl = BuildPublicIpLookupUrl("2a04:4e42:200::81");
        Require(string.Equals(ipv6LookupUrl, PublicIpLookupProvider + "2a04:4e42:200::81", StringComparison.Ordinal), "Public metadata lookup did not preserve a canonical IPv6 path.");
        Require(ipv6LookupUrl.IndexOf("%3A", StringComparison.OrdinalIgnoreCase) < 0, "Public metadata lookup percent-encoded IPv6 colons, which the provider rejects.");

        Require(!ShouldShowNetworkToolPingBlockedNote(new NetworkToolPingResult { Sent = 4, Received = 4 }), "Network Tools showed a blocked-ping warning after every ping succeeded.");
        Require(!ShouldShowNetworkToolPingBlockedNote(new NetworkToolPingResult { Sent = 4, Received = 3 }), "Network Tools showed a blocked-ping warning even though the host replied.");
        Require(ShouldShowNetworkToolPingBlockedNote(new NetworkToolPingResult { Sent = 4, Received = 0 }), "Network Tools omitted its ping warning when no replies were received.");

        var delayedDnsFailure = new TaskCompletionSource<bool>();
        var lateFailureObserver = ObserveLateTaskFailure(delayedDnsFailure.Task);
        delayedDnsFailure.SetException(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
        Require(lateFailureObserver.Wait(TimeSpan.FromSeconds(1)) && lateFailureObserver.Status == TaskStatus.RanToCompletion, "Timed-out DNS failures were left unobserved for the task finalizer.");

        var reverseDnsResult = new NetworkToolResult { NormalizedTarget = "8.8.8.8" };
        var reverseDnsAddress = new NetworkToolAddressResult { Address = IPAddress.Parse("8.8.8.8"), Scope = "Public", ReverseDns = "dns.google" };
        reverseDnsAddress.ReverseDnsAddresses.Add(IPAddress.Parse("8.8.8.8"));
        reverseDnsAddress.ReverseDnsAddresses.Add(IPAddress.Parse("2001:4860:4860::8888"));
        reverseDnsResult.Addresses.Add(reverseDnsAddress);
        using (var reverseDnsTree = new TreeView())
        {
            PopulateNetworkToolsTree(reverseDnsTree, reverseDnsResult);
            var resolvedNode = reverseDnsTree.Nodes.Cast<TreeNode>().FirstOrDefault(node => node.Text.StartsWith("Resolved address", StringComparison.OrdinalIgnoreCase));
            var relatedNode = resolvedNode == null ? null : resolvedNode.Nodes.Cast<TreeNode>().FirstOrDefault(node => node.Text.StartsWith("Addresses for reverse DNS name: dns.google", StringComparison.OrdinalIgnoreCase));
            Require(relatedNode != null, "Network Tools did not label reverse-DNS host addresses separately.");
            Require(relatedNode.Nodes.Cast<TreeNode>().Any(node => string.Equals(node.Text, "IPv6: 2001:4860:4860::8888", StringComparison.OrdinalIgnoreCase)), "Network Tools did not render an IPv6 address found through reverse DNS.");
        }

        var multiplePublicResult = new NetworkToolResult { NormalizedTarget = "example.test" };
        multiplePublicResult.Addresses.Add(new NetworkToolAddressResult { Address = IPAddress.Parse("8.8.8.8"), Scope = "Public" });
        multiplePublicResult.Addresses.Add(new NetworkToolAddressResult { Address = IPAddress.Parse("1.1.1.1"), Scope = "Public" });
        multiplePublicResult.PublicResults.Add(new NetworkToolPublicResult { Address = "8.8.8.8", Info = new InternetIpInfo { Success = true, Country = "First" } });
        multiplePublicResult.PublicResults.Add(new NetworkToolPublicResult { Address = "1.1.1.1", Info = new InternetIpInfo { Success = true, Country = "Second" } });
        using (var multiplePublicTree = new TreeView())
        {
            PopulateNetworkToolsTree(multiplePublicTree, multiplePublicResult);
            var resolvedNodes = multiplePublicTree.Nodes.Cast<TreeNode>().Where(node => node.Text.StartsWith("Resolved address", StringComparison.OrdinalIgnoreCase)).ToList();
            Require(resolvedNodes.Count == 2, "Network Tools did not render every resolved public address.");
            Require(resolvedNodes.All(node => node.Nodes.Cast<TreeNode>().Any(child => string.Equals(child.Text, "Public network information", StringComparison.OrdinalIgnoreCase))), "Network Tools did not nest public metadata beneath each matching IP address.");
            Require(!multiplePublicTree.Nodes.Cast<TreeNode>().Any(node => string.Equals(node.Text, "Public network information", StringComparison.OrdinalIgnoreCase)), "Network Tools retained a duplicate top-level public metadata tree.");
        }

        var privateResult = new NetworkToolResult { NormalizedTarget = "router.test" };
        privateResult.Addresses.Add(new NetworkToolAddressResult { Address = IPAddress.Parse("192.168.1.1"), Scope = "Private" });
        using (var privateTree = new TreeView())
        {
            PopulateNetworkToolsTree(privateTree, privateResult);
            var publicNode = privateTree.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(node.Text, "Public network information", StringComparison.OrdinalIgnoreCase));
            var statusText = publicNode == null || publicNode.Nodes.Count == 0 ? "" : publicNode.Nodes[0].Text;
            Require(statusText.IndexOf("skipped", StringComparison.OrdinalIgnoreCase) >= 0, "Network Tools did not explain that private-address online lookup was deliberately skipped.");
            Require(statusText.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0, "Network Tools described a deliberately skipped private-address lookup as a failed search.");
        }

        Require(networkToolsMenuItem != null && networkToolsMenuItem.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.T), "Network Tools lost its Control Shift T shortcut.");
        Require(networkToolsMenuItem.Text.IndexOf("Ctrl+Shift+T", StringComparison.OrdinalIgnoreCase) >= 0, "Network Tools Options item does not expose Control Shift T.");
    }

    private void SelfTestUsbSuperSpeedPlusSpeedDecoding()
    {
        const uint gen1Raw = (5u << 16) | (3u << 4);
        const uint gen2Raw = (10u << 16) | (3u << 4);

        Require(Math.Abs(DecodeSuperSpeedPlusBitsPerSecond(gen1Raw) - 5000000000.0) < 1.0, "USB Gen 1 SuperSpeedPlus speed did not decode to 5 Gbps.");
        Require(Math.Abs(DecodeSuperSpeedPlusBitsPerSecond(gen2Raw) - 10000000000.0) < 1.0, "USB Gen 2 SuperSpeedPlus speed did not decode to 10 Gbps.");
        Require(DecodeSuperSpeedPlusBitsPerSecond(0x000040b5) == 0, "Malformed SuperSpeedPlus speed must not be treated as 181000 bps.");
    }

    private void SelfTestBluetoothAndBatteryFiltering()
    {
        Require(IsBluetoothPnpDeviceCandidate("Logitech Pebble K380s", @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_AABBCCDDEEFF", "HIDClass"), "Bluetooth PnP fallback rejected a Bluetooth LE HID keyboard.");
        Require(IsBluetoothPnpDeviceCandidate("JBL Live 670NC", @"BTHENUM\DEV_AABBCCDDEEFF\7&123&0&BLUETOOTHDEVICE_AABBCCDDEEFF", "Bluetooth"), "Bluetooth PnP fallback rejected a Bluetooth audio device.");
        Require(!IsBluetoothPnpDeviceCandidate("USB Input Device", @"USB\VID_046D&PID_C548", "HIDClass"), "Bluetooth PnP fallback accepted a non-Bluetooth HID device.");
        Require(IsGenericBluetoothPnpName("Microsoft Bluetooth LE Enumerator"), "Bluetooth PnP fallback did not reject a generic enumerator.");
        Require(IsGenericBluetoothPnpName("Bluetooth LE Generic Attribute Service"), "Bluetooth PnP fallback did not reject a generic GATT service.");
        Require(IsGenericBluetoothPnpName("Object Push Service"), "Bluetooth PnP fallback did not reject a generic object push service.");
        Require(IsGenericBluetoothPnpName("Generic Access Profile"), "Bluetooth PnP fallback did not reject a generic access profile.");
        Require(!IsGenericBluetoothPnpName("Onj's iPhone 17 Pro"), "Bluetooth PnP fallback rejected a named phone device.");
        Require(FormatRecentElapsedAge(DateTime.Now.AddHours(-2), DateTime.Now).IndexOf("hour", StringComparison.OrdinalIgnoreCase) >= 0, "Bluetooth relative time did not include hours.");
        string childName;
        Require(TryBluetoothChildDeviceName("Onj's iPhone 17 Pro A2DP SNK", "Onj's iPhone 17 Pro", out childName) && childName == "A2DP SNK", "Bluetooth child device name was not folded under its parent.");
        Require(!TryBluetoothChildDeviceName("JBL Live 670NC", "Onj's iPhone 17 Pro", out childName), "Bluetooth child device name matched an unrelated parent.");
        string address;
        Require(TryExtractBluetoothAddressFromPnpId(@"BTHENUM\DEV_AABBCCDDEEFF", out address) && address == "AA:BB:CC:DD:EE:FF", "Bluetooth PnP fallback did not extract a Bluetooth address.");
        Require(TryExtractBluetoothAddressFromPnpId(@"BTHENUM\{0000110A-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&791E\8&310FFAE7&0&68EFDCDA9598_C00000000", out address) && address == "68:EF:DC:DA:95:98", "Bluetooth PnP fallback did not extract a trailing child-device Bluetooth address.");
        var parentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "68:EF:DC:DA:95:98", "Onj's iPhone 17 Pro" } };
        string parentName;
        Require(TryResolveBluetoothParentName("Onj's iPhone 17 Pro Hands-Free HF Audio", "", parentMap, out parentName) && parentName == "Onj's iPhone 17 Pro", "Bluetooth child device name did not resolve by parent-name prefix.");
        Require(MacVendorDatabase.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")).Lookup("34:6F:24:6A:E5:58").IndexOf("AzureWave", StringComparison.OrdinalIgnoreCase) >= 0, "OUI lookup did not identify the local Bluetooth adapter prefix.");
        Require(!IsUsefulWindowsPowerMeterReading(null), "Battery filtering accepted a power meter without unit metadata.");
        Require(!IsUsefulWindowsPowerMeterReading(0), "Battery filtering accepted a non-watt power meter.");
        Require(IsUsefulWindowsPowerMeterReading(7), "Battery filtering rejected a watt-based power meter.");
        Require(IsPlausibleWmiBatteryRuntimeMinutes(123), "A plausible WMI battery runtime was rejected.");
        Require(!IsPlausibleWmiBatteryRuntimeMinutes(71582788), "The Windows unknown-runtime sentinel was accepted as a battery runtime.");
        var runtimeDetails = BuildBatteryDetails(null, new WmiBatteryInfo { EstimatedRunTimeMinutes = 71582788 }, 0);
        Require(!runtimeDetails.ContainsKey("WMI estimated runtime"), "The Windows unknown-runtime sentinel leaked into battery details.");
        var liveBattery = new NativeBatteryInfo { CurrentCapacity = 9600, FullChargeCapacity = 48000 };
        var staleWmiBattery = new WmiBatteryInfo { EstimatedChargeRemaining = 2 };
        Require(Math.Abs(GetBatteryPercent(liveBattery, staleWmiBattery).GetValueOrDefault() - 20.0) < 0.01, "Cached WMI charge percentage overrode live native battery capacity.");
    }

    private void SelfTestCategoryNavigation()
    {
        EnsureSelfTestRows();

        settings.CategoryOrderKeys = new List<string>
        {
            "type|Devices",
            "type|Performance",
            "type|Temperature",
            "type|Fan",
            "type|SMART",
            "type|Network",
            "type|Bluetooth",
            "type|USB",
            "type|Audio",
            "type|Display",
            "type|Battery"
        };
        settings.HiddenCategoryKeys = new List<string>();
        UpdateDeviceList();
        Require(deviceList.Items.Count > 0, "Category list is empty.");
        var firstFilter = deviceList.Items[0] as DeviceFilter;
        Require(firstFilter != null && string.Equals(firstFilter.Key, "type|Devices", StringComparison.OrdinalIgnoreCase), "Custom category order was not applied.");
        Require(SelectCategoryByShortcut(Keys.D0), "Ctrl+0 category shortcut did not select the first category.");
        Require(deviceList.SelectedIndex == 0, "Ctrl+0 did not select category index 0.");
        Require(SelectCategoryByShortcut(Keys.D0, 10), "Ctrl+Shift+0 category shortcut did not select overflow category.");
        Require(deviceList.SelectedIndex == 10, "Ctrl+Shift+0 did not select category index 10.");
        Require(SelectCategoryByKey("type|Performance"), "Could not select Performance category before move test.");
        MoveSelectedCategory(1);
        Require(deviceList.SelectedIndex == 2, "Ctrl+Down-style category move did not keep moved category selected.");
        var movedFilter = deviceList.SelectedItem as DeviceFilter;
        Require(movedFilter != null && string.Equals(movedFilter.Key, "type|Performance", StringComparison.OrdinalIgnoreCase), "Moved category selection did not stay on Performance.");
        Require(settings.CategoryOrderKeys.Count > 2 && string.Equals(settings.CategoryOrderKeys[2], "type|Performance", StringComparison.OrdinalIgnoreCase), "Moved category order was not saved.");
        Require(string.Equals(RelativeMoveText("Battery", "Fans", -1), "Battery moved above Fans.", StringComparison.Ordinal), "Upward relative move feedback was incorrect.");
        Require(string.Equals(RelativeMoveText("Fans", "Temperatures", 1), "Fans moved below Temperatures.", StringComparison.Ordinal), "Downward relative move feedback was incorrect.");
        Require(string.Equals(RelativeMoveSpeechText("Battery", "Fans", -1, CategorySpeechBrief), "Battery above Fans.", StringComparison.Ordinal), "Brief upward move feedback was incorrect.");
        Require(string.Equals(RelativeMoveSpeechText("Fans", "Temperatures", 1, CategorySpeechBrief), "Fans below Temperatures.", StringComparison.Ordinal), "Brief downward move feedback was incorrect.");
        Require(string.IsNullOrEmpty(RelativeMoveSpeechText("Battery", "Fans", -1, CategorySpeechOff)), "Off category speech mode still produced move feedback.");
        settings.HiddenCategoryKeys = new List<string> { "type|Network" };
        UpdateDeviceList();
        Require(!deviceList.Items.Cast<object>().OfType<DeviceFilter>().Any(f => string.Equals(f.Key, "type|Network", StringComparison.OrdinalIgnoreCase)), "Hidden category was still visible.");
        for (var i = 0; i < deviceList.Items.Count; i++)
        {
            deviceList.SelectedIndex = i;
            UpdateReadingList();
            var filter = deviceList.Items[i] as DeviceFilter;
            var type = filter == null ? "" : filter.Type ?? "";
            if (latestRows.Any(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)))
            {
                Require(readingTree.Nodes.Count > 0 && !string.Equals(readingTree.Nodes[0].Name, "empty", StringComparison.Ordinal), "Reading tree empty for populated category " + deviceList.Items[i] + ".");
            }
        }

        var originalRows = latestRows.ToList();
        try
        {
            SetLatestRows(latestRows.Where(r => !string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase)).ToList());
            settings.HiddenCategoryKeys = new List<string>();
            lastReadingTreeFilterKey = "";
            lastReadingTreeSignature = "";
            lastReadingTreeShapeSignature = "";
            readingTreeExpansionInitialized = false;
            UpdateDeviceList();
            Require(deviceList.Items.Cast<object>().OfType<DeviceFilter>().Any(f => string.Equals(f.Key, "type|Battery", StringComparison.OrdinalIgnoreCase)), "Battery category was hidden when it had no readings.");
            Require(SelectCategoryByKey("type|Battery"), "Could not select empty Battery category.");
            UpdateReadingList();
            var emptyStateDebug = "Count=" + readingTree.Nodes.Count + "; " + string.Join("; ", readingTree.Nodes.Cast<TreeNode>().Select(n => "Name=" + n.Name + ", Text=" + n.Text).ToArray());
            Require(readingTree.Nodes.Count == 1 && string.Equals(readingTree.Nodes[0].Name, "empty", StringComparison.Ordinal), "Empty category did not show an empty-state row. " + emptyStateDebug);
            Require(readingTree.Nodes[0].Text.IndexOf("No data currently available for this category.", StringComparison.OrdinalIgnoreCase) >= 0, "Empty category did not explain that no data is currently available.");
            Require(readingTree.Nodes[0].Text.IndexOf("hide this category", StringComparison.OrdinalIgnoreCase) >= 0, "Empty category did not explain that the category can be hidden.");
        }
        finally
        {
            SetLatestRows(originalRows);
        }

        settings.CategoryOrderKeys = new List<string>();
        settings.HiddenCategoryKeys = new List<string>();
    }

    private void SelfTestPerformanceGroupBoundaries()
    {
        var disk = new SensorRow
        {
            Type = "Performance",
            Hardware = "C: Test disk",
            Name = "Read rate",
            Identifier = "logicaldisk/C:/read",
            Source = "Windows Logical Disk",
            DisplayValue = "1.0 MB/s"
        };
        var thermal = new SensorRow
        {
            Type = "Performance",
            Hardware = "Thermal",
            Name = "System thermal state",
            Identifier = "thermal-system-state",
            Source = "Lenovo Laptop Support Plug-In",
            DisplayValue = "Safe"
        };
        var powerSupply = new SensorRow
        {
            Type = "Performance",
            Hardware = "Test PSU",
            Name = "PSU output power",
            Identifier = "test-psu/output-power",
            Source = "Test PSU Plug-In",
            DisplayValue = "100 W"
        };
        var parent = new ReadingTreeItem { Key = "type|Performance", Text = "Performance" };
        AddPerformanceGroups(parent, new[] { disk, thermal, powerSupply });

        var storage = parent.Children.FirstOrDefault(item => string.Equals(item.Key, "performance|storage", StringComparison.Ordinal));
        var other = parent.Children.FirstOrDefault(item => string.Equals(item.Key, "performance|other", StringComparison.Ordinal));
        Require(storage != null, "Performance grouping omitted the Storage branch.");
        Require(other != null, "Performance grouping omitted the Other branch for non-storage readings.");
        Require(ReadingTreeContainsRow(storage, disk), "A logical-disk row was not grouped under Storage.");
        Require(!ReadingTreeContainsRow(storage, thermal), "A thermal row leaked into Storage.");
        Require(!ReadingTreeContainsRow(storage, powerSupply), "A power-supply row leaked into Storage.");
        Require(ReadingTreeContainsRow(other, thermal), "A thermal row was not retained under Other.");
        Require(ReadingTreeContainsRow(other, powerSupply), "A power-supply row was not retained under Other.");
    }

    private static bool ReadingTreeContainsRow(ReadingTreeItem item, SensorRow row)
    {
        return item != null && (ReferenceEquals(item.Row, row) || item.Children.Any(child => ReadingTreeContainsRow(child, row)));
    }

    private void SelfTestCategorySpeechModes()
    {
        var full = BuildCategorySelectionSpeechText(CategorySpeechFull, "Devices", "Ctrl+7", "Devices category selected. Shortcut Ctrl+7.");
        Require(string.Equals(full, "Devices category selected. Shortcut Ctrl+7.", StringComparison.Ordinal), "Full category speech did not use the full localized message.");

        var brief = BuildCategorySelectionSpeechText(CategorySpeechBrief, "Devices", "Ctrl+7", "Devices category selected. Shortcut Ctrl+7.");
        Require(string.Equals(brief, "Devices Ctrl+7", StringComparison.Ordinal), "Brief category speech did not use the compact category and shortcut form.");

        var off = BuildCategorySelectionSpeechText(CategorySpeechOff, "Devices", "Ctrl+7", "Devices category selected. Shortcut Ctrl+7.");
        Require(string.IsNullOrEmpty(off), "Off category speech still produced speech text.");

        Require(string.Equals(NormalizeCategorySpeechMode("Brief"), CategorySpeechBrief, StringComparison.Ordinal), "Brief category speech mode did not normalize.");
        Require(string.Equals(NormalizeCategorySpeechMode("Off"), CategorySpeechOff, StringComparison.Ordinal), "Off category speech mode did not normalize.");
        Require(string.Equals(NormalizeCategorySpeechMode("unexpected"), CategorySpeechFull, StringComparison.Ordinal), "Unknown category speech mode did not fall back to Full.");
        Require(string.Equals(NormalizeVisualSpokenFeedbackPlacement("Center"), "Center", StringComparison.Ordinal), "Visual spoken feedback placement did not normalize Center.");
        Require(string.Equals(NormalizeVisualSpokenFeedbackPlacement("unexpected"), "BottomRight", StringComparison.Ordinal), "Unknown visual spoken feedback placement did not fall back to BottomRight.");
        Require(NormalizeVisualSpokenFeedbackTimeoutSeconds(0) == 6, "Zero visual spoken feedback timeout did not fall back to 6 seconds.");
        Require(NormalizeVisualSpokenFeedbackTimeoutSeconds(99) == 30, "Visual spoken feedback timeout did not clamp to 30 seconds.");
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

    private void SelfTestReadingTreeExpansionPreference()
    {
        EnsureSelfTestRows();
        settings.ReadingTreeExpansionMode = ReadingTreeExpansionCollapsed;
        settings.ReadingTreeLastExpanded = true;
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) == 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Collapsed reading tree preference expanded nodes.");

        settings.ReadingTreeExpansionMode = ReadingTreeExpansionExpanded;
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) > 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Expanded reading tree preference did not expand nodes.");

        settings.ReadingTreeExpansionMode = ReadingTreeExpansionRemember;
        settings.ReadingTreeLastExpanded = false;
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) == 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Remember reading tree preference ignored collapsed state.");

        settings.ReadingTreeLastExpanded = true;
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) > 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Remember reading tree preference ignored expanded state.");

        settings.ReadingTreeExpansionMode = ReadingTreeExpansionRemember;
        settings.ReadingTreeLastExpanded = false;
        SaveSettings(settings);
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        ExpandAllReadings();
        Require(settings.ReadingTreeLastExpanded, "Expand all did not update remembered expanded state.");
        var reloadedAfterExpand = LoadSettings();
        Require(reloadedAfterExpand.ReadingTreeLastExpanded, "Expand all did not persist remembered expanded state.");
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) > 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Remember reading tree preference did not restore explicit expand all choice.");

        CollapseAllReadings();
        Require(!settings.ReadingTreeLastExpanded, "Collapse all did not update remembered collapsed state.");
        var reloadedAfterCollapse = LoadSettings();
        Require(!reloadedAfterCollapse.ReadingTreeLastExpanded, "Collapse all did not persist remembered collapsed state.");
        ResetReadingTreeExpansionForSelfTest();
        SelectCategoryByKey("type|Performance");
        UpdateReadingList();
        Require(CountExpandedNodes(readingTree.Nodes) == 0 || CountTreeNodes(readingTree.Nodes) <= 1, "Remember reading tree preference did not restore explicit collapse all choice.");
    }

    private void SelfTestTrayStatusText()
    {
        EnsureSelfTestRows();
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Memory total", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Memory total is not selectable for notification area/spoken hotkeys.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Paging file total", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Paging file total is not selectable for notification area/spoken hotkeys.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Physical + virtual memory total", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Physical + virtual memory total is not selectable for notification area/spoken hotkeys.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Connected disks total space", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Connected disks total space is not selectable for notification area/spoken hotkeys.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Total space", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Total space is not selectable for notification area/spoken hotkeys.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Performance", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Used space", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Used space is not selectable for notification area/spoken hotkeys.");
        Require(DefaultCategoryChoices().Any(c => string.Equals(c.Type, "Tasks", StringComparison.OrdinalIgnoreCase)), "Tasks category is missing from default categories.");
        Require(DefaultCategoryChoices().Any(c => string.Equals(c.Type, "Spoken Hotkeys", StringComparison.OrdinalIgnoreCase)), "Spoken Hotkeys category is missing from default categories.");
        Require(latestRows.Any(r => string.Equals(r.Type, "Tasks", StringComparison.OrdinalIgnoreCase) && string.Equals(CleanSensorName(r.Name), "Highest memory process", StringComparison.OrdinalIgnoreCase) && IsSelectableReadoutRow(r)), "Highest memory process is not selectable for notification area/spoken hotkeys.");
        foreach (var taskRow in new[] { "Highest CPU process", "Highest memory process", "Highest GPU process", "Highest GPU memory process" })
        {
            Require(IsSelectableReadoutRow(new SensorRow { Type = "Tasks", Hardware = "Processes", Name = taskRow, DisplayValue = "Self-test" }), taskRow + " is not selectable for notification area/spoken hotkeys.");
        }
        foreach (var taskRow in latestRows.Where(r => string.Equals(r.Type, "Tasks", StringComparison.OrdinalIgnoreCase)))
        {
            Require((taskRow.DisplayValue ?? "").IndexOf("PID ", StringComparison.OrdinalIgnoreCase) < 0, CleanSensorName(taskRow.Name) + " display value includes PID text.");
        }

        var publicIpRows = new[]
        {
            "Public IP lookup",
            "Public IP summary",
            "Public IP address",
            "IP country",
            "IP region",
            "IP city",
            "IP postal code",
            "IP coordinates",
            "Internet provider",
            "IP organization",
            "Autonomous system",
            "Connection type"
        };
        foreach (var publicIpRow in publicIpRows)
        {
            var row = new SensorRow { Type = "Network", Hardware = "Internet connection", Name = publicIpRow, DisplayValue = "Self-test" };
            Require(IsSelectableReadoutRow(row), publicIpRow + " is not selectable for notification area/spoken hotkeys.");
        }

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

        var previousTrayKeys = settings.TrayItemKeys == null ? new List<string>() : new List<string>(settings.TrayItemKeys);
        var previousSkipUnavailable = settings.TraySpeechSkipsUnavailableReadings;
        var inactiveHardware = "Self-test cellular";
        var inactiveStatus = new SensorRow { Type = "Network", Hardware = inactiveHardware, Name = "Status", Identifier = "self-test-cellular-status", DisplayValue = "Down", Source = "Self-test" };
        var inactiveRate = new SensorRow { Type = "Network", Hardware = inactiveHardware, Name = "Receive rate", Identifier = "self-test-cellular-rx", DisplayValue = "42 KB/s", Source = "Self-test" };
        latestRows.Add(inactiveStatus);
        latestRows.Add(inactiveRate);
        latestRowsBySettingsKey[RowSettingsKey(inactiveStatus)] = inactiveStatus;
        latestRowsBySettingsKey[RowSettingsKey(inactiveRate)] = inactiveRate;
        settings.TrayItemKeys = new List<string> { RowSettingsKey(inactiveRate) };
        settings.TraySpeechSkipsUnavailableReadings = false;
        Require(BuildCurrentSpeechStatusText().IndexOf("42", StringComparison.OrdinalIgnoreCase) >= 0, "Inactive row was skipped when notification-area skipping was disabled.");
        settings.TraySpeechSkipsUnavailableReadings = true;
        Require(string.Equals(BuildCurrentSpeechStatusText(), T("speech.noActiveReadings", "No active readings to announce."), StringComparison.Ordinal), "Inactive row was not skipped when notification-area skipping was enabled.");

        var profile = new SpokenHotKeySetting
        {
            Name = "Self-test conditional announcements",
            HotKey = "Ctrl+Alt+F8",
            SkipUnavailableReadings = true,
            ReadingKeys = new List<string> { RowSettingsKey(inactiveRate) }
        };
        Require(string.Equals(BuildSpeechStatusText(GetSpokenHotKeyRows(profile), profile.SkipUnavailableReadings), T("speech.noActiveReadings", "No active readings to announce."), StringComparison.Ordinal), "Inactive row was not skipped for spoken hotkey profile.");
        settings.TrayItemKeys = previousTrayKeys;
        settings.TraySpeechSkipsUnavailableReadings = previousSkipUnavailable;
    }

    private void SelfTestVisualStatusBadgesAndMeters()
    {
        var previousAutoRefresh = settings.AutoRefreshEnabled;
        try
        {
            settings.AutoRefreshEnabled = true;
            var normalMemory = new SensorRow { Type = "Performance", Hardware = "Memory", Name = "Memory used", Value = 52f, DisplayValue = "16.6 GB (52%)" };
            var hotTemperature = new SensorRow { Type = "Temperature", Hardware = "CPU", Name = "CPU package", Value = 86f, DisplayValue = "86 C" };
            var warmTemperature = new SensorRow { Type = "Temperature", Hardware = "CPU", Name = "CPU package", Value = 72f, DisplayValue = "72 C" };
            var disconnected = new SensorRow { Type = "Network", Hardware = "Wi-Fi", Name = "Status", DisplayValue = "Disconnected" };
            var lowBattery = new SensorRow { Type = "Battery", Hardware = "Battery", Name = "Charge level", Value = 8f, DisplayValue = "8%" };

            Require(VisualSignalForRow(hotTemperature) == ReadingVisualSignal.Critical, "Hot temperature did not produce a critical visual state.");
            Require(VisualSignalForRow(warmTemperature) == ReadingVisualSignal.Caution, "Warm temperature did not produce a caution visual state.");
            Require(VisualSignalForRow(disconnected) == ReadingVisualSignal.Offline, "Disconnected status did not produce an offline visual state.");
            Require(VisualSignalForRow(lowBattery) == ReadingVisualSignal.Critical, "Low battery did not produce a critical visual state.");
            Require(MeterVisualState(normalMemory, 96f) == MeterProgressBar.ErrorState, "High memory meter did not produce an error visual state.");
            Require(MeterVisualState(lowBattery, 20f) == MeterProgressBar.WarningState, "Low battery meter did not produce a warning visual state.");

            var visual = BuildTrayBadgeVisual(new List<SensorRow> { normalMemory, hotTemperature });
            Require(visual.Signal == ReadingVisualSignal.Critical && ReferenceEquals(visual.Row, hotTemperature), "Tray badge did not prioritize the critical configured reading.");
            using (var icon = CreateTrayIcon(visual))
            {
                Require(icon != null && icon.Width > 0 && icon.Height > 0, "Critical tray badge icon was not created.");
            }

            settings.AutoRefreshEnabled = false;
            var paused = BuildTrayBadgeVisual(new List<SensorRow> { normalMemory });
            Require(paused.Signal == ReadingVisualSignal.Paused && string.Equals(paused.Text, "||", StringComparison.Ordinal), "Paused tray badge was not selected.");
            using (var icon = CreateTrayIcon(paused))
            {
                Require(icon != null, "Paused tray badge icon was not created.");
            }

            using (var icon = LoadApplicationIcon())
            {
                Require(icon != null && icon.Width >= 16 && icon.Height >= 16, "Application icon was not available.");
            }
        }
        finally
        {
            settings.AutoRefreshEnabled = previousAutoRefresh;
        }
    }

    private void SelfTestByteUnitFormattingModes()
    {
        var previousMemoryUnitMode = activeMemoryUnitMode;
        var previousStorageUnitMode = activeStorageUnitMode;
        var previousTransferUnitMode = activeTransferUnitMode;
        try
        {
            activeMemoryUnitMode = ByteUnitClassic;
            activeStorageUnitMode = ByteUnitClassic;
            activeTransferUnitMode = ByteUnitClassic;
            Require(string.Equals(FormatBytes(1024.0 * 1024.0), "1.0 MB", StringComparison.Ordinal), "Classic memory formatting changed unexpectedly.");
            Require(string.Equals(FormatStorageBytes(1000000000000.0), "931.3 GB", StringComparison.Ordinal), "Classic storage formatting should use 1024 scale with GB labels.");
            Require(string.Equals(FormatBytesPerSecond(1000000.0), "976.6 KB/s", StringComparison.Ordinal), "Classic transfer formatting should use 1024 scale with KB/s labels.");

            activeMemoryUnitMode = ByteUnitBinary;
            activeStorageUnitMode = ByteUnitBinary;
            activeTransferUnitMode = ByteUnitBinary;
            Require(string.Equals(FormatBytes(1024.0 * 1024.0), "1.0 MiB", StringComparison.Ordinal), "Binary memory formatting should use IEC labels.");
            Require(string.Equals(FormatStorageBytes(1000000000000.0), "931.3 GiB", StringComparison.Ordinal), "Binary storage formatting should use 1024 scale with GiB labels.");
            Require(string.Equals(FormatBytesPerSecond(1000000.0), "976.6 KiB/s", StringComparison.Ordinal), "Binary transfer formatting should use IEC per-second labels.");

            activeMemoryUnitMode = ByteUnitDecimal;
            activeStorageUnitMode = ByteUnitDecimal;
            activeTransferUnitMode = ByteUnitDecimal;
            Require(string.Equals(FormatBytes(1000000.0), "1.0 MB", StringComparison.Ordinal), "Decimal memory formatting should use 1000 scale.");
            Require(string.Equals(FormatStorageBytes(1000000000000.0), "1.0 TB", StringComparison.Ordinal), "Decimal storage formatting should use 1000 scale.");
            Require(string.Equals(FormatBytesPerSecond(1000000.0), "1.0 MB/s", StringComparison.Ordinal), "Decimal transfer formatting should use 1000 scale per second.");
        }
        finally
        {
            activeMemoryUnitMode = previousMemoryUnitMode;
            activeStorageUnitMode = previousStorageUnitMode;
            activeTransferUnitMode = previousTransferUnitMode;
        }
    }

    private void SelfTestPendingRefreshCoalescing()
    {
        refreshInProgress = true;
        pendingRefreshRequested = false;
        pendingRefreshUpdateInteractiveUi = false;
        pendingRefreshSlowRows = false;
        pendingRefreshReason = "";
        QueuePendingRefresh(false, false, "auto");
        QueuePendingRefresh(true, true, "unit preferences");
        Require(pendingRefreshRequested, "Pending refresh was not queued.");
        Require(pendingRefreshUpdateInteractiveUi, "Pending refresh did not preserve interactive UI request.");
        Require(pendingRefreshSlowRows, "Pending refresh did not preserve slow-row request.");
        Require(pendingRefreshReason.IndexOf("auto", StringComparison.OrdinalIgnoreCase) >= 0, "Pending refresh lost first reason.");
        Require(pendingRefreshReason.IndexOf("unit preferences", StringComparison.OrdinalIgnoreCase) >= 0, "Pending refresh lost second reason.");
        refreshInProgress = false;
        pendingRefreshRequested = false;
        pendingRefreshUpdateInteractiveUi = false;
        pendingRefreshSlowRows = false;
        pendingRefreshReason = "";
    }

    private void SelfTestBackgroundHotKeyRefreshCadence()
    {
        var previousSpeakTrayHotKey = settings.SpeakTrayHotKey;
        var previousTrayItemKeys = settings.TrayItemKeys;
        var previousSpokenHotKeys = settings.SpokenHotKeys;
        var previousTrendLogging = settings.TrendLoggingEnabled;
        var previousAlarms = settings.Alarms;
        var previousFanCurves = settings.FanCurves;
        var previousRemoteConnections = settings.RemoteConnections;
        Dictionary<string, RemotePublishState> previousRemotePublishStates;
        lock (remotePublishStatesLock)
        {
            previousRemotePublishStates = remotePublishStates.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            remotePublishStates.Clear();
        }
        try
        {
            settings.SpeakTrayHotKey = "";
            settings.TrayItemKeys = new List<string>();
            settings.SpokenHotKeys = new List<SpokenHotKeySetting>();
            settings.TrendLoggingEnabled = false;
            settings.Alarms = new List<AlarmSetting>();
            settings.FanCurves = new List<FanCurveSetting>();
            settings.RemoteConnections = new List<RemoteConnectionSetting>();
            Require(!RequiresRealtimeBackgroundRefresh(), "Empty background configuration should not force realtime refresh.");

            const string cadenceConnectionId = "selftestremotecadence000000000001";
            settings.RemoteConnections = new List<RemoteConnectionSetting>
            {
                new RemoteConnectionSetting
                {
                    Id = cadenceConnectionId,
                    Name = "Self-test remote",
                    ServerUrl = "http://127.0.0.1:9137/",
                    ProtectedAccessToken = "self-test-access-token",
                    ProtectedPassword = "self-test-monitoring-password",
                    Enabled = true,
                    PublishThisComputer = true,
                    PollIntervalSeconds = 5
                }
            };
            Require(RequiresRealtimeBackgroundRefresh(), "An initial remote publication should request a hidden refresh.");
            lock (remotePublishStatesLock)
            {
                var state = new RemotePublishState();
                remotePublishStates[cadenceConnectionId] = state;
                Require(RemoteMonitoringEngine.TryBeginPublish(settings.RemoteConnections[0], state, DateTime.UtcNow), "The remote cadence fixture could not begin its first publication.");
            }
            Require(!RequiresRealtimeBackgroundRefresh(), "Remote publishing forced fast hidden refresh before its own interval was due.");
            lock (remotePublishStatesLock)
            {
                remotePublishStates[cadenceConnectionId].LastAttemptUtc = DateTime.UtcNow.AddSeconds(-6);
            }
            Require(RequiresRealtimeBackgroundRefresh(), "Remote publishing did not request fresh rows when its own interval became due.");
            settings.RemoteConnections = new List<RemoteConnectionSetting>();

            settings.SpeakTrayHotKey = "Ctrl+Shift+F11";
            settings.TrayItemKeys = new List<string> { "self-test-reading" };
            Require(RequiresRealtimeBackgroundRefresh(), "Tray hotkey readings should keep hidden refresh at the user interval.");

            settings.SpeakTrayHotKey = "";
            settings.TrayItemKeys = new List<string>();
            settings.SpokenHotKeys = new List<SpokenHotKeySetting>
            {
                new SpokenHotKeySetting
                {
                    Name = "Self-test",
                    HotKey = "Ctrl+Shift+F1",
                    ReadingKeys = new List<string> { "self-test-reading" }
                }
            };
            Require(RequiresRealtimeBackgroundRefresh(), "Spoken hotkey readings should keep hidden refresh at the user interval.");

            const string oldPowerKey = "Battery|Test battery|Power rate|battery/0/power-rate";
            const string newPowerKey = "Battery|Test battery|Battery charge/discharge power|battery/0/power-rate";
            const string lenovoDischargeKey = "Battery|ACPI battery|Discharge rate|acpi-battery-test-discharge-rate-mw";
            const string batteryCapacityKey = "Battery|Test battery|Full charge capacity|battery/0/full-charge-capacity";
            const string lenovoVoltageKey = "Battery|ACPI battery|Voltage|acpi-battery-test-voltage-mv";
            var normalizationSettings = new AppSettings
            {
                TrayItemKeys = new List<string> { oldPowerKey },
                SpokenHotKeys = new List<SpokenHotKeySetting>
                {
                    new SpokenHotKeySetting { Name = "New spoken hotkey", HotKey = "", ReadingKeys = new List<string>() },
                    new SpokenHotKeySetting { Name = "Intentional empty profile", HotKey = "", ReadingKeys = new List<string>() },
                    new SpokenHotKeySetting { Name = "Useful profile", HotKey = "Ctrl+Shift+F1", ReadingKeys = new List<string> { "self-test-reading", lenovoDischargeKey } }
                },
                HiddenReadingKeys = new List<string> { "row|" + batteryCapacityKey },
                TrendLoggingKeys = new List<string> { lenovoVoltageKey },
                ReadingSpeechLabels = new Dictionary<string, string> { { oldPowerKey, "Battery power" } },
                Alarms = new List<AlarmSetting>
                {
                    new AlarmSetting { Name = "Power", ReadingKey = oldPowerKey, Threshold = 20, ThresholdUnit = "value" },
                    new AlarmSetting { Name = "Discharge", ReadingKey = lenovoDischargeKey, Threshold = 11520, ThresholdUnit = "B/s" },
                    new AlarmSetting { Name = "Capacity", ReadingKey = batteryCapacityKey, Threshold = 50540, ThresholdUnit = "value" },
                    new AlarmSetting { Name = "Voltage", ReadingKey = lenovoVoltageKey, Threshold = 16561, ThresholdUnit = "value" }
                }
            };
            NormalizeSettings(normalizationSettings);
            Require(normalizationSettings.SpokenHotKeys.Count == 2, "Settings normalization did not prune the empty default spoken hotkey placeholder.");
            Require(!normalizationSettings.SpokenHotKeys.Any(p => string.Equals(p.Name, "New spoken hotkey", StringComparison.OrdinalIgnoreCase)), "Settings normalization kept an empty default spoken hotkey placeholder.");
            Require(normalizationSettings.TrayItemKeys.Contains(newPowerKey), "Battery power-row settings key was not migrated after its user-facing rename.");
            Require(normalizationSettings.ReadingSpeechLabels.ContainsKey(newPowerKey), "Battery power spoken label was not migrated after its user-facing rename.");
            Require(Math.Abs(normalizationSettings.Alarms[0].Threshold - 20) < 0.001 && normalizationSettings.Alarms[0].ThresholdUnit == "W", "Existing Windows battery power alarm was scaled incorrectly.");
            Require(Math.Abs(normalizationSettings.Alarms[1].Threshold - 11.52) < 0.001 && normalizationSettings.Alarms[1].ThresholdUnit == "W", "Existing Lenovo battery power alarm was not migrated from milliwatts.");
            Require(Math.Abs(normalizationSettings.Alarms[2].Threshold - 50.54) < 0.001 && normalizationSettings.Alarms[2].ThresholdUnit == "Wh", "Existing battery capacity alarm was not migrated from milliwatt-hours.");
            Require(Math.Abs(normalizationSettings.Alarms[3].Threshold - 16.561) < 0.001 && normalizationSettings.Alarms[3].ThresholdUnit == "V", "Existing Lenovo battery voltage alarm was not migrated from millivolts.");
            Require(PreferencesForm.AlarmThresholdUnits(new SensorRow { Type = "Battery", Name = "Discharge rate", DisplayValue = "11.52 W" }).SequenceEqual(new[] { "W" }), "Battery power alarm units were mistaken for transfer-rate units.");
            Require(PreferencesForm.AlarmThresholdUnits(new SensorRow { Type = "Battery", Name = "Full charge capacity", DisplayValue = "50.54 Wh" }).SequenceEqual(new[] { "Wh" }), "Battery capacity alarm units were not watt-hours.");
            Require(PreferencesForm.AlarmThresholdUnits(new SensorRow { Type = "Battery", Name = "Voltage", DisplayValue = "16.56 V" }).SequenceEqual(new[] { "V" }), "Battery voltage alarm units were not volts.");

            var onlineIdleBattery = new NativeBatteryInfo
            {
                PowerState = BatteryPowerState.Discharging | BatteryPowerState.Online,
                RateMilliwatts = 0
            };
            Require(BuildBatteryStatusText(onlineIdleBattery, null) == "AC connected", "An idle AC-connected battery was incorrectly described as discharging.");
        }
        finally
        {
            settings.SpeakTrayHotKey = previousSpeakTrayHotKey;
            settings.TrayItemKeys = previousTrayItemKeys;
            settings.SpokenHotKeys = previousSpokenHotKeys;
            settings.TrendLoggingEnabled = previousTrendLogging;
            settings.Alarms = previousAlarms;
            settings.FanCurves = previousFanCurves;
            settings.RemoteConnections = previousRemoteConnections;
            lock (remotePublishStatesLock)
            {
                remotePublishStates.Clear();
                foreach (var item in previousRemotePublishStates)
                {
                    remotePublishStates[item.Key] = item.Value;
                }
            }
        }
    }

    private void SelfTestFormattedRowCacheClearing()
    {
        lock (slowRowsLock)
        {
            cachedSlowRows = new List<SensorRow>
            {
                new SensorRow { Type = "SMART", Hardware = "Disk", Name = "Size", DisplayValue = "920.4 GiB" }
            };
            cachedSlowRowsUtc = DateTime.UtcNow;
        }

        lock (lhmRowsLock)
        {
            cachedLhmRows = new List<SensorRow>
            {
                new SensorRow { Type = "SMART", Hardware = "Disk", Name = "Total space", DisplayValue = "920.4 GiB" }
            };
            cachedLhmRowsUtc = DateTime.UtcNow;
        }

        ClearFormattedSensorRowCaches();

        lock (slowRowsLock)
        {
            Require(cachedSlowRows.Count == 0, "Slow formatted rows were not cleared.");
            Require(cachedSlowRowsUtc == DateTime.MinValue, "Slow row cache timestamp was not reset.");
        }

        lock (lhmRowsLock)
        {
            Require(cachedLhmRows.Count == 0, "LibreHardwareMonitor formatted rows were not cleared.");
            Require(cachedLhmRowsUtc == DateTime.MinValue, "LibreHardwareMonitor row cache timestamp was not reset.");
        }
    }

    private void SelfTestFragileWmiRowCaches()
    {
        List<SensorRow> previousOemRows;
        DateTime previousOemUtc;
        string previousOemSignature;
        List<SensorRow> previousPowerRows;
        DateTime previousPowerUtc;
        Dictionary<int, WmiBatteryInfo> previousWmiBatteryInfo;
        DateTime previousWmiBatteryInfoUtc;
        List<SensorRow> previousDeviceBatteryRows;
        DateTime previousDeviceBatteryRowsUtc;
        Dictionary<string, CachedDetailSnapshot> previousNetworkWmiDetails;
        List<SensorRow> previousGpuStatusRows;
        DateTime previousGpuStatusUtc;
        lock (oemProviderRowsLock)
        {
            previousOemRows = cachedOemProviderRows.ToList();
            previousOemUtc = cachedOemProviderRowsUtc;
            previousOemSignature = cachedOemProviderRowsSignature;
            cachedOemProviderRows = new List<SensorRow>
            {
                new SensorRow { Type = "Fan", Hardware = "Self-test", Name = "OEM cached row", Identifier = "self-test-oem-cache", DisplayValue = "1 RPM", Source = "Self-test" }
            };
            cachedOemProviderRowsUtc = DateTime.UtcNow;
            cachedOemProviderRowsSignature = GetOemProviderRowsCacheSignature(settings);
        }

        lock (windowsPowerRowsLock)
        {
            previousPowerRows = cachedWindowsPowerRows.ToList();
            previousPowerUtc = windowsPowerRowsLastReadUtc;
            cachedWindowsPowerRows = new List<SensorRow>();
            windowsPowerRowsLastReadUtc = DateTime.UtcNow;
        }

        lock (wmiBatteryInfoLock)
        {
            previousWmiBatteryInfo = CloneWmiBatteryInfo(cachedWmiBatteryInfo);
            previousWmiBatteryInfoUtc = wmiBatteryInfoLastReadUtc;
            cachedWmiBatteryInfo = new Dictionary<int, WmiBatteryInfo>
            {
                {
                    0,
                    new WmiBatteryInfo
                    {
                        EstimatedChargeRemaining = 77,
                        EstimatedRunTimeMinutes = 123,
                        BatteryStatus = 2,
                        Status = "Self-test",
                        RawDetails = new Dictionary<string, string> { { "Self-test", "WMI battery cache" } }
                    }
                }
            };
            wmiBatteryInfoLastReadUtc = DateTime.UtcNow;
        }

        lock (deviceBatteryRowsLock)
        {
            previousDeviceBatteryRows = cachedDeviceBatteryRows.ToList();
            previousDeviceBatteryRowsUtc = deviceBatteryRowsLastReadUtc;
            cachedDeviceBatteryRows = new List<SensorRow>();
            deviceBatteryRowsLastReadUtc = DateTime.UtcNow;
        }

        lock (networkWmiDetailsCacheLock)
        {
            previousNetworkWmiDetails = networkWmiDetailsCache.ToDictionary(
                pair => pair.Key,
                pair => new CachedDetailSnapshot
                {
                    TimestampUtc = pair.Value == null ? DateTime.MinValue : pair.Value.TimestampUtc,
                    Details = pair.Value == null ? new Dictionary<string, string>() : CloneDetails(pair.Value.Details)
                },
                StringComparer.OrdinalIgnoreCase);
            networkWmiDetailsCache.Clear();
            networkWmiDetailsCache["self-test-adapter"] = new CachedDetailSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                Details = new Dictionary<string, string> { { "WMI self-test detail", "Cached" } }
            };
        }

        lock (gpuStatusRowsCacheLock)
        {
            previousGpuStatusRows = cachedGpuStatusRows.ToList();
            previousGpuStatusUtc = cachedGpuStatusRowsUtc;
            cachedGpuStatusRows = new List<SensorRow>();
            cachedGpuStatusRowsUtc = DateTime.UtcNow;
        }

        try
        {
            var oemRows = GetOemProviderRows(false, false).ToList();
            Require(oemRows.Count == 1 && string.Equals(oemRows[0].Identifier, "self-test-oem-cache", StringComparison.OrdinalIgnoreCase), "OEM provider rows did not reuse a fresh cache.");

            lock (oemProviderRowsLock)
            {
                cachedOemProviderRows = new List<SensorRow>
                {
                    new SensorRow { Type = "Fan", Hardware = "Self-test", Name = "Stale OEM cached row", Identifier = "self-test-oem-cache", DisplayValue = "2 RPM", Source = "Self-test" }
                };
                cachedOemProviderRowsUtc = DateTime.UtcNow;
                cachedOemProviderRowsSignature = "stale-self-test-signature";
            }
            oemRows = GetOemProviderRows(false, false).ToList();
            Require(!oemRows.Any(r => string.Equals(r.Identifier, "self-test-oem-cache", StringComparison.OrdinalIgnoreCase)), "OEM provider rows reused a cache for the wrong plug-in state.");

            var wmiBattery = GetWmiBatteryInfo(false);
            Require(wmiBattery.ContainsKey(0) && string.Equals(wmiBattery[0].Status, "Self-test", StringComparison.Ordinal), "WMI battery info did not reuse a fresh cache.");

            var powerRows = GetWindowsPowerMeterRows(false);
            Require(powerRows.Count == 0, "Windows power rows did not reuse an empty fresh cache.");

            var deviceBatteryRows = GetDeviceBatteryRows(false);
            Require(deviceBatteryRows.Count == 0, "Device battery rows did not reuse an empty fresh cache.");

            Dictionary<string, string> networkDetails;
            Require(TryGetCachedNetworkWmiDetails("self-test-adapter", out networkDetails), "Network WMI details did not reuse a fresh cache.");
            Require(networkDetails.ContainsKey("WMI self-test detail"), "Network WMI details cache returned the wrong data.");

            var gpuRows = new List<SensorRow>();
            AddGpuMemoryStatusRows(gpuRows);
            Require(gpuRows.Count == 0, "GPU status rows did not reuse an empty fresh cache.");
        }
        finally
        {
            lock (oemProviderRowsLock)
            {
                cachedOemProviderRows = previousOemRows;
                cachedOemProviderRowsUtc = previousOemUtc;
                cachedOemProviderRowsSignature = previousOemSignature;
            }

            lock (windowsPowerRowsLock)
            {
                cachedWindowsPowerRows = previousPowerRows;
                windowsPowerRowsLastReadUtc = previousPowerUtc;
            }

            lock (wmiBatteryInfoLock)
            {
                cachedWmiBatteryInfo = previousWmiBatteryInfo;
                wmiBatteryInfoLastReadUtc = previousWmiBatteryInfoUtc;
            }

            lock (deviceBatteryRowsLock)
            {
                cachedDeviceBatteryRows = previousDeviceBatteryRows;
                deviceBatteryRowsLastReadUtc = previousDeviceBatteryRowsUtc;
            }

            lock (networkWmiDetailsCacheLock)
            {
                networkWmiDetailsCache.Clear();
                foreach (var pair in previousNetworkWmiDetails)
                {
                    networkWmiDetailsCache[pair.Key] = pair.Value;
                }
            }

            lock (gpuStatusRowsCacheLock)
            {
                cachedGpuStatusRows = previousGpuStatusRows;
                cachedGpuStatusRowsUtc = previousGpuStatusUtc;
            }
        }
    }

    private void SelfTestSpokenHotKeyMirrorOrder()
    {
        var sourceRows = new List<SensorRow>
        {
            new SensorRow { Type = "Performance", Hardware = "C: Test", Name = "Read rate", Identifier = "self-test-c-read", DisplayValue = "1 B/s", Source = "Self-test" },
            new SensorRow { Type = "Performance", Hardware = "C: Test", Name = "Write rate", Identifier = "self-test-c-write", DisplayValue = "2 B/s", Source = "Self-test" },
            new SensorRow { Type = "Performance", Hardware = "D: Test", Name = "Read rate", Identifier = "self-test-d-read", DisplayValue = "3 B/s", Source = "Self-test" },
            new SensorRow { Type = "Performance", Hardware = "D: Test", Name = "Write rate", Identifier = "self-test-d-write", DisplayValue = "4 B/s", Source = "Self-test" }
        };
        var previousTrayKeys = settings.TrayItemKeys;
        var previousProfiles = settings.SpokenHotKeys;
        var previousLabels = settings.ReadingSpeechLabels;
        try
        {
            settings.TrayItemKeys = new List<string>();
            settings.ReadingSpeechLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { RowSettingsKey(sourceRows[0]), "C: Read:" },
                { RowSettingsKey(sourceRows[1]), "Write:" },
                { RowSettingsKey(sourceRows[2]), "D: Read:" },
                { RowSettingsKey(sourceRows[3]), "Write:" }
            };
            settings.SpokenHotKeys = new List<SpokenHotKeySetting>
            {
                new SpokenHotKeySetting
                {
                    Name = "Read-Write",
                    HotKey = "Ctrl+Shift+F4",
                    ReadingKeys = new List<string>
                    {
                        RowSettingsKey(sourceRows[0]),
                        RowSettingsKey(sourceRows[1]),
                        RowSettingsKey(sourceRows[2]),
                        RowSettingsKey(sourceRows[3])
                    }
                }
            };

            var mirrorRows = BuildSpokenHotKeyCategoryRows(sourceRows)
                .Where(r => string.Equals(r.Hardware, "Read-Write", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Require(mirrorRows.Count == 4, "Spoken hotkey mirror did not include all configured rows.");
            Require(string.Equals(mirrorRows[0].Name, "C: Read:", StringComparison.Ordinal), "First mirrored row did not preserve configured order.");
            Require(string.Equals(mirrorRows[1].Name, "Write:", StringComparison.Ordinal), "Second mirrored row did not preserve configured order.");
            Require(string.Equals(mirrorRows[2].Name, "D: Read:", StringComparison.Ordinal), "Third mirrored row did not preserve configured order.");
            Require(string.Equals(mirrorRows[3].Name, "Write:", StringComparison.Ordinal), "Fourth mirrored row did not preserve configured order.");
        }
        finally
        {
            settings.TrayItemKeys = previousTrayKeys;
            settings.SpokenHotKeys = previousProfiles;
            settings.ReadingSpeechLabels = previousLabels;
        }
    }

    private void SelfTestTaskRowRefreshCache()
    {
        var cachedRow = new SensorRow { Type = "Tasks", Hardware = "Processes", Name = "Highest CPU process", Identifier = "self-test-cached-task", DisplayValue = "Cached: 1.0%", Source = "Self-test" };
        var cachedAt = DateTime.UtcNow;
        List<SensorRow> previousRows;
        DateTime previousUtc;
        lock (taskRowsCacheLock)
        {
            previousRows = cachedTaskRows.ToList();
            previousUtc = cachedTaskRowsUtc;
            cachedTaskRows = new List<SensorRow> { cachedRow };
            cachedTaskRowsUtc = cachedAt;
        }

        try
        {
            var rows = GetCachedTaskRows(false, false).ToList();
            DateTime afterUtc;
            lock (taskRowsCacheLock)
            {
                afterUtc = cachedTaskRowsUtc;
            }

            Require(rows.Count == 1 && string.Equals(rows[0].Identifier, cachedRow.Identifier, StringComparison.OrdinalIgnoreCase), "Immediate task refresh did not reuse cached rows.");
            Require(afterUtc == cachedAt, "Immediate task refresh unexpectedly replaced cached task rows.");
        }
        finally
        {
            lock (taskRowsCacheLock)
            {
                cachedTaskRows = previousRows;
                cachedTaskRowsUtc = previousUtc;
            }
        }
    }

    private void SelfTestProcessWatchReport()
    {
        var session = new ProcessWatchSession
        {
            ProcessId = 1234,
            ProcessName = "SelfTestProcess",
            ProcessPath = @"C:\SelfTest\SelfTestProcess.exe",
            StartedLocal = new DateTime(2026, 1, 1, 12, 0, 0),
            StoppedLocal = new DateTime(2026, 1, 1, 12, 0, 5),
            StopReason = "Self-test"
        };
        session.Samples.Add(new ProcessWatchSample
        {
            LocalTime = session.StartedLocal,
            ElapsedSeconds = 0,
            ProcessRunning = true,
            CpuPercent = 1.5,
            WorkingSetBytes = 100 * 1024 * 1024,
            PrivateMemoryBytes = 80 * 1024 * 1024,
            ThreadCount = 10,
            HandleCount = 100
        });
        session.Samples.Add(new ProcessWatchSample
        {
            LocalTime = session.StartedLocal.AddSeconds(5),
            ElapsedSeconds = 5,
            ProcessRunning = true,
            CpuPercent = 3.0,
            WorkingSetBytes = 120 * 1024 * 1024,
            PrivateMemoryBytes = 95 * 1024 * 1024,
            DedicatedGpuBytes = 20 * 1024 * 1024,
            SharedGpuBytes = 5 * 1024 * 1024,
            GpuUsagePercent = 2.5,
            ThreadCount = 11,
            HandleCount = 105
        });

        var report = BuildProcessWatchHtmlReport(session);
        Require(report.IndexOf("Sensor Readout process watch report", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing title.");
        Require(report.IndexOf("<table", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing HTML table.");
        Require(report.IndexOf("SelfTestProcess", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing process name.");
        Require(report.IndexOf("Working set change", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing growth summary.");
        Require(report.IndexOf("does not include keystrokes", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing privacy boundary.");
        Require(report.IndexOf("network payloads", StringComparison.OrdinalIgnoreCase) >= 0, "Process watch report missing network privacy boundary.");
    }

    private void SelfTestAudioLatencyAggregationAndPrivacy()
    {
        var run = new AudioLatencyRun
        {
            StartedLocal = new DateTime(2026, 1, 1, 12, 0, 0),
            StoppedLocal = new DateTime(2026, 1, 1, 12, 0, 5),
            IntervalStartedLocal = new DateTime(2026, 1, 1, 12, 0, 0),
            StopReason = "Self-test"
        };
        run.Images.Add(new AudioLatencyImage
        {
            BaseAddress = 0x1000,
            EndAddress = 0x2000,
            Path = @"C:\Windows\System32\drivers\selftest.sys"
        });
        RecordAudioLatencyRoutine(run, false, 0x1100, 125.5);
        RecordAudioLatencyRoutine(run, false, 0x1100, 40.0);
        RecordAudioLatencyRoutine(run, true, 0x1200, 27.25);
        RecordAudioLatencyHardFault(run, "PrivateSelfTestProcess", 4321);
        FinalizeAudioLatencySampleLocked(run, run.StartedLocal.AddSeconds(1), false);

        var dpc = BuildAudioLatencyDriverStats(run, false);
        var isr = BuildAudioLatencyDriverStats(run, true);
        Require(dpc.Count == 1 && string.Equals(dpc[0].Name, "selftest.sys", StringComparison.OrdinalIgnoreCase), "Audio latency DPC routine was not attributed to its driver image.");
        Require(dpc[0].Count == 2 && Math.Abs(dpc[0].MaximumMicroseconds - 125.5) < 0.001, "Audio latency DPC aggregation returned the wrong count or maximum.");
        Require(isr.Count == 1 && Math.Abs(isr[0].MaximumMicroseconds - 27.25) < 0.001, "Audio latency ISR aggregation returned the wrong maximum.");
        Require(run.Samples.Count == 1 && run.Samples[0].DpcCount == 2 && run.Samples[0].IsrCount == 1, "Audio latency live interval aggregation returned the wrong event counts.");
        Require(Math.Abs(run.Samples[0].MaximumDpcMicroseconds - 125.5) < 0.001 && run.Samples[0].MaximumDpcRoutine == 0x1100, "Audio latency live interval lost its DPC peak or driver routine.");
        for (var sampleIndex = 0; sampleIndex < 130; sampleIndex++)
        {
            run.IntervalDpcCount = sampleIndex;
            FinalizeAudioLatencySampleLocked(run, run.IntervalStartedLocal.AddSeconds(1), false);
        }
        Require(run.Samples.Count == 120, "Audio latency live history exceeded its bounded 120-sample limit.");

        UpdateAudioLatencyMenuItem();
        Require(audioLatencyMenuItem != null && audioLatencyMenuItem.Text.IndexOf("Ctrl+Shift+D", StringComparison.OrdinalIgnoreCase) >= 0, "Audio latency Options menu item did not expose Control Shift D.");
        Require(audioLatencyMenuItem != null && audioLatencyMenuItem.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.D), "Audio latency Options menu item lost its working shortcut.");

        AudioLatencyRun previous;
        lock (audioLatencyLock)
        {
            previous = latestAudioLatencyRun;
            latestAudioLatencyRun = run;
        }
        try
        {
            UpdateAudioLatencyMenuItem();
            Require(audioLatencyMonitorMenuItem != null && audioLatencyMonitorMenuItem.Available && audioLatencyMonitorMenuItem.Enabled, "Audio latency monitor command was not available for the latest test.");
            var snapshot = BuildAudioLatencyLiveSnapshot();
            Require(snapshot.HasRun && snapshot.Samples.Count == 120, "Audio latency live snapshot did not preserve the bounded sample history.");
            var rows = GetAudioLatencyRows().ToList();
            Require(rows.Any(r => string.Equals(r.Identifier, "audio-latency-top-dpc-driver", StringComparison.OrdinalIgnoreCase) && r.DisplayValue.IndexOf("selftest.sys", StringComparison.OrdinalIgnoreCase) >= 0), "Audio Latency category did not expose its highest DPC driver.");
            Require(rows.Any(r => string.Equals(r.Identifier, "audio-latency-latest-dpc", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Hardware, "Current interval", StringComparison.OrdinalIgnoreCase)), "Audio Latency category did not expose its grouped latest interval.");
            Require(rows.Any(r => string.Equals(r.Identifier, "audio-latency-recent-dpc", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Hardware, "Recent 60 seconds", StringComparison.OrdinalIgnoreCase)), "Audio Latency category did not expose its recent rolling peak.");
            Require(rows.All(r => !IsSelectableReadoutRow(r)), "Audio Latency diagnostic rows were exposed as alarm or spoken-hotkey candidates.");
            Require(DefaultCategoryChoices().Any(c => string.Equals(c.Type, "Audio Latency", StringComparison.OrdinalIgnoreCase)), "Audio Latency is missing from the default category list.");
        }
        finally
        {
            lock (audioLatencyLock)
            {
                latestAudioLatencyRun = previous;
            }
        }

        var privateSnapshot = new ReportSnapshot
        {
            AppVersion = AppVersion,
            MachineName = "PrivateMachine",
            Rows = new List<ReportSnapshotRow>
            {
                new ReportSnapshotRow
                {
                    Type = "Audio Latency",
                    Hardware = "Latest test",
                    Name = "Highest DPC driver",
                    DisplayValue = "selftest.sys",
                    Details = BuildAudioLatencyDetails(run)
                }
            }
        };
        var sanitized = SanitizeReportSnapshot(privateSnapshot);
        Require(!sanitized.Rows.Any(r => string.Equals(r.Type, "Audio Latency", StringComparison.OrdinalIgnoreCase)), "Anonymized reports retained Audio Latency process or driver data.");

        var report = BuildAudioLatencyHtmlReport(run);
        Require(report.IndexOf("Sensor Readout audio latency report", StringComparison.OrdinalIgnoreCase) >= 0, "Audio latency report missing its title.");
        Require(report.IndexOf("selftest.sys", StringComparison.OrdinalIgnoreCase) >= 0, "Audio latency report missing driver attribution.");
        Require(report.IndexOf("PrivateSelfTestProcess", StringComparison.OrdinalIgnoreCase) >= 0, "Audio latency report missing hard-fault process attribution.");
        Require(report.IndexOf("does not contain audio", StringComparison.OrdinalIgnoreCase) >= 0, "Audio latency report missing its privacy boundary.");

        string savedPath = null;
        lock (audioLatencyLock)
        {
            previous = latestAudioLatencyRun;
            latestAudioLatencyRun = run;
        }
        try
        {
            run.ReportPath = "";
            StopAudioLatencyForShutdown();
            savedPath = run.ReportPath;
            Require(!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath), "Application shutdown did not save the completed audio latency report.");
            SaveAudioLatencyReport(run);
            Require(string.Equals(savedPath, run.ReportPath, StringComparison.OrdinalIgnoreCase), "Audio latency report saving was not idempotent.");
        }
        finally
        {
            lock (audioLatencyLock)
            {
                latestAudioLatencyRun = previous;
            }
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                try { File.Delete(savedPath); } catch { }
            }
        }
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

    private void SelfTestUiMnemonicUniqueness()
    {
        EnsureSelfTestRows();
        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "General"))
        {
            preferences.CreateControl();
            var tabControls = FindControls<TabControl>(preferences.Controls).ToList();
            Require(tabControls.Count > 0, "Preferences form had no tab control to check.");
            foreach (var tabControl in tabControls)
            {
                foreach (TabPage page in tabControl.TabPages)
                {
                    RequireUniqueControlMnemonics("Preferences tab " + (page.Text ?? page.Name), page.Controls);
                }
            }
        }

        using (var remote = new RemoteMonitoringDialog(
            new List<RemoteConnectionSetting>(),
            null,
            null,
            null,
            null,
            null,
            null,
            delegate { return false; },
            delegate { return ""; },
            delegate { return ""; },
            T))
        {
            remote.CreateControl();
            RequireUniqueControlMnemonics("Remote monitoring dialog", remote.Controls);
        }

        RequireUniqueMenuMnemonics("Main menu bar", menuStrip.Items);
        foreach (ToolStripItem item in menuStrip.Items)
        {
            var dropDown = item as ToolStripDropDownItem;
            if (dropDown != null)
            {
                RequireUniqueMenuMnemonics("Menu " + StripMnemonicForSelfTest(item.Text), dropDown.DropDownItems);
            }
        }

        if (readingTree.ContextMenuStrip != null)
        {
            RequireUniqueMenuMnemonics("Reading tree context menu", readingTree.ContextMenuStrip.Items);
        }

        if (deviceList.ContextMenuStrip != null)
        {
            RequireUniqueMenuMnemonics("Category list context menu", deviceList.ContextMenuStrip.Items);
        }
    }

    private void SelfTestPreferencesCategoryAndShortcutBehavior()
    {
        EnsureSelfTestRows();
        settings.HiddenCategoryKeys = new List<string> { "type|Battery" };
        SaveSettings(settings);

        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Categories"))
        {
            preferences.CreateControl();
            SetPrivateField(preferences, "loadingPreferences", false);
            var categoryList = FindControls<CheckedListBox>(preferences.Controls)
                .FirstOrDefault(list => list.Items.Cast<object>().Any(item => item is CategoryChoice));
            Require(categoryList != null, "Preferences category list was not found.");

            var batteryIndex = -1;
            for (var i = 0; i < categoryList.Items.Count; i++)
            {
                var choice = categoryList.Items[i] as CategoryChoice;
                if (choice != null && string.Equals(choice.Key, "type|Battery", StringComparison.OrdinalIgnoreCase))
                {
                    batteryIndex = i;
                    break;
                }
            }

            Require(batteryIndex >= 0, "Battery category choice was not found.");
            categoryList.SelectedIndex = batteryIndex;
            InvokePrivate(preferences, "SetSelectedCategoryVisible", true);
            var hiddenCategoryKeys = settings.HiddenCategoryKeys ?? new List<string>();
            Require(!hiddenCategoryKeys.Contains("type|Battery", StringComparer.OrdinalIgnoreCase), "Showing a hidden category did not persist to live settings.");
        }

        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Fan profiles"))
        {
            preferences.CreateControl();
            SetPrivateField(preferences, "loadingPreferences", false);
            var tabs = FindControls<TabControl>(preferences.Controls).FirstOrDefault();
            Require(tabs != null, "Preferences tab control was not found for shortcut scoping.");
            Require(string.Equals(tabs.SelectedTab.Name, "Fan profiles", StringComparison.OrdinalIgnoreCase), "Preferences did not open on Fan profiles.");
            InvokeProcessCmdKey(preferences, Keys.Alt | Keys.D2);
            Require(string.Equals(tabs.SelectedTab.Name, "Fan profiles", StringComparison.OrdinalIgnoreCase), "Alt+2 on Fan profiles incorrectly switched to Hotkeys.");
        }

        settings.FanProfiles = new List<FanProfileSetting>
        {
            new FanProfileSetting
            {
                Name = "Self-test all fans",
                Actions = new List<FanProfileActionSetting>
                {
                    new FanProfileActionSetting { FanControlKey = "self-test-fan-a", Manual = true, Percent = 100 },
                    new FanProfileActionSetting { FanControlKey = "self-test-fan-b", Manual = true, Percent = 100 }
                }
            }
        };

        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Fan profiles"))
        {
            preferences.CreateControl();
            SetPrivateField(preferences, "loadingPreferences", false);
            InvokePrivate(preferences, "FinishInitialPreferenceLoad");
            var percentBox = FindControls<NumericUpDown>(preferences.Controls)
                .FirstOrDefault(box => string.Equals(box.AccessibleName, "Fan profile percent", StringComparison.OrdinalIgnoreCase));
            Require(percentBox != null, "Fan profile percent box was not found.");
            Require(Convert.ToInt32(percentBox.Value) == 100, "Fan profile editor did not load a saved 100 percent value.");

            percentBox.Text = "25";
            percentBox.Value = 25;
            InvokePrivate(preferences, "CommitPreferences");
            var actions = settings.FanProfiles == null || settings.FanProfiles.Count == 0 ? new List<FanProfileActionSetting>() : settings.FanProfiles[0].Actions;
            Require(actions != null && actions.Count == 2, "Fan profile self-test actions were lost.");
            Require(actions.All(a => a.Percent == 25), "Fan profile percent edit did not apply to all profile fan actions.");
        }
    }

    private void SelfTestWindowsSettingTargetMapping()
    {
        var temperatureGpuRow = new SensorRow
        {
            Type = "Temperatures",
            Hardware = "NVIDIA GeForce RTX self-test",
            Name = "GPU Core",
            DisplayValue = "55 C"
        };
        Require(GetWindowsSettingsTargetForSelfTest(temperatureGpuRow) == null, "Temperature GPU row should not open Display settings.");

        var displayRow = new SensorRow
        {
            Type = "Display",
            Hardware = "NVIDIA GeForce RTX self-test",
            Name = "Adapter",
            DisplayValue = "Available"
        };
        Require(GetWindowsSettingsTargetForSelfTest(displayRow) != null, "Display row should open Display settings.");

        var accessibilityRow = new SensorRow
        {
            Type = "Performance/Overview",
            Hardware = "Accessibility",
            Name = "High contrast",
            DisplayValue = "Off"
        };
        Require(GetWindowsSettingsTargetForSelfTest(accessibilityRow) != null, "Accessibility row should open a related Windows setting.");

        remoteViewMode = true;
        try
        {
            Require(GetWindowsSettingsTargetForSelfTest(displayRow) == null, "A remote row must not open a Windows setting on the local computer.");
        }
        finally
        {
            remoteViewMode = false;
        }
    }

    private void SelfTestPlugInPreferenceIdentity()
    {
        EnsureSelfTestRows();
        const string hpId = "sensorreadout.hp.experimental";
        const string huaweiId = "sensorreadout.huawei.matebook.experimental";
        settings.PlugInsEnabled = LoadPlugInPreferenceInfos(settings)
            .ToDictionary(plugIn => plugIn.Id, plugIn => false, StringComparer.OrdinalIgnoreCase);
        settings.PlugInsEnabled[huaweiId] = true;

        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Plug-Ins"))
        {
            preferences.CreateControl();
            var plugInList = FindControls<CheckedListBox>(preferences.Controls)
                .FirstOrDefault(list => list.Items.Cast<object>().Any(item => item is PlugInPreferenceInfo));
            Require(plugInList != null, "Preferences plug-in list was not found.");

            var hpIndex = -1;
            var huaweiIndex = -1;
            for (var i = 0; i < plugInList.Items.Count; i++)
            {
                var plugIn = plugInList.Items[i] as PlugInPreferenceInfo;
                if (plugIn == null)
                {
                    continue;
                }

                if (string.Equals(plugIn.Id, hpId, StringComparison.OrdinalIgnoreCase))
                {
                    hpIndex = i;
                }
                else if (string.Equals(plugIn.Id, huaweiId, StringComparison.OrdinalIgnoreCase))
                {
                    huaweiIndex = i;
                }
            }

            Require(hpIndex >= 0 && huaweiIndex >= 0, "HP and Huawei plug-ins were not found for identity testing.");

            plugInList.SetItemChecked(hpIndex, true);
            plugInList.SetItemChecked(huaweiIndex, false);
            SetPrivateField(preferences, "loadingPreferences", false);
            InvokePrivate(preferences, "SaveLivePreferences");
            Require(settings.PlugInsEnabled.ContainsKey(hpId) && !settings.PlugInsEnabled[hpId], "A displaced checkbox enabled the wrong plug-in ID.");
            Require(settings.PlugInsEnabled.ContainsKey(huaweiId) && settings.PlugInsEnabled[huaweiId], "A displaced checkbox disabled the intended plug-in ID.");

            InvokePrivate(preferences, "SynchronizePlugInCheckStates");
            Require(!plugInList.GetItemChecked(hpIndex), "HP checkbox did not resynchronize from its stable plug-in ID.");
            Require(plugInList.GetItemChecked(huaweiIndex), "Huawei checkbox did not resynchronize from its stable plug-in ID.");
        }
    }

    private object GetWindowsSettingsTargetForSelfTest(SensorRow row)
    {
        var method = typeof(SensorReadoutForm).GetMethod("GetRelatedWindowsSettingsTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("GetRelatedWindowsSettingsTarget not found for self-test.");
        }

        try
        {
            return method.Invoke(this, new object[] { row });
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("Private method not found for self-test: " + methodName);
        }

        try
        {
            method.Invoke(target, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException("Private field not found for self-test: " + fieldName);
        }

        field.SetValue(target, value);
    }

    private static bool InvokeProcessCmdKey(Form form, Keys keyData)
    {
        var method = form.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("ProcessCmdKey not found for self-test.");
        }

        var message = Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        var args = new object[] { message, keyData };
        try
        {
            return (bool)method.Invoke(form, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static IEnumerable<T> FindControls<T>(Control.ControlCollection controls) where T : Control
    {
        foreach (Control control in controls)
        {
            var match = control as T;
            if (match != null)
            {
                yield return match;
            }

            foreach (var child in FindControls<T>(control.Controls))
            {
                yield return child;
            }
        }
    }

    private static void RequireUniqueControlMnemonics(string scope, Control.ControlCollection controls)
    {
        var seen = new Dictionary<char, string>();
        foreach (var control in FlattenControls(controls))
        {
            if (!(control is ButtonBase) || string.IsNullOrWhiteSpace(control.Text))
            {
                continue;
            }

            char key;
            if (!TryGetControlMnemonicKey(control, out key))
            {
                continue;
            }

            var label = StripMnemonicForSelfTest(control.Text);
            string existing;
            if (seen.TryGetValue(key, out existing))
            {
                throw new InvalidOperationException(scope + " uses Alt+" + key + " for both \"" + existing + "\" and \"" + label + "\".");
            }

            seen[key] = label;
        }
    }

    private static IEnumerable<Control> FlattenControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            yield return control;
            foreach (var child in FlattenControls(control.Controls))
            {
                yield return child;
            }
        }
    }

    private static bool TryGetControlMnemonicKey(Control control, out char key)
    {
        key = '\0';
        var shortcutButton = control as ShortcutButton;
        if (shortcutButton != null && TryGetAltShortcutMnemonicKey(shortcutButton.ShortcutKeys, out key))
        {
            return true;
        }

        return TryGetMnemonicKey(control == null ? "" : control.Text, out key);
    }

    private static bool TryGetAltShortcutMnemonicKey(Keys keys, out char key)
    {
        key = '\0';
        if ((keys & Keys.Alt) != Keys.Alt || (keys & Keys.Control) == Keys.Control)
        {
            return false;
        }

        var code = keys & Keys.KeyCode;
        if (code >= Keys.A && code <= Keys.Z)
        {
            key = (char)('A' + (code - Keys.A));
            return true;
        }

        if (code >= Keys.D0 && code <= Keys.D9)
        {
            key = (char)('0' + (code - Keys.D0));
            return true;
        }

        if (code >= Keys.NumPad0 && code <= Keys.NumPad9)
        {
            key = (char)('0' + (code - Keys.NumPad0));
            return true;
        }

        return false;
    }

    private static void RequireUniqueMenuMnemonics(string scope, ToolStripItemCollection items)
    {
        var seen = new Dictionary<char, string>();
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator || string.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            char key;
            if (!TryGetMnemonicKey(item.Text, out key))
            {
                continue;
            }

            var label = StripMnemonicForSelfTest(item.Text);
            string existing;
            if (seen.TryGetValue(key, out existing))
            {
                throw new InvalidOperationException(scope + " uses Alt+" + key + " for both \"" + existing + "\" and \"" + label + "\".");
            }

            seen[key] = label;
        }
    }

    private static bool TryGetMnemonicKey(string text, out char key)
    {
        key = '\0';
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&')
            {
                continue;
            }

            if (text[i + 1] == '&')
            {
                i++;
                continue;
            }

            key = char.ToUpperInvariant(text[i + 1]);
            return true;
        }

        return false;
    }

    private static string StripMnemonicForSelfTest(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
    }

    private void SelfTestSpokenHotKeyAssignment()
    {
        EnsureSelfTestRows();
        var row = latestRows.FirstOrDefault(IsSelectableReadoutRow);
        Require(row != null, "No selectable row for spoken hotkey assignment.");
        var key = RowSettingsKey(row);
        settings.TrayItemKeys = new List<string>();
        settings.TrayItemKeys.Add(key);
        settings.SpeakTrayHotKey = "Ctrl+Alt+F11";
        settings.TrayStatusEnabled = true;
        SaveSettings(settings);
        var trayTargetText = TrayAssignmentDisplayText();
        Require(trayTargetText.IndexOf("Ctrl+Alt+F11", StringComparison.OrdinalIgnoreCase) >= 0 &&
            trayTargetText.IndexOf("1 reading", StringComparison.OrdinalIgnoreCase) >= 0,
            "Tray quick assignment target did not show hotkey and reading count.");
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
                SpokenMessage = "Self-test spoken alarm message",
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
                HighPercent = 100,
                EmergencyTemperatureC = 85,
                EmergencyPercent = 100
            }
        };
        settings.FanProfiles = new List<FanProfileSetting>
        {
            new FanProfileSetting
            {
                Name = "Self-test fan profile",
                Actions = new List<FanProfileActionSetting>
                {
                    new FanProfileActionSetting
                    {
                        FanControlKey = "self-test-fan-control",
                        Manual = true,
                        Percent = 100
                    }
                }
            }
        };
        SaveSettings(settings);
        var reloaded = LoadSettings();
        Require(reloaded.Alarms.Any(a => string.Equals(a.Name, "Self-test alarm", StringComparison.Ordinal)), "Alarm did not persist.");
        Require(reloaded.Alarms.Any(a => string.Equals(a.SpokenMessage, "Self-test spoken alarm message", StringComparison.Ordinal)), "Alarm spoken message did not persist.");
        Require(reloaded.FanCurves.Any(c => string.Equals(c.Name, "Self-test disabled fan curve", StringComparison.Ordinal)), "Fan curve did not persist.");
        Require(reloaded.FanCurves.Any(c => c.HighPercent == 100 && c.EmergencyPercent == 100), "Fan curve 100 percent values did not persist.");
        Require(reloaded.FanProfiles.Any(p => p.Actions != null && p.Actions.Any(a => a.Percent == 100)), "Fan profile action 100 percent value did not persist.");
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
        AssertSelfTestTextReportSanity(txtText, "TXT report");
        AssertSelfTestHtmlReportSanity(htmlText, "HTML report");
    }

    private void SelfTestStorageInventoryParsersAndPrivacy()
    {
        var ext2 = BuildSelfTestExtSuperblock(0, 0);
        FileSystemProbeResult parsed;
        Require(TryParseExtSuperblock(ext2, out parsed), "EXT2 superblock was not detected.");
        Require(parsed != null && parsed.FileSystem == "EXT2", "EXT2 superblock was classified incorrectly.");
        Require(parsed.Label == "SR EXT TEST", "EXT volume label was not decoded.");
        Require(!string.IsNullOrWhiteSpace(parsed.Uuid), "EXT UUID was not decoded.");

        var ext3 = BuildSelfTestExtSuperblock(0x4, 0);
        Require(TryParseExtSuperblock(ext3, out parsed) && parsed.FileSystem == "EXT3", "EXT3 journal feature was not detected.");

        var ext4 = BuildSelfTestExtSuperblock(0x4, 0x40);
        Require(TryParseExtSuperblock(ext4, out parsed) && parsed.FileSystem == "EXT4", "EXT4 extent feature was not detected.");
        Require(!TryParseExtSuperblock(new byte[1024], out parsed), "Invalid EXT data was accepted.");

        var apfs = new byte[4096];
        WriteSelfTestUInt32(apfs, 32, 0x4253584E);
        WriteSelfTestUInt32(apfs, 36, 4096);
        WriteSelfTestUInt64(apfs, 40, 262144);
        for (var index = 0; index < 16; index++) apfs[72 + index] = (byte)(index + 1);
        Require(TryParseApfsContainerBlock(apfs, out parsed), "APFS container superblock was not detected.");
        Require(parsed != null && parsed.FileSystem == "APFS", "APFS superblock was classified incorrectly.");
        Require(!string.IsNullOrWhiteSpace(parsed.Uuid), "APFS container UUID was not decoded.");
        apfs[36] = 1;
        Require(!TryParseApfsContainerBlock(apfs, out parsed), "APFS superblock with an invalid block size was accepted.");

        Require(DecodeFileSystemType(2) == "UFS", "Windows UFS filesystem code was decoded incorrectly.");
        Require(DecodeFileSystemType(11) == "EXT2", "Windows EXT2 filesystem code was decoded incorrectly.");
        Require(DecodeFileSystemType(14) == "NTFS", "Windows NTFS filesystem code was decoded incorrectly.");
        Require(DecodeFileSystemType(15) == "ReFS", "Windows ReFS filesystem code was decoded incorrectly.");

        var partitionWmiPath = GetDetailTreePath("Partition 1 WMI Block Size");
        Require(partitionWmiPath.Groups.Length == 2 && partitionWmiPath.Groups[0] == "WMI" && partitionWmiPath.Groups[1] == "Partition 1",
            "Partition WMI data was not placed under the WMI detail branch.");
        Require(partitionWmiPath.Label == "Block Size", "Partition WMI detail retained a redundant WMI prefix.");
        var storageWmiPath = GetDetailTreePath("Storage partition 2 storage WMI Offset");
        Require(storageWmiPath.Groups.Length == 2 && storageWmiPath.Groups[0] == "WMI" && storageWmiPath.Groups[1] == "Storage partition 2",
            "Windows Storage partition WMI data was not placed under the WMI detail branch.");
        Require(DecodeSmbMappingStatus(0) == T("value.Network drive connected", "Connected"), "Connected SMB mapping status was decoded incorrectly.");
        Require(DecodeSmbMappingStatus(2) == T("value.Network drive disconnected", "Disconnected"), "Disconnected SMB mapping status was decoded incorrectly.");

        var privateSnapshot = new ReportSnapshot
        {
            AppVersion = AppVersion,
            Title = "Sensor Readout report for PRIVATE-PC",
            MachineName = "PRIVATE-PC",
            GeneratedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Rows = new List<ReportSnapshotRow>
            {
                new ReportSnapshotRow
                {
                    Type = "Performance",
                    Hardware = "Z: Family Files",
                    Name = "Serial number",
                    Identifier = "secret-device-id",
                    DisplayValue = "SERIAL-PRIVATE-123",
                    Source = "Self-test",
                    Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Drive label", "Family Files" },
                        { "Partition 2 volume 1 label", "Family Files" },
                        { "Volume WMI Volume Name", "Family Files" },
                        { "Z: Family Files free", "100 GB" },
                        { "Z: Family Files used", "20 GB" },
                        { "Remote location", @"\\PRIVATE-SERVER\Personal" },
                        { "Disk path", @"\\?\PhysicalDrive9" },
                        { "Volume UUID", "12345678-1234-1234-1234-1234567890AB" }
                    }
                }
            }
        };
        var sanitized = SanitizeReportSnapshot(privateSnapshot);
        var sanitizedJson = JsonConvert.SerializeObject(sanitized);
        foreach (var secret in new[] { "PRIVATE-PC", "Family Files", "PRIVATE-SERVER", "Personal", "PhysicalDrive9", "SERIAL-PRIVATE-123", "secret-device-id", "12345678-1234-1234-1234-1234567890AB" })
        {
            Require(sanitizedJson.IndexOf(secret, StringComparison.OrdinalIgnoreCase) < 0, "Anonymized storage data exposed: " + secret);
        }
        Require(sanitizedJson.IndexOf("Z: [drive label] free", StringComparison.OrdinalIgnoreCase) >= 0,
            "Anonymized storage summary lost its free-space field after masking the drive label.");
        Require(sanitizedJson.IndexOf("Z: [drive label] used", StringComparison.OrdinalIgnoreCase) >= 0,
            "Anonymized storage summary lost its used-space field after masking the drive label.");
    }

    private static byte[] BuildSelfTestExtSuperblock(uint compatibleFeatures, uint incompatibleFeatures)
    {
        var data = new byte[1024];
        WriteSelfTestUInt32(data, 0x04, 1048576);
        WriteSelfTestUInt32(data, 0x0C, 524288);
        WriteSelfTestUInt32(data, 0x18, 2);
        WriteSelfTestUInt16(data, 0x34, 3);
        WriteSelfTestUInt16(data, 0x36, 20);
        WriteSelfTestUInt16(data, 0x38, 0xEF53);
        WriteSelfTestUInt16(data, 0x3A, 1);
        WriteSelfTestUInt32(data, 0x48, 0);
        WriteSelfTestUInt32(data, 0x5C, compatibleFeatures);
        WriteSelfTestUInt32(data, 0x60, incompatibleFeatures);
        for (var index = 0; index < 16; index++) data[0x68 + index] = (byte)(0xA0 + index);
        Encoding.UTF8.GetBytes("SR EXT TEST").CopyTo(data, 0x78);
        return data;
    }

    private static void WriteSelfTestUInt16(byte[] data, int offset, ushort value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    private static void WriteSelfTestUInt32(byte[] data, int offset, uint value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    private static void WriteSelfTestUInt64(byte[] data, int offset, ulong value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    private void AssertSelfTestTextReportSanity(string text, string label)
    {
        Require(!string.IsNullOrWhiteSpace(text), label + " is empty.");
        Require(text.Contains("Sensor Readout"), label + " does not look like a Sensor Readout report.");
        Require(text.Contains("Generated by Sensor Readout"), label + " missing generated-by line.");
        Require(text.Contains("Download Sensor Readout:"), label + " missing Sensor Readout download link.");
        Require(text.Contains("Unit preferences:"), label + " missing unit preference summary.");
        Require(!text.Contains("[SensorReadoutReportData]"), label + " should be human-readable and should not contain wrapped internal report data.");
        Require(!Regex.IsMatch(text, @"(?m)^.{600,}$"), label + " contains an unexpectedly long line.");
        Require(!Regex.IsMatch(text, @"(?im)^[ \t]*Printer[ \t]+[^\r\n]+[ \t]+(status|driver|port|offline|shared|jobs queued|paper size|resolution|color|duplex):"),
            label + " contains verbose printer prefixes instead of the grouped printer tree.");

        AssertSelfTestReportTextDoesNotContainUiNoise(text, label);

        var headings = Regex.Matches(text, @"(?m)^#\s+(.+?)\s*$")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        Require(headings.Count >= 3, label + " has too few top-level sections.");
        var duplicateHeading = headings
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        Require(duplicateHeading == null, label + " repeats top-level section: " + (duplicateHeading == null ? "" : duplicateHeading.Key));

    }

    private void AssertSelfTestHtmlReportSanity(string html, string label)
    {
        Require(!string.IsNullOrWhiteSpace(html), label + " is empty.");
        Require(html.IndexOf("Sensor Readout", StringComparison.OrdinalIgnoreCase) >= 0, label + " does not look like a Sensor Readout report.");
        Require(html.IndexOf("Unit preferences:", StringComparison.OrdinalIgnoreCase) >= 0, label + " missing unit preference summary.");
        Require(Regex.Matches(html, "id=[\"']sensor-readout-report-data[\"']", RegexOptions.IgnoreCase).Count == 1, label + " must contain exactly one structured report payload.");
        Require(!html.Contains("[SensorReadoutReportData]"), label + " contains legacy TXT report markers.");
        AssertSelfTestReportTextDoesNotContainUiNoise(html, label);

        ReportSnapshot snapshot;
        Require(TryReadEmbeddedReportSnapshot(html, out snapshot), label + " structured payload could not be decoded.");
        AssertSelfTestReportSnapshotSanity(snapshot, label + " snapshot");

        var headings = Regex.Matches(html, "<h2>(?<text>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Select(m => Regex.Replace(System.Net.WebUtility.HtmlDecode(m.Groups["text"].Value), "<.*?>", "").Trim())
            .Where(s => s.Length > 0)
            .ToList();
        Require(headings.Count >= 3, label + " has too few visible category sections.");
        var duplicateHeading = headings
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        Require(duplicateHeading == null, label + " repeats visible category section: " + (duplicateHeading == null ? "" : duplicateHeading.Key));
    }

    private void AssertSelfTestReportSnapshotSanity(ReportSnapshot snapshot, string label)
    {
        Require(snapshot != null, label + " is missing.");
        Require(!string.IsNullOrWhiteSpace(snapshot.AppVersion), label + " missing app version.");
        Require(!string.IsNullOrWhiteSpace(snapshot.MachineName), label + " missing machine name.");
        Require(snapshot.Rows != null && snapshot.Rows.Count > 0, label + " has no rows.");
        Require(snapshot.Rows.Count(r => !string.IsNullOrWhiteSpace(r.Type)) >= 3, label + " has too few typed rows.");
        var blankRow = snapshot.Rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Type) || string.IsNullOrWhiteSpace(r.Name));
        Require(blankRow == null, label + " contains a row with a blank type or name.");
    }

    private void AssertSelfTestReportTextDoesNotContainUiNoise(string text, string label)
    {
        foreach (var term in new[]
        {
            "Data appears here after a refresh",
            "will appear after a refresh",
            "Refreshing sensors",
            "No meter for selected reading",
            "Has Details"
        })
        {
            Require(text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0, label + " contains UI/status/fallback text: " + term);
        }

        Require(!Regex.IsMatch(text, @"(?im)(^|[\s>])(?:ui|a11y|message)\.[A-Za-z0-9_.-]+"),
            label + " contains an untranslated UI/status key.");
    }

    private void AssertSelfTestAnonymizedReportSanity(string text, string label)
    {
        Require(!string.IsNullOrWhiteSpace(text), label + " is empty.");
        var machine = Environment.MachineName ?? "";
        if (!string.IsNullOrWhiteSpace(machine))
        {
            Require(text.IndexOf(machine, StringComparison.OrdinalIgnoreCase) < 0, label + " still contains the current computer name.");
        }

        Require(!Regex.IsMatch(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b"), label + " still contains an IPv4 address.");
        Require(!Regex.IsMatch(text, @"\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b", RegexOptions.IgnoreCase), label + " still contains a MAC address.");
        Require(!Regex.IsMatch(text, @"(?i)\b[A-Z]:\\Users\\|\\Users\\|/Users/"), label + " still contains a user-profile filesystem path.");
        Require(!Regex.IsMatch(text, @"(?i)\\\\[^\\\s;]+\\[^\s;,\r\n]+"), label + " still contains a network location.");
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
        var reportSpeech = BuildCurrentSpeechStatusText();
        Require(reportSpeech.IndexOf("static report", StringComparison.OrdinalIgnoreCase) >= 0,
            "Report-mode hotkey speech did not identify static report data.");
        var previousTrayKeys = settings.TrayItemKeys == null ? new List<string>() : new List<string>(settings.TrayItemKeys);
        settings.TrayItemKeys = new List<string> { "Missing|Report|Reading|self-test" };
        reportSpeech = BuildCurrentSpeechStatusText();
        Require(reportSpeech.IndexOf("does not contain", StringComparison.OrdinalIgnoreCase) >= 0 &&
            reportSpeech.IndexOf("wait", StringComparison.OrdinalIgnoreCase) < 0,
            "Report-mode missing hotkey rows used live-data waiting wording.");
        settings.TrayItemKeys = previousTrayKeys;
        var emptyReportItems = BuildReadingTree(new List<SensorRow>(), new DeviceFilter { Type = "Fan" });
        Require(emptyReportItems.Count == 1 &&
            emptyReportItems[0].Text.IndexOf("static report", StringComparison.OrdinalIgnoreCase) >= 0 &&
            emptyReportItems[0].Text.IndexOf("refresh", StringComparison.OrdinalIgnoreCase) < 0,
            "Report-mode empty category used live-refresh wording.");
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

        EnsureSelfTestRows();
        var foreignReportRows = latestRows.Select(ToReportSnapshotRow).ToList();
        EnterReportView(new ReportSnapshot
        {
            AppVersion = AppVersion,
            Title = "Sensor Readout report for OTHERBOX",
            MachineName = "OTHERBOX",
            GeneratedLocal = "2026-01-01 00:00:00",
            Rows = foreignReportRows
        }, Path.Combine(outputFolder, "foreign-report.html"));
        Require(reportViewMode, "Foreign report did not enter report view.");
        ReturnToLiveReadings();
        var liveSnapshot = BuildReportSnapshot();
        Require(!string.Equals(liveSnapshot.MachineName, "OTHERBOX", StringComparison.OrdinalIgnoreCase),
            "Returning to live readings kept the opened report machine name in generated report metadata.");
    }

    private void SelfTestReportToolsAndHistory(string outputFolder)
    {
        EnsureSelfTestRows();
        Require(latestRows.Any(r => string.Equals(r.Hardware, T("ui.Data sources", "Data sources"), StringComparison.OrdinalIgnoreCase)), "Data source summary rows missing.");

        var before = BuildReportSnapshot();
        var changedRow = before.Rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.DisplayValue));
        Require(changedRow != null, "No row available for report comparison.");
        var after = new ReportSnapshot
        {
            AppVersion = before.AppVersion,
            Title = before.Title,
            MachineName = before.MachineName,
            GeneratedLocal = before.GeneratedLocal,
            Rows = before.Rows.Select(r => new ReportSnapshotRow
            {
                Type = r.Type,
                Hardware = r.Hardware,
                Name = r.Name,
                Identifier = r.Identifier,
                Value = r.Value,
                DisplayValue = r == changedRow ? r.DisplayValue + " self-test" : r.DisplayValue,
                Source = r.Source,
                Details = r.Details == null ? null : new Dictionary<string, string>(r.Details, StringComparer.OrdinalIgnoreCase)
            }).ToList()
        };
        var comparison = BuildReportComparisonText(before, "before.html", after, "after.html");
        Require(comparison.IndexOf("Changed readings", StringComparison.OrdinalIgnoreCase) >= 0, "Report comparison missing changed section.");
        Require(comparison.IndexOf("self-test", StringComparison.OrdinalIgnoreCase) >= 0, "Report comparison did not report changed value.");

        var sanitized = SanitizeReportSnapshot(before);
        Require(string.Equals(sanitized.MachineName, "Computer", StringComparison.Ordinal), "Anonymized report did not replace machine name.");
        Require(!sanitized.Rows.Any(r => string.Equals(r.Type, "Tasks", StringComparison.OrdinalIgnoreCase)), "Anonymized report still contains Tasks rows.");
        Require(!sanitized.Rows.Any(r => string.Equals(r.Type, "Audio Latency", StringComparison.OrdinalIgnoreCase)), "Anonymized report still contains Audio Latency rows.");
        Require(!sanitized.Rows.Any(r => string.Equals(r.Type, "Spoken Hotkeys", StringComparison.OrdinalIgnoreCase)), "Anonymized report still contains Spoken Hotkeys rows.");
        AssertSelfTestReportSnapshotSanity(sanitized, "Anonymized report snapshot");
        var sanitizedHtml = BuildHtmlReport("", sanitized);
        Require(sanitizedHtml.IndexOf(Environment.MachineName ?? "", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrWhiteSpace(Environment.MachineName), "Anonymized report still contains the current computer name.");
        AssertSelfTestHtmlReportSanity(sanitizedHtml, "Anonymized HTML report");
        AssertSelfTestAnonymizedReportSanity(sanitizedHtml, "Anonymized HTML report");
        var sanitizedText = BuildTextReport("", sanitized);
        AssertSelfTestTextReportSanity(sanitizedText, "Anonymized TXT report");
        AssertSelfTestAnonymizedReportSanity(sanitizedText, "Anonymized TXT report");

        var row = latestRows.FirstOrDefault(IsSelectableReadoutRow);
        Require(row != null, "No selectable row available for reading history.");
        settings.TrendLoggingEnabled = true;
        settings.TrendLoggingKeys = new List<string> { RowSettingsKey(row) };
        LogTrendRows(latestRows);
        Require(File.Exists(GetTrendLogFilePath()), "Reading history CSV was not written.");
    }

    private void SelfTestCommunityStatsPayloadPrivacy()
    {
        EnsureSelfTestRows();
        settings.CommunityStatsClientId = "self-test-client-id";
        SaveSettings(settings);
        var payload = BuildCommunityStatsPayload();
        var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        Require(json.IndexOf("self-test-client-id", StringComparison.OrdinalIgnoreCase) < 0, "Community stats payload exposed the raw client ID.");
        Require(json.IndexOf(Environment.MachineName ?? "", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrWhiteSpace(Environment.MachineName), "Community stats payload exposed the machine name.");
        Require(!Regex.IsMatch(json, @"\b(?:\d{1,3}\.){3}\d{1,3}\b"), "Community stats payload exposed an IPv4 address.");
        Require(!Regex.IsMatch(json, @"\b[0-9A-F]{2}(?:[:-][0-9A-F]{2}){5}\b", RegexOptions.IgnoreCase), "Community stats payload exposed a MAC address.");
        Require(json.IndexOf("rowsByCategory", StringComparison.OrdinalIgnoreCase) >= 0, "Community stats payload missing category counts.");
        Require(json.IndexOf("anonymousClientIdHash", StringComparison.OrdinalIgnoreCase) >= 0, "Community stats payload missing client hash.");
        Require(json.IndexOf("Rows", StringComparison.OrdinalIgnoreCase) < 0 || json.IndexOf("full report rows", StringComparison.OrdinalIgnoreCase) >= 0, "Community stats payload appears to include full report row data.");
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
        var caseTempFolders = Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory, "*_case_tmp", SearchOption.TopDirectoryOnly);
        Require(caseTempFolders.Length == 0, "Temporary folder case-repair leftovers found: " + string.Join(", ", caseTempFolders.Select(Path.GetFileName).ToArray()));
        RefreshLanguageChoices(true);
        Require(languageChoices.Count > 0, "No language choices loaded.");
        Require(UpdateCheckDialogTitle().IndexOf("Sensor Readout", StringComparison.OrdinalIgnoreCase) >= 0,
            "The update dialog title does not identify Sensor Readout.");

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

        foreach (var languageFile in languageFiles)
        {
            var values = ReadLanguageFile(languageFile);
            var networkToolMnemonics = new HashSet<char>();
            foreach (var key in new[] { "ui.&Address or host name:", "ui.&Run", "ui.Include &ping test" })
            {
                string value;
                char mnemonic = '\0';
                Require(values.TryGetValue(key, out value) && TryGetMnemonicKey(value, out mnemonic), Path.GetFileName(languageFile) + " missing a Network Tools mnemonic for " + key + ".");
                Require(networkToolMnemonics.Add(mnemonic), Path.GetFileName(languageFile) + " has a duplicate Network Tools mnemonic: Alt+" + char.ToUpperInvariant(mnemonic) + ".");
            }


            var remoteMnemonics = new HashSet<char>();
            foreach (var key in new[]
            {
                "ui.&Add server...", "ui.&Edit server...", "ui.&Import connection...", "ui.&Remove server",
                "ui.&Host this computer", "ui.Save host &connection...",
                "ui.Re&fresh computers", "ui.&View computer", "ui.Run fan &profile...",
                "ui.E&nable this server", "ui.&Share this computer", "ui.A&llow remote saved fan profiles"
            })
            {
                string value;
                char mnemonic = '\0';
                Require(values.TryGetValue(key, out value) && TryGetMnemonicKey(value, out mnemonic), Path.GetFileName(languageFile) + " missing a Remote Monitoring mnemonic for " + key + ".");
                Require(remoteMnemonics.Add(mnemonic), Path.GetFileName(languageFile) + " has a duplicate Remote Monitoring mnemonic: Alt+" + char.ToUpperInvariant(mnemonic) + ".");
            }
        }

        var frenchLanguagePath = Path.Combine(GetLanguagesFolderPath(), "Francais.txt");
        if (File.Exists(frenchLanguagePath))
        {
            var frenchText = File.ReadAllText(frenchLanguagePath, Encoding.UTF8);
            Require(frenchText.IndexOf("mise à jour", StringComparison.OrdinalIgnoreCase) >= 0, "French language file lost accented update wording.");
            Require(frenchText.IndexOf("télécharger", StringComparison.OrdinalIgnoreCase) >= 0, "French language file lost accented download wording.");
            Require(frenchText.IndexOf("périph", StringComparison.OrdinalIgnoreCase) >= 0, "French language file lost accented device wording.");
            Require(frenchText.IndexOf("lecteur d’écran", StringComparison.OrdinalIgnoreCase) >= 0 || frenchText.IndexOf("lecteur d'écran", StringComparison.OrdinalIgnoreCase) >= 0, "French language file lost accented screen-reader wording.");
            Require(frenchText.IndexOf("Lecture ajoutée ? l", StringComparison.OrdinalIgnoreCase) < 0, "French language file still contains replacement characters in history wording.");
            Require(frenchText.IndexOf("mises a jour", StringComparison.OrdinalIgnoreCase) < 0, "French language file still contains unaccented update wording.");
        }

        var italianLanguagePath = Path.Combine(GetLanguagesFolderPath(), "Italiano.txt");
        if (File.Exists(italianLanguagePath))
        {
            var italianText = File.ReadAllText(italianLanguagePath, Encoding.UTF8);
            Require(italianText.IndexOf("La lettura selezionata ? ", StringComparison.OrdinalIgnoreCase) < 0, "Italian language file still contains replacement question marks in selection status wording.");
            Require(italianText.IndexOf("Sensor Readout è", StringComparison.OrdinalIgnoreCase) >= 0, "Italian language file lost accented essere wording.");
            Require(italianText.IndexOf("più", StringComparison.OrdinalIgnoreCase) >= 0, "Italian language file lost accented piu wording.");
        }

        foreach (var manual in Directory.GetFiles(GetDocsFolderPath(), "README-*.html"))
        {
            var html = File.ReadAllText(manual);
            Require(Regex.IsMatch(html, @"<p>[^<]*\b" + Regex.Escape(AppVersion) + @"\.</p>"), Path.GetFileName(manual) + " missing visible current version " + AppVersion + ".");
            Require(html.IndexOf("<h3>" + AppVersion + "</h3>", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing changelog entry for " + AppVersion + ".");
            Require(html.IndexOf("<h2 id=\"categories-and-readings\"", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing Categories and Readings section.");
            var categoriesHeading = html.IndexOf("<h2 id=\"categories-and-readings\"", StringComparison.OrdinalIgnoreCase);
            var remoteMonitoringHeading = html.IndexOf("<h2 id=\"remote-monitoring\"", StringComparison.OrdinalIgnoreCase);
            var networkToolsHeading = html.IndexOf("<h2 id=\"network-tools\"", StringComparison.OrdinalIgnoreCase);
            var audioLatencyHeading = html.IndexOf("<h2 id=\"audio-latency-diagnostic\"", StringComparison.OrdinalIgnoreCase);
            Require(networkToolsHeading >= 0, Path.GetFileName(manual) + " missing a top-level Network Tools section outside the changelog.");
            Require(remoteMonitoringHeading >= 0, Path.GetFileName(manual) + " missing a top-level Remote Monitoring section outside the changelog.");
            Require(html.IndexOf("href=\"#remote-monitoring\"", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing Remote Monitoring from the table of contents.");
            Require(html.IndexOf("href=\"#network-tools\"", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing Network Tools from the table of contents.");
            Require(categoriesHeading < remoteMonitoringHeading && remoteMonitoringHeading < networkToolsHeading && networkToolsHeading < audioLatencyHeading, Path.GetFileName(manual) + " has Remote Monitoring or Network Tools nested in or misplaced around Categories and Audio Latency.");
            Require(html.IndexOf(".srconnection", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " does not explain remote server connection files.");
            Require(html.IndexOf("SensorReadout-Server-", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " does not document the separate Linux relay download.");
            Require(html.IndexOf("sudo python3 sensor_readout_server_control.py install", StringComparison.Ordinal) >= 0, Path.GetFileName(manual) + " does not document the guided Linux relay installer.");
            Require(html.IndexOf("sensor-readout-server-control setup", StringComparison.Ordinal) >= 0, Path.GetFileName(manual) + " does not document post-install Linux relay setup.");
            Require(html.IndexOf("server-vX.Y.Z", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " does not document the dedicated Linux relay update channel.");
            Require(html.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0 && html.IndexOf("HTTPS", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " does not explain safe remote relay network exposure.");
            Require(html.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0, Path.GetFileName(manual) + " favors one VPN product instead of using generic private-network guidance.");
            var networkToolShortcutCount = Regex.Matches(html,
                @"(?i)(Ctrl\+Shift\+T|Strg\+Umschalt\+T|Ctrl\+Maj\+T|Ctrl\+Mayús\+T|Ctrl\+Maiusc\+T)").Count;
            Require(networkToolShortcutCount >= 3, Path.GetFileName(manual) + " does not document the Network Tools shortcut in its changelog, shortcut table, and normal guidance.");
            Require(html.IndexOf("<code>Tab</code>", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing Tab guidance for moving from categories to readings.");
            Require(html.IndexOf("<code>Enter</code> / <code>Alt+Enter</code>", StringComparison.OrdinalIgnoreCase) < 0, Path.GetFileName(manual) + " still describes Alt+Enter as a Details shortcut.");
            Require(!Regex.IsMatch(html, @"(?i)(Enter\s+or\s+Alt\+Enter|Enter\s+oder\s+Alt\+Enter|Enter\s+ou\s+Alt\+Enter|Enter\s+o\s+Alt\+Enter)"), Path.GetFileName(manual) + " contains stale Enter/Alt+Enter Details wording.");
        }
    }

    private void SelfTestUpdateChannelSeparation(string outputFolder)
    {
        var serverRelease = new GitHubReleaseInfo
        {
            TagName = "server-v99.0.0",
            Assets = new List<GitHubReleaseAsset>
            {
                new GitHubReleaseAsset { Name = "SensorReadout-Server-99.0.0.zip", BrowserDownloadUrl = "https://example.invalid/server.zip", Digest = "sha256:" + new string('a', 64) }
            }
        };
        var draftClient = new GitHubReleaseInfo
        {
            TagName = "v98.0.0",
            Draft = true,
            Assets = new List<GitHubReleaseAsset>
            {
                new GitHubReleaseAsset { Name = "SensorReadout-98.0.0.zip", BrowserDownloadUrl = "https://example.invalid/draft.zip", Digest = "sha256:" + new string('b', 64) }
            }
        };
        var clientRelease = new GitHubReleaseInfo
        {
            TagName = "v6.0.1",
            Assets = new List<GitHubReleaseAsset>
            {
                new GitHubReleaseAsset { Name = "SensorReadout-Server-6.0.1.zip", BrowserDownloadUrl = "https://example.invalid/wrong.zip", Digest = "sha256:" + new string('c', 64) },
                new GitHubReleaseAsset { Name = "SensorReadout-6.0.1.zip", BrowserDownloadUrl = "https://example.invalid/client.zip", Digest = "sha256:" + new string('d', 64) }
            }
        };
        var selected = LatestVersionedRelease(new[] { serverRelease, draftClient, clientRelease });
        Require(object.ReferenceEquals(clientRelease, selected), "The Windows updater did not ignore server and draft release channels.");
        Require(ReleaseVersion(serverRelease) == null, "The Windows updater accepted a server release tag.");
        var asset = FindPortableZipAsset(clientRelease);
        Require(asset != null && asset.Name == "SensorReadout-6.0.1.zip", "The Windows updater did not require the exact client archive name.");
        clientRelease.Assets.Add(new GitHubReleaseAsset { Name = "sensorreadout-6.0.1.ZIP", BrowserDownloadUrl = "https://example.invalid/duplicate.zip", Digest = "sha256:" + new string('e', 64) });
        Require(FindPortableZipAsset(clientRelease) == null, "The Windows updater accepted duplicate matching client archives.");
        clientRelease.Assets.RemoveAt(clientRelease.Assets.Count - 1);
        clientRelease.Assets[1].Digest = "";
        Require(FindPortableZipAsset(clientRelease) == null, "The Windows updater accepted a client archive without a valid GitHub SHA-256 digest.");

        Require(!Program.WaitForProcessExit(Process.GetCurrentProcess().Id, 1), "The Windows updater treated a still-running process as exited.");

        var deleteRoot = Path.Combine(outputFolder, "updater-required-delete-self-test");
        Directory.CreateDirectory(deleteRoot);
        var lockedFile = Path.Combine(deleteRoot, "obsolete-shipped.dll");
        File.WriteAllText(lockedFile, "self-test");
        var rejectedLockedRemoval = false;
        using (var lockedStream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { Program.DeleteDirectoryRequired(deleteRoot); }
            catch (IOException) { rejectedLockedRemoval = true; }
            catch (UnauthorizedAccessException) { rejectedLockedRemoval = true; }
        }
        Require(rejectedLockedRemoval && Directory.Exists(deleteRoot), "The Windows updater ignored a failed shipped-folder removal.");
        Program.DeleteDirectoryRequired(deleteRoot);
        Require(!Directory.Exists(deleteRoot), "The Windows updater did not remove an unlocked shipped folder completely.");
    }

    private void SelfTestBundledPlugInManifestRepair(string outputFolder)
    {
        var sourcePlugIns = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plug-Ins");
        var asusDll = Path.Combine(sourcePlugIns, "AsusRog", "AsusRogPlugIn.dll");
        var dellDll = Path.Combine(sourcePlugIns, "DellLatitude", "DellLatitudePlugIn.dll");
        if (!File.Exists(asusDll) || !File.Exists(dellDll))
        {
            LogMessage("Debug", "Skipping bundled plug-in manifest repair self-test because bundled plug-in DLLs are not present beside the executable.");
            return;
        }

        var tempRoot = Path.Combine(outputFolder, "self-test-plugin-manifest");
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }

        var tempPlugIns = Path.Combine(tempRoot, "Plug-Ins");
        var tempData = Path.Combine(tempRoot, "Data");
        Directory.CreateDirectory(tempData);
        CopySelfTestPlugInDll(asusDll, Path.Combine(tempPlugIns, "AsusRog", "AsusRogPlugIn.dll"));
        CopySelfTestPlugInDll(dellDll, Path.Combine(tempPlugIns, "DellLatitude", "DellLatitudePlugIn.dll"));
        var customDll = Path.Combine(tempPlugIns, "CommunityPlugIn", "CommunityPlugIn.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(customDll));
        File.WriteAllText(customDll, "custom plug-in placeholder");

        var manifestPath = Path.Combine(tempData, "BundledPlugInHashes.json");
        var asusRelative = @"AsusRog\AsusRogPlugIn.dll";
        var dellRelative = @"DellLatitude\DellLatitudePlugIn.dll";
        var oldHash = new string('0', 64);
        var asusHash = ComputeSha256ForSelfTest(Path.Combine(tempPlugIns, asusRelative));
        var dellHash = ComputeSha256ForSelfTest(Path.Combine(tempPlugIns, dellRelative));

        WriteSelfTestManifest(manifestPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { asusRelative, oldHash },
            { dellRelative, dellHash }
        });
        Require(!Program.RepairBundledPlugInHashManifestForTest(tempRoot), "Manifest repair ran when only one bundled DLL differed; this could hide user edits.");
        var partialManifest = File.ReadAllText(manifestPath);
        Require(partialManifest.IndexOf(oldHash, StringComparison.OrdinalIgnoreCase) >= 0, "Partial mismatch manifest was unexpectedly changed.");

        WriteSelfTestManifest(manifestPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { asusRelative, oldHash },
            { dellRelative, oldHash }
        });
        Require(Program.RepairBundledPlugInHashManifestForTest(tempRoot), "Manifest repair did not run for legacy bundled plug-in hashes.");
        var repairedManifest = File.ReadAllText(manifestPath);
        Require(repairedManifest.IndexOf(asusHash, StringComparison.OrdinalIgnoreCase) >= 0, "Repaired manifest missing current Asus plug-in hash.");
        Require(repairedManifest.IndexOf(dellHash, StringComparison.OrdinalIgnoreCase) >= 0, "Repaired manifest missing current Dell plug-in hash.");
        Require(repairedManifest.IndexOf("CommunityPlugIn", StringComparison.OrdinalIgnoreCase) < 0, "Repaired manifest incorrectly included a third-party plug-in folder.");
    }

    private static void CopySelfTestPlugInDll(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.Copy(source, target, true);
    }

    private static void WriteSelfTestManifest(string path, Dictionary<string, string> hashes)
    {
        var lines = new List<string>
        {
            "{",
            "    \"Version\":  1,",
            "    \"UpdatedUtc\":  \"" + DateTime.UtcNow.ToString("o") + "\",",
            "    \"Files\":  {"
        };
        var ordered = hashes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var pair = ordered[i];
            lines.Add("                  \"" + pair.Key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\":  \"" + pair.Value + "\"" + (i + 1 < ordered.Count ? "," : ""));
        }

        lines.Add("              }");
        lines.Add("}");
        File.WriteAllLines(path, lines.ToArray());
    }

    private static string ComputeSha256ForSelfTest(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
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

    private void ResetReadingTreeExpansionForSelfTest()
    {
        readingTreeExpansionInitialized = false;
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        lastReadingTreeFilterKey = "";
        lastAppliedReadingTreeExpansionMode = "";
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
