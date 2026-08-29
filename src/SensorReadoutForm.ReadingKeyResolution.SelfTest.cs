using System;
using System.Collections.Generic;

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
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TransferSpokenHotKeys });
            Require(settings.SpokenHotKeys.Count == 1 && settings.SpokenHotKeys[0].ReadingKeys.Count == 2, "Imported spoken hotkey did not retain both equivalent readings.");
            Require(settings.SpokenHotKeys[0].HotKey == "", "Imported spoken hotkey retained a global key assignment.");
            Require(settings.ReadingSpeechLabels.ContainsKey(RowSettingsKey(amdTemperature)) && settings.ReadingSpeechLabels[RowSettingsKey(amdTemperature)] == "Processor temperature", "Imported custom CPU speech label did not follow its equivalent reading.");
            Require(settings.ReadingSpeechLabels.ContainsKey(RowSettingsKey(remoteCRead)) && settings.ReadingSpeechLabels[RowSettingsKey(remoteCRead)] == "C read", "Imported custom drive speech label did not follow its stable drive reading.");
        }
        finally
        {
            SetLatestRows(originalRows);
            settings.SpokenHotKeys = originalProfiles;
            settings.ReadingSpeechLabels = originalLabels;
        }
    }
}
