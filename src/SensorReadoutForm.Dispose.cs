using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    protected override void Dispose(bool disposing)
    {
        if (disposing && lhmComputer != null)
        {
            lhmComputer.Close();
        }

        if (disposing)
        {
            DisposeLogicalDiskPerformanceCounters();
        }

        if (disposing)
        {
            UnregisterGlobalHotKeys();
        }

        if (disposing && trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        if (disposing && trayFlashTimer != null)
        {
            trayFlashTimer.Dispose();
        }

        if (disposing && trayStatusIcon != null)
        {
            trayStatusIcon.Dispose();
        }

        if (disposing && alarmTrayIcon != null)
        {
            alarmTrayIcon.Dispose();
        }

        if (disposing && hotKeyWindow != null)
        {
            hotKeyWindow.Dispose();
        }

        if (disposing && languageTimer != null)
        {
            languageTimer.Dispose();
        }

        if (disposing && updateCheckTimer != null)
        {
            updateCheckTimer.Dispose();
        }

        if (disposing && visibleRefreshTimer != null)
        {
            visibleRefreshTimer.Dispose();
        }

        if (disposing)
        {
            ScreenReaderOutput.Shutdown();
        }

        base.Dispose(disposing);
    }

    private void DisposeLogicalDiskPerformanceCounters()
    {
        lock (logicalDiskCountersLock)
        {
            foreach (var counters in logicalDiskCounters.Values)
            {
                counters.Dispose();
            }

            logicalDiskCounters.Clear();
        }
    }
}
