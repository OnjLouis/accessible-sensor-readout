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

        // Per-device failure backoff (brief step 1).
        private const int MaxConsecutiveFailures = 5;
        private const int DeviceBackoffMs = 30000;

        // Idle dormancy: with no host contact for this long and nothing under this plug-in's
        // control, there is nobody to show readings to, so polling stops entirely.
        private const int DormancyIdleMs = 15 * 60 * 1000;

        // A host thread may never block longer than this on the worker's device lock.
        private const int DeviceLockTimeoutMs = 5000;

        // Shutdown budget. A ProcessExit handler gets roughly two seconds in total, and the two
        // restores are what actually matter there -- a PSU left in manual mode stays that way until
        // something writes 0xF0 = 0x00. So neither wait is generous: an idle worker joins almost
        // instantly, and a worker that has not joined in a quarter of a second is stuck in a HID
        // transaction that waiting longer will not rescue. Both waits together are capped at half a
        // second, leaving about 1.5 s for the restores, which is what their shortened per-transfer
        // timeouts are sized for.
        private const int WorkerJoinMs = 250;
        private const int ShutdownDeviceLockMs = 250;

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
        /// Takes the device lock for a control call, or reports why it could not.
        ///
        /// The lock can be held for a while: a refresh tick holds it for one device's refresh, and
        /// a scan holds it across every <c>Connect</c> in that scan (about 550 ms for a hub and a
        /// PSU on this machine, multi-second when another Corsair program is contending for the
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
        /// Stops the worker and hands every device back to whatever was driving it. Idempotent, and
        /// safe to call from a ProcessExit handler.
        ///
        /// Order matters and is deliberate:
        /// <list type="number">
        /// <item>signal the loop and join briefly -- the thread is a background thread, so an
        /// unresponsive tick is abandoned rather than waited out, and the join is deliberately
        /// short so it cannot spend the budget the restores need;</item>
        /// <item>PSU restore first: a PSU left in manual mode stays there until something writes
        /// 0xF0 = 0x00 or it is power-cycled, so it is the one real hazard of a killed process. The
        /// hub is second because it reverts on its own once nothing keeps writing to it;</item>
        /// <item>hub restore, requesting hardware mode only when this plug-in actually took the
        /// hub;</item>
        /// <item>any remaining session closed, releasing its HID handle;</item>
        /// <item>the shared guard disposed last, because everything above needs it.</item>
        /// </list>
        ///
        /// Each step gets its own try/catch and iterates its own copy of the device list, so that
        /// neither a throw nor a worker thread that never joined (and is therefore still mutating
        /// those lists) can cost a later step its turn.
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

            CorsairDeviceGuard localGuard = null;
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(deviceLock, ShutdownDeviceLockMs, ref lockTaken);
                if (!lockTaken)
                {
                    // Degenerate case: the worker never joined and still holds the lock. Continue
                    // anyway -- the device sessions serialize themselves internally, and leaving a
                    // PSU in manual mode is worse than a contended shutdown. Every step below
                    // iterates its own copy of the device list precisely because of this path.
                    Log("Debug", "Corsair plug-in: the Corsair worker still held the device lock at shutdown; restoring anyway.");
                }

                // One try/catch per step: a throw in the PSU restore must not cost the hub its
                // restore, and neither must cost the sessions their handles.
                RunShutdownStep(RestorePsusAtShutdown, "returning the Corsair power supplies to automatic control");
                RunShutdownStep(RestoreHubsAtShutdown, "returning the iCUE LINK hubs to hardware mode");
                RunShutdownStep(CloseRemainingSessions, "closing the remaining Corsair device sessions");

                // The guard swap belongs inside this bounded hold rather than in a second,
                // unbounded one: taking the same lock again after deliberately bounding the first
                // wait would give back exactly the guarantee that bound exists to provide.
                localGuard = guard;
                guard = null;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(deviceLock);
                }
            }

            // Last: CorsairDeviceGuard's methods do not survive its disposal, so nothing above may
            // still need it.
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
            if (stopRequested || Interlocked.CompareExchange(ref shutdownState, 0, 0) != 0)
            {
                // Shutdown has begun. A straggler cycle must touch nothing: the guard may already
                // be disposed, and EnsureGuard would otherwise happily create a replacement that
                // nobody will ever dispose.
                return PausedWaitMs;
            }

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
                // Publish the flag before the final idle re-check, and pair it with NoteContact,
                // which writes lastContactTicks and only then reads this flag. With that ordering
                // one of the two always wins: either the re-check below sees the fresh tick and
                // backs out, or NoteContact sees dormant == true and signals the wake event. Set
                // the flag without re-checking and a call arriving in this window would be
                // swallowed -- its wake would land before the WaitOne that is about to be re-armed,
                // and the worker would sleep for a full minute with a caller waiting.
                dormant = true;
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
