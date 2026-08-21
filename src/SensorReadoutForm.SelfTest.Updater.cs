using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void SelfTestUpdaterLauncherDependencies(string outputFolder)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceExe = Application.ExecutablePath;
        var sourceJson = Path.Combine(appDir, "Resources", "Newtonsoft.Json.dll");
        Require(File.Exists(sourceJson), "The built application is missing the updater's JSON dependency.");

        var updaterRoot = Path.Combine(outputFolder, "self-test-updater-launcher");
        if (Directory.Exists(updaterRoot))
        {
            Directory.Delete(updaterRoot, true);
        }

        var updaterExe = Program.PrepareUpdaterLauncher(appDir, sourceExe, updaterRoot);
        var updaterJson = Path.Combine(updaterRoot, "Resources", "Newtonsoft.Json.dll");
        Require(File.Exists(updaterExe), "The temporary updater executable was not prepared.");
        Require(File.Exists(updaterJson), "The temporary updater omitted its JSON dependency.");
        Require(ComputeSha256ForSelfTest(sourceExe) == ComputeSha256ForSelfTest(updaterExe),
            "The temporary updater executable differs from the running Sensor Readout executable.");
        Require(ComputeSha256ForSelfTest(sourceJson) == ComputeSha256ForSelfTest(updaterJson),
            "The temporary updater JSON dependency differs from the shipped copy.");

        var sourceConfig = sourceExe + ".config";
        if (File.Exists(sourceConfig))
        {
            var updaterConfig = updaterExe + ".config";
            Require(File.Exists(updaterConfig), "The temporary updater omitted its application configuration.");
            Require(ComputeSha256ForSelfTest(sourceConfig) == ComputeSha256ForSelfTest(updaterConfig),
                "The temporary updater application configuration differs from the shipped copy.");
        }
    }

    private void SelfTestBundledPlugInManifestRepair(string outputFolder)
    {
        var sourcePlugIns = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plug-Ins");
        var asusDll = Path.Combine(sourcePlugIns, "AsusRog", "AsusRogPlugIn.dll");
        var corsairDll = Path.Combine(sourcePlugIns, "Corsair", "CorsairPlugIn.dll");
        var dellDll = Path.Combine(sourcePlugIns, "DellLatitude", "DellLatitudePlugIn.dll");
        if (!File.Exists(asusDll) || !File.Exists(corsairDll) || !File.Exists(dellDll))
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
        CopySelfTestPlugInDll(corsairDll, Path.Combine(tempPlugIns, "Corsair", "CorsairPlugIn.dll"));
        CopySelfTestPlugInDll(dellDll, Path.Combine(tempPlugIns, "DellLatitude", "DellLatitudePlugIn.dll"));
        var customDll = Path.Combine(tempPlugIns, "CommunityPlugIn", "CommunityPlugIn.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(customDll));
        File.WriteAllText(customDll, "custom plug-in placeholder");

        var manifestPath = Path.Combine(tempData, "BundledPlugInHashes.json");
        var asusRelative = @"AsusRog\AsusRogPlugIn.dll";
        var corsairRelative = @"Corsair\CorsairPlugIn.dll";
        var dellRelative = @"DellLatitude\DellLatitudePlugIn.dll";
        var oldHash = new string('0', 64);
        var asusHash = ComputeSha256ForSelfTest(Path.Combine(tempPlugIns, asusRelative));
        var corsairHash = ComputeSha256ForSelfTest(Path.Combine(tempPlugIns, corsairRelative));
        var dellHash = ComputeSha256ForSelfTest(Path.Combine(tempPlugIns, dellRelative));

        WriteSelfTestManifest(manifestPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { asusRelative, oldHash },
            { corsairRelative, corsairHash },
            { dellRelative, dellHash }
        });
        Require(!Program.RepairBundledPlugInHashManifestForTest(tempRoot), "Manifest repair ran when only one bundled DLL differed; this could hide user edits.");
        var partialManifest = File.ReadAllText(manifestPath);
        Require(partialManifest.IndexOf(oldHash, StringComparison.OrdinalIgnoreCase) >= 0, "Partial mismatch manifest was unexpectedly changed.");

        WriteSelfTestManifest(manifestPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { asusRelative, oldHash },
            { corsairRelative, oldHash },
            { dellRelative, oldHash }
        });
        Require(Program.RepairBundledPlugInHashManifestForTest(tempRoot), "Manifest repair did not run for legacy bundled plug-in hashes.");
        var repairedManifest = File.ReadAllText(manifestPath);
        Require(repairedManifest.IndexOf(asusHash, StringComparison.OrdinalIgnoreCase) >= 0, "Repaired manifest missing current Asus plug-in hash.");
        Require(repairedManifest.IndexOf(corsairHash, StringComparison.OrdinalIgnoreCase) >= 0, "Repaired manifest missing current Corsair plug-in hash.");
        Require(repairedManifest.IndexOf(dellHash, StringComparison.OrdinalIgnoreCase) >= 0, "Repaired manifest missing current Dell plug-in hash.");
        Require(repairedManifest.IndexOf("CommunityPlugIn", StringComparison.OrdinalIgnoreCase) < 0, "Repaired manifest incorrectly included a third-party plug-in folder.");
    }

    private void SelfTestBundledPlugInRuntimeStateUpdate(string outputFolder)
    {
        var tempRoot = Path.Combine(outputFolder, "self-test-plugin-runtime-state");
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }

        var sourceRoot = Path.Combine(tempRoot, "source");
        var targetRoot = Path.Combine(tempRoot, "target");
        var backupRoot = Path.Combine(tempRoot, "backups");
        var incomingCorsair = Path.Combine(sourceRoot, "Plug-Ins", "Corsair");
        var existingCorsair = Path.Combine(targetRoot, "Plug-Ins", "Corsair");
        Directory.CreateDirectory(incomingCorsair);
        Directory.CreateDirectory(existingCorsair);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Data"));

        var bundledRelative = @"Corsair\plugin.json";
        var markerRelative = @"Corsair\corsair-hub-test123.controlled";
        var customRelative = @"Corsair\user-notes.txt";
        var bundledExisting = Path.Combine(targetRoot, "Plug-Ins", bundledRelative);
        var bundledIncoming = Path.Combine(sourceRoot, "Plug-Ins", bundledRelative);
        var markerExisting = Path.Combine(targetRoot, "Plug-Ins", markerRelative);
        var customExisting = Path.Combine(targetRoot, "Plug-Ins", customRelative);
        File.WriteAllText(bundledExisting, "old bundled file");
        File.WriteAllText(bundledIncoming, "new bundled file");
        File.WriteAllText(markerExisting, "app-owned control state");
        File.WriteAllText(customExisting, "user-owned file");

        var previousHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { bundledRelative, ComputeSha256ForSelfTest(bundledExisting) }
        };
        WriteSelfTestManifest(Path.Combine(sourceRoot, "Data", "BundledPlugInHashes.json"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { bundledRelative, ComputeSha256ForSelfTest(bundledIncoming) }
            });

        Program.ReplacePlugInsFolderForTest(sourceRoot, targetRoot, backupRoot, previousHashes);

        Require(File.Exists(markerExisting) && File.ReadAllText(markerExisting) == "app-owned control state",
            "The updater did not preserve Corsair fan-control runtime state.");
        Require(File.ReadAllText(bundledExisting) == "new bundled file",
            "The updater did not replace the bundled Corsair plug-in file.");
        Require(!File.Exists(customExisting), "The updater left a custom bundled plug-in file active.");
        var backupZips = Directory.Exists(backupRoot)
            ? Directory.GetFiles(backupRoot, "Custom-Bundled-Plug-Ins*.zip", SearchOption.AllDirectories)
            : new string[0];
        Require(backupZips.Length == 1, "The updater did not create exactly one custom plug-in backup.");
        using (var archive = ZipFile.OpenRead(backupZips[0]))
        {
            Require(archive.Entries.Any(entry => string.Equals(entry.FullName.Replace('/', '\\'), customRelative, StringComparison.OrdinalIgnoreCase)),
                "The updater did not retain the genuine custom plug-in file in its backup.");
            Require(!archive.Entries.Any(entry => string.Equals(entry.FullName.Replace('/', '\\'), markerRelative, StringComparison.OrdinalIgnoreCase)),
                "The updater incorrectly backed up Corsair runtime state as a user-modified plug-in file.");
        }
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
}
