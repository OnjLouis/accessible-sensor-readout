using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private static void ConfigureWmiTelemetry(bool enabled, Action<string> logger)
    {
        lock (WmiTelemetryLock)
        {
            wmiTelemetryEnabled = enabled && logger != null;
            wmiTelemetryLogger = wmiTelemetryEnabled ? logger : null;
        }
    }

    private sealed class WmiQueryResult : IEnumerable<ManagementObject>, IDisposable
    {
        private ManagementObjectCollection collection;
        private bool enumerated;

        public WmiQueryResult(ManagementObjectCollection collection)
        {
            this.collection = collection;
        }

        public IEnumerator<ManagementObject> GetEnumerator()
        {
            if (enumerated || collection == null)
            {
                return Enumerable.Empty<ManagementObject>().GetEnumerator();
            }

            enumerated = true;
            return new WmiObjectEnumerator(this, collection.GetEnumerator());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
            var ownedCollection = collection;
            collection = null;
            if (ownedCollection != null)
            {
                ownedCollection.Dispose();
            }
        }

        private sealed class WmiObjectEnumerator : IEnumerator<ManagementObject>
        {
            private WmiQueryResult owner;
            private readonly IEnumerator inner;
            private ManagementObject current;

            public WmiObjectEnumerator(WmiQueryResult owner, IEnumerator inner)
            {
                this.owner = owner;
                this.inner = inner;
            }

            public ManagementObject Current { get { return current; } }
            object IEnumerator.Current { get { return Current; } }

            public bool MoveNext()
            {
                DisposeCurrent();
                while (inner.MoveNext())
                {
                    current = inner.Current as ManagementObject;
                    if (current != null)
                        return true;
                }

                Dispose();
                return false;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                DisposeCurrent();
                var disposable = inner as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }

                var ownedResult = owner;
                owner = null;
                if (ownedResult != null)
                {
                    ownedResult.Dispose();
                }
            }

            private void DisposeCurrent()
            {
                if (current != null)
                {
                    current.Dispose();
                    current = null;
                }
            }
        }
    }

    private static WmiQueryResult ExecuteWmiQuery(ManagementObjectSearcher searcher, string context, [CallerMemberName] string caller = "")
    {
        if (searcher == null)
        {
            return new WmiQueryResult(null);
        }

        var stopwatch = Stopwatch.StartNew();
        var effectiveContext = string.IsNullOrWhiteSpace(context) || string.Equals(context, "WMI", StringComparison.OrdinalIgnoreCase)
            ? caller
            : context;
        var scope = GetWmiScopeText(searcher);
        var query = GetWmiQueryText(searcher);
        try
        {
            if (searcher.Options != null && searcher.Options.Timeout == ManagementOptions.InfiniteTimeout)
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(30);
            }

            var result = searcher.Get();
            stopwatch.Stop();
            LogWmiTelemetry(effectiveContext, scope, query, stopwatch.ElapsedMilliseconds, null);
            return new WmiQueryResult(result);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogWmiTelemetry(effectiveContext, scope, query, stopwatch.ElapsedMilliseconds, ex);
            throw;
        }
    }

    private static void LogWmiTelemetry(string context, string scope, string query, long elapsedMs, Exception failure)
    {
        Action<string> logger;
        lock (WmiTelemetryLock)
        {
            if (!wmiTelemetryEnabled || wmiTelemetryLogger == null)
            {
                return;
            }

            logger = wmiTelemetryLogger;
        }

        var message = "WMI query " + (failure == null ? "completed" : "failed") +
            ": context=" + SanitizeWmiLogPart(context) +
            "; scope=" + SanitizeWmiLogPart(scope) +
            "; query=" + SanitizeWmiLogPart(query) +
            "; elapsed=" + elapsedMs + " ms";
        if (failure != null)
        {
            message += "; error=" + failure.GetType().Name + ": " + SanitizeWmiLogPart(failure.Message);
        }

        logger(message);
    }

    private static string GetWmiScopeText(ManagementObjectSearcher searcher)
    {
        try
        {
            return searcher.Scope == null || searcher.Scope.Path == null
                ? ""
                : Convert.ToString(searcher.Scope.Path);
        }
        catch
        {
            return "";
        }
    }

    private static string GetWmiQueryText(ManagementObjectSearcher searcher)
    {
        try
        {
            return searcher.Query == null ? "" : searcher.Query.QueryString;
        }
        catch
        {
            return "";
        }
    }

    private static string SanitizeWmiLogPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = Regex.Replace(value.Trim(), "\\s+", " ");
        return text.Length <= 500 ? text : text.Substring(0, 500) + "...";
    }

    private void LogWmiProviderProcessSnapshot()
    {
        if (!ShouldLog("Debug"))
        {
            return;
        }

        try
        {
            var providers = Process.GetProcessesByName("WmiPrvSE");
            if (providers == null || providers.Length == 0)
            {
                LogMessage("Debug", "WMI provider processes: none running.");
                return;
            }

            var snapshots = new List<KeyValuePair<int, long>>();
            foreach (var provider in providers)
            {
                using (provider)
                {
                    long privateBytes = 0;
                    try
                    {
                        privateBytes = provider.PrivateMemorySize64;
                    }
                    catch
                    {
                    }

                    snapshots.Add(new KeyValuePair<int, long>(provider.Id, privateBytes));
                }
            }

            var largest = snapshots.OrderByDescending(p => p.Value).First();
            var totalBytes = snapshots.Sum(p => p.Value);
            LogMessage("Debug", "WMI provider processes: count=" + snapshots.Count +
                "; largestPid=" + largest.Key +
                "; largestPrivate=" + FormatBytes(largest.Value) +
                "; totalPrivate=" + FormatBytes(totalBytes) + ".");
        }
        catch (Exception ex)
        {
            LogMessage("Debug", "WMI provider process snapshot failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string CleanWmiText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static bool IsGenericSystemModel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        return string.Equals(text, "System Product Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "System Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By OEM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Default string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Not Available", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static object GetWmiPropertyValue(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            var property = obj == null ? null : obj.Properties[propertyName];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWmiPropertyText(ManagementBaseObject obj, string propertyName)
    {
        return CleanWmiText(Convert.ToString(GetWmiPropertyValue(obj, propertyName)));
    }

    private static string FormatMajorMinor(object majorValue, object minorValue, bool allow255)
    {
        int major;
        int minor;
        if (!TryConvertToInt32(majorValue, out major) || !TryConvertToInt32(minorValue, out minor))
        {
            return "";
        }

        if (!allow255 && major == 255 && minor == 255)
        {
            return "";
        }

        if (major < 0 || minor < 0)
        {
            return "";
        }

        return major + "." + minor;
    }

    private static bool TryConvertToInt32(object value, out int result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        try
        {
            result = Convert.ToInt32(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertToInt64(object value, out long result)
    {
        result = 0;
        if (value == null)
        {
            return false;
        }

        try
        {
            result = Convert.ToInt64(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetFirmwareMode()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
            {
                int firmwareType;
                if (!TryConvertToInt32(key == null ? null : key.GetValue("PEFirmwareType"), out firmwareType))
                {
                    return "";
                }

                if (firmwareType == 1) return "Legacy BIOS";
                if (firmwareType == 2) return "UEFI";
            }
        }
        catch
        {
        }

        return "";
    }

    private static string GetSecureBootState()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            {
                int enabled;
                if (!TryConvertToInt32(key == null ? null : key.GetValue("UEFISecureBootEnabled"), out enabled))
                {
                    return "";
                }

                return enabled == 0 ? "Off" : "On";
            }
        }
        catch
        {
            return "";
        }
    }

    private static string FormatWmiDate(object value)
    {
        DateTime parsed;
        return TryParseWmiDate(value, out parsed) ? parsed.ToString("yyyy-MM-dd") : "";
    }

    private static string FormatWmiDateWithAge(object value)
    {
        DateTime parsed;
        return TryParseWmiDate(value, out parsed) ? FormatDateTimeWithAge(parsed, false) : "";
    }

    private static string FormatDateTimeWithAge(DateTime value, bool includeTime)
    {
        var text = includeTime
            ? value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTime.Now;
        if (value > now.AddMinutes(1))
        {
            return text;
        }

        var age = FormatRecentElapsedAge(value, now);
        return string.IsNullOrWhiteSpace(age) ? text : text + " (" + age + ")";
    }

    private static string FormatRecentElapsedAge(DateTime value, DateTime now)
    {
        if (value > now)
        {
            return "just now";
        }

        var elapsed = now - value;
        if (elapsed.TotalMinutes < 1)
        {
            var seconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds));
            return seconds <= 1 ? "just now" : seconds + " seconds ago";
        }

        if (elapsed.TotalHours < 1)
        {
            var minutes = Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes));
            return minutes + " minute" + (minutes == 1 ? "" : "s") + " ago";
        }

        if (elapsed.TotalDays < 1)
        {
            var hours = Math.Max(1, (int)Math.Floor(elapsed.TotalHours));
            var minutes = elapsed.Minutes;
            var parts = new List<string>();
            AddAgePart(parts, hours, "hour");
            AddAgePart(parts, minutes, "minute");
            return string.Join(", ", parts.ToArray()) + " ago";
        }

        var startDate = value.Date;
        var endDate = now.Date;
        var totalDays = Math.Max(0, (endDate - startDate).Days);
        return FormatElapsedDateAge(startDate, endDate, totalDays);
    }

    private static bool TryParseWmiDate(object value, out DateTime parsed)
    {
        parsed = DateTime.MinValue;
        if (value is DateTime)
        {
            parsed = (DateTime)value;
            return true;
        }

        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            text = text.Trim();
            var looksLikeDmtf = text.Length >= 14 && text.Take(14).All(char.IsDigit);
            if (looksLikeDmtf)
            {
                parsed = ManagementDateTimeConverter.ToDateTime(text);
                return true;
            }

            return DateTime.TryParse(text, out parsed);
        }
        catch
        {
            parsed = DateTime.MinValue;
            return false;
        }
    }
}
