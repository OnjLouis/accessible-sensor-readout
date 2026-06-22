using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

public static partial class Program
{
    public const string CloseRequestEventName = @"Local\OnjSensorReadoutCloseRequest";
    public const string ShowRequestEventName = @"Local\OnjSensorReadoutShowRequest";
    private const int CrashRestartLimit = 3;
    private static readonly TimeSpan CrashRestartWindow = TimeSpan.FromMinutes(10);
    private static string[] startupArgs = new string[0];

    [STAThread]
    public static void Main(string[] args)
    {
        startupArgs = args ?? new string[0];
        ConfigureRuntimeDependencyPaths();
        InstallCrashLogging();

        if (HasArg(args, "--close"))
        {
            CloseOtherInstances();
            return;
        }

        if (HasArg(args, "--apply-update"))
        {
            ApplyUpdateFromCommandLine(args);
            return;
        }

        CleanupObsoleteRootStateAfterLegacyUpdate();

        var loggingLevel = GetRequestedLoggingLevel(args);
        if (!string.IsNullOrWhiteSpace(loggingLevel))
        {
            SensorReadoutForm.SetLoggingLevelPreference(loggingLevel);
        }

        var reportPath = "";
        var reportHtml = false;
        var saveReport = TryGetOptionValue(args, "--report-html", out reportPath);
        if (saveReport)
        {
            reportHtml = true;
        }
        else
        {
            saveReport = TryGetOptionValue(args, "--report-txt", out reportPath);
        }

        var anonymizedReportPath = "";
        var anonymizedReportHtml = false;
        var saveAnonymizedReport = TryGetOptionValue(args, "--anonymized-report-html", out anonymizedReportPath) ||
            TryGetOptionValue(args, "--anonymous-report-html", out anonymizedReportPath);
        if (saveAnonymizedReport)
        {
            anonymizedReportHtml = true;
        }
        else
        {
            saveAnonymizedReport = TryGetOptionValue(args, "--anonymized-report-txt", out anonymizedReportPath) ||
                TryGetOptionValue(args, "--anonymous-report-txt", out anonymizedReportPath);
        }

        string compareBeforePath;
        string compareAfterPath;
        string compareOutputPath;
        var compareReports = TryGetTwoOptionValues(args, "--compare-reports", out compareBeforePath, out compareAfterPath, out compareOutputPath);

        string diagnosticsPath;
        var runDiagnostics = TryGetOptionValue(args, "--diagnostics", out diagnosticsPath);
        if (!runDiagnostics)
        {
            runDiagnostics = TryGetOptionValue(args, "--run-diagnostics", out diagnosticsPath);
        }
        var quietDiagnostics = HasArg(args, "--diagnostics-quiet");
        var noDiagnosticsSpeech = quietDiagnostics || HasArg(args, "--no-diagnostics-speech");
        var noDiagnosticsSounds = quietDiagnostics || HasArg(args, "--no-diagnostics-sounds");
        string selfTestPath;
        var runSelfTest = TryGetOptionValue(args, "--self-test", out selfTestPath);
        string communityStatsPath;
        var saveCommunityStatsPayload = TryGetOptionValue(args, "--community-stats-json", out communityStatsPath);

        if (HasArg(args, "--help") || HasArg(args, "-?") || HasArg(args, "/?"))
        {
            ShowCommandLineHelp();
            return;
        }

        RepairBundledHashManifests();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (saveReport)
        {
            SaveCommandLineReport(reportPath, reportHtml);
            return;
        }

        if (saveAnonymizedReport)
        {
            SaveCommandLineAnonymizedReport(anonymizedReportPath, anonymizedReportHtml);
            return;
        }

        if (compareReports)
        {
            SaveCommandLineReportComparison(compareBeforePath, compareAfterPath, compareOutputPath);
            return;
        }

        if (runDiagnostics)
        {
            CloseOtherInstances();
            SaveCommandLineDiagnostics(diagnosticsPath, noDiagnosticsSpeech ? (bool?)false : null, noDiagnosticsSounds ? (bool?)false : null);
            return;
        }

        if (runSelfTest)
        {
            SensorReadoutForm.RunSelfTest(selfTestPath);
            return;
        }

        if (saveCommunityStatsPayload)
        {
            SaveCommandLineCommunityStatsPayload(communityStatsPath);
            return;
        }

        if (ShowExistingInstanceFromSameExecutable())
        {
            return;
        }

        CloseOtherInstances();

        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, @"Local\OnjSensorReadoutApp", out createdNew))
        {
            if (!createdNew)
            {
                SignalShowRequest();
                return;
            }

            Application.Run(new SensorReadoutForm(ShouldStartMinimized(args)));
        }
    }

    private static void ConfigureRuntimeDependencyPaths()
    {
        var resourcesFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        if (!System.IO.Directory.Exists(resourcesFolder))
        {
            return;
        }

        try
        {
            NativeMethods.SetDllDirectory(resourcesFolder);
        }
        catch
        {
        }

        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            try
            {
                var assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    return null;
                }

                var path = System.IO.Path.Combine(resourcesFolder, assemblyName + ".dll");
                return System.IO.File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null;
            }
        };
    }

    private static void CleanupObsoleteRootStateAfterLegacyUpdate()
    {
        try
        {
            DeleteObsoleteRootFiles(AppDomain.CurrentDomain.BaseDirectory);
            CleanupObsoleteRootUpdateFolders(AppDomain.CurrentDomain.BaseDirectory, null);
            CleanupObsoleteBundledPlugInBackups(AppDomain.CurrentDomain.BaseDirectory);
            CleanupObsoleteShippedFolderBackups(AppDomain.CurrentDomain.BaseDirectory);
            CleanupEmptyBackupFolders(AppDomain.CurrentDomain.BaseDirectory);
        }
        catch
        {
        }
    }

    private static void RepairBundledHashManifests()
    {
        try
        {
            var baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            var dataFolder = System.IO.Path.Combine(baseFolder, "Data");
            if (!System.IO.Directory.Exists(dataFolder))
            {
                return;
            }

            RepairBundledPlugInHashManifest(baseFolder);
        }
        catch
        {
        }
    }

    internal static bool RepairBundledPlugInHashManifestForTest(string baseFolder)
    {
        return RepairBundledPlugInHashManifest(baseFolder);
    }

    private static bool RepairBundledPlugInHashManifest(string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            return false;
        }

        var plugInsFolder = System.IO.Path.Combine(baseFolder, "Plug-Ins");
        var manifestPath = System.IO.Path.Combine(System.IO.Path.Combine(baseFolder, "Data"), "BundledPlugInHashes.json");
        if (!System.IO.Directory.Exists(plugInsFolder) || !System.IO.File.Exists(manifestPath))
        {
            return false;
        }

        var existingManifest = ReadManifestHashes(manifestPath);
        if (existingManifest.Count == 0)
        {
            return false;
        }

        var files = GetKnownBundledPlugInFiles(plugInsFolder);
        if (files.Count == 0)
        {
            return false;
        }

        var dllsWithManifestEntries = files
            .Where(path => string.Equals(System.IO.Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => existingManifest.ContainsKey(ManifestRelativePath(plugInsFolder, path).Replace("/", "\\")))
            .ToList();
        if (dllsWithManifestEntries.Count == 0)
        {
            return false;
        }

        var mismatchedCurrentDlls = 0;
        foreach (var dll in dllsWithManifestEntries)
        {
            var relative = ManifestRelativePath(plugInsFolder, dll).Replace("/", "\\");
            var expectedHash = existingManifest[relative];
            var actualHash = GetManifestFileSha256(dll);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsCurrentVersionFile(dll))
                {
                    return false;
                }

                mismatchedCurrentDlls++;
            }
        }

        if (mismatchedCurrentDlls != dllsWithManifestEntries.Count)
        {
            return false;
        }

        return WriteBundledHashManifestIfDifferent(plugInsFolder, manifestPath, files);
    }

    private static List<string> GetKnownBundledPlugInFiles(string plugInsFolder)
    {
        var knownBundledFolders = new[]
        {
            "AsusRog",
            "DellLatitude",
            "Framework",
            "HP",
            "LenovoThinkPad",
            "MsiLaptop"
        };
        var files = new List<string>();
        foreach (var folderName in knownBundledFolders)
        {
            var folder = System.IO.Path.Combine(plugInsFolder, folderName);
            if (!System.IO.Directory.Exists(folder))
            {
                continue;
            }

            files.AddRange(System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories));
        }

        return files
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsCurrentVersionFile(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.Equals(version, SensorReadoutForm.AppVersion + ".0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ReadManifestHashes(string manifestPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(manifestPath) || !System.IO.File.Exists(manifestPath))
        {
            return hashes;
        }

        var text = System.IO.File.ReadAllText(manifestPath);
        var filesMatch = System.Text.RegularExpressions.Regex.Match(text, "\"Files\"\\s*:\\s*\\{(?<files>.*?)\\}\\s*\\}", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!filesMatch.Success)
        {
            return hashes;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(filesMatch.Groups["files"].Value, "\"(?<path>(?:\\\\.|[^\"])*)\"\\s*:\\s*\"(?<hash>[A-Fa-f0-9]{64})\""))
        {
            var relativePath = JsonUnescape(match.Groups["path"].Value).Replace("/", "\\");
            var hash = match.Groups["hash"].Value;
            if (!string.IsNullOrWhiteSpace(relativePath) && !hashes.ContainsKey(relativePath))
            {
                hashes.Add(relativePath, hash);
            }
        }

        return hashes;
    }

    private static bool WriteBundledHashManifestIfDifferent(string sourceFolder, string manifestPath, List<string> files)
    {
        if (!System.IO.Directory.Exists(sourceFolder) || string.IsNullOrWhiteSpace(manifestPath))
        {
            return false;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("    \"Version\":  1,");
        builder.AppendLine("    \"UpdatedUtc\":  \"" + DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\",");
        builder.AppendLine("    \"Files\":  {");
        for (var i = 0; i < files.Count; i++)
        {
            var relative = ManifestRelativePath(sourceFolder, files[i]).Replace("/", "\\");
            builder.Append("                  \"");
            builder.Append(JsonEscape(relative));
            builder.Append("\":  \"");
            builder.Append(GetManifestFileSha256(files[i]));
            builder.Append("\"");
            if (i + 1 < files.Count)
            {
                builder.Append(",");
            }

            builder.AppendLine();
        }

        builder.AppendLine("              }");
        builder.AppendLine("}");

        var next = builder.ToString();
        var current = System.IO.File.Exists(manifestPath) ? System.IO.File.ReadAllText(manifestPath) : "";
        if (string.Equals(NormalizeManifestForComparison(current), NormalizeManifestForComparison(next), StringComparison.Ordinal))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifestPath));
        System.IO.File.WriteAllText(manifestPath, next, System.Text.Encoding.UTF8);
        return true;
    }

    private static string NormalizeManifestForComparison(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return System.Text.RegularExpressions.Regex.Replace(text, "\"UpdatedUtc\"\\s*:\\s*\"[^\"]*\"", "\"UpdatedUtc\":\"\"");
    }

    private static string ManifestRelativePath(string root, string path)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var fullPath = System.IO.Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(fullRoot.Length) : System.IO.Path.GetFileName(path);
    }

    private static string JsonEscape(string value)
    {
        return (value ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private static string JsonUnescape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }

    private static string GetManifestFileSha256(string path)
    {
        using (var stream = System.IO.File.OpenRead(path))
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }

    private static void InstallCrashLogging()
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) =>
            {
                var restart = TryRestartAfterCrash("Windows Forms UI thread", e.Exception);
                WriteCrashLog("Windows Forms UI thread", e.Exception, true, restart);
                MessageBox.Show(
                    restart
                        ? "Sensor Readout hit an unexpected error and wrote a crash log in the Logs folder. It will restart now."
                        : "Sensor Readout hit an unexpected error and wrote a crash log in the Logs folder.",
                    "Sensor Readout",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Application.Exit();
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var exception = e.ExceptionObject as Exception;
                var restart = TryRestartAfterCrash("Unhandled application exception", exception);
                WriteCrashLog("Unhandled application exception", exception, e.ExceptionObject, e.IsTerminating, restart);
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                WriteCrashLog("Unobserved background task exception", e.Exception, e.Exception, false, false);
            };
        }
        catch
        {
        }
    }

    private static void WriteCrashLog(string source, Exception exception, bool terminating, bool restartRequested)
    {
        WriteCrashLog(source, exception, exception, terminating, restartRequested);
    }

    private static void WriteCrashLog(string source, Exception exception, object exceptionObject, bool terminating, bool restartRequested)
    {
        try
        {
            var path = GetCrashLogPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            RotateCrashLogIfNeeded(path);

            var message =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " [Crash] " +
                source +
                ", terminating=" +
                terminating +
                Environment.NewLine +
                "Version: " +
                SensorReadoutForm.AppVersion +
                Environment.NewLine +
                "Executable: " +
                Application.ExecutablePath +
                Environment.NewLine +
                "Base folder: " +
                AppDomain.CurrentDomain.BaseDirectory +
                Environment.NewLine +
                "Current directory: " +
                Environment.CurrentDirectory +
                Environment.NewLine +
                "Command line: " +
                Environment.CommandLine +
                Environment.NewLine +
                "OS: " +
                GetWindowsVersionText() +
                Environment.NewLine +
                "Regular log setting: ignored; crash logs are always attempted." +
                Environment.NewLine +
                "Restart requested: " +
                restartRequested +
                Environment.NewLine +
                "Exception:" +
                Environment.NewLine +
                CrashExceptionText(exception, exceptionObject) +
                Environment.NewLine +
                Environment.NewLine;

            System.IO.File.AppendAllText(path, message, System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }

    internal static string WriteCrashLogForSelfTest()
    {
        var path = GetCrashLogPath();
        WriteCrashLog("Self-test crash log", new InvalidOperationException("Self-test crash log exception"), false, false);
        return path;
    }

    private static string CrashExceptionText(Exception exception, object exceptionObject)
    {
        if (exception != null)
        {
            return exception.ToString();
        }

        if (exceptionObject == null)
        {
            return "(No exception object was provided.)";
        }

        try
        {
            return "Non-Exception object: " + exceptionObject;
        }
        catch
        {
            return "Non-Exception object was provided, but could not be converted to text.";
        }
    }

    private static bool TryRestartAfterCrash(string source, Exception exception)
    {
        try
        {
            if (!ShouldRestartAfterCrash())
            {
                return false;
            }

            var state = ReadCrashRestartState();
            var now = DateTime.UtcNow;
            if (state.FirstCrashUtc == DateTime.MinValue || now - state.FirstCrashUtc > CrashRestartWindow)
            {
                state.FirstCrashUtc = now;
                state.Count = 0;
            }

            state.Count++;
            WriteCrashRestartState(state);
            AppendCrashRestartLine(source, exception, state.Count);
            if (state.Count > CrashRestartLimit)
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return true;
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
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
            using (var results = searcher.Get())
            {
                foreach (System.Management.ManagementObject item in results)
                {
                    var caption = Convert.ToString(item["Caption"]);
                    var version = Convert.ToString(item["Version"]);
                    var build = Convert.ToString(item["BuildNumber"]);
                    var architecture = Convert.ToString(item["OSArchitecture"]);
                    var parts = new System.Collections.Generic.List<string>();
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

    private static bool ShouldRestartAfterCrash()
    {
        if (startupArgs == null)
        {
            return true;
        }

        return !HasArg(startupArgs, "--close") &&
               !HasOption(startupArgs, "--report-txt") &&
               !HasOption(startupArgs, "--report-html") &&
               !HasOption(startupArgs, "--anonymized-report-txt") &&
               !HasOption(startupArgs, "--anonymized-report-html") &&
               !HasOption(startupArgs, "--anonymous-report-txt") &&
               !HasOption(startupArgs, "--anonymous-report-html") &&
               !HasOption(startupArgs, "--compare-reports") &&
               !HasOption(startupArgs, "--diagnostics") &&
               !HasOption(startupArgs, "--run-diagnostics") &&
               !HasOption(startupArgs, "--self-test") &&
               !HasOption(startupArgs, "--community-stats-json") &&
               !HasArg(startupArgs, "--apply-update") &&
               !HasArg(startupArgs, "--help") &&
               !HasArg(startupArgs, "-?") &&
               !HasArg(startupArgs, "/?");
    }

    private static bool HasOption(string[] args, string name)
    {
        return args != null && args.Any(a =>
            string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(a) && a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)));
    }

    private static CrashRestartState ReadCrashRestartState()
    {
        try
        {
            var path = GetCrashRestartStatePath();
            if (!System.IO.File.Exists(path))
            {
                return new CrashRestartState();
            }

            var parts = System.IO.File.ReadAllText(path).Split('|');
            DateTime firstCrashUtc;
            int count;
            return new CrashRestartState
            {
                FirstCrashUtc = parts.Length > 0 && DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out firstCrashUtc) ? firstCrashUtc : DateTime.MinValue,
                Count = parts.Length > 1 && int.TryParse(parts[1], out count) ? count : 0
            };
        }
        catch
        {
            return new CrashRestartState();
        }
    }

    private static void WriteCrashRestartState(CrashRestartState state)
    {
        try
        {
            var path = GetCrashRestartStatePath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, state.FirstCrashUtc.ToString("o") + "|" + state.Count);
        }
        catch
        {
        }
    }

    private static void AppendCrashRestartLine(string source, Exception exception, int count)
    {
        try
        {
            var path = GetCrashLogPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.AppendAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " [CrashRestart] attempt " +
                count +
                " of " +
                CrashRestartLimit +
                " after " +
                source +
                ": " +
                (exception == null ? "(no exception object)" : exception.GetType().Name + ": " + exception.Message) +
                Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string GetCrashLogPath()
    {
        return System.IO.Path.Combine(GetCrashLogsFolder(), "Crash-" + SafeFileName(Environment.MachineName) + ".log");
    }

    private static string GetCrashRestartStatePath()
    {
        return System.IO.Path.Combine(GetCrashLogsFolder(), "CrashRestart-" + SafeFileName(Environment.MachineName) + ".txt");
    }

    private static string GetCrashLogsFolder()
    {
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    }

    private sealed class CrashRestartState
    {
        public DateTime FirstCrashUtc = DateTime.MinValue;
        public int Count = 0;
    }

    private static string SafeFileName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Computer" : value;
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        return text;
    }

    private static void RotateCrashLogIfNeeded(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return;
            }

            var info = new System.IO.FileInfo(path);
            if (info.Length < 262144)
            {
                return;
            }

            var oldPath = path + ".old";
            if (System.IO.File.Exists(oldPath))
            {
                System.IO.File.Delete(oldPath);
            }

            System.IO.File.Move(path, oldPath);
        }
        catch
        {
        }
    }

    private static bool HasArg(string[] args, string name)
    {
        return args != null && args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetOptionValue(string[] args, string name, out string value)
    {
        value = "";
        if (args == null)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i] ?? "";
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(name.Length + 1).Trim('"');
                return true;
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !IsOptionName(args[i + 1]))
                {
                    value = args[i + 1].Trim('"');
                }

                return true;
            }
        }

        return false;
    }

    private static bool TryGetTwoOptionValues(string[] args, string name, out string firstValue, out string secondValue, out string thirdValue)
    {
        firstValue = "";
        secondValue = "";
        thirdValue = "";
        if (args == null)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i] ?? "";
            if (!string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 2 >= args.Length || IsOptionName(args[i + 1]) || IsOptionName(args[i + 2]))
            {
                throw new ArgumentException(name + " requires two report paths.");
            }

            firstValue = args[i + 1].Trim('"');
            secondValue = args[i + 2].Trim('"');
            if (i + 3 < args.Length && !IsOptionName(args[i + 3]))
            {
                thirdValue = args[i + 3].Trim('"');
            }

            return true;
        }

        return false;
    }

    private static bool IsOptionName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && (value.StartsWith("--") || value.StartsWith("/") || value.StartsWith("-"));
    }

    private static string GetRequestedLoggingLevel(string[] args)
    {
        string level;
        if (TryGetOptionValue(args, "--log", out level))
        {
            return level;
        }

        return "";
    }

    private static void SaveCommandLineReport(string path, bool html)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = System.IO.Path.Combine(
                SensorReadoutForm.GetReportsFolderPath(),
                SensorReadoutForm.DefaultReportFileName(html));
        }

        using (var form = new SensorReadoutForm(false))
        {
            form.SaveReportToFile(path, html, true);
        }
    }

    private static void SaveCommandLineAnonymizedReport(string path, bool html)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = System.IO.Path.Combine(
                SensorReadoutForm.GetReportsFolderPath(),
                "SensorReadout-Anonymized-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (html ? ".html" : ".txt"));
        }

        using (var form = new SensorReadoutForm(false))
        {
            form.SaveAnonymizedReportToFile(path, html);
        }
    }

    private static void SaveCommandLineReportComparison(string beforePath, string afterPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = System.IO.Path.Combine(
                SensorReadoutForm.GetReportsFolderPath(),
                "SensorReadout-Comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        }

        using (var form = new SensorReadoutForm(false))
        {
            var folder = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            System.IO.File.WriteAllText(outputPath, form.BuildReportComparisonFileText(beforePath, afterPath));
        }
    }

    private static void SaveCommandLineDiagnostics(string path, bool? speakOverride, bool? soundOverride)
    {
        using (var form = new SensorReadoutForm(false))
        {
            form.SaveDiagnosticsToZip(path, speakOverride, soundOverride);
        }
    }

    private static void SaveCommandLineCommunityStatsPayload(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = System.IO.Path.Combine(
                SensorReadoutForm.GetReportsFolderPath(),
                "SensorReadout-CommunityStats-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        }

        using (var form = new SensorReadoutForm(false))
        {
            form.SaveCommunityStatsPayloadToFile(path);
        }
    }

    private static void CloseOtherInstances()
    {
        var current = Process.GetCurrentProcess();
        var processName = System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        var otherProcesses = Process.GetProcessesByName(processName)
            .Where(process => process.Id != current.Id)
            .ToList();
        if (otherProcesses.Count == 0)
        {
            ResetCloseRequest();
            return;
        }

        SignalCloseRequest();
        foreach (var process in otherProcesses)
        {
            using (process)
            {
                try
                {
                    if (process.WaitForExit(8000))
                    {
                        continue;
                    }

                    if (process.CloseMainWindow() && process.WaitForExit(5000))
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch
                {
                }
            }
        }
        ResetCloseRequest();
    }

    private static bool ShowExistingInstanceFromSameExecutable()
    {
        var current = Process.GetCurrentProcess();
        var currentPath = SafeFullPath(Application.ExecutablePath);
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        var processName = System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    var otherPath = SafeFullPath(process.MainModule == null ? "" : process.MainModule.FileName);
                    if (!string.IsNullOrWhiteSpace(otherPath) &&
                        string.Equals(currentPath, otherPath, StringComparison.OrdinalIgnoreCase))
                    {
                        SignalShowRequest();
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path ?? "";
        }
    }

    private static void SignalCloseRequest()
    {
        try
        {
            using (var closeRequest = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, CloseRequestEventName))
            {
                closeRequest.Set();
            }
        }
        catch
        {
        }
    }

    private static void SignalShowRequest()
    {
        try
        {
            using (var showRequest = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, ShowRequestEventName))
            {
                showRequest.Set();
            }
        }
        catch
        {
        }
    }

    private static void ResetCloseRequest()
    {
        try
        {
            using (var closeRequest = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, CloseRequestEventName))
            {
                closeRequest.Reset();
            }
        }
        catch
        {
        }
    }

    private static void ShowCommandLineHelp()
    {
        MessageBox.Show(
            "Sensor Readout command-line options:" + Environment.NewLine + Environment.NewLine +
            "--minimized or --tray" + Environment.NewLine +
            "Start minimized to the notification area." + Environment.NewLine + Environment.NewLine +
            "--close" + Environment.NewLine +
            "Close any running Sensor Readout instance and exit." + Environment.NewLine + Environment.NewLine +
            "--report-txt [path]" + Environment.NewLine +
            "Save a text report and exit. If no path is supplied, a timestamped file is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--report-html [path]" + Environment.NewLine +
            "Save an HTML report and exit. If no path is supplied, a timestamped file is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--anonymized-report-txt [path]" + Environment.NewLine +
            "Save an anonymized text report and exit. If no path is supplied, a timestamped file is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--anonymized-report-html [path]" + Environment.NewLine +
            "Save an anonymized HTML report and exit. If no path is supplied, a timestamped file is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--anonymous-report-txt [path] or --anonymous-report-html [path]" + Environment.NewLine +
            "Aliases for the anonymized report switches, intended for automated privacy checks." + Environment.NewLine + Environment.NewLine +
            "--compare-reports before after [output]" + Environment.NewLine +
            "Compare two Sensor Readout reports and save the comparison text. If no output path is supplied, a timestamped file is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--diagnostics [path]" + Environment.NewLine +
            "Run diagnostics, save a ZIP, and exit. If no path is supplied, a computer-named timestamped ZIP is created in the Reports folder." + Environment.NewLine + Environment.NewLine +
            "--diagnostics-quiet" + Environment.NewLine +
            "Do not speak or play sounds when used with --diagnostics." + Environment.NewLine + Environment.NewLine +
            "--no-diagnostics-speech or --no-diagnostics-sounds" + Environment.NewLine +
            "Disable only speech or only sounds when used with --diagnostics." + Environment.NewLine + Environment.NewLine +
            "--self-test [path]" + Environment.NewLine +
            "Run internal non-interactive self-tests and write results to the chosen folder." + Environment.NewLine + Environment.NewLine +
            "--community-stats-json [path]" + Environment.NewLine +
            "Write the allow-listed anonymous community stats payload and exit. This does not upload it." + Environment.NewLine + Environment.NewLine +
            "--apply-update --update-zip path --update-target folder --update-exe path" + Environment.NewLine +
            "Install a local Sensor Readout update ZIP through the same updater used for online updates." + Environment.NewLine + Environment.NewLine +
            "--log off|error|normal|debug" + Environment.NewLine +
            "Set the logging level before continuing.",
            "Sensor Readout",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static bool ShouldStartMinimized(string[] args)
    {
        if (args == null)
        {
            return false;
        }

        return args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));
    }
}
