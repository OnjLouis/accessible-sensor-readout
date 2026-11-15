using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[Flags]
internal enum SensorChange
{
    None = 0,
    Storage = 1,
    Power = 2,
    Network = 4
}

public sealed partial class SensorReadoutForm
{
    private const int ChangeSettleMilliseconds = 650;
    private const int PowerBroadcast = 0x0218;
    private const int PowerSettingChange = 0x8013;
    private const uint WlanAcm = 8;
    private const uint WlanConnectionComplete = 10;
    private const uint WlanDisconnected = 21;
    private static readonly Guid PowerSourceSetting = new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    private System.Windows.Forms.Timer sensorChangeTimer;
    private SensorChange settlingSensorChanges;
    private SensorChange readySensorChanges;
    private IntPtr powerSettingNotification;
    private IntPtr wifiNotificationHandle;
    private WlanChangeCallback wifiChangeCallback;
    private volatile bool sensorChangesListening;
    private int networkChangePosted;

    private void RegisterSensorChangeNotifications()
    {
        sensorChangeTimer = new System.Windows.Forms.Timer { Interval = ChangeSettleMilliseconds };
        sensorChangeTimer.Tick += delegate
        {
            sensorChangeTimer.Stop();
            readySensorChanges |= settlingSensorChanges;
            settlingSensorChanges = SensorChange.None;
            TryRefreshChangedSensors();
        };
        sensorChangesListening = true;
        var powerSource = PowerSourceSetting;
        powerSettingNotification = RegisterPowerSettingNotification(Handle, ref powerSource, 0);
        try { NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged; }
        catch (NetworkInformationException) { }

        try
        {
            uint version, previous;
            if (WlanOpenHandle(2, IntPtr.Zero, out version, out wifiNotificationHandle) != 0)
                return;
            wifiChangeCallback = OnWifiNotification;
            if (WlanRegisterNotification(wifiNotificationHandle, WlanAcm, true, wifiChangeCallback,
                IntPtr.Zero, IntPtr.Zero, out previous) != 0)
                CloseWifiChangeNotifications();
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { CloseWifiChangeNotifications(); }
    }

    private void StopSensorChangeNotifications()
    {
        if (!sensorChangesListening) return;
        sensorChangesListening = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        CloseWifiChangeNotifications();
        if (powerSettingNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(powerSettingNotification);
            powerSettingNotification = IntPtr.Zero;
        }
        sensorChangeTimer.Dispose();
        sensorChangeTimer = null;
    }

    private void CloseWifiChangeNotifications()
    {
        if (wifiNotificationHandle != IntPtr.Zero)
        {
            WlanCloseHandle(wifiNotificationHandle, IntPtr.Zero);
            wifiNotificationHandle = IntPtr.Zero;
        }
        wifiChangeCallback = null;
    }

    private void OnWifiNotification(ref WlanChangeData data, IntPtr context)
    {
        if (data.Source == WlanAcm && (data.Code == WlanConnectionComplete || data.Code == WlanDisconnected))
            PostNetworkChange();
    }

    private void OnNetworkAddressChanged(object sender, EventArgs args)
    {
        PostNetworkChange();
    }

    private void PostNetworkChange()
    {
        if (!sensorChangesListening || Interlocked.Exchange(ref networkChangePosted, 1) != 0) return;
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                Interlocked.Exchange(ref networkChangePosted, 0);
                QueueSensorChange(SensorChange.Network);
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref networkChangePosted, 0); }
    }

    private void CheckSensorChangeNotification(Message message)
    {
        if (!sensorChangesListening) return;
        if (DiskInterfaceNotifications.IsDiskChange(message, true) || DiskInterfaceNotifications.IsVolumeChange(message))
            QueueSensorChange(SensorChange.Storage);
        else if (message.Msg == PowerBroadcast && message.WParam.ToInt32() == PowerSettingChange &&
            message.LParam != IntPtr.Zero && (Guid)Marshal.PtrToStructure(message.LParam, typeof(Guid)) == PowerSourceSetting)
            QueueSensorChange(SensorChange.Power);
    }

    private void QueueSensorChange(SensorChange change)
    {
        if (!sensorChangesListening) return;
        settlingSensorChanges |= change;
        if (!sensorChangeTimer.Enabled) sensorChangeTimer.Start();
    }

    private void TryRefreshChangedSensors()
    {
        if (!sensorChangesListening || readySensorChanges == SensorChange.None || refreshInProgress ||
            pendingRefreshRequested || localRows.Count == 0 ||
            !CanRefreshSensorChanges(settings.AutoRefreshEnabled, reportViewMode, ContainsFocus, settings.RefreshWhileFocused)) return;
        RefreshSensors(true, false, "system change", true);
    }

    private static bool CanRefreshSensorChanges(bool autoRefresh, bool viewingReport, bool focused, bool refreshWhileFocused)
    {
        return autoRefresh && !viewingReport && (!focused || refreshWhileFocused);
    }

    private static IEnumerable<SensorRow> MarkSensorChangeRows(IEnumerable<SensorRow> rows, SensorChange section)
    {
        foreach (var row in rows)
        {
            if (row == null) continue;
            row.ChangeSection = section;
            yield return row;
        }
    }

    private static IEnumerable<SensorRow> KeepUnchangedSensorRows(IEnumerable<SensorRow> previousRows, SensorChange changes)
    {
        return previousRows.Where(row => (row.ChangeSection & changes) == 0 &&
            !(row.Identifier ?? "").StartsWith("data-source/", StringComparison.Ordinal) &&
            !(row.Identifier ?? "").StartsWith("spoken-hotkeys|", StringComparison.Ordinal));
    }

    private static List<SensorRow> MergeChangedSensorRows(IEnumerable<SensorRow> previousRows, SensorChange changes, List<SensorRow> freshRows)
    {
        return KeepUnchangedSensorRows(previousRows, changes)
            .Concat(ConsolidateRelatedRows(freshRows))
            .GroupBy(SensorDeduplicationKey)
            .Select(group => group.First()).ToList();
    }

    private List<SensorRow> CollectChangedSensorRows(SensorChange changes, List<SensorRow> previousRows)
    {
        var rows = new List<SensorRow>();
        if ((changes & SensorChange.Storage) != 0)
        {
            rows.AddRange(RefreshWindowsStorageInventory());
            rows.AddRange(MarkSensorChangeRows(GetWindowsLogicalDiskRows().Concat(GetLogicalDiskPerformanceRows()), SensorChange.Storage));
            rows = AttachStorageDetailsToRows(rows);
        }
        if ((changes & SensorChange.Power) != 0)
            rows.AddRange(MarkSensorChangeRows(GetBatteryRows(false), SensorChange.Power));
        if ((changes & SensorChange.Network) != 0)
            rows.AddRange(MarkSensorChangeRows(GetNetworkAdapterRows(false), SensorChange.Network));

        return FinishSensorRows(MergeChangedSensorRows(previousRows, changes, rows));
    }

    private List<SensorRow> RefreshWindowsStorageInventory()
    {
        cachedStorageTopologyDetailsUtc = DateTime.MinValue;
        var rows = MarkSensorChangeRows(GetWindowsSmartRows(), SensorChange.Storage).ToList();
        lock (slowRowsLock)
        {
            cachedSlowRows.RemoveAll(row => row.ChangeSection == SensorChange.Storage);
            cachedSlowRows.InsertRange(0, rows.Select(CloneSensorRow));
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanChangeData
    {
        internal uint Source;
        internal uint Code;
        internal Guid InterfaceGuid;
        internal uint DataSize;
        internal IntPtr Data;
    }

    private delegate void WlanChangeCallback(ref WlanChangeData data, IntPtr context);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanRegisterNotification(IntPtr handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicates,
        WlanChangeCallback callback, IntPtr context, IntPtr reserved, out uint previousSource);

    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr notification);
}
