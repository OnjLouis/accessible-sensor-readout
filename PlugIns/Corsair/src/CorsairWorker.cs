using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// The plug-in's single background worker: it owns every Corsair device session in this
    /// process, polls them on its own thread, and publishes immutable snapshots that the host's
    /// refresh thread reads without ever touching hardware.
    ///
    /// Why a worker at all: one hub sensor refresh can block for tens of seconds in the worst case
    /// (a contended cross-process guard plus HID timeouts on several transactions), and the host
    /// calls <c>GetReadings</c> on a thread the user interface waits on. So <see cref="GetSnapshot"/>
    /// only copies memory, and every control call is bounded at
    /// <see cref="DeviceLockTimeoutMs"/> ms (<see cref="Monitor.TryEnter(object,int)"/>) rather than
    /// inheriting the device layer's worst case.
    ///
    /// Read-only by default, exactly like the device classes underneath. The worker never sends a
    /// mode change or a duty on its own initiative:
    /// <list type="bullet">
    /// <item>a hub is only put into software mode by <see cref="SetHubChannelPercent"/>, or by a
    /// resume path that re-asserts control this worker had already recorded taking;</item>
    /// <item>the PSU fan registers are only written by <see cref="SetPsuFanPercent"/> /
    /// <see cref="ResetPsuFan"/>, or by the same recorded-intent resume path. In particular nothing
    /// here ever pushes an initial 0 %, which on a Corsair PSU means "mode 0x00" and would take the
    /// fan away from whatever program already drives it.</item>
    /// </list>
    ///
    /// The worker also carries the user's intent across reconnects. A freshly constructed device
    /// object starts with enumeration-default percentages and no ownership, so after a
    /// dispose-and-rescan (device unplugged, machine resumed from standby) the worker replays what
    /// it recorded: <c>ReassertControl</c> first, then each non-default channel percentage, and for
    /// the PSU the last requested manual duty.
    ///
    /// Locking: <c>deviceLock</c> guards the device lists and every call into a device;
    /// <c>snapshotLock</c> guards the published snapshot only. They are never held in the other
    /// order and <c>snapshotLock</c> is never held across a device call, so there is no inversion.
    /// </summary>
    public sealed class CorsairWorker
    {
        // Product ids this plug-in talks to. Deliberately an allow-list: other Corsair HID devices
        // (keyboards, the 0x0C4E interface that sits next to the hub, ...) speak different
        // protocols and must never be opened by this plug-in.
        private const ushort HubProductId = 0x0C3F;
        private static readonly ushort[] PsuProductIds =
        {
            0x1C03, 0x1C04, 0x1C05, 0x1C06, 0x1C07, 0x1C08, 0x1C09, 0x1C0A,
            0x1C0B, 0x1C0C, 0x1C0D, 0x1C1E, 0x1C1F, 0x1C23, 0x1C27
        };

        // Tick pacing.
        private const int TickIntervalOwnedMs = 1000;   // a curve is being driven: keep it responsive
        private const int TickIntervalIdleMs = 2000;    // read-only monitoring
        private const int ScanIntervalMs = 30000;       // re-scan cadence while nothing was found
        private const int PresentRescanMs = 300000;     // slow re-scan so a hot-plugged device is noticed
        private const int PausedWaitMs = 1000;
        private const int DormantWaitMs = 60000;

        // Per-device failure backoff (brief step 1).
        private const int MaxConsecutiveFailures = 5;
        private const int DeviceBackoffMs = 30000;

        // Idle dormancy: with no host contact for this long and nothing under this plug-in's
        // control, there is nobody to show readings to, so polling stops entirely.
        private const int DormancyIdleMs = 15 * 60 * 1000;

        // A host thread may never block longer than this on the worker's device lock.
        private const int DeviceLockTimeoutMs = 5000;

        // Shutdown budget. The thread is a background thread, so an unresponsive tick is abandoned
        // rather than waited out; the restores below matter more than a clean join.
        private const int WorkerJoinMs = 2000;
        private const int ShutdownDeviceLockMs = 1000;

        // Annex §2/§10: a resumed machine needs a moment before its HID stack answers again.
        private const int ResumeDelayMs = 3000;

        private const int ForceRefreshPollMs = 20;

        // Mirrors CorsairHidPsuDevice's own threshold (annex §7.2). Anything below it means "give
        // the fan back to the PSU", which is never something to replay as a restore.
        private const int PsuManualThresholdPercent = 30;

        private const string StartingUpStatus = "Corsair support is starting up.";
        private const string NoDevicesStatus = "No supported Corsair device was found (iCUE LINK Hub or HXi/RMi power supply).";
        private const string NoGuardStatus = "Corsair support could not create the shared Corsair device guard, so no device is being read.";
        private const string StoppedStatus = "Corsair support has been shut down.";

        private static readonly object instanceSync = new object();
        private static CorsairWorker instance;

        private readonly object lifecycleLock = new object();
        private readonly object deviceLock = new object();
        private readonly object snapshotLock = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);

        private readonly List<HubEntry> hubs = new List<HubEntry>();
        private readonly List<PsuEntry> psus = new List<PsuEntry>();

        // The user's intent, keyed by device identity and outliving the device objects themselves:
        // a reconnected device is a blank slate, and only this record knows what to put back.
        private readonly Dictionary<string, HubIntent> hubIntents = new Dictionary<string, HubIntent>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PsuIntent> psuIntents = new Dictionary<string, PsuIntent>(StringComparer.OrdinalIgnoreCase);

        // The host recreates its plug-in context on every refresh, so the sink is swapped rather
        // than captured once. Volatile: written by host threads, read by the worker thread.
        private volatile Action<string, string> log;

        private CorsairDeviceGuard guard;       // deviceLock
        private Thread thread;                  // lifecycleLock
        private CorsairSnapshot published;      // snapshotLock

        private volatile bool stopRequested;
        private volatile bool paused;
        private volatile bool resumePending;
        private volatile int resumeAtTicks;
        // True from the start so the very first cycle always scans: nextScanTicks starts at zero,
        // and on a machine that has been up long enough for Environment.TickCount to have wrapped
        // into negative numbers the "is the next scan due" comparison would otherwise say no.
        private volatile bool scanRequested = true;
        private volatile bool dormant;
        private volatile int lastContactTicks;
        private volatile string statusMessage = StartingUpStatus;
        private volatile string lastScanSummary = string.Empty;
        private volatile string lastError = string.Empty;
        private volatile bool hasLastError;
        private DateTime lastErrorUtc;          // only meaningful while hasLastError is true

        // Interlocked / VolatileRead only -- never decorated volatile, because a volatile field
        // cannot be passed by reference to Interlocked without a compiler warning.
        private int startedTicks;
        private int completedTicks;
        private int failedDeviceReads;
        private int lastTickDurationMs;
        private int shutdownState;              // 0 = running, 1 = stopped (idempotence latch)
        private int forceRefreshRequested;      // 0 = no, 1 = one pending; read-and-cleared atomically

        private bool systemEventsHooked;        // lifecycleLock
        private bool appDomainHooked;           // lifecycleLock
        private bool idleOwnedLogged;           // worker thread only
        private int nextScanTicks;              // worker thread only

        private CorsairWorker()
        {
            // So that a worker started on a machine whose uptime already puts Environment.TickCount
            // far from zero does not read as "idle for fifteen minutes" on its very first cycle.
            lastContactTicks = Environment.TickCount;
        }

        /// <summary>
        /// The one worker for this process. The host may construct and discard the plug-in object
        /// itself repeatedly (once per refresh context in some versions), which is precisely why
        /// device sessions and the user's control intent live here instead.
        /// </summary>
        public static CorsairWorker Instance
        {
            get
            {
                lock (instanceSync)
                {
                    if (instance == null)
                    {
                        instance = new CorsairWorker();
                    }

                    return instance;
                }
            }
        }

        // ---- Diagnostics accessors -------------------------------------------------------------
        //
        // Additive to the snapshot contract: the diagnostics view renders these as a "Corsair
        // worker" details bundle. All of them are cheap reads of a single field.

        public bool IsRunning
        {
            get
            {
                lock (lifecycleLock)
                {
                    return thread != null && Interlocked.CompareExchange(ref shutdownState, 0, 0) == 0;
                }
            }
        }

        public bool IsDormant
        {
            get { return dormant; }
        }

        public int StartedTicks
        {
            get { return Thread.VolatileRead(ref startedTicks); }
        }

        public int CompletedTicks
        {
            get { return Thread.VolatileRead(ref completedTicks); }
        }

        public int FailedDeviceReads
        {
            get { return Thread.VolatileRead(ref failedDeviceReads); }
        }

        public int LastTickDurationMs
        {
            get { return Thread.VolatileRead(ref lastTickDurationMs); }
        }

        /// <summary>Most recent unexpected failure, or an empty string when there has not been one.</summary>
        public string LastError
        {
            get { return lastError; }
        }

        /// <summary>When <see cref="LastError"/> was recorded, or null when there has not been one.</summary>
        public DateTime? LastErrorUtc
        {
            get { return hasLastError ? (DateTime?)lastErrorUtc : null; }
        }

        /// <summary>The raw HID enumeration lines from the most recent scan, newline separated.</summary>
        public string LastScanSummary
        {
            get { return lastScanSummary; }
        }

        // ---- Host entry points -----------------------------------------------------------------

        /// <summary>
        /// Starts the worker if it is not running and adopts <paramref name="log"/> as the current
        /// log sink. Safe to call on every host refresh; a null sink simply discards messages.
        /// After <see cref="StopAndRestore"/> this is a no-op -- the process is going away and
        /// re-opening devices then would be the opposite of helpful.
        /// </summary>
        public void EnsureStarted(Action<string, string> log)
        {
            this.log = log;
            NoteContact();

            if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
            {
                return;
            }

            lock (lifecycleLock)
            {
                if (thread == null)
                {
                    HookSystemEvents();
                    HookAppDomain();

                    var worker = new Thread(WorkerLoop);
                    worker.IsBackground = true;
                    worker.Name = "CorsairPlugInWorker";
                    thread = worker;
                    worker.Start();
                }
            }
        }

        /// <summary>
        /// The last completed tick, deep cloned. Never blocks on hardware and never returns null.
        /// </summary>
        public CorsairSnapshot GetSnapshot()
        {
            NoteContact();

            lock (snapshotLock)
            {
                if (published == null)
                {
                    var empty = new CorsairSnapshot();
                    empty.CapturedUtc = DateTime.UtcNow;
                    empty.Status = statusMessage;
                    empty.Hubs = new List<HubSnapshot>();
                    empty.Psus = new List<PsuSnapshot>();
                    return empty;
                }

                return published.Clone();
            }
        }

        /// <summary>
        /// Wakes the worker and waits up to <paramref name="waitMs"/> ms for a tick that started
        /// after this call to finish, so a diagnostics view reports what the hardware says right
        /// now rather than what it said up to two seconds ago. Returns false on timeout; the
        /// caller then simply renders the previous snapshot.
        /// </summary>
        public bool ForceRefresh(int waitMs)
        {
            NoteContact();

            if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
            {
                return false;
            }

            lock (lifecycleLock)
            {
                if (thread == null)
                {
                    return false;
                }
            }

            // Ticks started, read before the request. A tick may be in flight, in which case its
            // data predates this call; waiting for completion number startedBefore + 1 is what
            // makes the answer genuinely fresh in both cases.
            var startedBefore = Thread.VolatileRead(ref startedTicks);
            Interlocked.Exchange(ref forceRefreshRequested, 1);
            wake.Set();

            var budget = waitMs < 0 ? 0 : waitMs;
            var startTicks = Environment.TickCount;
            while (true)
            {
                if (unchecked(Thread.VolatileRead(ref completedTicks) - startedBefore) >= 1)
                {
                    return true;
                }

                // unchecked: Environment.TickCount wraps roughly every 24.9 days and unchecked
                // subtraction still yields the correct elapsed duration across the wraparound.
                if (unchecked(Environment.TickCount - startTicks) >= budget)
                {
                    return unchecked(Thread.VolatileRead(ref completedTicks) - startedBefore) >= 1;
                }

                Thread.Sleep(ForceRefreshPollMs);
            }
        }

        /// <summary>
        /// Takes the device lock for a control call, or reports why it could not. A tick can hold
        /// the lock for as long as one device refresh takes, and a device refresh can in the worst
        /// case take tens of seconds (a contended cross-process guard plus HID timeouts). The host
        /// calls control methods from a thread the user interface waits on, so the wait is bounded
        /// and a timeout is an honest failure rather than a frozen window.
        /// </summary>
        private bool TryEnterForControl(string what)
        {
            if (Monitor.TryEnter(deviceLock, DeviceLockTimeoutMs))
            {
                return true;
            }

            Log("Error", "Corsair plug-in: the Corsair worker was busy for " + DeviceLockTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + " ms, so " + what + " was not applied.");
            return false;
        }

        /// <summary>
        /// Sets one hub channel's duty. Bounded by <see cref="TryEnterForControl"/>.
        /// </summary>
        public bool SetHubChannelPercent(string serial, int channel, int percent)
        {
            NoteContact();

            if (string.IsNullOrEmpty(serial))
            {
                return false;
            }

            if (!TryEnterForControl("the duty change for iCUE LINK hub " + serial + " channel "
                + channel.ToString(CultureInfo.InvariantCulture)))
            {
                return false;
            }

            try
            {
                var entry = FindHub(serial);
                if (entry == null)
                {
                    Log("Debug", "Corsair plug-in: no iCUE LINK hub with serial " + serial + " is connected; the duty change was ignored.");
                    return false;
                }

                var ok = entry.Device.SetChannelPercent(channel, percent);
                RecordHubIntent(entry);
                PublishSnapshot(BuildSnapshot());
                return ok;
            }
            finally
            {
                Monitor.Exit(deviceLock);
            }
        }

        /// <summary>
        /// Returns one hub channel to its default duty. When this plug-in does not own the hub the
        /// device layer treats this as bookkeeping and sends nothing, which is what keeps a reset
        /// from stealing the hub from its real owner.
        /// </summary>
        public bool ResetHubChannel(string serial, int channel)
        {
            NoteContact();

            if (string.IsNullOrEmpty(serial))
            {
                return false;
            }

            if (!TryEnterForControl("the reset of iCUE LINK hub " + serial + " channel "
                + channel.ToString(CultureInfo.InvariantCulture)))
            {
                return false;
            }

            try
            {
                var entry = FindHub(serial);
                if (entry == null)
                {
                    Log("Debug", "Corsair plug-in: no iCUE LINK hub with serial " + serial + " is connected; the reset was ignored.");
                    return false;
                }

                var ok = entry.Device.ResetChannel(channel);
                RecordHubIntent(entry);
                PublishSnapshot(BuildSnapshot());
                return ok;
            }
            finally
            {
                Monitor.Exit(deviceLock);
            }
        }

        /// <summary>
        /// Sets the PSU fan duty. A percentage below the PSU's 30 % manual floor means "hand the fan
        /// back to the PSU's own curve" (annex §7.2), which is also how zero-RPM behaviour returns.
        /// </summary>
        public bool SetPsuFanPercent(string pidHex, int percent)
        {
            NoteContact();

            if (string.IsNullOrEmpty(pidHex))
            {
                return false;
            }

            if (!TryEnterForControl("the fan duty change for Corsair PSU " + pidHex))
            {
                return false;
            }

            try
            {
                var entry = FindPsu(pidHex);
                if (entry == null)
                {
                    Log("Debug", "Corsair plug-in: no Corsair PSU with product id " + pidHex + " is connected; the fan duty change was ignored.");
                    return false;
                }

                var ok = entry.Device.SetFanPercent(percent);
                RecordPsuIntent(entry, ok && percent < PsuManualThresholdPercent);
                PublishSnapshot(BuildSnapshot());
                return ok;
            }
            finally
            {
                Monitor.Exit(deviceLock);
            }
        }

        /// <summary>
        /// Returns the PSU fan to the PSU's own control (duty 0, then mode 0x00).
        /// </summary>
        public bool ResetPsuFan(string pidHex)
        {
            NoteContact();

            if (string.IsNullOrEmpty(pidHex))
            {
                return false;
            }

            if (!TryEnterForControl("the fan reset for Corsair PSU " + pidHex))
            {
                return false;
            }

            try
            {
                var entry = FindPsu(pidHex);
                if (entry == null)
                {
                    Log("Debug", "Corsair plug-in: no Corsair PSU with product id " + pidHex + " is connected; the fan reset was ignored.");
                    return false;
                }

                var ok = entry.Device.ResetFan();
                RecordPsuIntent(entry, ok);
                PublishSnapshot(BuildSnapshot());
                return ok;
            }
            finally
            {
                Monitor.Exit(deviceLock);
            }
        }

        /// <summary>
        /// Stops the worker and hands every device back to whatever was driving it. Idempotent, and
        /// safe to call from a ProcessExit handler.
        ///
        /// Order matters and is deliberate:
        /// <list type="number">
        /// <item>signal the loop and join briefly -- the thread is a background thread, so an
        /// unresponsive tick is abandoned rather than waited out;</item>
        /// <item>PSU restore first: a PSU left in manual mode stays there until something writes
        /// 0xF0 = 0x00 or it is power-cycled, so it is the one real hazard of a killed process. The
        /// hub is second because it reverts on its own once nothing keeps writing to it;</item>
        /// <item>hub restore, requesting hardware mode only when this plug-in actually took the
        /// hub;</item>
        /// <item>any remaining session closed, releasing its HID handle;</item>
        /// <item>the shared guard disposed last, because everything above needs it.</item>
        /// </list>
        /// </summary>
        public void StopAndRestore()
        {
            if (Interlocked.Exchange(ref shutdownState, 1) != 0)
            {
                return;
            }

            // Before the restores (a power event arriving mid-shutdown could only schedule work
            // that will never run).
            UnhookSystemEvents();
            UnhookAppDomain();

            stopRequested = true;
            try
            {
                wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            Thread worker;
            lock (lifecycleLock)
            {
                worker = thread;
                thread = null;
            }

            if (worker != null)
            {
                try
                {
                    if (!worker.Join(WorkerJoinMs))
                    {
                        Log("Debug", "Corsair plug-in: the Corsair worker thread did not stop within "
                            + WorkerJoinMs.ToString(CultureInfo.InvariantCulture)
                            + " ms; it is a background thread, so shutdown continues without it.");
                    }
                }
                catch (Exception ex)
                {
                    Log("Debug", "Corsair plug-in: waiting for the Corsair worker thread to stop threw (" + ex.Message + ").");
                }
            }

            var lockHeld = false;
            try
            {
                lockHeld = Monitor.TryEnter(deviceLock, ShutdownDeviceLockMs);
                if (!lockHeld)
                {
                    // Degenerate case: the worker never joined and still holds the lock. Continue
                    // anyway -- the device sessions serialize themselves internally, and leaving a
                    // PSU in manual mode is worse than a contended shutdown.
                    Log("Debug", "Corsair plug-in: the Corsair worker still held the device lock at shutdown; restoring anyway.");
                }

                RestorePsusAtShutdown();
                RestoreHubsAtShutdown();
                CloseRemainingSessions();
            }
            catch (Exception ex)
            {
                Log("Error", "Corsair plug-in: the Corsair shutdown restore threw (" + ex.Message + "); the devices may keep their last settings.");
            }
            finally
            {
                if (lockHeld)
                {
                    Monitor.Exit(deviceLock);
                }
            }

            // Last: CorsairDeviceGuard's methods do not survive its disposal, so nothing above may
            // still need it.
            CorsairDeviceGuard localGuard;
            lock (deviceLock)
            {
                localGuard = guard;
                guard = null;
            }

            if (localGuard != null)
            {
                try
                {
                    localGuard.Dispose();
                }
                catch (Exception ex)
                {
                    Log("Debug", "Corsair plug-in: disposing the Corsair device guard threw (" + ex.Message + ").");
                }
            }

            // Replace the last snapshot rather than leaving devices on screen that are no longer
            // open: a host that keeps asking after shutdown should be told so, not shown stale rows.
            statusMessage = StoppedStatus;
            PublishSnapshot(BuildEmptySnapshot());

            Log("Debug", "Corsair plug-in: the Corsair worker has stopped and every device session is closed.");
        }

        // Step 2: PSUs that this worker put into manual mode, restored first.
        private void RestorePsusAtShutdown()
        {
            for (var i = 0; i < psus.Count; i++)
            {
                var entry = psus[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                var intent = FindPsuIntent(entry.PidHex);
                if (intent == null || !intent.EverSetManual)
                {
                    continue;
                }

                // IsGone is read before Disconnect on purpose: Disconnect clears it, and a device
                // that has already vanished must not be asked to accept a restore write.
                if (entry.Device.IsGone)
                {
                    continue;
                }

                Log("Debug", "Corsair plug-in: returning the fan of Corsair PSU " + entry.PidHex + " to automatic control before shutdown.");
                try
                {
                    entry.Device.Disconnect(true);
                }
                catch (Exception ex)
                {
                    Log("Error", "Corsair plug-in: the shutdown restore on Corsair PSU " + entry.PidHex + " threw (" + ex.Message + ").");
                }

                entry.Closed = true;
            }
        }

        // Step 3: hubs. The hub firmware falls back to its own profile once nothing drives it, so
        // this is the less urgent of the two restores.
        private void RestoreHubsAtShutdown()
        {
            for (var i = 0; i < hubs.Count; i++)
            {
                var entry = hubs[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                var intent = FindHubIntent(entry.Serial);
                var owned = entry.Device.OwnsSoftwareControl || (intent != null && intent.EverOwned);
                var gone = entry.Device.IsGone;     // before Disconnect: Disconnect clears it

                try
                {
                    entry.Device.Disconnect(owned && !gone);
                }
                catch (Exception ex)
                {
                    Log("Error", "Corsair plug-in: the shutdown restore on iCUE LINK hub " + entry.Serial + " threw (" + ex.Message + ").");
                }

                entry.Closed = true;
            }
        }

        // Step 4: whatever is left just needs its HID handle released.
        private void CloseRemainingSessions()
        {
            for (var i = 0; i < psus.Count; i++)
            {
                var entry = psus[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                try
                {
                    entry.Device.Disconnect(false);
                }
                catch (Exception ex)
                {
                    Log("Debug", "Corsair plug-in: closing Corsair PSU " + entry.PidHex + " threw (" + ex.Message + ").");
                }

                entry.Closed = true;
            }

            hubs.Clear();
            psus.Clear();
        }

        // ---- Worker thread ---------------------------------------------------------------------

        private void WorkerLoop()
        {
            Log("Debug", "Corsair plug-in: the Corsair worker thread has started.");

            while (!stopRequested)
            {
                var waitMs = TickIntervalIdleMs;
                try
                {
                    waitMs = RunCycle();
                }
                catch (Exception ex)
                {
                    // A throw must never take the thread down: the plug-in would then silently
                    // report nothing for the rest of the session.
                    NoteError("the Corsair worker tick", ex);
                }

                if (stopRequested)
                {
                    break;
                }

                wake.WaitOne(waitMs);
            }

            Log("Debug", "Corsair plug-in: the Corsair worker thread is exiting.");
        }

        /// <summary>
        /// One pass of the loop. Returns how long to wait before the next one.
        /// </summary>
        private int RunCycle()
        {
            if (paused)
            {
                return PausedWaitMs;
            }

            if (resumePending)
            {
                // unchecked: TickCount wraps roughly every 24.9 days; the subtraction is still
                // correct across the wraparound.
                if (unchecked(Environment.TickCount - resumeAtTicks) < 0)
                {
                    return ForceRefreshPollMs * 10;
                }

                resumePending = false;
                HandleResume();
            }

            var forced = Interlocked.Exchange(ref forceRefreshRequested, 0) != 0;

            if (!forced && ShouldStayDormant())
            {
                return DormantWaitMs;
            }

            if (dormant)
            {
                dormant = false;
                Log("Debug", "Corsair plug-in: the Corsair worker is leaving idle dormancy after fresh host contact; re-scanning for devices.");
                scanRequested = true;
            }

            if (!EnsureGuard())
            {
                statusMessage = NoGuardStatus;
                PublishSnapshot(BuildEmptySnapshot());
                return ScanIntervalMs;
            }

            if (scanRequested || unchecked(Environment.TickCount - nextScanTicks) >= 0)
            {
                scanRequested = false;
                ScanDevices();
            }

            Interlocked.Increment(ref startedTicks);
            var tickStart = Environment.TickCount;

            if (forced)
            {
                ClearBackoffs();
            }

            RefreshAllDevices();

            lock (deviceLock)
            {
                PublishSnapshot(BuildSnapshot());
            }

            Thread.VolatileWrite(ref lastTickDurationMs, unchecked(Environment.TickCount - tickStart));
            Interlocked.Increment(ref completedTicks);

            return NextIntervalMs();
        }

        /// <summary>
        /// True while there has been no host contact for <see cref="DormancyIdleMs"/> and nothing is
        /// under this plug-in's control. Something being controlled keeps the poll alive regardless:
        /// the hub's software mode has no keep-alive requirement, but a curve the user configured is
        /// live state worth watching even when no window is open.
        /// </summary>
        private bool ShouldStayDormant()
        {
            if (unchecked(Environment.TickCount - lastContactTicks) < DormancyIdleMs)
            {
                idleOwnedLogged = false;
                return false;
            }

            if (AnythingOwned())
            {
                if (!idleOwnedLogged)
                {
                    idleOwnedLogged = true;
                    Log("Debug", "Corsair plug-in: nothing has asked the Corsair plug-in for readings in a while, but it still controls a device, so polling continues.");
                }

                return false;
            }

            if (!dormant)
            {
                dormant = true;
                Log("Debug", "Corsair plug-in: nothing has asked the Corsair plug-in for readings for "
                    + (DormancyIdleMs / 60000).ToString(CultureInfo.InvariantCulture)
                    + " minutes and it controls nothing, so it has stopped polling until it is asked again.");
            }

            return true;
        }

        private int NextIntervalMs()
        {
            lock (deviceLock)
            {
                if (hubs.Count == 0 && psus.Count == 0)
                {
                    return ScanIntervalMs;
                }

                for (var i = 0; i < hubs.Count; i++)
                {
                    if (hubs[i].Device != null && hubs[i].Device.OwnsSoftwareControl)
                    {
                        return TickIntervalOwnedMs;
                    }
                }

                return TickIntervalIdleMs;
            }
        }

        private bool AnythingOwned()
        {
            lock (deviceLock)
            {
                for (var i = 0; i < hubs.Count; i++)
                {
                    if (hubs[i].Device != null && hubs[i].Device.OwnsSoftwareControl)
                    {
                        return true;
                    }
                }

                foreach (var intent in hubIntents.Values)
                {
                    if (intent.EverOwned)
                    {
                        return true;
                    }
                }

                foreach (var intent in psuIntents.Values)
                {
                    if (intent.EverSetManual)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private void ClearBackoffs()
        {
            lock (deviceLock)
            {
                var now = Environment.TickCount;
                for (var i = 0; i < hubs.Count; i++)
                {
                    hubs[i].NextDueTicks = now;
                }

                for (var i = 0; i < psus.Count; i++)
                {
                    psus[i].NextDueTicks = now;
                }
            }
        }

        // ---- Device refresh ---------------------------------------------------------------------

        /// <summary>
        /// Refreshes every device, taking and releasing the device lock once per device rather than
        /// once per tick, so a control call from the host waits at most one device's refresh instead
        /// of the whole sweep.
        /// </summary>
        private void RefreshAllDevices()
        {
            List<HubEntry> hubList;
            List<PsuEntry> psuList;
            lock (deviceLock)
            {
                hubList = new List<HubEntry>(hubs);
                psuList = new List<PsuEntry>(psus);
            }

            for (var i = 0; i < hubList.Count && !stopRequested; i++)
            {
                lock (deviceLock)
                {
                    RefreshHub(hubList[i]);
                }
            }

            for (var i = 0; i < psuList.Count && !stopRequested; i++)
            {
                lock (deviceLock)
                {
                    RefreshPsu(psuList[i]);
                }
            }
        }

        private void RefreshHub(HubEntry entry)
        {
            if (entry.Device == null || entry.Closed)
            {
                return;
            }

            if (unchecked(Environment.TickCount - entry.NextDueTicks) < 0)
            {
                return;
            }

            var ok = entry.Device.RefreshSensors();

            if (entry.Device.IsGone)
            {
                DropHub(entry, "it disappeared from the HID bus");
                return;
            }

            NoteDeviceResult(entry, ok, "iCUE LINK hub " + entry.Serial);

            // Keep the recorded intent in step with what the device actually did: the device layer
            // can take software control on its own recovery path, and the shutdown restore has to
            // know about it.
            var intent = FindHubIntent(entry.Serial);
            if (entry.Device.OwnsSoftwareControl)
            {
                if (intent == null)
                {
                    intent = new HubIntent();
                    hubIntents[entry.Serial] = intent;
                }

                intent.EverOwned = true;

                if (entry.Device.LastReadWrongMode)
                {
                    // Belt and braces. The device layer already re-asserts from inside its own
                    // refresh when it owns the hub and a read comes back "hardware mode", so this
                    // is only reachable if that ever stops being true -- and it is safe precisely
                    // because OwnsSoftwareControl says the control is ours to resume.
                    Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial
                        + " still answers in hardware mode while this plug-in owns it; re-asserting control.");
                    if (entry.Device.ReassertControl())
                    {
                        ReapplyHubPercents(entry, intent);
                    }
                }
            }
        }

        private void RefreshPsu(PsuEntry entry)
        {
            if (entry.Device == null || entry.Closed)
            {
                return;
            }

            if (unchecked(Environment.TickCount - entry.NextDueTicks) < 0)
            {
                return;
            }

            // False here means a core read failed (temperatures, fan speed, fan mode). Input
            // voltage and output power are best-effort extras on models that implement them and
            // never influence this result.
            var ok = entry.Device.RefreshSensors();

            if (entry.Device.IsGone)
            {
                DropPsu(entry, "it disappeared from the HID bus");
                return;
            }

            NoteDeviceResult(entry, ok, "Corsair PSU " + entry.PidHex);

            if (entry.Device.RequestedPercent >= PsuManualThresholdPercent)
            {
                RecordPsuIntent(entry, false);
            }
        }

        private void NoteDeviceResult(DeviceEntry entry, bool ok, string what)
        {
            if (ok)
            {
                if (entry.BackedOff)
                {
                    entry.BackedOff = false;
                    Log("Debug", "Corsair plug-in: " + what + " is answering again; it is back on the normal polling interval.");
                }

                entry.ConsecutiveFailures = 0;
                entry.NextDueTicks = Environment.TickCount;
                return;
            }

            Interlocked.Increment(ref failedDeviceReads);
            entry.ConsecutiveFailures++;

            if (entry.ConsecutiveFailures < MaxConsecutiveFailures)
            {
                return;
            }

            // Five failures in a row is a device that is busy, wedged, or owned by a program that
            // will not share. Backing off keeps the log and the HID bus quiet without giving up.
            if (!entry.BackedOff)
            {
                entry.BackedOff = true;
                Log("Debug", "Corsair plug-in: " + what + " has failed " + entry.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture)
                    + " reads in a row; slowing it to one attempt every "
                    + (DeviceBackoffMs / 1000).ToString(CultureInfo.InvariantCulture) + " s until one succeeds.");
            }

            entry.NextDueTicks = unchecked(Environment.TickCount + DeviceBackoffMs);
        }

        private void DropHub(HubEntry entry, string why)
        {
            Log("Debug", "Corsair plug-in: closing the session with iCUE LINK hub " + entry.Serial + " because " + why + ".");
            try
            {
                // restoreHardwareMode: false -- the device is not reachable, so a restore write can
                // only fail, and the intent record keeps what the user asked for.
                entry.Device.Disconnect(false);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: closing iCUE LINK hub " + entry.Serial + " threw (" + ex.Message + ").");
            }

            entry.Closed = true;
            hubs.Remove(entry);
            scanRequested = true;
        }

        private void DropPsu(PsuEntry entry, string why)
        {
            Log("Debug", "Corsair plug-in: closing the session with Corsair PSU " + entry.PidHex + " because " + why + ".");
            try
            {
                entry.Device.Disconnect(false);
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: closing Corsair PSU " + entry.PidHex + " threw (" + ex.Message + ").");
            }

            entry.Closed = true;
            psus.Remove(entry);
            scanRequested = true;
        }

        // ---- Scanning and connecting -------------------------------------------------------------

        private bool EnsureGuard()
        {
            lock (deviceLock)
            {
                if (guard != null)
                {
                    return true;
                }

                try
                {
                    guard = new CorsairDeviceGuard();
                    return true;
                }
                catch (Exception ex)
                {
                    NoteError("creating the shared Corsair device guard", ex);
                    return false;
                }
            }
        }

        private void ScanDevices()
        {
            var summaryBuilder = new StringBuilder();
            List<CorsairHidDeviceInfo> found;
            try
            {
                found = CorsairHidEnumerator.FindCorsairDevices(delegate(string message)
                {
                    if (summaryBuilder.Length > 0)
                    {
                        summaryBuilder.Append(Environment.NewLine);
                    }

                    summaryBuilder.Append(message);
                });
            }
            catch (Exception ex)
            {
                NoteError("enumerating Corsair HID devices", ex);
                found = new List<CorsairHidDeviceInfo>();
            }

            var summary = summaryBuilder.ToString();
            if (!string.Equals(summary, lastScanSummary, StringComparison.Ordinal))
            {
                // Only when the HID picture actually changed: this runs every 30 s while nothing is
                // found, and repeating the same three lines forever would drown the Debug log.
                lastScanSummary = summary;
                if (summary.Length > 0)
                {
                    Log("Debug", "Corsair plug-in: HID enumeration found:" + Environment.NewLine + summary);
                }
            }

            var added = 0;
            lock (deviceLock)
            {
                for (var i = 0; i < found.Count; i++)
                {
                    var info = found[i];
                    if (info == null || string.IsNullOrEmpty(info.Path))
                    {
                        continue;
                    }

                    if (info.ProductId == HubProductId)
                    {
                        if (FindHubByPath(info.Path) == null && ConnectHub(info))
                        {
                            added++;
                        }
                    }
                    else if (IsPsuProductId(info.ProductId))
                    {
                        if (FindPsuByPath(info.Path) == null && ConnectPsu(info))
                        {
                            added++;
                        }
                    }
                }

                statusMessage = (hubs.Count + psus.Count) > 0 ? string.Empty : NoDevicesStatus;

                // While nothing is found, look again soon; once something is, keep a slow re-scan
                // so a device plugged in later is still noticed.
                nextScanTicks = unchecked(Environment.TickCount + ((hubs.Count + psus.Count) > 0 ? PresentRescanMs : ScanIntervalMs));
            }

            if (added > 0)
            {
                Log("Debug", "Corsair plug-in: " + added.ToString(CultureInfo.InvariantCulture)
                    + " Corsair device session(s) opened.");
            }
        }

        // Called with deviceLock held.
        private bool ConnectHub(CorsairHidDeviceInfo info)
        {
            var device = new CorsairLinkHubDevice(info, guard, Log);
            var connected = false;
            try
            {
                connected = device.Connect();
            }
            catch (Exception ex)
            {
                NoteError("connecting to the iCUE LINK hub at " + info.Path, ex);
            }

            if (!connected)
            {
                try
                {
                    device.Disconnect(false);
                }
                catch (Exception)
                {
                }

                return false;
            }

            var entry = new HubEntry();
            entry.Device = device;
            entry.Info = info;
            entry.Serial = device.Serial;
            entry.NextDueTicks = Environment.TickCount;
            hubs.Add(entry);

            Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial + " is connected with "
                + device.Channels.Count.ToString(CultureInfo.InvariantCulture) + " channel(s).");

            RestoreHubIntent(entry);
            return true;
        }

        // Called with deviceLock held.
        private bool ConnectPsu(CorsairHidDeviceInfo info)
        {
            var device = new CorsairHidPsuDevice(info, guard, Log);
            var connected = false;
            try
            {
                connected = device.Connect();
            }
            catch (Exception ex)
            {
                NoteError("connecting to the Corsair PSU at " + info.Path, ex);
            }

            if (!connected)
            {
                try
                {
                    device.Disconnect(false);
                }
                catch (Exception)
                {
                }

                return false;
            }

            var entry = new PsuEntry();
            entry.Device = device;
            entry.Info = info;
            entry.PidHex = device.PidHex;
            entry.NextDueTicks = Environment.TickCount;
            psus.Add(entry);

            Log("Debug", "Corsair plug-in: Corsair PSU " + device.ModelName + " [" + entry.PidHex + "] is connected.");

            RestorePsuIntent(entry);
            return true;
        }

        // ---- Intent: what the user asked for, across device object lifetimes ----------------------

        /// <summary>
        /// Re-takes a hub this worker had already taken, then replays every channel the user moved
        /// off its default. Does nothing at all unless this worker recorded taking the hub in this
        /// process: <c>ReassertControl</c> takes control unconditionally, so calling it speculatively
        /// would steal the hub from whatever program owns it.
        /// </summary>
        private void RestoreHubIntent(HubEntry entry)
        {
            var intent = FindHubIntent(entry.Serial);
            if (intent == null || !intent.EverOwned)
            {
                return;
            }

            Log("Debug", "Corsair plug-in: iCUE LINK hub " + entry.Serial
                + " was under this plug-in's control before it was re-opened; re-asserting control and re-applying the requested duties.");

            if (!entry.Device.ReassertControl())
            {
                Log("Debug", "Corsair plug-in: re-asserting control of iCUE LINK hub " + entry.Serial
                    + " did not succeed; it will be retried after the next reconnect.");
                return;
            }

            ReapplyHubPercents(entry, intent);
        }

        // A re-created device object starts every channel at its enumeration default, so the
        // percentages the user chose only exist in the intent record.
        private void ReapplyHubPercents(HubEntry entry, HubIntent intent)
        {
            if (intent.Percents.Count == 0)
            {
                return;
            }

            var channels = new List<int>(intent.Percents.Keys);
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                int percent;
                if (!intent.Percents.TryGetValue(channel, out percent))
                {
                    continue;
                }

                if (!entry.Device.SetChannelPercent(channel, percent))
                {
                    Log("Debug", "Corsair plug-in: re-applying " + percent.ToString(CultureInfo.InvariantCulture)
                        + " % to channel " + channel.ToString(CultureInfo.InvariantCulture)
                        + " of iCUE LINK hub " + entry.Serial + " did not reach the hardware.");
                }
            }

            RecordHubIntent(entry);
        }

        /// <summary>
        /// Re-applies the PSU's manual duty after the device object was re-created. Only ever fires
        /// when this worker recorded a manual duty at or above the PSU's 30 % floor -- there is no
        /// path here that writes a fan register on its own initiative.
        /// </summary>
        private void RestorePsuIntent(PsuEntry entry)
        {
            var intent = FindPsuIntent(entry.PidHex);
            if (intent == null || !intent.EverSetManual)
            {
                return;
            }

            if (intent.RequestedPercent < PsuManualThresholdPercent)
            {
                // Manual control was taken and the hand-back to the PSU's own curve did not land
                // before the device went away. The new device object has no memory of that, so
                // without this the PSU could sit in manual mode indefinitely -- the one hazard the
                // shutdown ordering exists to prevent. This is the give-it-back direction only
                // (duty 0, then mode 0x00); nothing here can put a fan under manual control.
                Log("Debug", "Corsair plug-in: the fan of Corsair PSU " + entry.PidHex
                    + " may still be in manual mode from an incomplete hand-back; returning it to automatic control.");
                if (entry.Device.ResetFan())
                {
                    intent.EverSetManual = false;
                }

                intent.RequestedPercent = entry.Device.RequestedPercent;
                return;
            }

            Log("Debug", "Corsair plug-in: Corsair PSU " + entry.PidHex + " was running a manual fan duty of "
                + intent.RequestedPercent.ToString(CultureInfo.InvariantCulture) + " % before it was re-opened; re-applying it.");

            if (!entry.Device.SetFanPercent(intent.RequestedPercent))
            {
                Log("Debug", "Corsair plug-in: re-applying the manual fan duty to Corsair PSU " + entry.PidHex + " did not reach the hardware.");
            }

            RecordPsuIntent(entry, false);
        }

        // Mirrors the device's live channel state into the intent record. Called with deviceLock
        // held, after anything that can change a duty.
        private void RecordHubIntent(HubEntry entry)
        {
            var intent = FindHubIntent(entry.Serial);
            if (intent == null)
            {
                intent = new HubIntent();
                hubIntents[entry.Serial] = intent;
            }

            if (entry.Device.OwnsSoftwareControl)
            {
                intent.EverOwned = true;
            }

            var channels = entry.Device.Channels;
            for (var i = 0; i < channels.Count; i++)
            {
                var state = channels[i];
                if (state.PercentIsDefault)
                {
                    intent.Percents.Remove(state.Channel);
                }
                else
                {
                    intent.Percents[state.Channel] = state.RequestedPercent;
                }
            }
        }

        // Mirrors the PSU's live state into the intent record. <paramref name="handedBack"/> is set
        // by the two call sites that successfully returned the fan to the PSU -- the device clears
        // its own "manual was taken" flag only on success, and this record has to match, or a
        // shutdown would send a pointless restore (or, worse, skip a needed one).
        private void RecordPsuIntent(PsuEntry entry, bool handedBack)
        {
            var intent = FindPsuIntent(entry.PidHex);
            if (intent == null)
            {
                intent = new PsuIntent();
                psuIntents[entry.PidHex] = intent;
            }

            intent.RequestedPercent = entry.Device.RequestedPercent;
            if (handedBack)
            {
                intent.EverSetManual = false;
            }
            else if (intent.RequestedPercent >= PsuManualThresholdPercent)
            {
                intent.EverSetManual = true;
            }
        }

        private HubIntent FindHubIntent(string serial)
        {
            HubIntent intent;
            return (serial != null && hubIntents.TryGetValue(serial, out intent)) ? intent : null;
        }

        private PsuIntent FindPsuIntent(string pidHex)
        {
            PsuIntent intent;
            return (pidHex != null && psuIntents.TryGetValue(pidHex, out intent)) ? intent : null;
        }

        // ---- Power events -------------------------------------------------------------------------

        private void HookSystemEvents()
        {
            if (systemEventsHooked)
            {
                return;
            }

            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                systemEventsHooked = true;
            }
            catch (Exception ex)
            {
                // Subscribing needs a working message pump, which some session types do not have.
                // Losing sleep/wake handling is a degradation, not a failure.
                Log("Debug", "Corsair plug-in: the Corsair worker could not subscribe to power mode changes (" + ex.Message
                    + "); sleep and wake will be handled by the ordinary reconnect path instead.");
            }
        }

        private void UnhookSystemEvents()
        {
            lock (lifecycleLock)
            {
                if (!systemEventsHooked)
                {
                    return;
                }

                systemEventsHooked = false;
                try
                {
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                }
                catch (Exception)
                {
                }
            }
        }

        // Flags only: this runs on the SystemEvents thread, which must never be made to wait on a
        // HID transaction or on the device lock.
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.Mode == PowerModes.Suspend)
            {
                paused = true;
            }
            else if (e.Mode == PowerModes.Resume)
            {
                resumeAtTicks = unchecked(Environment.TickCount + ResumeDelayMs);
                resumePending = true;
                paused = false;
            }
            else
            {
                return;
            }

            try
            {
                wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Runs on the worker thread once the post-resume delay has elapsed.
        private void HandleResume()
        {
            Log("Debug", "Corsair plug-in: the machine resumed; re-opening the Corsair devices.");

            lock (deviceLock)
            {
                for (var i = 0; i < hubs.Count; i++)
                {
                    var entry = hubs[i];
                    try
                    {
                        // restore=false: the handle is very likely dead anyway, and the intent
                        // record is what puts the user's settings back after the rescan.
                        entry.Device.Disconnect(false);
                    }
                    catch (Exception ex)
                    {
                        Log("Debug", "Corsair plug-in: closing iCUE LINK hub " + entry.Serial + " after resume threw (" + ex.Message + ").");
                    }

                    entry.Closed = true;
                }

                for (var i = 0; i < psus.Count; i++)
                {
                    var entry = psus[i];
                    try
                    {
                        entry.Device.Disconnect(false);
                    }
                    catch (Exception ex)
                    {
                        Log("Debug", "Corsair plug-in: closing Corsair PSU " + entry.PidHex + " after resume threw (" + ex.Message + ").");
                    }

                    entry.Closed = true;
                }

                hubs.Clear();
                psus.Clear();
            }

            // ConnectHub / ConnectPsu call the intent restores, which re-assert hub control only
            // where this worker recorded owning it and re-apply a PSU duty only where it recorded
            // asking for one.
            scanRequested = true;
        }

        // ---- AppDomain hooks -----------------------------------------------------------------------

        private void HookAppDomain()
        {
            if (appDomainHooked)
            {
                return;
            }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                AppDomain.CurrentDomain.DomainUnload += OnProcessExit;
                appDomainHooked = true;
            }
            catch (Exception ex)
            {
                Log("Debug", "Corsair plug-in: the Corsair worker could not hook process exit (" + ex.Message + ").");
            }
        }

        private void UnhookAppDomain()
        {
            lock (lifecycleLock)
            {
                if (!appDomainHooked)
                {
                    return;
                }

                appDomainHooked = false;
                try
                {
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                    AppDomain.CurrentDomain.DomainUnload -= OnProcessExit;
                }
                catch (Exception)
                {
                }
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            StopAndRestore();
        }

        // ---- Snapshots -------------------------------------------------------------------------

        // Called with deviceLock held: it reads the live, mutable channel list, which no thread
        // outside this one may touch.
        private CorsairSnapshot BuildSnapshot()
        {
            var snapshot = new CorsairSnapshot();
            snapshot.CapturedUtc = DateTime.UtcNow;
            snapshot.Hubs = new List<HubSnapshot>();
            snapshot.Psus = new List<PsuSnapshot>();

            for (var i = 0; i < hubs.Count; i++)
            {
                var entry = hubs[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                var device = entry.Device;
                var hub = new HubSnapshot();
                hub.Serial = entry.Serial;
                hub.FirmwareVersion = device.FirmwareVersion;
                hub.OwnsSoftwareControl = device.OwnsSoftwareControl;
                hub.WrongModeReadFailure = device.LastReadWrongMode;
                hub.DutiesPending = device.DutiesPending;
                hub.LastStatusByte = device.LastStatusByte;
                hub.Channels = new List<HubChannelSnapshot>();

                var channels = device.Channels;
                for (var c = 0; c < channels.Count; c++)
                {
                    var state = channels[c];
                    var row = new HubChannelSnapshot();
                    row.Channel = state.Channel;
                    row.DeviceName = state.Device != null ? state.Device.Name : string.Empty;
                    row.DeviceId = state.DeviceId;
                    row.IsPump = state.Device != null && state.Device.IsPump;
                    row.HasRpm = state.Device != null && state.Device.HasRpm;
                    row.HasTemp = state.Device != null && state.Device.HasTemp;
                    row.HasControl = state.Device != null && state.Device.HasControl;
                    row.Rpm = state.Rpm;
                    row.TemperatureC = state.TemperatureC;
                    row.RequestedPercent = state.RequestedPercent;
                    row.PercentIsDefault = state.PercentIsDefault;
                    hub.Channels.Add(row);
                }

                snapshot.Hubs.Add(hub);
            }

            for (var i = 0; i < psus.Count; i++)
            {
                var entry = psus[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                var device = entry.Device;
                var psu = new PsuSnapshot();
                psu.ModelName = device.ModelName;
                psu.PidHex = entry.PidHex;
                psu.Temperature1C = device.Temperature1C;
                psu.Temperature2C = device.Temperature2C;
                psu.FanRpm = device.FanRpm;
                psu.FanIsManual = device.FanIsManual;
                psu.InputVoltage = device.InputVoltage;
                psu.OutputPowerW = device.OutputPowerW;
                psu.RequestedPercent = device.RequestedPercent;
                snapshot.Psus.Add(psu);
            }

            snapshot.Status = (snapshot.Hubs.Count + snapshot.Psus.Count) > 0 ? string.Empty : statusMessage;
            return snapshot;
        }

        private CorsairSnapshot BuildEmptySnapshot()
        {
            var snapshot = new CorsairSnapshot();
            snapshot.CapturedUtc = DateTime.UtcNow;
            snapshot.Status = statusMessage;
            snapshot.Hubs = new List<HubSnapshot>();
            snapshot.Psus = new List<PsuSnapshot>();
            return snapshot;
        }

        // snapshotLock is taken on its own, never while a device call is in flight, so it can never
        // participate in a lock cycle with deviceLock.
        private void PublishSnapshot(CorsairSnapshot snapshot)
        {
            lock (snapshotLock)
            {
                published = snapshot;
            }
        }

        // ---- Small helpers ------------------------------------------------------------------------

        private static bool IsPsuProductId(ushort productId)
        {
            for (var i = 0; i < PsuProductIds.Length; i++)
            {
                if (PsuProductIds[i] == productId)
                {
                    return true;
                }
            }

            return false;
        }

        // Called with deviceLock held.
        private HubEntry FindHub(string serial)
        {
            for (var i = 0; i < hubs.Count; i++)
            {
                if (!hubs[i].Closed && string.Equals(hubs[i].Serial, serial, StringComparison.OrdinalIgnoreCase))
                {
                    return hubs[i];
                }
            }

            return null;
        }

        // Called with deviceLock held.
        private HubEntry FindHubByPath(string path)
        {
            for (var i = 0; i < hubs.Count; i++)
            {
                if (hubs[i].Info != null && string.Equals(hubs[i].Info.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return hubs[i];
                }
            }

            return null;
        }

        // Called with deviceLock held. Identity is the product id: this PSU family reports an empty
        // USB serial number.
        private PsuEntry FindPsu(string pidHex)
        {
            for (var i = 0; i < psus.Count; i++)
            {
                if (!psus[i].Closed && string.Equals(psus[i].PidHex, pidHex, StringComparison.OrdinalIgnoreCase))
                {
                    return psus[i];
                }
            }

            return null;
        }

        // Called with deviceLock held.
        private PsuEntry FindPsuByPath(string path)
        {
            for (var i = 0; i < psus.Count; i++)
            {
                if (psus[i].Info != null && string.Equals(psus[i].Info.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return psus[i];
                }
            }

            return null;
        }

        private void NoteContact()
        {
            lastContactTicks = Environment.TickCount;
            if (dormant)
            {
                try
                {
                    wake.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void NoteError(string what, Exception ex)
        {
            lastError = what + ": " + ex.Message;
            lastErrorUtc = DateTime.UtcNow;
            hasLastError = true;
            Log("Error", "Corsair plug-in: " + what + " failed (" + ex.Message + ").");
        }

        private void Log(string level, string message)
        {
            var sink = log;
            if (sink == null)
            {
                return;
            }

            try
            {
                sink(level, message);
            }
            catch (Exception)
            {
                // A host log sink that throws must not be able to break device polling.
            }
        }

        // ---- Bookkeeping types ---------------------------------------------------------------------

        private abstract class DeviceEntry
        {
            public CorsairHidDeviceInfo Info;
            public int ConsecutiveFailures;
            public int NextDueTicks;
            public bool BackedOff;
            public bool Closed;
        }

        private sealed class HubEntry : DeviceEntry
        {
            public CorsairLinkHubDevice Device;
            public string Serial;
        }

        private sealed class PsuEntry : DeviceEntry
        {
            public CorsairHidPsuDevice Device;
            public string PidHex;
        }

        /// <summary>
        /// What this worker has asked one hub for, keyed by serial and surviving the device object.
        /// <see cref="Percents"/> holds only channels the user moved off their default.
        /// </summary>
        private sealed class HubIntent
        {
            public bool EverOwned;
            public readonly Dictionary<int, int> Percents = new Dictionary<int, int>();
        }

        /// <summary>
        /// What this worker has asked one PSU for, keyed by product id (this family reports an empty
        /// USB serial). <see cref="EverSetManual"/> mirrors the device's own restore arming: set
        /// before a manual duty write, cleared only when a hand-back actually landed.
        /// </summary>
        private sealed class PsuIntent
        {
            public bool EverSetManual;
            public int RequestedPercent = -1;
        }
    }
}
