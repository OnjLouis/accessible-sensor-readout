using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public static partial class Program
{
    private static void ApplyUpdateFromCommandLine(string[] args)
    {
        try
        {
            string zipUrl;
            string targetDir;
            string exePath;
            string tempBase;
            string pidText;
            TryGetOptionValue(args, "--update-url", out zipUrl);
            TryGetOptionValue(args, "--update-target", out targetDir);
            TryGetOptionValue(args, "--update-exe", out exePath);
            TryGetOptionValue(args, "--update-temp", out tempBase);
            TryGetOptionValue(args, "--update-wait-pid", out pidText);

            if (string.IsNullOrWhiteSpace(zipUrl) || string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("The updater was not given enough information to install the update.");
            }

            int processId;
            if (int.TryParse(pidText, out processId) && processId > 0)
            {
                WaitForProcessExit(processId);
            }

            ApplyUpdate(zipUrl, targetDir, exePath, string.IsNullOrWhiteSpace(tempBase) ? Path.GetTempPath() : tempBase, HasArg(args, "--update-no-restart"));
        }
        catch (Exception ex)
        {
            WriteUpdaterLog(args, ex);
            MessageBox.Show(
                "Sensor Readout update failed:" + Environment.NewLine + Environment.NewLine + ex.Message,
                "Sensor Readout updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ApplyUpdate(string zipUrl, string targetDir, string exePath, string tempBase, bool noRestart)
    {
        Directory.CreateDirectory(tempBase);
        var root = Path.Combine(tempBase, "SensorReadoutUpdate_" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(root, "update.zip");
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(stage);

        try
        {
            DownloadUpdateZip(zipUrl, zip);
            ZipFile.ExtractToDirectory(zip, stage);

            var source = FindUpdateSourceFolder(stage);
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException("The downloaded ZIP does not contain Sensor Readout.exe.");
            }

            Directory.CreateDirectory(targetDir);
            var backupRoot = Path.Combine(Path.Combine(targetDir, "Backups\\Updates"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var legacyBackups = Path.Combine(targetDir, "Config\\Update Backups");
            if (Directory.Exists(legacyBackups))
            {
                NewBackupZip(legacyBackups, backupRoot, "Legacy-Config-Update-Backups");
                TryDeleteDirectory(legacyBackups);
            }

            var previousLanguageHashes = ReadHashManifest(Path.Combine(Path.Combine(targetDir, "Data"), "BundledLanguageHashes.json"));
            var previousPlugInHashes = ReadHashManifest(Path.Combine(Path.Combine(targetDir, "Data"), "BundledPlugInHashes.json"));

            RemoveNestedDuplicateFolders(targetDir);
            foreach (var name in new[] { "Docs", "Langs", "Data" })
            {
                ReplaceShippedFolder(source, targetDir, name, backupRoot, previousLanguageHashes);
            }

            ReplacePlugInsFolder(source, targetDir, backupRoot, previousPlugInHashes);
            UpdateSoundsFolder(source, targetDir);

            var preservedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Config", "Logs", "Reports", "Backups", "Update Backups", "Update Temp",
                "Docs", "Langs", "Data", "Plug-Ins", "Sounds"
            };

            foreach (var path in Directory.GetFileSystemEntries(source))
            {
                var name = Path.GetFileName(path);
                if (Directory.Exists(path) && preservedFolders.Contains(name))
                {
                    continue;
                }

                var destination = Path.Combine(targetDir, name);
                if (Directory.Exists(path))
                {
                    if (Directory.Exists(destination))
                    {
                        TryDeleteDirectory(destination);
                    }

                    CopyDirectory(path, destination);
                }
                else
                {
                    File.Copy(path, destination, true);
                }
            }

            RemoveNestedDuplicateFolders(targetDir);
            RemoveEmptyDirectory(backupRoot);
            TryDeleteFile(Path.Combine(targetDir, "README.md"));
            TryDeleteFile(Path.Combine(targetDir, "nvdaControllerClient.dll"));
            TryDeleteFile(Path.Combine(targetDir, "nvdaControllerClient.LICENSE.txt"));
            DeleteMarkdownFiles(Path.Combine(targetDir, "Docs"));
            DeleteMarkdownFiles(Path.Combine(targetDir, "docs"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        if (!noRestart)
        {
            TryRestartUpdatedApp(exePath, targetDir);
        }
    }

    private static void TryRestartUpdatedApp(string exePath, string targetDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                throw new FileNotFoundException("The updated Sensor Readout executable could not be found.", exePath ?? "");
            }

            var workingDirectory = Directory.Exists(targetDir) ? targetDir : Path.GetDirectoryName(exePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WriteUpdaterLog(null, ex);
            MessageBox.Show(
                "Sensor Readout was updated, but it could not be restarted automatically." +
                Environment.NewLine +
                Environment.NewLine +
                "Please start Sensor Readout from its installed folder or shortcut." +
                Environment.NewLine +
                Environment.NewLine +
                ex.Message,
                "Sensor Readout updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private static void DownloadUpdateZip(string zipUrl, string destination)
    {
        try
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol |
                SecurityProtocolType.Tls |
                (SecurityProtocolType)768 |
                (SecurityProtocolType)3072;
        }
        catch
        {
        }

        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.UserAgent] = "Sensor Readout updater";
            client.DownloadFile(zipUrl, destination);
        }
    }

    private static string FindUpdateSourceFolder(string stage)
    {
        var direct = Path.Combine(stage, "Sensor Readout.exe");
        if (File.Exists(direct))
        {
            return stage;
        }

        var candidates = Directory.GetFiles(stage, "Sensor Readout.exe", SearchOption.AllDirectories);
        return candidates.Length == 0 ? "" : Path.GetDirectoryName(candidates[0]);
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                process.WaitForExit(30000);
            }
        }
        catch
        {
        }
    }

    private static void ReplaceShippedFolder(string sourceRoot, string targetRoot, string name, string backupRoot, Dictionary<string, string> previousLanguageHashes)
    {
        var incoming = Path.Combine(sourceRoot, name);
        if (!Directory.Exists(incoming))
        {
            return;
        }

        var existing = Path.Combine(targetRoot, name);
        if (Directory.Exists(existing))
        {
            if (string.Equals(name, "Langs", StringComparison.OrdinalIgnoreCase))
            {
                BackupCustomLanguages(existing, incoming, sourceRoot, backupRoot, previousLanguageHashes);
            }

            TryDeleteDirectory(existing);
        }

        CopyDirectory(incoming, existing);
    }

    private static void BackupCustomLanguages(string existingLangs, string incomingLangs, string sourceRoot, string backupRoot, Dictionary<string, string> previousLanguageHashes)
    {
        if (previousLanguageHashes == null)
        {
            previousLanguageHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var incomingLanguageHashes = ReadHashManifest(Path.Combine(Path.Combine(sourceRoot, "Data"), "BundledLanguageHashes.json"));
        if (incomingLanguageHashes.Count == 0)
        {
            incomingLanguageHashes = GetHashMap(incomingLangs);
        }

        var customRoot = Path.Combine(Path.GetTempPath(), "SensorReadoutCustomLangs_" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(existingLangs, "*", SearchOption.AllDirectories))
        {
            var relative = RelativePath(existingLangs, file);
            var currentHash = GetFileSha256(file);
            var previousHash = previousLanguageHashes.ContainsKey(relative) ? previousLanguageHashes[relative] : "";
            var incomingHash = incomingLanguageHashes.ContainsKey(relative) ? incomingLanguageHashes[relative] : "";
            var matchesPreviousBundle = !string.IsNullOrWhiteSpace(previousHash) && string.Equals(currentHash, previousHash, StringComparison.OrdinalIgnoreCase);
            var matchesIncomingBundle = !string.IsNullOrWhiteSpace(incomingHash) && string.Equals(currentHash, incomingHash, StringComparison.OrdinalIgnoreCase);
            if (matchesPreviousBundle || matchesIncomingBundle)
            {
                continue;
            }

            var destination = Path.Combine(customRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
        }

        if (Directory.Exists(customRoot))
        {
            NewBackupZip(customRoot, backupRoot, "Custom-Langs");
            TryDeleteDirectory(customRoot);
        }
    }

    private static void ReplacePlugInsFolder(string sourceRoot, string targetRoot, string backupRoot, Dictionary<string, string> previousPlugInHashes)
    {
        var incoming = Path.Combine(sourceRoot, "Plug-Ins");
        if (!Directory.Exists(incoming))
        {
            return;
        }

        var existing = Path.Combine(targetRoot, "Plug-Ins");
        Directory.CreateDirectory(existing);
        BackupCustomPlugInFiles(existing, incoming, sourceRoot, backupRoot, previousPlugInHashes);

        foreach (var incomingItem in Directory.GetFileSystemEntries(incoming))
        {
            var name = Path.GetFileName(incomingItem);
            var oldPath = Path.Combine(existing, name);
            if (Directory.Exists(oldPath))
            {
                TryDeleteDirectory(oldPath);
            }
            else
            {
                TryDeleteFile(oldPath);
            }
        }

        foreach (var incomingItem in Directory.GetFileSystemEntries(incoming))
        {
            var destination = Path.Combine(existing, Path.GetFileName(incomingItem));
            if (Directory.Exists(incomingItem))
            {
                CopyDirectory(incomingItem, destination);
            }
            else
            {
                File.Copy(incomingItem, destination, true);
            }
        }
    }

    private static void BackupCustomPlugInFiles(string existingPlugIns, string incomingPlugIns, string sourceRoot, string backupRoot, Dictionary<string, string> previousPlugInHashes)
    {
        if (previousPlugInHashes == null)
        {
            previousPlugInHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var incomingPlugInHashes = ReadHashManifest(Path.Combine(Path.Combine(sourceRoot, "Data"), "BundledPlugInHashes.json"));
        if (incomingPlugInHashes.Count == 0)
        {
            incomingPlugInHashes = GetHashMap(incomingPlugIns);
        }

        var incomingTopNames = new HashSet<string>(
            Directory.GetFileSystemEntries(incomingPlugIns).Select(Path.GetFileName),
            StringComparer.OrdinalIgnoreCase);
        var customRoot = Path.Combine(Path.GetTempPath(), "SensorReadoutCustomPlugIns_" + Guid.NewGuid().ToString("N"));

        foreach (var file in Directory.GetFiles(existingPlugIns, "*", SearchOption.AllDirectories))
        {
            var relative = RelativePath(existingPlugIns, file);
            var parts = relative.Split(new[] { '\\', '/' }, 2);
            if (parts.Length == 0 || !incomingTopNames.Contains(parts[0]))
            {
                continue;
            }

            var currentHash = GetFileSha256(file);
            var previousHash = previousPlugInHashes.ContainsKey(relative) ? previousPlugInHashes[relative] : "";
            var incomingHash = incomingPlugInHashes.ContainsKey(relative) ? incomingPlugInHashes[relative] : "";
            var matchesPreviousBundle = !string.IsNullOrWhiteSpace(previousHash) && string.Equals(currentHash, previousHash, StringComparison.OrdinalIgnoreCase);
            var matchesIncomingBundle = !string.IsNullOrWhiteSpace(incomingHash) && string.Equals(currentHash, incomingHash, StringComparison.OrdinalIgnoreCase);
            if (matchesPreviousBundle || matchesIncomingBundle)
            {
                continue;
            }

            var destination = Path.Combine(customRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
        }

        if (Directory.Exists(customRoot))
        {
            NewBackupZip(customRoot, backupRoot, "Custom-Bundled-Plug-Ins");
            TryDeleteDirectory(customRoot);
        }
    }

    private static void UpdateSoundsFolder(string sourceRoot, string targetRoot)
    {
        var incoming = Path.Combine(sourceRoot, "Sounds");
        if (!Directory.Exists(incoming))
        {
            return;
        }

        var existing = Path.Combine(targetRoot, "Sounds");
        Directory.CreateDirectory(existing);
        foreach (var file in Directory.GetFiles(incoming))
        {
            File.Copy(file, Path.Combine(existing, Path.GetFileName(file)), true);
        }
    }

    private static Dictionary<string, string> ReadHashManifest(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        try
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, "\"(?<key>(?:\\\\.|[^\"])*)\"\\s*:\\s*\"(?<hash>[A-Fa-f0-9]{64})\""))
            {
                map[UnescapeJsonString(match.Groups["key"].Value)] = match.Groups["hash"].Value;
            }
        }
        catch
        {
        }

        return map;
    }

    private static Dictionary<string, string> GetHashMap(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return map;
        }

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            map[RelativePath(root, file)] = GetFileSha256(file);
        }

        return map;
    }

    private static string GetFileSha256(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }

    private static void NewBackupZip(string path, string backupRoot, string name)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(backupRoot);
        var safeName = Regex.Replace(name ?? "Backup", "[\\\\/:*?\"<>|]", "_");
        var zipPath = Path.Combine(backupRoot, safeName + ".zip");
        if (File.Exists(zipPath))
        {
            zipPath = Path.Combine(backupRoot, safeName + "-" + Guid.NewGuid().ToString("N") + ".zip");
        }

        ZipFile.CreateFromDirectory(path, zipPath);
    }

    private static void RemoveNestedDuplicateFolders(string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
        {
            return;
        }

        foreach (var folder in Directory.GetDirectories(rootFolder, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length).ToList())
        {
            var nested = Path.Combine(folder, Path.GetFileName(folder));
            if (Directory.Exists(nested))
            {
                TryDeleteDirectory(nested);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, RelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, RelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }
    }

    private static string RelativePath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(fullRoot.Length) : Path.GetFileName(path);
    }

    private static string UnescapeJsonString(string value)
    {
        return (value ?? "")
            .Replace("\\\\", "\\")
            .Replace("\\\"", "\"")
            .Replace("\\/", "/");
    }

    private static void DeleteMarkdownFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(folder, "*.md"))
        {
            TryDeleteFile(file);
        }
    }

    private static void RemoveEmptyDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.GetFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void WriteUpdaterLog(string[] args, Exception exception)
    {
        try
        {
            string targetDir;
            if (args == null || !TryGetOptionValue(args, "--update-target", out targetDir) || string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = AppDomain.CurrentDomain.BaseDirectory;
            }

            var logRoot = Path.Combine(targetDir, "Logs");
            Directory.CreateDirectory(logRoot);
            var path = Path.Combine(logRoot, "Updater.log");
            File.AppendAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " Sensor Readout updater error" +
                Environment.NewLine +
                (exception == null ? "(No exception object.)" : exception.ToString()) +
                Environment.NewLine +
                Environment.NewLine);
        }
        catch
        {
        }
    }
}
