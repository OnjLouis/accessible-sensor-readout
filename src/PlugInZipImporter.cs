using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

internal static class PlugInZipImporter
{
    public static bool PromptAndImport(IWin32Window owner, AppSettings settings)
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP");
            dialog.Filter = "Sensor Readout Plug-In ZIP (*.zip)|*.zip|All files (*.*)|*.*";
            dialog.CheckFileExists = true;
            dialog.Multiselect = false;
            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                return false;
            }

            return Import(owner, settings, dialog.FileName);
        }
    }

    public static bool Import(IWin32Window owner, AppSettings settings, string zipPath)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "SensorReadoutPlugInImport_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempFolder);
            ExtractZipSafely(zipPath, tempFolder);
            var manifests = Directory.GetFiles(tempFolder, "plugin.json", SearchOption.AllDirectories);
            if (manifests.Length == 0)
            {
                MessageBox.Show(owner, SensorReadoutForm.L("message.pluginZipMissingManifest", "This ZIP does not contain a plugin.json file."), SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (manifests.Length > 1)
            {
                MessageBox.Show(owner, SensorReadoutForm.L("message.pluginZipMultipleManifests", "This ZIP contains more than one plugin.json file. Import one Plug-In at a time."), SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var manifestPath = manifests[0];
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var plugInId = SafeJsonText(manifest, "id");
            if (string.IsNullOrWhiteSpace(plugInId))
            {
                MessageBox.Show(owner, SensorReadoutForm.L("message.pluginZipMissingId", "The Plug-In manifest must include a stable id."), SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var plugInName = SafeJsonText(manifest, "name");
            var plugInFolderName = SanitizeFolderName(plugInId);
            var sourceFolder = Path.GetDirectoryName(manifestPath);
            var targetRoot = SensorReadoutForm.GetPlugInsFolderPath();
            Directory.CreateDirectory(targetRoot);
            var targetFolder = FindExistingPlugInFolderById(targetRoot, plugInId);
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                targetFolder = Path.Combine(targetRoot, plugInFolderName);
            }
            else
            {
                plugInFolderName = Path.GetFileName(targetFolder);
            }

            if (Directory.Exists(targetFolder))
            {
                var replace = MessageBox.Show(
                    owner,
                    string.Format(SensorReadoutForm.L("message.pluginFolderExists", "A Plug-In folder named {0} already exists. Replace it?"), plugInFolderName),
                    SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (replace != DialogResult.Yes)
                {
                    return false;
                }

                Directory.Delete(targetFolder, true);
            }

            CopyDirectory(sourceFolder, targetFolder);
            if (settings.PlugInsEnabled == null)
            {
                settings.PlugInsEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            settings.PlugInsEnabled[plugInId] = false;
            SensorReadoutForm.SaveSettings(settings);
            MessageBox.Show(owner, string.Format(SensorReadoutForm.L("message.pluginImportedDisabled", "Imported {0}. It is disabled until you check it."), string.IsNullOrWhiteSpace(plugInName) ? plugInId : plugInName), SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, string.Format(SensorReadoutForm.L("message.pluginImportFailed", "Could not import Plug-In ZIP: {0}"), ex.Message), SensorReadoutForm.L("ui.Import Plug-In ZIP", "Import Plug-In ZIP"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder))
                {
                    Directory.Delete(tempFolder, true);
                }
            }
            catch
            {
            }
        }
    }

    private static string FindExistingPlugInFolderById(string targetRoot, string plugInId)
    {
        if (string.IsNullOrWhiteSpace(targetRoot) || string.IsNullOrWhiteSpace(plugInId) || !Directory.Exists(targetRoot))
        {
            return "";
        }

        foreach (var manifestPath in Directory.GetFiles(targetRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                if (string.Equals(SafeJsonText(manifest, "id"), plugInId, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(manifestPath);
                }
            }
            catch
            {
            }
        }

        return "";
    }

    private static void ExtractZipSafely(string zipPath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination);
        if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            destinationRoot += Path.DirectorySeparatorChar;
        }

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The ZIP contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.ExtractToFile(targetPath, true);
            }
        }
    }

    private static void CopyDirectory(string sourceFolder, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        foreach (var directory in Directory.GetDirectories(sourceFolder, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetFolder, directory.Substring(sourceFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        foreach (var file in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = file.Substring(sourceFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetPath = Path.Combine(targetFolder, relativePath);
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(file, targetPath, true);
        }
    }

    private static string SafeJsonText(JObject obj, string key)
    {
        return obj == null || obj[key] == null ? "" : Convert.ToString(obj[key]).Trim();
    }

    private static string SanitizeFolderName(string value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "ImportedPlugIn" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        safe = safe.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(safe) ? "ImportedPlugIn" : safe;
    }
}
