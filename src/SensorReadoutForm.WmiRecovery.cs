using System;
using System.Diagnostics;
using System.Management;

public sealed partial class SensorReadoutForm
{
    private static readonly object WmiRecoveryLock = new object();
    private static bool wmiRecoveryAttemptedThisSession;

    private bool EnsureCoreWmiAvailable(string reason)
    {
        Exception failure;
        if (TryProbeCoreWmi(out failure))
        {
            return true;
        }

        if (!IsRecoverableWmiFailure(failure))
        {
            LogMessage("Debug", "Core WMI probe failed during " + reason + ": " + DescribeException(failure));
            return false;
        }

        lock (WmiRecoveryLock)
        {
            if (wmiRecoveryAttemptedThisSession)
            {
                LogMessage("Debug", "Core WMI recovery was already attempted this session; skipping retry for " + reason + ". Last failure: " + DescribeException(failure));
                return false;
            }

            wmiRecoveryAttemptedThisSession = true;
        }

        if (!IsAdministrator())
        {
            LogMessage("Normal", "Core WMI appears to be stuck during " + reason + ", but Sensor Readout is not running as administrator so it cannot restart the WMI service. Failure: " + DescribeException(failure));
            return false;
        }

        LogMessage("Normal", "Core WMI appears to be stuck during " + reason + "; attempting one administrator-level WMI service restart. Failure: " + DescribeException(failure));
        string restartMessage;
        if (!TryRestartWmiService(out restartMessage))
        {
            LogMessage("Error", "WMI service restart failed: " + restartMessage);
            return false;
        }

        System.Threading.Thread.Sleep(1500);
        Exception retryFailure;
        if (TryProbeCoreWmi(out retryFailure))
        {
            LogMessage("Normal", "WMI service restart completed and core WMI queries are responding again.");
            return true;
        }

        LogMessage("Error", "WMI service restart completed, but core WMI is still failing: " + DescribeException(retryFailure));
        return false;
    }

    private static bool TryProbeCoreWmi(out Exception failure)
    {
        failure = null;
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystem"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject ignored in results)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return false;
    }

    private static bool IsRecoverableWmiFailure(Exception exception)
    {
        var text = DescribeException(exception);
        return text.IndexOf("0x800705af", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("paging file is too small", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryRestartWmiService(out string message)
    {
        message = "";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Restart-Service -Name winmgmt -Force -ErrorAction Stop\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    message = "Could not start PowerShell.";
                    return false;
                }

                if (!process.WaitForExit(30000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    message = "PowerShell timed out while restarting winmgmt.";
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();
                if (process.ExitCode != 0)
                {
                    message = FirstNonEmpty(error, output, "PowerShell exited with code " + process.ExitCode + ".");
                    return false;
                }

                message = FirstNonEmpty(output, "winmgmt restarted.");
                return true;
            }
        }
        catch (Exception ex)
        {
            message = DescribeException(ex);
            return false;
        }
    }

    private static string DescribeException(Exception exception)
    {
        if (exception == null)
        {
            return "unknown error";
        }

        var hresult = exception.HResult == 0 ? "" : " (0x" + exception.HResult.ToString("X8") + ")";
        return exception.GetType().Name + hresult + ": " + exception.Message;
    }
}
