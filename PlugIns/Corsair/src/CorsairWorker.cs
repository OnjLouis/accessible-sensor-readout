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
    /// How long <c>deviceLock</c> stays held varies a great deal: a refresh tick takes and releases
    /// it once per device, but a scan holds it across every <c>Connect</c> in that scan (about
    /// 550 ms for a hub and a PSU on the machine this was written against, and multi-second when
    /// another Corsair program is contending for the cross-process guard). That is exactly why
    /// control calls bound their wait instead of assuming the lock is only ever held briefly.
    ///
    /// Partial across two files purely to stay inside the 2000-line source limit: this one holds
    /// the public surface, the loop and the shutdown path, and <c>CorsairWorker.Devices.cs</c>
    /// holds scanning, per-device refresh and the intent bookkeeping.
    /// </summary>
    public sealed partial class CorsairWorker
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

        // Per-device failure backoff.
        private const int MaxConsecutiveFailures = 5;
        private const int DeviceBackoffMs = 30000;

        // Idle dormancy: with no host contact for this long and nothing under this plug-in's
        // control, there is nobody to show readings to, so polling stops entirely.
        private const int DormancyIdleMs = 15 * 60 * 1000;

        // A host thread may never block longer than this on the worker's device lock.
        private const int DeviceLockTimeoutMs = 5000;

        // Normal plug-in disable/reload can wait for a bounded HID cycle to finish. ProcessExit has
        // a much smaller CLR budget, so its fallback wait is deliberately shorter. Device cleanup
        // always runs on the worker thread; the caller never races it by closing live handles.
        //
        // Both waits are what the restores get to run in, and the restores are the reason shutdown
        // exists at all now that this plug-in writes duties: a PSU left in manual mode stays there
        // until something writes 0xF0 = 0x00 or it is power-cycled. The normal budget is generous
        // because it covers a deliberate hand-back; the ProcessExit fallback is short because the
        // CLR gives it no more.
        private const int NormalShutdownJoinMs = 15000;
        private const int ProcessExitJoinMs = 1500;

        // Deferred hand-back window. The host calls IPluginLifecycle.Shutdown for two very
        // different things and gives the plug-in no way to tell them apart: "you are going away"
        // (app exit, user disabled the plug-in) and "I am rebuilding my plug-in manager and will
        // load you again in a moment" (which Sensor Readout does on *every* live preference save,
        // including the one it fires the instant the Preferences window appears). Restoring the
        // hardware on the second kind is what made the fans jump to the hub's own loud profile
        // every time the user opened Preferences.
        //
        // So Shutdown no longer hands the hardware back; it arms a hand-back that the next
        // EnsureStarted cancels. A reload therefore costs nothing at all -- same worker, same
        // sessions, same software mode, curves never interrupted -- while a plug-in that really
        // was disabled still gives the hub and the PSU back once the grace period elapses.
        // ProcessExit bypasses the grace entirely, so app exit still restores immediately.
        //
        // The grace is derived from how often the host has actually been asking for readings
        // (three missed refreshes means it is not coming back), because that interval is a user
        // setting the plug-in cannot see: the refresh interval is 1-300 s and the host serves
        // plug-in rows from a 10 s foreground / 5 min background cache on top of it. The clamp
        // keeps the worst case bounded either way.
        private const int DeferredTeardownDefaultGraceMs = 30000;
        private const int DeferredTeardownMinGraceMs = 20000;
        private const int DeferredTeardownMaxGraceMs = 90000;
        private const int DeferredTeardownIntervalMultiplier = 3;

        // Floor on the loop wait while a hand-back is armed, so the deadline is honoured promptly
        // without the loop spinning. At most one extra pass, and the deadline is checked before
        // that pass touches a device.
        private const int DeferredTeardownPollFloorMs = 50;

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

        // Where this plug-in's own files live, from IPluginContext.PluginDirectory. Same story as
        // the log sink -- supplied by whichever host thread called EnsureStarted last, read by the
        // worker thread -- and null or empty simply turns the marker feature off.
        private volatile string pluginDirectory;

        private CorsairDeviceGuard guard;       // deviceLock
        private Thread thread;                  // lifecycleLock
        private CorsairSnapshot published;      // snapshotLock

        private volatile bool stopRequested;
        // A hand-back has been armed by ReleaseFromHost and has not been cancelled or committed
        // yet. Written under lifecycleLock so cancelling (host thread) and committing (worker
        // thread) can never both win; read without it on the fast paths, hence volatile.
        private volatile bool teardownPending;
        private volatile bool hostRefreshSeen;
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
        private int cleanupComplete;             // 0 = worker may still own resources, 1 = closed
        private int forceRefreshRequested;      // 0 = no, 1 = one pending; read-and-cleared atomically
        private int teardownDueTicks;           // only meaningful while teardownPending is true
        private int lastHostRefreshTicks;       // only meaningful once hostRefreshSeen is true
        private int hostRefreshIntervalMs;      // 0 until two host refreshes have been seen

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
                    if (instance == null || Thread.VolatileRead(ref instance.cleanupComplete) != 0)
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
        /// Starts the worker if it is not running, cancels a hand-back armed by
        /// <see cref="ReleaseFromHost"/>, and adopts <paramref name="log"/> as the current log
        /// sink. Safe to call on every host refresh; a null sink simply discards messages. Once
        /// the hand-back has actually run this is a no-op -- this instance has given its devices
        /// back, and <see cref="Instance"/> hands out a fresh worker for a later re-enable.
        ///
        /// <paramref name="pluginDirectory"/> is where the sticky-control marker files live (see
        /// <c>CorsairWorker.Devices.cs</c>). It is tolerated as null or empty -- the marker feature
        /// simply switches itself off, and the plug-in stays strictly read-only until the user asks
        /// for a fan change.
        /// </summary>
        public void EnsureStarted(Action<string, string> log, string pluginDirectory)
        {
            this.log = log;
            this.pluginDirectory = pluginDirectory;
            NoteContact();
            NoteHostRefresh();

            if (!AdoptHostContact())
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
        /// Records that the host asked for readings and cancels any armed hand-back: a host that
        /// is still calling this plug-in was reloading it, not disposing of it. Returns false once
        /// the hand-back has committed, which is this instance saying "I am finished; ask
        /// <see cref="Instance"/> for a fresh worker".
        ///
        /// Split out of <see cref="EnsureStarted"/> so the decision can be exercised on its own
        /// (the self-test drives it against a worker that was never started, and therefore never
        /// reaches a device).
        /// </summary>
        internal bool AdoptHostContact()
        {
            var cancelled = false;
            lock (lifecycleLock)
            {
                if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
                {
                    return false;
                }

                if (teardownPending)
                {
                    teardownPending = false;
                    cancelled = true;
                }
            }

            if (cancelled)
            {
                Log("Debug", "Corsair plug-in: the host asked for Corsair readings again, so it was reloading the plug-in rather than shutting it down; the pending hand-back was cancelled and fan control was never interrupted.");
            }

            return true;
        }

        /// <summary>True while a hand-back is armed and has been neither cancelled nor run.</summary>
        internal bool IsTeardownDeferred
        {
            get { return teardownPending; }
        }

        /// <summary>True once this instance has begun or finished handing its devices back.</summary>
        internal bool IsStopped
        {
            get { return Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0; }
        }

        /// <summary>
        /// Arms the hand-back without performing it. Exposed to the self-test so the reload
        /// contract can be asserted on a worker with no thread and no devices.
        /// </summary>
        internal void ArmDeferredTeardown(int graceMs)
        {
            lock (lifecycleLock)
            {
                if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
                {
                    return;
                }

                Thread.VolatileWrite(ref teardownDueTicks, unchecked(Environment.TickCount + (graceMs < 0 ? 0 : graceMs)));
                teardownPending = true;
            }
        }

        /// <summary>
        /// Hands the devices back if an armed hand-back has come due. Called once per worker cycle
        /// before that cycle touches anything, and directly by the self-test.
        ///
        /// The decision is taken under <c>lifecycleLock</c>, which is the same lock
        /// <see cref="AdoptHostContact"/> cancels under, so a host refresh arriving exactly on the
        /// deadline either cancels the hand-back or arrives after it committed -- never both.
        /// </summary>
        internal bool CommitDeferredTeardownIfDue()
        {
            lock (lifecycleLock)
            {
                if (!teardownPending)
                {
                    return false;
                }

                // unchecked: TickCount wraps roughly every 24.9 days and the subtraction stays
                // correct across the wraparound.
                if (unchecked(Environment.TickCount - Thread.VolatileRead(ref teardownDueTicks)) < 0)
                {
                    return false;
                }

                teardownPending = false;
            }

            Log("Debug", "Corsair plug-in: nothing has asked the Corsair plug-in for readings since the host released it, so it was disabled rather than reloaded; the Corsair devices are being handed back now.");
            StopAndRestore(NormalShutdownJoinMs);
            return true;
        }

        // The host's refresh cadence, as observed from the calls it actually makes. Only used to
        // size the hand-back grace; a wildly long gap (the app was minimized and served plug-in
        // rows from its 5-minute cache) is ignored rather than allowed to stretch the grace.
        private void NoteHostRefresh()
        {
            var now = Environment.TickCount;
            var previous = Thread.VolatileRead(ref lastHostRefreshTicks);
            var seen = hostRefreshSeen;
            Thread.VolatileWrite(ref lastHostRefreshTicks, now);
            hostRefreshSeen = true;
            if (!seen)
            {
                return;
            }

            var gap = unchecked(now - previous);
            if (gap <= 0 || gap > DeferredTeardownMaxGraceMs)
            {
                return;
            }

            Thread.VolatileWrite(ref hostRefreshIntervalMs, gap);
        }

        private int DeferredTeardownGraceMs()
        {
            var interval = Thread.VolatileRead(ref hostRefreshIntervalMs);
            if (interval <= 0)
            {
                return DeferredTeardownDefaultGraceMs;
            }

            // interval is capped at DeferredTeardownMaxGraceMs above, so this cannot overflow.
            var grace = interval * DeferredTeardownIntervalMultiplier;
            if (grace < DeferredTeardownMinGraceMs)
            {
                return DeferredTeardownMinGraceMs;
            }

            return grace > DeferredTeardownMaxGraceMs ? DeferredTeardownMaxGraceMs : grace;
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
            try
            {
                wake.Set();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown can dispose the event between the checks above and this line; the loop
                // below then simply times out against the already-stopped worker. Same guard as
                // NoteContact, OnPowerModeChanged and StopAndRestore.
                return false;
            }

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
        /// Takes the device lock for a control call, or reports why it could not.
        ///
        /// The lock can be held for a while: a refresh tick holds it for one device's refresh, and
        /// a scan holds it across every <c>Connect</c> in that scan (about 550 ms for a hub and a
        /// PSU on the reference test system, multi-second when another Corsair program is contending for the
        /// cross-process guard). The host calls control methods from a thread the user interface
        /// waits on, so the wait is bounded and a timeout is reported as an honest failure rather
        /// than becoming a frozen window.
        ///
        /// Uses the <c>ref bool lockTaken</c> overload so that a thread abort landing between the
        /// successful acquire and the caller's try block cannot leak the lock.
        /// </summary>
        private bool TryEnterForControl(string what, ref bool lockTaken)
        {
            Monitor.TryEnter(deviceLock, DeviceLockTimeoutMs, ref lockTaken);
            if (lockTaken)
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

            var lockTaken = false;
            try
            {
                if (!TryEnterForControl("the duty change for iCUE LINK hub " + serial + " channel "
                    + channel.ToString(CultureInfo.InvariantCulture), ref lockTaken))
                {
                    return false;
                }

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
                if (lockTaken)
                {
                    Monitor.Exit(deviceLock);
                }
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

            var lockTaken = false;
            try
            {
                if (!TryEnterForControl("the reset of iCUE LINK hub " + serial + " channel "
                    + channel.ToString(CultureInfo.InvariantCulture), ref lockTaken))
                {
                    return false;
                }

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
                if (lockTaken)
                {
                    Monitor.Exit(deviceLock);
                }
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

            var lockTaken = false;
            try
            {
                if (!TryEnterForControl("the fan duty change for Corsair PSU " + pidHex, ref lockTaken))
                {
                    return false;
                }

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
                if (lockTaken)
                {
                    Monitor.Exit(deviceLock);
                }
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

            var lockTaken = false;
            try
            {
                if (!TryEnterForControl("the fan reset for Corsair PSU " + pidHex, ref lockTaken))
                {
                    return false;
                }

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
                if (lockTaken)
                {
                    Monitor.Exit(deviceLock);
                }
            }
        }

        /// <summary>
        /// What the host's <c>IPluginLifecycle.Shutdown</c> calls. It does **not** hand the
        /// hardware back; it arms a hand-back that the next <see cref="EnsureStarted"/> cancels.
        ///
        /// The host uses one call for two unrelated things and gives the plug-in nothing to tell
        /// them apart. Sensor Readout disposes and rebuilds its whole plug-in manager on every
        /// live preference save -- including the one it fires the moment the Preferences window
        /// appears -- so treating Shutdown as "give the hardware back" meant the iCUE LINK hub
        /// dropped to its own (loud) firmware profile every time the user opened Preferences, and
        /// then had to re-baseline when the reload took control again.
        ///
        /// The three ways out of the deferred state, all bounded:
        /// <list type="bullet">
        /// <item>the host loads the plug-in again and calls <see cref="EnsureStarted"/> -- the
        /// hand-back is cancelled and nothing at all happened to the hardware;</item>
        /// <item>nothing calls back within the grace period (the plug-in really was disabled) --
        /// the worker runs the ordinary hand-back itself, see
        /// <see cref="CommitDeferredTeardownIfDue"/>;</item>
        /// <item>the process exits -- <see cref="OnProcessExit"/> ignores the grace entirely and
        /// runs the hand-back inside the ProcessExit budget.</item>
        /// </list>
        ///
        /// A worker that was never started owns nothing, so there is nothing to defer: it stops
        /// immediately, which is also what makes <see cref="Instance"/> replaceable again for a
        /// plug-in that is enabled and disabled before its first reading.
        /// </summary>
        public void ReleaseFromHost()
        {
            if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
            {
                return;
            }

            var grace = DeferredTeardownGraceMs();
            bool running;
            lock (lifecycleLock)
            {
                // Re-checked inside the lock: a ProcessExit or an elapsed hand-back could have
                // landed since the cheap check above, and arming a hand-back on a worker that has
                // already given its devices back would leave a flag nothing ever clears.
                if (Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
                {
                    return;
                }

                running = thread != null;
                if (running)
                {
                    Thread.VolatileWrite(ref teardownDueTicks, unchecked(Environment.TickCount + grace));
                    teardownPending = true;
                }
            }

            if (!running)
            {
                StopAndRestore(NormalShutdownJoinMs);
                return;
            }

            Log("Debug", "Corsair plug-in: the host released the Corsair plug-in; its devices stay under this plug-in's control for up to "
                + grace.ToString(CultureInfo.InvariantCulture) + " ms in case the host is only reloading it.");
            try
            {
                wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Stops the worker and hands every device back to whatever was driving it. Idempotent.
        /// Cleanup runs on the worker thread after its current bounded HID operation finishes, so
        /// shutdown never closes a handle concurrently with an in-flight read, and a caller on any
        /// other thread waits for it under a bounded join.
        ///
        /// The order inside <see cref="CleanupOnWorkerThread"/> is deliberate: power supplies
        /// first, because a PSU left in manual mode stays there until something writes
        /// 0xF0 = 0x00 or it is power-cycled; hubs second, because the hub firmware falls back to
        /// its own profile once nothing drives it; then the remaining sessions; then the shared
        /// guard, which everything above needs.
        /// </summary>
        public void StopAndRestore()
        {
            StopAndRestore(NormalShutdownJoinMs);
        }

        private void StopAndRestore(int joinMs)
        {
            if (Interlocked.Exchange(ref shutdownState, 1) != 0)
            {
                return;
            }

            // A real stop supersedes an armed hand-back; CommitDeferredTeardownIfDue and
            // AdoptHostContact both see shutdownState first and stand down.
            teardownPending = false;

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
            }

            // The worker itself commits the deferred hand-back, so it can land here; it must not
            // join itself. Its own loop is about to fall out of the while and run the cleanup.
            if (worker != null && object.ReferenceEquals(worker, Thread.CurrentThread))
            {
                return;
            }

            if (worker != null)
            {
                try
                {
                    if (!worker.Join(joinMs))
                    {
                        Log("Debug", "Corsair plug-in: the Corsair worker thread did not stop within "
                            + joinMs.ToString(CultureInfo.InvariantCulture)
                            + " ms; it will finish cleanup on its own background thread.");
                    }
                }
                catch (Exception ex)
                {
                    Log("Debug", "Corsair plug-in: waiting for the Corsair worker thread to stop threw (" + ex.Message + ").");
                }
            }
            else
            {
                // A plug-in can be loaded and disabled before its first reading. In that case no
                // worker owns resources, but the singleton still has to become replaceable so a
                // later re-enable starts from a clean instance.
                statusMessage = StoppedStatus;
                try
                {
                    wake.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                Interlocked.Exchange(ref cleanupComplete, 1);
            }
        }

        private void CleanupOnWorkerThread()
        {
            CorsairDeviceGuard localGuard = null;
            lock (deviceLock)
            {
                RunShutdownStep(RestorePsusAtShutdown, "returning the Corsair power supplies to automatic control");
                RunShutdownStep(RestoreHubsAtShutdown, "returning the iCUE LINK hubs to hardware mode");
                RunShutdownStep(CloseRemainingSessions, "closing the remaining Corsair device sessions");
                localGuard = guard;
                guard = null;
                hubIntents.Clear();
                psuIntents.Clear();
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

            statusMessage = StoppedStatus;
            PublishSnapshot(BuildEmptySnapshot());
            lock (lifecycleLock)
            {
                thread = null;
            }
            try
            {
                wake.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            Interlocked.Exchange(ref cleanupComplete, 1);
            Log("Debug", "Corsair plug-in: the Corsair worker has stopped and every device session is closed.");
        }

        private void RunShutdownStep(Action step, string what)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Log("Error", "Corsair plug-in: " + what + " threw during shutdown (" + ex.Message
                    + "); the remaining shutdown steps still run.");
            }
        }

        // Step 2: the power supplies, first, because a PSU left in manual mode stays there until
        // something writes 0xF0 = 0x00 or it is power-cycled.
        //
        // Disconnect(true) is asked for unconditionally rather than gated on this worker's own
        // record of having taken manual control. The device arms its restore *before* it writes the
        // duty register, while this worker records the same fact *after* the call returns, so a
        // throw in between would leave the PSU manual with nothing here knowing it -- a gate that
        // fails open. The device's own flag is the authority, and it makes Disconnect(true) a no-op
        // whenever nothing was ever taken, so deferring to it is both safer and simpler. Same shape
        // as the hub step, which ORs the device's live state into its decision.
        private void RestorePsusAtShutdown()
        {
            var entries = new List<PsuEntry>(psus);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Device == null || entry.Closed)
                {
                    continue;
                }

                // IsGone is read before Disconnect on purpose: Disconnect clears it, and a device
                // that has already vanished must not be asked to accept a restore write.
                if (entry.Device.IsGone)
                {
                    continue;
                }

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
            var entries = new List<HubEntry>(hubs);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
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

        // Step 4: whatever is left -- a device that had already vanished, so its handle just needs
        // releasing and a restore write could only fail.
        private void CloseRemainingSessions()
        {
            var entries = new List<PsuEntry>(psus);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
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
            try
            {
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

                    wake.WaitOne(CapWaitForPendingTeardown(waitMs));
                }
            }
            finally
            {
                CleanupOnWorkerThread();
            }

            Log("Debug", "Corsair plug-in: the Corsair worker thread is exiting.");
        }

        /// <summary>
        /// One pass of the loop. Returns how long to wait before the next one.
        /// </summary>
        private int RunCycle()
        {
            if (stopRequested || Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
            {
                // Shutdown has begun. A straggler cycle must touch nothing: the guard may already
                // be disposed, and EnsureGuard would otherwise happily create a replacement that
                // nobody will ever dispose.
                return PausedWaitMs;
            }

            if (paused)
            {
                // A suspending machine is the one moment not to hand hardware back: the writes
                // would very likely fail anyway. The deadline is simply re-checked after resume,
                // and a host that comes back first cancels it as usual.
                return PausedWaitMs;
            }

            // Before anything in this cycle touches a device: a hand-back that has come due ends
            // the loop, and CleanupOnWorkerThread in the loop's finally does the restores.
            if (CommitDeferredTeardownIfDue())
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

        // While a hand-back is armed the loop must not sleep past its deadline, or a disabled
        // plug-in would hold the hub in software mode for up to one extra tick interval.
        private int CapWaitForPendingTeardown(int waitMs)
        {
            if (!teardownPending)
            {
                return waitMs;
            }

            var remaining = unchecked(Thread.VolatileRead(ref teardownDueTicks) - Environment.TickCount);
            if (remaining < DeferredTeardownPollFloorMs)
            {
                remaining = DeferredTeardownPollFloorMs;
            }

            return remaining < waitMs ? remaining : waitMs;
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
                // Publish the flag before the final idle re-check, and pair it with NoteContact,
                // which writes lastContactTicks and only then reads this flag. Both sides write one
                // value and then read the other, and on a weakly-ordered CPU a plain store is not
                // guaranteed to be visible before a later load runs (StoreLoad reordering) -- so
                // without the barriers below, this thread could still be reading a stale
                // lastContactTicks after its own dormant = true has not yet become visible to
                // NoteContact, and NoteContact's wake.Set() could run before it observes dormant ==
                // true. Either miss swallows the wake: the caller's contact lands, dormant looks
                // false to it, so it never calls wake.Set(), and this thread goes on to sleep for a
                // full DormantWaitMs with a caller that already asked for readings. The barrier after
                // each store forces the store to retire before either thread's next load, so one of
                // the two always wins: either the re-check below sees the fresh tick and backs out,
                // or NoteContact's read of dormant sees true and signals the wake event.
                dormant = true;
                Thread.MemoryBarrier();
                if (unchecked(Environment.TickCount - lastContactTicks) < DormancyIdleMs)
                {
                    dormant = false;
                    idleOwnedLogged = false;
                    return false;
                }

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

        // Scanning, connecting, per-device refresh and the intent bookkeeping live in
        // CorsairWorker.Devices.cs -- same class, same thread, split only for the file size limit.

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
            StopAndRestore(ProcessExitJoinMs);
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
                hub.HardwareModeBlocked = device.HardwareModeBlocked;
                hub.DutiesPending = device.DutiesPending;
                hub.LastStatusByte = device.LastStatusByte;
                hub.BackedOff = entry.BackedOff;
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
                    // Diagnostics-only carry: raw enumeration bytes, since the friendly DeviceName
                    // does not round-trip to them (annex sec 6.2 -- e.g. "H100i" is model 0x07 with
                    // variant 0x00 or 0x04).
                    row.ModelCode = state.Device != null ? state.Device.Model : (byte)0;
                    row.VariantCode = state.Device != null ? state.Device.Variant : (byte)0;
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
                psu.BackedOff = entry.BackedOff;
                snapshot.Psus.Add(psu);
            }

            snapshot.Status = StatusFor(snapshot.Hubs.Count + snapshot.Psus.Count);
            return snapshot;
        }

        private CorsairSnapshot BuildEmptySnapshot()
        {
            var snapshot = new CorsairSnapshot();
            snapshot.CapturedUtc = DateTime.UtcNow;
            snapshot.Status = StatusFor(0);
            snapshot.Hubs = new List<HubSnapshot>();
            snapshot.Psus = new List<PsuSnapshot>();
            return snapshot;
        }

        /// <summary>
        /// The status line for a snapshot carrying <paramref name="deviceCount"/> devices: empty
        /// when there is something to show, and otherwise an explanation.
        ///
        /// Derived from the snapshot's own device count, never from the scan alone. statusMessage
        /// is only rewritten by a scan, so between a device disappearing (which drops it from the
        /// list immediately) and the rescan that follows, it still says "devices present" -- and a
        /// snapshot with no devices and no explanation renders as a plug-in that has nothing to
        /// say rather than one whose device went away. The fallback covers that window.
        /// </summary>
        private string StatusFor(int deviceCount)
        {
            if (deviceCount > 0)
            {
                return string.Empty;
            }

            var message = statusMessage;
            return string.IsNullOrEmpty(message) ? NoDevicesStatus : message;
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
            Thread.MemoryBarrier();
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
