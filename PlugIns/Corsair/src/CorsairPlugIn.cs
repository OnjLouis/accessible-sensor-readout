using System;
using System.Collections.Generic;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// The plug-in's SDK-facing surface: turns <see cref="CorsairWorker"/>'s periodic snapshots
    /// into <see cref="SensorReading"/> rows. Row building lives in
    /// <c>CorsairPlugIn.Rows.cs</c>; this file holds the SDK entry points and explicit lifecycle.
    /// </summary>
    public sealed partial class CorsairPlugIn : ISensorReadoutPlugin, IPluginLifecycle
    {
        // Diagnostics may pay up to three seconds for a genuinely fresh read. A
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
            Name = "Corsair iCUE Link and PSU Monitoring (experimental)",
            Version = "0.2.0",
            Author = "Robin Kipp and Sensor Readout contributors",
            Description = "Experimental, opt-in read-only monitoring for Corsair iCUE LINK Hub cooling devices and Corsair HXi/RMi digital power supplies."
        };

        // One-shot latch for the first-snapshot wait below. A worker whose guard creation
        // permanently fails never completes a tick, so gating on CompletedTicks == 0 would make
        // every refresh pay FirstSnapshotWaitMs forever; this flag makes the wait happen at most
        // once per plug-in instance (the host keeps a single instance per process).
        // GetReadings is called on one collection thread at a time, but reading/writing this bool
        // without a lock is fine either way: it is a one-shot pessimistic latch, so the worst case
        // of a race is two early callers both waiting once.
        private bool firstSnapshotWaited;

        public PluginInfo Info
        {
            get { return info; }
        }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
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

        public void Shutdown()
        {
            CorsairWorker.Instance.StopAndRestore();
        }
    }
}
