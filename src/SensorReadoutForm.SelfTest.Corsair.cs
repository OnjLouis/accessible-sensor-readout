// These self-test steps arrived with the Corsair fan-control work. They live in their own file,
// separate from SensorReadoutForm.SelfTest.cs, purely to keep that file under the repository's
// 3000-line size audit: it grew past the limit once the upstream 6.0.0 release added its own
// self-test steps, and moving these three out was less disruptive than reorganizing the rest of
// that file. Nothing here behaves any differently for having moved; RunSelfTest still registers
// and runs these steps from SensorReadoutForm.SelfTest.cs, in the same order as before.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    // The watchdog that stops one wedged collection from latching the refresh pipeline for the life
    // of the process. Its decision is a pure function of elapsed time, the user's refresh interval
    // and whether this is the first collection of the process, so it can be checked here without
    // anything actually stalling; the latch and the flag release are checked against the real
    // method, driven with synthetic clock readings and with its Error line suppressed. Suppressed
    // because that line names a stall duration and tells the user to restart the app and send their
    // log: written from a test it is indistinguishable from a real firing, in the one file support
    // reads as evidence.
    private void SelfTestStalledRefreshWatchdog()
    {
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(2), 5, false), "A refresh that has just started must not be superseded.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(45), 5, false), "A slow but recent refresh must not be superseded.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromMilliseconds(20371), 5, false),
            "The slowest genuine collection seen in production was 20371 ms; the threshold must keep real headroom over it.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(61), 5, false),
            "A collection past the old one-minute floor must not be superseded any more; that floor left only about three times the observed worst case.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(119), 1, false), "The two-minute floor must protect the shortest refresh interval.");
        Require(ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(121), 5, false), "A collection in flight past the threshold must be superseded.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(121), 5, true),
            "The first collection of the process must get double the threshold: it is the expensive one, and a false stall there tells the user to restart an app they just launched.");
        Require(ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(241), 5, true), "A genuinely wedged first collection must still be superseded eventually.");
        Require(!ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(300), 300, false), "Six intervals must win over the floor at long refresh intervals.");
        Require(ShouldSupersedeStalledRefresh(TimeSpan.FromSeconds(1801), 300, false), "A stall past six long intervals must still be superseded.");

        // The clock half. Elapsed time has to be measured on a clock that excludes time the machine
        // spent suspended: all five watchdog firings in 3.5 days of production logs were
        // hibernations, and the last one reported exactly the machine's accumulated sleep bias as a
        // stall. Ten hours of synthetic awake time as a base, so none of the arithmetic below
        // depends on how long this machine has actually been awake.
        const long awakeBaseMs = 10L * 60 * 60 * 1000;
        var awakeNowMs = NativeMethods.TryGetAwakeMilliseconds();
        Require(awakeNowMs >= 0 && NativeMethods.UnbiasedInterruptTimeAvailable,
            "QueryUnbiasedInterruptTime, the only clock that does not count suspended time, is not answering on this machine.");
        Require(awakeNowMs <= (long)NativeMethods.GetTickCount64(),
            "The awake clock read ahead of the biased tick count, which an unbiased clock cannot do.");
        Require(!ShouldSupersedeStalledRefresh(awakeBaseMs, awakeBaseMs - 10000, 5, false),
            "A collection ten awake-seconds old was treated as stalled; a sleep must not be counted as elapsed time.");
        Require(ShouldSupersedeStalledRefresh(awakeBaseMs, awakeBaseMs - (10 * 60 * 1000), 5, false),
            "A collection ten awake-minutes old was not treated as stalled.");
        Require(!ShouldSupersedeStalledRefresh(-1, -1, 5, false),
            "Without an unbiased clock a stall cannot be measured, so it must never be reported.");
        Require(!ShouldSupersedeStalledRefresh(awakeBaseMs, awakeBaseMs + 60000, 5, false),
            "A start stamp ahead of the current reading must not be read as a stall.");

        // Proved rather than trusted: the self-test forces debug logging on and writes to the same
        // log file users are asked to send in for support, so an Error line written from here would
        // be read as a genuine watchdog firing. Counted by phrase rather than by file size because
        // the running app appends to that file too.
        const string stallErrorPhrase = "Sensor collection has not completed in";
        var stallErrorsBefore = SelfTestCountLogLinesContaining(stallErrorPhrase);

        var previousRefreshInProgress = refreshInProgress;
        var previousRefreshInProgressSinceAwakeMs = refreshInProgressSinceAwakeMs;
        var previousRefreshStallReported = refreshStallReported;
        var previousRefreshGeneration = refreshGeneration;
        try
        {
            refreshInProgress = true;
            refreshStallReported = false;
            // Past the first collection of the process, so the threshold under test here is the
            // steady-state one rather than the doubled start-up one.
            refreshGeneration = 2;
            refreshInProgressSinceAwakeMs = awakeBaseMs;
            Require(!TrySupersedeStalledRefresh(awakeBaseMs + 2000, false), "A collection that had just started was treated as stalled.");
            Require(refreshInProgress, "A healthy in-flight collection had the refresh flag cleared under it.");

            // The production shape: four hours of wall clock across a hibernation, no awake time.
            Require(!TrySupersedeStalledRefresh(awakeBaseMs, false), "A collection superseded across a sleep would have been reported as a stall.");
            Require(refreshInProgress, "A sleep released the refresh pipeline under a healthy collection.");

            Require(TrySupersedeStalledRefresh(awakeBaseMs + (10 * 60 * 1000), false), "A ten-minute-old collection was not treated as stalled.");
            Require(!refreshInProgress, "Superseding a stalled collection did not release the refresh pipeline.");

            // One recovery attempt and one Error line per stall episode. Without the latch every
            // later timer tick would start another collection behind the wedged one's lock.
            refreshInProgress = true;
            Require(!TrySupersedeStalledRefresh(awakeBaseMs + (10 * 60 * 1000), false), "The stall latch did not stop a second recovery attempt.");
            Require(refreshInProgress, "A second recovery attempt released the refresh pipeline again.");
        }
        finally
        {
            refreshInProgress = previousRefreshInProgress;
            refreshInProgressSinceAwakeMs = previousRefreshInProgressSinceAwakeMs;
            refreshStallReported = previousRefreshStallReported;
            refreshGeneration = previousRefreshGeneration;
        }

        Require(SelfTestCountLogLinesContaining(stallErrorPhrase) == stallErrorsBefore,
            "Driving the stall watchdog wrote its Error line into the user's log, where it is indistinguishable from a genuine firing of the failure this test only pretends to have.");
    }

    // Read-only, and tolerant of the log being written by the running app at the same time: the
    // share flags let the append through, and an unreadable log counts as zero on both sides of the
    // comparison rather than failing the step.
    private int SelfTestCountLogLinesContaining(string phrase)
    {
        try
        {
            var path = GetLogFilePath();
            if (!File.Exists(path))
            {
                return 0;
            }

            var count = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf(phrase, StringComparison.Ordinal) >= 0)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ApplyPreferencesFromDialog used to dispose the plug-in manager on every live preference save,
    // which is one Shutdown and reload of every loaded plug-in per keystroke in the dialog. The
    // observable seam is the manager instance: it has to survive a save that leaves the enabled
    // plug-in set alone, and must not survive one that changes it, because PlugInManager reads that
    // set exactly once. Both directions go through the real apply method so that restoring the
    // unconditional teardown fails this step. No plug-in is instantiated: PlugInManager loads
    // assemblies lazily on its first GetRows call, which this step never makes, and the enabled set
    // is restored either way. The apply writes the settings file each time, which is safe because
    // Build.ps1 -SelfTest runs this against an isolated SelfTest-<stamp>\App copy with its own
    // Config folder, never a user's install, and the finally restores the enabled set regardless.
    private void SelfTestPlugInManagerRebuildGuard()
    {
        EnsureSelfTestRows();
        var previousPlugInsEnabled = settings.PlugInsEnabled;
        try
        {
            settings.PlugInsEnabled = LoadPlugInPreferenceInfos(settings)
                .ToDictionary(plugIn => plugIn.Id, plugIn => false, StringComparer.OrdinalIgnoreCase);
            using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Plug-Ins"))
            {
                preferences.CreateControl();
                var plugInList = FindControls<CheckedListBox>(preferences.Controls)
                    .FirstOrDefault(list => list.Items.Cast<object>().Any(item => item is PlugInPreferenceInfo));
                Require(plugInList != null && plugInList.Items.Count > 0, "Preferences plug-in list was not found.");

                EnsurePlugInManager();
                var manager = plugInManager;
                Require(manager != null, "The plug-in manager was not created for the rebuild guard test.");

                ApplyPreferencesFromDialog(preferences, false, false);
                Require(ReferenceEquals(plugInManager, manager), "A preference save that left the enabled plug-in set alone tore the plug-in manager down.");

                // What SavePlugInCheckChange does to the model when the user ticks a box. The dialog
                // reports PlugInsEnabled from these items, not from the checkbox states.
                var toggled = (PlugInPreferenceInfo)plugInList.Items[0];
                toggled.Enabled = true;
                ApplyPreferencesFromDialog(preferences, false, false);
                Require(plugInManager == null, "Enabling a plug-in did not rebuild the plug-in manager, so the change could not take effect.");

                EnsurePlugInManager();
                Require(plugInManager != null, "The plug-in manager was not recreated for the rebuild guard test.");
                toggled.Enabled = false;
                ApplyPreferencesFromDialog(preferences, false, false);
                Require(plugInManager == null, "Disabling a plug-in did not rebuild the plug-in manager, so it would have kept running.");
            }
        }
        finally
        {
            DisposePlugInManager();
            settings.PlugInsEnabled = previousPlugInsEnabled;
            SaveSettings(settings);
        }
    }

    private void SelfTestZeroRpmFanControlVisibility()
    {
        var control = new SensorRow
        {
            Type = "Fan Control",
            Hardware = "Self test",
            Name = "Self-test zero-RPM control",
            Identifier = "selftest/zero-rpm/control/0"
        };
        Require(!ShouldShowFanControl(control), "A stopped fan control without the zero-RPM marker should be hidden by default.");

        control.Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Zero RPM capable", "Self-test marker" }
        };
        Require(ShouldShowFanControl(control), "A fan control carrying the \"Zero RPM capable\" Details key should stay visible at 0 RPM.");
    }
}
