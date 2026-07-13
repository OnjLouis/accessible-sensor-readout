using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private IEnumerable<SensorRow> GetOemProviderRows()
    {
        return GetOemProviderRows(false, false);
    }

    private IEnumerable<SensorRow> GetOemProviderRows(bool diagnosticsMode, bool backgroundRefresh)
    {
        var cacheInterval = backgroundRefresh ? BackgroundOemProviderRowsMinimumInterval : ForegroundOemProviderRowsMinimumInterval;
        var cacheSignature = GetOemProviderRowsCacheSignature();
        if (!diagnosticsMode)
        {
            lock (oemProviderRowsLock)
            {
                if (cachedOemProviderRowsUtc != DateTime.MinValue &&
                    string.Equals(cachedOemProviderRowsSignature, cacheSignature, StringComparison.Ordinal) &&
                    DateTime.UtcNow - cachedOemProviderRowsUtc < cacheInterval)
                {
                    return cachedOemProviderRows.Select(CloneSensorRow).ToList();
                }
            }
        }

        var rows = GetPlugInRows(diagnosticsMode).Where(r => r != null).Select(CloneSensorRow).ToList();
        if (!diagnosticsMode)
        {
            lock (oemProviderRowsLock)
            {
                cachedOemProviderRows = rows.Select(CloneSensorRow).ToList();
                cachedOemProviderRowsUtc = DateTime.UtcNow;
                cachedOemProviderRowsSignature = cacheSignature;
            }
        }

        return rows;
    }

    private void ClearOemProviderRowsCache()
    {
        lock (oemProviderRowsLock)
        {
            cachedOemProviderRows.Clear();
            cachedOemProviderRowsUtc = DateTime.MinValue;
            cachedOemProviderRowsSignature = "";
        }
    }

    private string GetOemProviderRowsCacheSignature()
    {
        return GetOemProviderRowsCacheSignature(settings);
    }

    private static string GetOemProviderRowsCacheSignature(AppSettings appSettings)
    {
        var enabled = appSettings == null || appSettings.PlugInsEnabled == null
            ? Enumerable.Empty<string>()
            : appSettings.PlugInsEnabled
                .Where(pair => pair.Value && !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => pair.Key.Trim())
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        return string.Join("|", enabled.ToArray());
    }
}
