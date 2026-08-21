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
    private const long MaximumUpdateDownloadBytes = 512L * 1024L * 1024L;
    private const long MaximumUpdateExtractedBytes = 2L * 1024L * 1024L * 1024L;
    private const int MaximumUpdateArchiveEntries = 20000;

    private static void ApplyUpdateFromCommandLine(string[] args)
    {
        try
        {
            string zipUrl;
            string zipPath;
            string targetDir;
            string exePath;
            string tempBase;
            string pidText;
            string expectedSha256;
            TryGetOptionValue(args, "--update-url", out zipUrl);
            TryGetOptionValue(args, "--update-zip", out zipPath);
            TryGetOptionValue(args, "--update-target", out targetDir);
            TryGetOptionValue(args, "--update-exe", out exePath);
            TryGetOptionValue(args, "--update-temp", out tempBase);
            TryGetOptionValue(args, "--update-wait-pid", out pidText);
            TryGetOptionValue(args, "--update-sha256", out expectedSha256);

            if ((string.IsNullOrWhiteSpace(zipUrl) && string.IsNullOrWhiteSpace(zipPath)) ||
                (!string.IsNullOrWhiteSpace(zipUrl) && !string.IsNullOrWhiteSpace(zipPath)) ||
                string.IsNullOrWhiteSpace(targetDir) ||
                string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("The updater was not given enough information to install the update.");
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && !File.Exists(zipPath))
            {
                throw new FileNotFoundException("The local update ZIP could not be found.", zipPath);
            }

            if (!string.IsNullOrWhiteSpace(zipUrl) && NormalizeExpectedSha256(expectedSha256) == null)
            {
                throw new InvalidOperationException("The online update did not include a valid SHA-256 digest.");
            }

            WriteUpdateHistory(targetDir, "Update command received. Source=" + (string.IsNullOrWhiteSpace(zipPath) ? "online" : "local ZIP") + "; noRestart=" + HasArg(args, "--update-no-restart") + ".");
            int processId;
            if (int.TryParse(pidText, out processId) && processId > 0)
            {
                WriteUpdateHistory(targetDir, "Waiting for Sensor Readout process " + processId + " to exit.");
                if (!WaitForProcessExit(processId, 30000))
                {
                    throw new InvalidOperationException("Sensor Readout did not close within 30 seconds. The update was not started.");
                }
            }

            ApplyUpdate(zipUrl, zipPath, expectedSha256, targetDir, exePath, string.IsNullOrWhiteSpace(tempBase) ? Path.GetTempPath() : tempBase, HasArg(args, "--update-no-restart"));
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            WriteUpdateHistory(args, "ERROR: " + ex.Message);
            WriteUpdaterLog(args, ex);
            if (!HasArg(args, "--update-no-ui"))
            {
                MessageBox.Show(
                    "Sensor Readout update failed:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Sensor Readout updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private static void ApplyUpdate(string zipUrl, string zipPath, string expectedSha256, string targetDir, string exePath, string tempBase, bool noRestart)
    {
        Directory.CreateDirectory(tempBase);
        var root = Path.Combine(tempBase, "SensorReadoutUpdate_" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(root, "update.zip");
        var stage = Path.Combine(root, "stage");
        var rollback = Path.Combine(root, "rollback");
        var rollbackReady = false;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(stage);

        try
        {
            if (!string.IsNullOrWhiteSpace(zipPath))
            {
                WriteUpdateHistory(targetDir, "Copying local update ZIP into staging folder.");
                File.Copy(zipPath, zip, true);
            }
            else
            {
                WriteUpdateHistory(targetDir, "Downloading update ZIP.");
                DownloadUpdateZip(zipUrl, zip);
            }

            VerifyUpdateSha256(zip, expectedSha256, !string.IsNullOrWhiteSpace(zipUrl));

            WriteUpdateHistory(targetDir, "Extracting update ZIP.");
            SafeExtractUpdateArchive(zip, stage);

            var source = FindUpdateSourceFolder(stage);
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException("The update ZIP does not contain Sensor Readout.exe.");
            }

            WriteUpdateHistory(targetDir, "Verifying Sensor Readout update signature and packaged files.");
            UpdatePackageSignature.VerifyAndRemove(source);

            WriteUpdateHistory(targetDir, "Update source located. Applying files.");
            Directory.CreateDirectory(targetDir);
            CreateUpdateRollback(targetDir, rollback);
            rollbackReady = true;
            var backupRoot = Path.Combine(Path.Combine(targetDir, "Backups\\Updates"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var legacyBackups = Path.Combine(targetDir, "Config\\Update Backups");
            if (Directory.Exists(legacyBackups))
            {
                WriteUpdateHistory(targetDir, "Moving legacy Config\\Update Backups into top-level Backups.");
                NewBackupZip(legacyBackups, backupRoot, "Legacy-Config-Update-Backups");
                TryDeleteDirectory(legacyBackups);
            }

            CleanupObsoleteRootUpdateFolders(targetDir, backupRoot);

            var previousLanguageHashes = ReadHashManifest(Path.Combine(Path.Combine(targetDir, "Data"), "BundledLanguageHashes.json"));
            var previousPlugInHashes = ReadHashManifest(Path.Combine(Path.Combine(targetDir, "Data"), "BundledPlugInHashes.json"));

            RemoveNestedDuplicateFolders(targetDir);
            foreach (var name in new[] { "Docs", "Langs", "Data" })
            {
                WriteUpdateHistory(targetDir, "Replacing shipped folder: " + name + ".");
                ReplaceShippedFolder(source, targetDir, name, backupRoot, previousLanguageHashes);
            }

            WriteUpdateHistory(targetDir, "Replacing shipped plug-ins.");
            ReplacePlugInsFolder(source, targetDir, backupRoot, previousPlugInHashes);
            WriteUpdateHistory(targetDir, "Updating shipped sounds.");
            UpdateSoundsFolder(source, targetDir);

            var preservedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Config", "Logs", "Reports", "Backups",
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
                        DeleteDirectoryRequired(destination);
                    }

                    CopyDirectory(path, destination);
                }
                else
                {
                    File.Copy(path, destination, true);
                }
            }

            WriteUpdateHistory(targetDir, "Running post-update cleanup.");
            RemoveNestedDuplicateFolders(targetDir);
            RemoveEmptyDirectory(backupRoot);
            CleanupObsoleteBundledPlugInBackups(targetDir);
            CleanupObsoleteShippedFolderBackups(targetDir);
            CleanupEmptyBackupFolders(targetDir);
            DeleteFileRequired(Path.Combine(targetDir, "README.md"));
            DeleteObsoleteRootFiles(targetDir);
            DeleteMarkdownFiles(Path.Combine(targetDir, "Docs"));
            DeleteMarkdownFiles(Path.Combine(targetDir, "docs"));
        }
        catch (Exception updateError)
        {
            if (rollbackReady)
            {
                try
                {
                    WriteUpdateHistory(targetDir, "Update failed during replacement. Restoring the previous program files.");
                    RestoreUpdateRollback(targetDir, rollback);
                    WriteUpdateHistory(targetDir, "Previous program files restored successfully.");
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The update failed and Sensor Readout could not completely restore the previous program files.",
                        updateError,
                        rollbackError);
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        if (!noRestart)
        {
            WriteUpdateHistory(targetDir, "Update applied. Restarting Sensor Readout.");
            TryRestartUpdatedApp(exePath, targetDir);
        }
        else
        {
            WriteUpdateHistory(targetDir, "Update applied. Restart skipped by updater argument.");
        }
    }

    private static void CleanupObsoleteRootUpdateFolders(string targetDir, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        var rootUpdateBackups = Path.Combine(targetDir, "Update Backups");
        if (Directory.Exists(rootUpdateBackups))
        {
            var destination = string.IsNullOrWhiteSpace(backupRoot)
                ? Path.Combine(Path.Combine(targetDir, "Backups\\Updates"), DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                : backupRoot;
            NewBackupZip(rootUpdateBackups, destination, "Legacy-Root-Update-Backups");
            TryDeleteDirectory(rootUpdateBackups);
        }

        TryDeleteDirectory(Path.Combine(targetDir, "Update Temp"));
    }

    private static void DeleteObsoleteRootFiles(string targetDir)
    {
        foreach (var fileName in new[]
        {
            "BlackSharp.Core.dll",
            "DiskInfoToolkit.dll",
            "HidSharp.dll",
            "Install-Prerequisites.cmd",
            "Install-Prerequisites.ps1",
            "LibreHardwareMonitorLib.dll",
            "Newtonsoft.Json.dll",
            "nvdaControllerClient.dll",
            "nvdaControllerClient.LICENSE.txt",
            "nvdaControllerClient64.dll",
            "prism.dll",
            "Prism.LICENSE.txt",
            "RAMSPDToolkit-NDD.dll",
            "SAAPI64.dll",
            "SensorReadout.PluginSdk.dll",
            "System.Buffers.dll",
            "System.Memory.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "Tolk.dll",
            "Tolk.NVDA-LICENSE.txt",
            UpdatePackageSignature.ManifestFileName
        })
        {
            DeleteFileRequired(Path.Combine(targetDir, fileName));
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

        Uri updateUri;
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out updateUri) || !IsAllowedUpdateDownloadUri(updateUri))
        {
            throw new InvalidOperationException("The update download address is not a permitted HTTPS or local test address.");
        }

        var request = (HttpWebRequest)WebRequest.Create(updateUri);
        request.Method = "GET";
        request.UserAgent = "Sensor Readout updater";
        request.Accept = "application/octet-stream";
        request.AllowAutoRedirect = true;
        request.MaximumAutomaticRedirections = 5;
        request.Timeout = 90000;
        request.ReadWriteTimeout = 90000;
        using (var response = (HttpWebResponse)request.GetResponse())
        {
            if (response.ResponseUri == null || !IsAllowedUpdateDownloadUri(response.ResponseUri))
            {
                throw new InvalidOperationException("The update download redirected to an untrusted address.");
            }
            if (response.ContentLength > MaximumUpdateDownloadBytes)
            {
                throw new InvalidOperationException("The update package was unexpectedly large.");
            }

            using (var input = response.GetResponseStream())
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while (input != null && (read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumUpdateDownloadBytes)
                    {
                        throw new InvalidOperationException("The update package was unexpectedly large.");
                    }
                    output.Write(buffer, 0, read);
                }
            }
        }
    }

    private static bool IsAllowedUpdateDownloadUri(Uri uri)
    {
        return uri != null &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback));
    }

    private static string NormalizeExpectedSha256(string value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("sha256:".Length);
        }
        return Regex.IsMatch(normalized, "\\A[0-9A-Fa-f]{64}\\z") ? normalized.ToUpperInvariant() : null;
    }

    private static void VerifyUpdateSha256(string zipPath, string expectedSha256, bool required)
    {
        var expected = NormalizeExpectedSha256(expectedSha256);
        if (expected == null)
        {
            if (required)
            {
                throw new InvalidOperationException("The online update did not include a valid SHA-256 digest.");
            }
            return;
        }

        var actual = GetFileSha256(zipPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The downloaded update failed its SHA-256 verification.");
        }
    }

    private static void SafeExtractUpdateArchive(string zipPath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            if (archive.Entries.Count > MaximumUpdateArchiveEntries)
            {
                throw new InvalidOperationException("The update archive contains too many entries.");
            }

            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                extractedBytes += entry.Length;
                if (entry.Length > MaximumUpdateDownloadBytes || extractedBytes > MaximumUpdateExtractedBytes)
                {
                    throw new InvalidOperationException("The extracted update would be unexpectedly large.");
                }

                var name = (entry.FullName ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || Regex.IsMatch(name, "\\A[A-Za-z]:"))
                {
                    throw new InvalidOperationException("The update archive contains an unsafe path.");
                }
                var parts = name.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => part == ".." || part == "."))
                {
                    throw new InvalidOperationException("The update archive contains an unsafe path.");
                }
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                {
                    throw new InvalidOperationException("The update archive contains a symbolic link.");
                }

                var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, name));
                if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase) || !destinations.Add(outputPath))
                {
                    throw new InvalidOperationException("The update archive contains an unsafe or duplicated path.");
                }
                var directoryEntry = entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                if (directoryEntry)
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                using (var input = entry.Open())
                using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }
    }

    private static string FindUpdateSourceFolder(string stage)
    {
        var candidates = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "Sensor Readout.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length != 1)
        {
            return "";
        }

        var source = Path.GetDirectoryName(candidates[0]);
        return Directory.Exists(Path.Combine(source, "Resources")) &&
            Directory.Exists(Path.Combine(source, "Data")) &&
            Directory.Exists(Path.Combine(source, "Langs"))
            ? source
            : "";
    }

    internal static bool WaitForProcessExit(int processId, int timeoutMilliseconds)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                return process.WaitForExit(Math.Max(0, timeoutMilliseconds));
            }
        }
        catch (ArgumentException)
        {
            return true;
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

            DeleteDirectoryRequired(existing);
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
        var preservedRuntimeFiles = CaptureBundledPlugInRuntimeFiles(existing);
        BackupCustomPlugInFiles(existing, incoming, sourceRoot, backupRoot, previousPlugInHashes);

        foreach (var incomingItem in Directory.GetFileSystemEntries(incoming))
        {
            var name = Path.GetFileName(incomingItem);
            var oldPath = Path.Combine(existing, name);
            if (Directory.Exists(oldPath))
            {
                DeleteDirectoryRequired(oldPath);
            }
            else
            {
                DeleteFileRequired(oldPath);
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

        RestoreBundledPlugInRuntimeFiles(existing, preservedRuntimeFiles);
    }

    internal static void ReplacePlugInsFolderForTest(string sourceRoot, string targetRoot, string backupRoot, Dictionary<string, string> previousPlugInHashes)
    {
        ReplacePlugInsFolder(sourceRoot, targetRoot, backupRoot, previousPlugInHashes);
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

            if (IsBundledPlugInBinaryBackupCandidate(relative))
            {
                continue;
            }

            if (IsPreservedBundledPlugInRuntimeFile(relative, new FileInfo(file).Length))
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

    private static Dictionary<string, byte[]> CaptureBundledPlugInRuntimeFiles(string plugInsRoot)
    {
        var captured = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(plugInsRoot))
        {
            return captured;
        }

        foreach (var file in Directory.GetFiles(plugInsRoot, "*.controlled", SearchOption.AllDirectories))
        {
            var relative = RelativePath(plugInsRoot, file);
            var length = new FileInfo(file).Length;
            if (IsPreservedBundledPlugInRuntimeFile(relative, length))
            {
                captured[relative] = File.ReadAllBytes(file);
            }
        }

        return captured;
    }

    private static void RestoreBundledPlugInRuntimeFiles(string plugInsRoot, Dictionary<string, byte[]> captured)
    {
        foreach (var pair in captured)
        {
            var destination = Path.Combine(plugInsRoot, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllBytes(destination, pair.Value);
        }
    }

    private static bool IsPreservedBundledPlugInRuntimeFile(string relative, long length)
    {
        if (string.IsNullOrWhiteSpace(relative) || length < 0 || length > 4096)
        {
            return false;
        }

        var normalized = relative.Replace('/', '\\');
        var parts = normalized.Split('\\');
        if (parts.Length != 2 || !string.Equals(parts[0], "Corsair", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string prefix = "corsair-hub-";
        const string suffix = ".controlled";
        var name = parts[1];
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var keyLength = name.Length - prefix.Length - suffix.Length;
        if (keyLength < 1 || keyLength > 256)
        {
            return false;
        }

        for (var i = prefix.Length; i < prefix.Length + keyLength; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBundledPlugInBinaryBackupCandidate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var extension = Path.GetExtension(relativePath);
        return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupObsoleteBundledPlugInBackups(string targetDir)
    {
        try
        {
            var backupRoot = Path.Combine(targetDir, "Backups\\Updates");
            if (!Directory.Exists(backupRoot))
            {
                return;
            }

            foreach (var zipPath in Directory.GetFiles(backupRoot, "Custom-Bundled-Plug-Ins*.zip", SearchOption.AllDirectories))
            {
                if (BackupZipContainsOnlyBundledPlugInBinaries(zipPath))
                {
                    TryDeleteFile(zipPath);
                    RemoveEmptyDirectory(Path.GetDirectoryName(zipPath));
                }
            }
        }
        catch
        {
        }
    }

    private static bool BackupZipContainsOnlyBundledPlugInBinaries(string zipPath)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var sawFile = false;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name))
                    {
                        continue;
                    }

                    sawFile = true;
                    var normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.StartsWith("custom-plug-ins/", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = normalized.Substring("custom-plug-ins/".Length);
                    }

                    if (!IsBundledPlugInBinaryBackupCandidate(normalized))
                    {
                        return false;
                    }
                }

                return sawFile;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupObsoleteShippedFolderBackups(string targetDir)
    {
        try
        {
            var backupRoot = Path.Combine(targetDir, "Backups\\Updates");
            if (!Directory.Exists(backupRoot))
            {
                return;
            }

            foreach (var zipPath in Directory.GetFiles(backupRoot, "*.zip", SearchOption.AllDirectories))
            {
                if (BackupZipContainsOnlyObsoleteShippedFiles(zipPath))
                {
                    TryDeleteFile(zipPath);
                    RemoveEmptyDirectory(Path.GetDirectoryName(zipPath));
                }
            }
        }
        catch
        {
        }
    }

    private static bool BackupZipContainsOnlyObsoleteShippedFiles(string zipPath)
    {
        var fileName = Path.GetFileName(zipPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.StartsWith("Nested-Sounds", StringComparison.OrdinalIgnoreCase))
        {
            return BackupZipContainsOnlyAllowedEntries(zipPath, IsBundledSoundBackupEntry);
        }

        if (fileName.StartsWith("Previous-Data", StringComparison.OrdinalIgnoreCase))
        {
            return BackupZipContainsOnlyAllowedEntries(zipPath, IsBundledDataBackupEntry);
        }

        if (fileName.StartsWith("Previous-Docs", StringComparison.OrdinalIgnoreCase))
        {
            return BackupZipContainsOnlyAllowedEntries(zipPath, IsBundledDocsBackupEntry);
        }

        if (fileName.StartsWith("Previous-Plug-Ins", StringComparison.OrdinalIgnoreCase))
        {
            return BackupZipContainsOnlyAllowedEntries(zipPath, IsBundledPlugInBackupEntry);
        }

        return false;
    }

    private static bool BackupZipContainsOnlyAllowedEntries(string zipPath, Func<string, bool> allowed)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var sawFile = false;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name))
                    {
                        continue;
                    }

                    sawFile = true;
                    var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (!allowed(normalized))
                    {
                        return false;
                    }
                }

                return sawFile;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBundledSoundBackupEntry(string normalized)
    {
        if (!normalized.StartsWith("Sounds/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = FileNameFromZipPath(normalized);
        return Regex.IsMatch(name ?? "", @"^SR(0[1-9]|1[0-2])\.wav$", RegexOptions.IgnoreCase);
    }

    private static bool IsBundledDataBackupEntry(string normalized)
    {
        if (!normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = FileNameFromZipPath(normalized);
        return string.Equals(name, "BundledLanguageHashes.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "BundledPlugInHashes.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "oui.csv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "oui.LICENSE.txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "usb.ids", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "usb.ids.LICENSE.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBundledDocsBackupEntry(string normalized)
    {
        if (!normalized.StartsWith("Docs/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = FileNameFromZipPath(normalized);
        return Regex.IsMatch(name ?? "", @"^README-[a-z]{2}\.html$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(name ?? "", @"^README-[a-z]{2}\.md$", RegexOptions.IgnoreCase) ||
            string.Equals(name, "Plug-In-development.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "SOURCE-MAP.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBundledPlugInBackupEntry(string normalized)
    {
        if (!normalized.StartsWith("Plug-Ins/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = normalized.Split('/');
        if (parts.Length < 3)
        {
            return false;
        }

        var plugInFolder = parts[1];
        var name = parts[parts.Length - 1];
        if (!IsBundledPlugInFolderName(plugInFolder))
        {
            return false;
        }

        return string.Equals(name, "plugin.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "NOTICE.txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "GPL-3.0.txt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("PlugIn.dll", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("PlugIn.pdb", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileNameFromZipPath(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        var parts = normalized.Split('/');
        return parts.Length == 0 ? normalized : parts[parts.Length - 1];
    }

    private static bool IsBundledPlugInFolderName(string folderName)
    {
        foreach (var known in new[] { "AsusRog", "Corsair", "DellLatitude", "Framework", "HP", "HuaweiMateBook", "LenovoThinkPad", "MsiLaptop" })
        {
            if (string.Equals(folderName, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void CleanupEmptyBackupFolders(string targetDir)
    {
        try
        {
            var backups = Path.Combine(targetDir, "Backups");
            if (!Directory.Exists(backups))
            {
                return;
            }

            foreach (var folder in Directory.GetDirectories(backups, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length))
            {
                RemoveEmptyDirectory(folder);
            }

            RemoveEmptyDirectory(Path.Combine(backups, "Updates"));
            RemoveEmptyDirectory(backups);
        }
        catch
        {
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
                DeleteDirectoryRequired(nested);
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

    private static bool PreserveDuringProgramRollback(string name)
    {
        return string.Equals(name, "Config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Logs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Reports", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Backups", StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateUpdateRollback(string targetRoot, string rollbackRoot)
    {
        Directory.CreateDirectory(rollbackRoot);
        foreach (var path in Directory.GetFileSystemEntries(targetRoot))
        {
            var name = Path.GetFileName(path);
            if (PreserveDuringProgramRollback(name))
            {
                continue;
            }

            var destination = Path.Combine(rollbackRoot, name);
            if (Directory.Exists(path))
            {
                CopyDirectory(path, destination);
            }
            else
            {
                File.Copy(path, destination, true);
            }
        }
    }

    private static void RestoreUpdateRollback(string targetRoot, string rollbackRoot)
    {
        if (!Directory.Exists(rollbackRoot))
        {
            throw new DirectoryNotFoundException("The temporary update rollback folder could not be found.");
        }

        foreach (var path in Directory.GetFileSystemEntries(targetRoot))
        {
            var name = Path.GetFileName(path);
            if (!PreserveDuringProgramRollback(name))
            {
                DeleteUpdateEntry(path);
            }
        }

        foreach (var path in Directory.GetFileSystemEntries(rollbackRoot))
        {
            var destination = Path.Combine(targetRoot, Path.GetFileName(path));
            if (Directory.Exists(path))
            {
                CopyDirectory(path, destination);
            }
            else
            {
                File.Copy(path, destination, true);
            }
        }
    }

    private static void DeleteUpdateEntry(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, true);
            return;
        }

        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
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
            DeleteFileRequired(file);
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

    internal static void DeleteDirectoryRequired(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, true);
        if (Directory.Exists(path))
        {
            throw new IOException("The updater could not completely remove the shipped folder: " + path);
        }
    }

    private static void DeleteFileRequired(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException("The updater could not remove the obsolete shipped file: " + path);
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

    private static void WriteUpdateHistory(string[] args, string message)
    {
        try
        {
            string targetDir;
            if (args == null || !TryGetOptionValue(args, "--update-target", out targetDir) || string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = AppDomain.CurrentDomain.BaseDirectory;
            }

            WriteUpdateHistory(targetDir, message);
        }
        catch
        {
        }
    }

    private static void WriteUpdateHistory(string targetDir, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = AppDomain.CurrentDomain.BaseDirectory;
            }

            var logRoot = Path.Combine(targetDir, "Logs");
            Directory.CreateDirectory(logRoot);
            var path = Path.Combine(logRoot, "Update.log");
            File.AppendAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " " +
                (string.IsNullOrWhiteSpace(message) ? "(no update message)" : message) +
                Environment.NewLine);
        }
        catch
        {
        }
    }
}
