using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private void SelfTestPortableReadingKeyResolution()
    {
        var originalRows = new List<SensorRow>(latestRows);
        var originalProfiles = settings.SpokenHotKeys == null ? new List<SpokenHotKeySetting>() : new List<SpokenHotKeySetting>(settings.SpokenHotKeys);
        var originalLabels = settings.ReadingSpeechLabels == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(settings.ReadingSpeechLabels, StringComparer.OrdinalIgnoreCase);
        try
        {
            var remoteCRead = new SensorRow { Type = "Performance", Hardware = "C: Win Drive", Name = "Read rate", Identifier = "logicaldisk/C:/read", DisplayValue = "4 MB/s" };
            var remoteCWrite = new SensorRow { Type = "Performance", Hardware = "C: Win Drive", Name = "Write rate", Identifier = "logicaldisk/C:/write", DisplayValue = "2 MB/s" };
            var remoteDRead = new SensorRow { Type = "Performance", Hardware = "D: Other Label", Name = "Read rate", Identifier = "logicaldisk/D:/read", DisplayValue = "8 MB/s" };
            var amdTemperature = new SensorRow { Type = "Temperature", Hardware = "AMD Ryzen 7 7840U", Name = "Core (Tctl/Tdie)", Identifier = "/amdcpu/0/temperature/2", DisplayValue = "48 C" };
            var gpuTemperature = new SensorRow { Type = "Temperature", Hardware = "AMD Radeon 780M Graphics", Name = "GPU Core", Identifier = "/gpu-amd/0/temperature/0", DisplayValue = "44 C" };
            var remoteWifiSignal = new SensorRow { Type = "Network", Hardware = "Realtek 8852CE WiFi 6E PCI-E NIC", Name = "Wi-Fi signal strength", Value = 82, DisplayValue = "82%" };
            SetLatestRows(new[] { remoteCRead, remoteCWrite, remoteDRead, amdTemperature, gpuTemperature, remoteWifiSignal });

            var localCReadKey = "Performance|C: Crunch|Read rate|logicaldisk/C:/read";
            var localCWriteKey = "Performance|C: Crunch|Write rate|logicaldisk/C:/write";
            var localDReadKey = "Performance|D: Next drive|Read rate|logicaldisk/D:/read";
            var intelTemperatureKey = "Temperature|Intel Core i7-1360P|CPU Package|/intelcpu/0/temperature/0";
            var intelWifiSignalKey = "Network|Intel(R) Wi-Fi 6E AX211 160MHz|Wi-Fi signal strength";
            Require(ReferenceEquals(ResolveReadingKeyAgainstRows(localCReadKey, latestRows), remoteCRead), "C: read rate did not follow its stable logical-disk identifier across a changed volume label.");
            Require(ReferenceEquals(ResolveReadingKeyAgainstRows(localCWriteKey, latestRows), remoteCWrite), "C: write rate did not follow its stable logical-disk identifier across a changed volume label.");
            Require(ReferenceEquals(ResolveReadingKeyAgainstRows(localDReadKey, latestRows), remoteDRead), "D: read rate did not remain distinct from C: while resolving a changed volume label.");
            Require(ReferenceEquals(ResolveReadingKeyAgainstRows(intelTemperatureKey, latestRows), amdTemperature), "Intel CPU package temperature did not resolve to the equivalent AMD primary CPU temperature.");
            Require(ReferenceEquals(ResolveReadingKeyAgainstRows(intelWifiSignalKey, latestRows), remoteWifiSignal), "Wi-Fi signal strength did not resolve across different adapter vendors.");
            Require(ResolveReadingKeyAgainstRows("Performance|C: Missing|Read rate|logicaldisk/C:/read", new[] { remoteDRead }) == null, "A missing C: reading incorrectly resolved to D:.");

            var internalBattery = new SensorRow { Type = "Battery", Hardware = "Laptop battery", Name = "Charge", Identifier = "battery/0/charge", DisplayValue = "71%" };
            var deviceBattery = new SensorRow { Type = "Battery", Hardware = "Wireless keyboard", Name = "Charge", Identifier = "device-battery/keyboard/charge", DisplayValue = "100%" };
            Require(ResolveReadingKeyAgainstRows("Battery|Old keyboard|Charge|device-battery/old-keyboard/charge", new[] { internalBattery }) == null, "A missing device battery incorrectly resolved to the internal battery.");
            Require(ResolveReadingKeyAgainstRows("Battery|Old laptop battery|Charge|battery/0/charge", new[] { deviceBattery }) == null, "A missing internal battery incorrectly resolved to an external device battery.");

            var secondCpuTemperature = new SensorRow { Type = "Temperature", Hardware = "AMD Ryzen socket 2", Name = "CPU Package", Identifier = "/amdcpu/1/temperature/0", DisplayValue = "46 C" };
            var firstCpuTemperature = new SensorRow { Type = "Temperature", Hardware = "AMD Ryzen socket 1", Name = "CPU Package", Identifier = "/amdcpu/0/temperature/0", DisplayValue = "45 C" };
            Require(ResolveReadingKeyAgainstRows(intelTemperatureKey, new[] { firstCpuTemperature, secondCpuTemperature }) == null, "Ambiguous multi-CPU temperature mapping did not fail closed.");

            settings.SpokenHotKeys = new List<SpokenHotKeySetting>();
            settings.ReadingSpeechLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var imported = new MachineAppSettings
            {
                SpokenHotKeys = new List<SpokenHotKeySetting>
                {
                    new SpokenHotKeySetting
                    {
                        Name = "Portable CPU and disk",
                        HotKey = "Ctrl+Shift+F8",
                        ReadingKeys = new List<string> { intelTemperatureKey, localCReadKey }
                    }
                },
                ReadingSpeechLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { intelTemperatureKey, "Processor temperature" },
                    { localCReadKey, "C read" }
                }
            };
            ApplySettingsTransferPackage(
                new SettingsTransferPackage { MachineSettings = imported },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TransferSpokenHotKeys },
                new SpokenHotKeyImport(settings, true, key => true));
            Require(settings.SpokenHotKeys.Count == 1 && settings.SpokenHotKeys[0].ReadingKeys.Count == 2, "Imported spoken hotkey did not retain both equivalent readings.");
            Require(settings.SpokenHotKeys[0].HotKey == "Ctrl+Shift+F8", "An available exported key was discarded during settings import.");
            Require(settings.ReadingSpeechLabels.ContainsKey(RowSettingsKey(amdTemperature)) && settings.ReadingSpeechLabels[RowSettingsKey(amdTemperature)] == "Processor temperature", "Imported custom CPU speech label did not follow its equivalent reading.");
            Require(settings.ReadingSpeechLabels.ContainsKey(RowSettingsKey(remoteCRead)) && settings.ReadingSpeechLabels[RowSettingsKey(remoteCRead)] == "C read", "Imported custom drive speech label did not follow its stable drive reading.");

            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TransferSpokenHotKeys };
            var exported = BuildSettingsTransferPackage(categories);
            Require(exported.MachineSettings.SpokenHotKeys[0].HotKey == "Ctrl+Shift+F8", "Export lost the restored shortcut.");
            ApplySettingsTransferPackage(exported, categories);
            Require(settings.SpokenHotKeys[0].HotKey == "" && settings.SpokenHotKeys[0].ReadingKeys.Count == 2,
                "Declining shortcuts must still import the readings.");
        }
        finally
        {
            SetLatestRows(originalRows);
            settings.SpokenHotKeys = originalProfiles;
            settings.ReadingSpeechLabels = originalLabels;
        }
    }

    private void SelfTestSpokenShortcutImportConflicts()
    {
        var targetSettings = new AppSettings
        {
            ShowHideHotKey = "Ctrl+Shift+F1",
            SpeakTrayHotKey = "Ctrl+Shift+F2",
            DiskActivityHotKey = "Ctrl+Shift+F3",
            SpokenHotKeys = new List<SpokenHotKeySetting> { new SpokenHotKeySetting { HotKey = "Ctrl+Shift+F5" } },
            FanProfiles = new List<FanProfileSetting> { new FanProfileSetting { HotKey = "Ctrl+Shift+F6" } }
        };
        var probed = new List<string>();
        var append = new SpokenHotKeyImport(targetSettings, false, key =>
        {
            probed.Add(key);
            return key != "Ctrl+Shift+F7";
        });
        foreach (var blocked in new[] { "Ctrl+Shift+F1", "Ctrl+Shift+F2", "Ctrl+Shift+F3", "Ctrl+Shift+F5", "Ctrl+Shift+F6", "Ctrl+Shift+Alt+F4" })
        {
            Require(append.Assign("Blocked", blocked, 0, true) == "", "Import did not reject local or unsafe shortcut: " + blocked);
        }
        Require(probed.Count == 0, "Locally reserved or invalid shortcuts should not be probed.");
        Require(append.Assign("Windows conflict", "Ctrl+Shift+F7", 1, true) == "" && probed.Count == 1,
            "A Windows registration conflict was not rejected.");
        Require(append.Assign("CPU", "Shift+Control+F8", 2, true) == "Ctrl+Shift+F8", "A free shortcut was not normalized and restored.");
        Require(append.Assign("Duplicate", "Ctrl+Shift+F8", 3, true) == "", "A later imported profile stole an earlier imported shortcut.");
        Require(append.Assign("No matching readings", "Ctrl+Shift+F9", 4, false) == "", "An empty profile reserved a shortcut.");
        Require(append.Assign("Beyond the registration limit", "Ctrl+Shift+F9", MaxGlobalHotKeyProfiles, true) == "", "An unregistrable profile reserved a shortcut.");
        Require(append.Assign("No shortcut", "", 4, true) == "", "An unassigned profile gained a shortcut.");
        Require(append.Summary().Contains("Windows conflict (Ctrl+Shift+F7)") && append.Summary().Contains("Duplicate (Ctrl+Shift+F8)"),
            "Import summary did not identify unavailable shortcuts.");
        var replace = new SpokenHotKeyImport(targetSettings, true, key => true);
        Require(replace.Assign("Replacement", "Ctrl+Shift+F5", 0, true) == "Ctrl+Shift+F5",
            "Replacing the spoken profile list blocked a shortcut released by that list.");

        var window = new NativeWindow();
        window.CreateHandle(new CreateParams());
        const int fixtureId = 1;
        var occupied = "";
        try
        {
            var modifiers = NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModAlt;
            for (var key = Keys.F13; key <= Keys.F23; key++)
            {
                if (NativeMethods.RegisterHotKey(window.Handle, fixtureId, modifiers, (uint)key))
                {
                    occupied = "Ctrl+Alt+Shift+" + key;
                    break;
                }
            }
            Require(occupied.Length > 0, "Windows did not provide a free shortcut for the collision test.");
            Require(!CanRegisterImportedHotKey(occupied), "The live Windows probe accepted an occupied shortcut.");
        }
        finally
        {
            NativeMethods.UnregisterHotKey(window.Handle, fixtureId);
            window.DestroyHandle();
        }

        Require(CanRegisterImportedHotKey(occupied) && CanRegisterImportedHotKey(occupied),
            "The live Windows probe did not release an available shortcut after checking it.");
    }

    private void SelfTestSpokenHotKeyExport()
    {
        const string reading = "Performance|System|Uptime";
        var profile = new SpokenHotKeySetting
        {
            Name = "System status",
            HotKey = "Ctrl+Shift+F8",
            SkipUnavailableReadings = true,
            ReadingKeys = new List<string> { reading }
        };
        var package = BuildSpokenHotKeyTransferPackage(new List<SpokenHotKeySetting> { profile },
            new Dictionary<string, string> { { reading, "My uptime" }, { "Unrelated label", "Do not export" } });
        var json = JsonConvert.SerializeObject(package);
        var saved = JsonConvert.DeserializeObject<SettingsTransferPackage>(json);
        Require(saved.SharedSettings == null && AvailableImportCategories(saved).SequenceEqual(new[] { TransferSpokenHotKeys }),
            "The Hotkeys export included categories other than spoken hotkey profiles.");
        Require(saved.MachineSettings.ReadingSpeechLabels.Count == 0, "Spoken labels were written into the tray label section.");
        var imported = ReadSpokenHotKeyTransfer(json);
        var restored = imported.SpokenHotKeys.Single();
        Require(restored.Name == profile.Name && restored.HotKey == profile.HotKey && restored.SkipUnavailableReadings &&
            restored.ReadingKeys.SequenceEqual(profile.ReadingKeys), "The Hotkeys export lost profile data.");
        Require(imported.ReadingSpeechLabels.Count == 1 && imported.ReadingSpeechLabels[reading] == "My uptime",
            "The Hotkeys export did not preserve just the labels used by its profiles.");
        Require(ReadSpokenHotKeyTransfer(JsonConvert.SerializeObject(imported)).SpokenHotKeys.Single().HotKey == profile.HotKey,
            "The Hotkeys import no longer reads legacy machine configurations.");
        Require(ReadSpokenHotKeyTransfer(JsonConvert.SerializeObject(new SettingsTransferPackage { MachineSettings = imported }))
            .ReadingSpeechLabels[reading] == "My uptime", "The Hotkeys import lost a legacy settings package label.");
        Require(PreferencesForm.SpokenHotKeysExportFileName("ExamplePC") == "ExamplePC Hotkeys.srsettings.json",
            "The dedicated export filename did not follow its own naming convention.");

        using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Hotkeys"))
        {
            var tabs = FindControls<TabControl>(preferences.Controls).First();
            var export = FindControls<ShortcutButton>(tabs.SelectedTab.Controls).Single(button => button.ShortcutKeys == Keys.E);
            string helpName;
            Require(export.ShortcutText == "Alt+E" && export.AccessibilityObject.KeyboardShortcut == "Alt+E" &&
                ControlHelpCatalog.Describe(export, out helpName).Contains("custom reading labels"),
                "The Hotkeys Export button lacks its accessible Alt+E binding or focused help.");
        }
    }
}
