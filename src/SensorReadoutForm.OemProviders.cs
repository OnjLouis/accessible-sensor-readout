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
        // The long minimized/background cache exists so fragile vendor providers are not polled
        // while nobody is looking. When an enabled fan curve takes its temperature from a plug-in
        // reading, that reading is load-bearing for cooling decisions, so the foreground interval
        // is used even in the background; plug-ins keeping their own caches stay cheap to query.
        var cacheInterval = backgroundRefresh && !AnyEnabledFanCurveUsesPlugInReading()
            ? BackgroundOemProviderRowsMinimumInterval
            : ForegroundOemProviderRowsMinimumInterval;
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

        bool servedByLiveManager;
        var rows = GetPlugInRows(diagnosticsMode, out servedByLiveManager).Where(r => r != null).Select(CloneSensorRow).ToList();
        // Only cache rows a live plug-in manager actually produced. If a preference save disposed the
        // manager while this call was in flight, the empty result says nothing about the plug-ins, and
        // caching it would suppress every plug-in reading until the interval expires - up to five
        // minutes with the app in the tray. Leaving the cache untouched makes the next refresh re-read.
        if (!diagnosticsMode && servedByLiveManager)
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

    // Decided from the curve's temperature identifier alone, not from the row cache: plug-in
    // identifiers never start with "/" (Docs/Plug-In-development.md) and LibreHardwareMonitor's
    // always do. Consulting the cache's contents instead meant that a cache filled while the plug-in
    // had not yet produced its temperature rows (a status-only first snapshot, a hub still in
    // hardware mode) kept the five-minute interval for the whole five minutes - in precisely the
    // start-minimized case this exemption exists for - because nothing clears that cache when a
    // plug-in's rows change.
    private bool AnyEnabledFanCurveUsesPlugInReading()
    {
        var curves = settings == null ? null : settings.FanCurves;
        if (curves == null || curves.Count == 0)
        {
            return false;
        }

        foreach (var curve in curves)
        {
            if (curve == null || !curve.Enabled || curve.SuspendedByManualControl || string.IsNullOrWhiteSpace(curve.TemperatureReadingKey))
            {
                continue;
            }

            var identifier = IdentifierFromSettingsKey(curve.TemperatureReadingKey);
            if (!string.IsNullOrWhiteSpace(identifier) && !identifier.StartsWith("/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
