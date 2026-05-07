using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (saveReport)
            {
                SaveCommandLineReport(reportPath, reportHtml);
                return;
            }

            Application.Run(new SensorReadoutForm(ShouldStartMinimized(args)));
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
            path = "SensorReadout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (html ? ".html" : ".txt");
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

    private static void ShowCommandLineHelp()
    {
        MessageBox.Show(
            "Sensor Readout command-line options:" + Environment.NewLine + Environment.NewLine +
            "--minimized or --tray" + Environment.NewLine +
            "Start minimized to the notification area." + Environment.NewLine + Environment.NewLine +
            "--close" + Environment.NewLine +
            "Close any running Sensor Readout instance and exit." + Environment.NewLine + Environment.NewLine +
            "--report-txt [path]" + Environment.NewLine +
            "Save a text report and exit. If no path is supplied, a timestamped file is created in the current folder." + Environment.NewLine + Environment.NewLine +
            "--report-html [path]" + Environment.NewLine +
            "Save an HTML report and exit. If no path is supplied, a timestamped file is created in the current folder." + Environment.NewLine + Environment.NewLine +
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
