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
            form.RunSelfTestStep(results, "Global hotkey validation", delegate { form.SelfTestGlobalHotKeyValidation(); });
            form.RunSelfTestStep(results, "Sensor collection", delegate { form.SelfTestSensorCollection(); });
            form.RunSelfTestStep(results, "PCIe slot summary wording", delegate { form.SelfTestPciSlotSummaryWording(); });
            form.RunSelfTestStep(results, "Wi-Fi BSS list bounds", delegate { form.SelfTestWifiBssListBounds(); });
            form.RunSelfTestStep(results, "Listening port details split", delegate { form.SelfTestListeningPortDetailsSplit(); });
            form.RunSelfTestStep(results, "USB SuperSpeedPlus speed decoding", delegate { form.SelfTestUsbSuperSpeedPlusSpeedDecoding(); });
            form.RunSelfTestStep(results, "Bluetooth and battery filtering", delegate { form.SelfTestBluetoothAndBatteryFiltering(); });
            form.RunSelfTestStep(results, "Category tree navigation", delegate { form.SelfTestCategoryNavigation(); });
            form.RunSelfTestStep(results, "Category speech modes", delegate { form.SelfTestCategorySpeechModes(); });
            form.RunSelfTestStep(results, "Expand and collapse commands", delegate { form.SelfTestExpandCollapse(); });
            form.RunSelfTestStep(results, "Reading tree expansion preference", delegate { form.SelfTestReadingTreeExpansionPreference(); });
            form.RunSelfTestStep(results, "Show/hide expansion preservation", delegate { form.SelfTestExpansionPreservation(); });
            form.RunSelfTestStep(results, "Tray tooltip modes", delegate { form.SelfTestTrayStatusText(); });
            form.RunSelfTestStep(results, "Byte unit formatting modes", delegate { form.SelfTestByteUnitFormattingModes(); });
            form.RunSelfTestStep(results, "Pending refresh coalescing", delegate { form.SelfTestPendingRefreshCoalescing(); });
            form.RunSelfTestStep(results, "Background hotkey refresh cadence", delegate { form.SelfTestBackgroundHotKeyRefreshCadence(); });
            form.RunSelfTestStep(results, "Formatted row cache clearing", delegate { form.SelfTestFormattedRowCacheClearing(); });
            form.RunSelfTestStep(results, "Fragile WMI row caches", delegate { form.SelfTestFragileWmiRowCaches(); });
            form.RunSelfTestStep(results, "Spoken hotkey mirror order", delegate { form.SelfTestSpokenHotKeyMirrorOrder(); });
            form.RunSelfTestStep(results, "Task row refresh cache", delegate { form.SelfTestTaskRowRefreshCache(); });
            form.RunSelfTestStep(results, "Process watch report", delegate { form.SelfTestProcessWatchReport(); });
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
        var transferPackage = BuildSettingsTransferPackage(new HashSet<string>(new[] { TransferTray }, StringComparer.OrdinalIgnoreCase));
        Require(transferPackage.MachineSettings == null || string.IsNullOrWhiteSpace(transferPackage.MachineSettings.CommunityStatsClientId), "Settings transfer exported the local community stats client ID.");
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
    }

    private void SelfTestInstalledAppRegistration(string outputFolder)
    {
        var testKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sensor Readout SelfTest " + Guid.NewGuid().ToString("N");
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
        }
        finally
        {
            UnregisterInstalledAppEntry(testKeyPath);
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
        var tcp = new List<IPEndPoint> { tcpEndpoint };
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
        Require(tcpEndpointText.StartsWith("localhost:12345", StringComparison.OrdinalIgnoreCase), "Loopback listening endpoint did not start with localhost and port.");
        Require(wildcardEndpointText.StartsWith("all IPv4 addresses:34567", StringComparison.OrdinalIgnoreCase), "Wildcard IPv4 listening endpoint did not use a friendly host label.");
        Require(udpDetails.ContainsKey("UDP listening port count"), "UDP listening details did not include UDP count.");
        Require(!udpDetails.ContainsKey("TCP listening port count"), "UDP listening details included TCP count.");
        Require(udpDetails.Values.Any(v => v.IndexOf("udp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "UDP listening details did not include UDP owner.");
        Require(!udpDetails.Values.Any(v => v.IndexOf("tcp-test.exe", StringComparison.OrdinalIgnoreCase) >= 0), "UDP listening details included TCP owner.");
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
        Require(!IsUsefulWindowsPowerMeterReading("Microsoft Power Meter Device", 0, 0), "Battery filtering accepted a zero-value raw Microsoft power meter.");
        Require(IsUsefulWindowsPowerMeterReading("Microsoft Power Meter Device", 0, 1), "Battery filtering rejected a non-zero raw Microsoft power meter.");
        Require(IsUsefulWindowsPowerMeterReading("AC adapter", 7, 0), "Battery filtering rejected a watt-based power meter.");
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
        try
        {
            settings.SpeakTrayHotKey = "";
            settings.TrayItemKeys = new List<string>();
            settings.SpokenHotKeys = new List<SpokenHotKeySetting>();
            settings.TrendLoggingEnabled = false;
            settings.Alarms = new List<AlarmSetting>();
            settings.FanCurves = new List<FanCurveSetting>();
            Require(!RequiresRealtimeBackgroundRefresh(), "Empty background configuration should not force realtime refresh.");

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

            var normalizationSettings = new AppSettings
            {
                SpokenHotKeys = new List<SpokenHotKeySetting>
                {
                    new SpokenHotKeySetting { Name = "New spoken hotkey", HotKey = "", ReadingKeys = new List<string>() },
                    new SpokenHotKeySetting { Name = "Intentional empty profile", HotKey = "", ReadingKeys = new List<string>() },
                    new SpokenHotKeySetting { Name = "Useful profile", HotKey = "Ctrl+Shift+F1", ReadingKeys = new List<string> { "self-test-reading" } }
                }
            };
            NormalizeSettings(normalizationSettings);
            Require(normalizationSettings.SpokenHotKeys.Count == 2, "Settings normalization did not prune the empty default spoken hotkey placeholder.");
            Require(!normalizationSettings.SpokenHotKeys.Any(p => string.Equals(p.Name, "New spoken hotkey", StringComparison.OrdinalIgnoreCase)), "Settings normalization kept an empty default spoken hotkey placeholder.");
        }
        finally
        {
            settings.SpeakTrayHotKey = previousSpeakTrayHotKey;
            settings.TrayItemKeys = previousTrayItemKeys;
            settings.SpokenHotKeys = previousSpokenHotKeys;
            settings.TrendLoggingEnabled = previousTrendLogging;
            settings.Alarms = previousAlarms;
            settings.FanCurves = previousFanCurves;
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

    private static object GetWindowsSettingsTargetForSelfTest(SensorRow row)
    {
        var method = typeof(SensorReadoutForm).GetMethod("GetRelatedWindowsSettingsTarget", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("GetRelatedWindowsSettingsTarget not found for self-test.");
        }

        try
        {
            return method.Invoke(null, new object[] { row });
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
            Require(html.IndexOf("<code>Tab</code>", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(manual) + " missing Tab guidance for moving from categories to readings.");
            Require(html.IndexOf("<code>Enter</code> / <code>Alt+Enter</code>", StringComparison.OrdinalIgnoreCase) < 0, Path.GetFileName(manual) + " still describes Alt+Enter as a Details shortcut.");
            Require(!Regex.IsMatch(html, @"(?i)(Enter\s+or\s+Alt\+Enter|Enter\s+oder\s+Alt\+Enter|Enter\s+ou\s+Alt\+Enter|Enter\s+o\s+Alt\+Enter)"), Path.GetFileName(manual) + " contains stale Enter/Alt+Enter Details wording.");
        }
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
