using System;
using System.Collections.Generic;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// The plug-in's SDK-facing surface: turns <see cref="CorsairWorker"/>'s periodic snapshots
    /// into <see cref="SensorReading"/> rows and routes fan-control calls back to it. Row building
    /// and identifier parsing live in <c>CorsairPlugIn.Rows.cs</c> -- this file only holds the SDK
    /// entry points, split purely to keep both files well under the 2000-line cap.
    ///
    /// <see cref="CorsairWorker.StopAndRestore"/> is deliberately never called from anywhere in this
    /// class. The worker registers its own <c>AppDomain.ProcessExit</c>/<c>DomainUnload</c> hooks
    /// the first time <see cref="CorsairWorker.EnsureStarted"/> runs, and stopping it here -- from a
    /// refresh, an error path, or a disable toggle -- would be permanent: <c>EnsureStarted</c> is a
    /// documented no-op once the worker has been stopped, so nothing in this process could ever
    /// restart it (task-7 carry (a)).
    /// </summary>
    public sealed partial class CorsairPlugIn : ISensorReadoutPlugin, IFanControllablePlugin
    {
        // Diagnostics may pay up to three seconds for a genuinely fresh read (brief item J). A
        // normal refresh must never do this -- GetSnapshot below is the only hardware-adjacent call
        // on that path, and it never blocks.
        private const int DiagnosticsForceRefreshWaitMs = 3000;

        // The very first GetReadings after the worker starts would otherwise race the worker's
        // first tick and return only the "starting up" status row -- which is the whole report in
        // the app's one-pass command-line report mode. Waiting briefly for the first completed tick
        // happens once per process (one-shot latch); every later refresh takes the never-blocking path.
        private const int FirstSnapshotWaitMs = 2500;

        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.corsair.experimental",
            Name = "Corsair iCUE Link and PSU Support (experimental)",
            Version = "0.1.0",
            Author = "Robin Kipp, Claude Code, and Sensor Readout contributors",
            Description = "Experimental, opt-in support for Corsair iCUE LINK Hub cooling devices and Corsair HXi/RMi digital power supplies."
        };

        // The SDK does not pass a context into TrySetFanPercent/TryResetFan, so the most recent
        // GetReadings context is kept for logging from those calls -- same pattern as
        // MsiLaptopPlugIn.lastContext.
        private IPluginContext lastContext;

        // One-shot latch for the first-snapshot wait below. A worker whose guard creation
        // permanently fails never completes a tick, so gating on CompletedTicks == 0 would make
        // every refresh pay FirstSnapshotWaitMs forever; this flag makes the wait happen at most
        // once per process regardless. GetReadings is called on one collection thread at a time,
        // but reading/writing this bool without a lock is fine either way: it is a one-shot
        // pessimistic latch, so the worst case of a race is two early callers both waiting once.
        private bool firstSnapshotWaited;

        public PluginInfo Info
        {
            get { return info; }
        }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            lastContext = context;

            var worker = CorsairWorker.Instance;
            worker.EnsureStarted(context == null ? null : new Action<string, string>(context.Log));

            var diagnosticsMode = context != null && context.DiagnosticsMode;
            if (diagnosticsMode)
            {
                worker.ForceRefresh(DiagnosticsForceRefreshWaitMs);
            }
            else if (!firstSnapshotWaited)
            {
                firstSnapshotWaited = true;
                worker.ForceRefresh(FirstSnapshotWaitMs);
            }

            var snapshot = worker.GetSnapshot();
            return BuildRows(snapshot, diagnosticsMode, worker);
        }

        public bool TrySetFanPercent(string identifier, int percent)
        {
            bool isHub;
            string deviceKey;
            int channel;
            if (!TryParseControlIdentifier(identifier, out isHub, out deviceKey, out channel))
            {
                // Cheap parse-only gate, no I/O: anything not shaped like a Corsair control
                // identifier -- including every other plug-in's and LibreHardwareMonitor's -- is
                // rejected here so the host's fan-control loop can move on to the next candidate.
                return false;
            }

            try
            {
                var ok = isHub
                    ? CorsairWorker.Instance.SetHubChannelPercent(deviceKey, channel, percent)
                    : CorsairWorker.Instance.SetPsuFanPercent(deviceKey, percent);

                if (!ok)
                {
                    Log(lastContext, "Debug", "Corsair plug-in: control change could not be applied for " + identifier + ".");
                }

                return ok;
            }
            catch (Exception ex)
            {
                Log(lastContext, "Error", "Corsair plug-in: setting the fan percent for " + identifier + " threw (" + ex.Message + ").");
                return false;
            }
        }

        public bool TryResetFan(string identifier)
        {
            bool isHub;
            string deviceKey;
            int channel;
            if (!TryParseControlIdentifier(identifier, out isHub, out deviceKey, out channel))
            {
                return false;
            }

            try
            {
                var ok = isHub
                    ? CorsairWorker.Instance.ResetHubChannel(deviceKey, channel)
                    : CorsairWorker.Instance.ResetPsuFan(deviceKey);

                if (!ok)
                {
                    Log(lastContext, "Debug", "Corsair plug-in: control change could not be applied for " + identifier + ".");
                }

                return ok;
            }
            catch (Exception ex)
            {
                Log(lastContext, "Error", "Corsair plug-in: resetting the fan for " + identifier + " threw (" + ex.Message + ").");
                return false;
            }
        }

        private static void Log(IPluginContext context, string level, string message)
        {
            if (context == null)
            {
                return;
            }

            try
            {
                context.Log(level, message);
            }
            catch (Exception)
            {
                // A host log sink that throws must not be able to break a control call.
            }
        }
    }
}
