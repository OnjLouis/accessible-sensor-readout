using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class Program
{
    public const string CloseRequestEventName = @"Local\OnjSensorReadoutCloseRequest";
    private const int CrashRestartLimit = 3;
    private static readonly TimeSpan CrashRestartWindow = TimeSpan.FromMinutes(10);
    private static string[] startupArgs = new string[0];

    [STAThread]
    public static void Main(string[] args)
    {
        startupArgs = args ?? new string[0];
        InstallCrashLogging();

        if (HasArg(args, "--close"))
        {
            CloseOtherInstances();
            return;
        }

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

        if (HasArg(args, "--help") || HasArg(args, "-?") || HasArg(args, "/?"))
        {
            ShowCommandLineHelp();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (saveReport)
        {
            SaveCommandLineReport(reportPath, reportHtml);
            return;
        }

        CloseOtherInstances();

        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, @"Local\OnjSensorReadoutApp", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show(
                    "Sensor Readout is already running.",
                    "Sensor Readout",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.Run(new SensorReadoutForm(ShouldStartMinimized(args)));
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
                WriteCrashLog("Unhandled application exception", exception, e.IsTerminating, restart);
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                WriteCrashLog("Unobserved background task exception", e.Exception, false, false);
            };
        }
        catch
        {
        }
    }

    private static void WriteCrashLog(string source, Exception exception, bool terminating, bool restartRequested)
    {
        try
        {
            var logsFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            System.IO.Directory.CreateDirectory(logsFolder);
            var path = System.IO.Path.Combine(logsFolder, "Crash-" + SafeFileName(Environment.MachineName) + ".log");
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
                "OS: " +
                Environment.OSVersion +
                Environment.NewLine +
                "Restart requested: " +
                restartRequested +
                Environment.NewLine +
                "Exception:" +
                Environment.NewLine +
                (exception == null ? "(No exception object was provided.)" : exception.ToString()) +
                Environment.NewLine +
                Environment.NewLine;

            System.IO.File.AppendAllText(path, message);
        }
        catch
        {
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

    private static bool ShouldRestartAfterCrash()
    {
        if (startupArgs == null)
        {
            return true;
        }

        return !HasArg(startupArgs, "--close") &&
               !HasOption(startupArgs, "--report-txt") &&
               !HasOption(startupArgs, "--report-html") &&
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
                Environment.NewLine);
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
                "SensorReadout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (html ? ".html" : ".txt"));
        }

        using (var form = new SensorReadoutForm(false))
        {
            form.SaveReportToFile(path, html, true);
        }
    }

    private static void CloseOtherInstances()
    {
        var current = Process.GetCurrentProcess();
        var processName = System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        SignalCloseRequest();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

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
