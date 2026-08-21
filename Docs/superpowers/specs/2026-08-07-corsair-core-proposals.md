# Core Change Proposals Accompanying the Corsair Plug-In

Date: 2026-08-07, revised for the fan-control branch. Branch: `feature/corsair-fan-control`
(based on the 5.2.0 read-only Corsair integration). Author: Robin Kipp with Claude Code.

Per `Docs/Coding-agent-plug-in-rules.md`, core changes require a written case and explicit
approval. Four proposals came out of building and field-testing the Corsair plug-in. One of
them — Fan rows keeping their `Details` when a control percent is attached — was accepted and
is already in 5.2.0, so it is not repeated here. **Three remain**, listed below.

Each is generic (any plug-in benefits), isolated, and separately revertible — one commit per
proposal. None changes behavior for users without plug-ins or fan curves.

All three are on this branch because this branch restores the Corsair plug-in's fan-control
write path. Proposals 2 and 3 below only have observable effects when a plug-in exposes fan
controls, which is exactly what was deferred out of 5.2.0; they are re-proposed together with
the feature that needs them, not on their own.

**Added 2026-08-07 after a field bug report:** proposals **4 and 5** at the end of this document
come out of a reproducible regression Robin hit on live hardware — opening Preferences with
Corsair fan curves active made the fans spin up at once and never come back down until the app
was restarted. Both are core-side, and **both are now implemented on this branch**, one commit
each. The plug-in-side fix that also ships here works around proposal 4 entirely and merely
softens proposal 5, so neither core change is load-bearing for the Corsair plug-in; they are here
because the two defects they fix affect every plug-in and, in proposal 5's case, the app itself.
Full evidence: `.superpowers/sdd/2026-08-07-corsair-plugin/preferences-teardown-fix.md`.

### Disclosure up front: the Corsair plug-in now deviates from the documented `Shutdown` contract

`Docs/Plug-In-development.md` says `IPluginLifecycle.Shutdown` "should stop background work and
release hardware handles". **As of this branch the Corsair plug-in does neither at call time.**
`Shutdown` arms a hand-back and returns; the next `GetReadings` cancels it; the plug-in's own
worker performs the hand-back once a grace period (three observed host refresh intervals, clamped
to 20–90 s) elapses with no host contact; and `AppDomain.ProcessExit` performs it immediately,
so app exit still restores.

This is stated here rather than buried because it is a real loosening of the contract, and it is
a workaround for proposal 4, not a design preference. The plug-in cannot distinguish "you are
being disabled" from "I am rebuilding my manager and will load you again in a moment" — the SDK
gives it nothing — and on a host without proposal 4 the second case happens on every preference
keystroke. Proposal 4 is implemented on this branch, so on **this** build the rebuild-per-keystroke
is gone and the deviation is no longer what stops the ramp. It stays because the plug-in has to
behave on 5.2.0 and any host that does not take proposal 4, and because a rebuild is still possible
(the user actually toggles a plug-in) with no way for the plug-in to tell it apart from a disable.
If proposal 4 ships, a later change could reasonably go back to restoring synchronously inside
`Shutdown`. The costs are documented in `PlugIns/Corsair/TESTING.md` (a disabled plug-in
hands the hardware back after a delay, so "quit the app" is now the recommended way to switch to
another control program) and in `PlugIns/Corsair/docs/hardware-validation-plan.md` (new pending
items P6 and P7).

One thing the deviation buys back, unrelated to fans: `PlugInManager.Dispose` calls `Shutdown()`
**on the UI thread while holding its `sync` lock** (`src/SensorReadoutForm.PlugIns.cs:171-190`).
The previous implementation could block there for up to its 15 s worker join, on any of the 78
live-save events described in proposal 4 — a whole class of multi-second UI freezes, on a path
the user reaches by opening a settings window. `ReleaseFromHost` is non-blocking, so that class is
gone regardless of what happens to proposal 4, and proposal 4 removes the events that reached it.
It is worth noting that any plug-in whose `Shutdown` does real work has the same exposure on a host
without proposal 4.

## Proposal 1 — Foreground cache interval for plug-in readings that feed enabled fan curves

- **What the plug-in cannot do today:** while the app is minimized, plug-in rows are served
  from a 5-minute host cache (`BackgroundOemProviderRowsMinimumInterval`). A fan curve keyed
  on a plug-in temperature (e.g. the Corsair hub's liquid temperature) therefore reacts up
  to 5 minutes late exactly when the machine is under load with the app in the tray. The
  plug-in cannot influence the host cache from its side of the SDK.
- **Concern this has to answer — "does this make fragile vendor providers get polled more
  often?"** No. The exemption is not "a plug-in is enabled" and not "a plug-in returned rows".
  It fires only when *all* of the following hold at the moment of the refresh:
  1. the user has at least one fan curve in settings,
  2. that curve is `Enabled` and not `SuspendedByManualControl` (the same definition of a live
     curve that `ApplyFanCurvesAsync` uses),
  3. its `TemperatureReadingKey` resolves to an identifier, and
  4. that identifier matches a row **already in the plug-in row cache**.

  A user who enables the Lenovo, MSI, ASUS, HP, Dell, Framework or Huawei plug-in and never
  builds a fan curve on one of its readings sees byte-identical behaviour to today: the
  5-minute background cache still applies. The only way to opt in is to point an enabled fan
  curve at a plug-in reading, which is a deliberate act with an obvious cost/benefit: the user
  has asked that reading to drive cooling, so it must be fresh enough to drive cooling. The
  check itself is data-driven — it compares identifiers, and there is no vendor name anywhere
  in it. It also reads only the existing cache, so it cannot itself trigger a provider call.
- **Why generic:** any plug-in exposing a temperature that a user selects as a fan-curve
  input has the same problem (Framework EC temperatures, future OEM plug-ins).
- **File changed:** `src/SensorReadoutForm.OemProviders.cs` only, one condition plus one
  private helper.
- **Test:** full self-test passes; manual verification on live hardware (Corsair plug-in +
  liquid-temperature curve responds at foreground cadence while minimized; with the curve
  disabled, the 5-minute background cache is used as before).

## Proposal 2 — Opt-in "Zero RPM capable" marker keeps semi-passive fan controls visible

- **What the plug-in cannot do today:** `ShouldShowFanControl` hides any control whose
  paired Fan row reads 0 RPM (correct for unused motherboard headers). Semi-passive PSU
  fans (Corsair HXi zero-RPM mode) legitimately sit at 0 RPM most of the time, so their
  control is invisible unless the user finds "Show stopped fans". The plug-in cannot
  distinguish itself from an unused header through the current row contract.
- **Why it is proposed now:** it only matters when a plug-in exposes a fan control for a
  semi-passive fan, which this branch restores. Without it, the Corsair PSU fan control —
  the one control on this hardware a user is most likely to want during a sustained load —
  is hidden by default on the machine it was written for.
- **Why generic:** any provider of semi-passive fans (PSUs, hybrid GPU-style coolers
  surfaced by future plug-ins) needs the same distinction. The marker is a Details key
  (`"Zero RPM capable"`), so it needs no SDK change and is ignored by older hosts. Worst
  case for a plug-in that marks controls indiscriminately is dialog clutter the user could
  already produce with "Show stopped fans" — the marker is opt-in per control, and the
  plug-in development guide says to add it only when the hardware genuinely stops its fan by
  design.
- **Files changed:** `src/SensorReadoutForm.FanControls.cs` — one early return in
  `ShouldShowFanControl` when the control's Details contain the key, and the visibility
  filter now runs before `EnrichFanControlRow` (which strips Details) at both call sites
  (`FanControls.cs` and `FanCurves.cs`) — behavior-identical for existing rows because the
  filter only reads `Identifier` and `Details`. `src/SensorReadoutForm.SelfTest.cs` gains a
  "Zero-RPM fan control visibility" step asserting that a marked stopped control stays
  visible and an unmarked one stays hidden. `Docs/Plug-In-development.md` documents the key
  as part of the plug-in contract.
- **Test:** the new self-test step locks the marker contract in `ShouldShowFanControl`; full
  self-test passes; live check that the PSU fan control is visible at 0 RPM without "Show
  stopped fans", and that ordinary 0-RPM header controls remain hidden.

## Proposal 3 — "All fans reset" also resets plug-in fan controls

- **What the plug-in cannot do today:** the Fan Controls dialog's "All fans reset" button
  calls `SetAllLibreHardwareMonitorControlsDefault()`, which iterates LibreHardwareMonitor
  control sensors only. Plug-in controls are silently skipped: the wire reset never
  happens, though settings are saved as automatic and suspended curves resume (which can
  mask the gap when curves are active). Found during real-world testing — the user pressed
  the button and the Corsair fans did not react. Every other control path (single-fan
  actions, fan profiles, curves, diagnostics, startup re-apply) already routes through the
  plug-in-aware `SetLibreHardwareMonitorControl`; this is the one remaining LHM-only path.
- **Why it is proposed now:** same reason as proposal 2 — it is only observable when a
  plug-in actually owns fan controls. With the Corsair write path restored, "All fans reset"
  is the one bulk action that would not reach the hardware it claims to have reset, which is
  the worst kind of gap for a safety-shaped button.
- **Why generic:** affects every `IFanControllablePlugin` (MSI, ASUS, Corsair).
- **File changed:** `src/SensorReadoutForm.FanControls.cs` — `ResetAllFanControls` collects
  plug-in control identifiers (never `/`-prefixed) on the UI thread and resets each via
  `TryPlugInFanControl(identifier, 50, manual: false)` in the same worker, counting
  successes into the existing status message.
- **Test:** full self-test passes; live check that "All fans reset" audibly returns
  manually-set Corsair fans to their defaults immediately (previously only the curve
  engine corrected them later).

## Already accepted in 5.2.0 — Fan rows keep `Details` and `WindowsSettingsUri`

Recorded here only so the numbering in earlier discussion still maps to something.
`AttachFanControlPercentsToFanRows` rebuilt each Fan row to append "NN%" to the display
value and dropped `Details`/`WindowsSettingsUri` in the process, so a fan under manual or
curve control lost its details dialog content. The rebuilt row now deep-copies `Details` and
carries `WindowsSettingsUri`. No further action is needed.

---

## Proposal 4 — Preferences should not rebuild the plug-in manager unless the plug-in set actually changed

- **Symptom that produced it:** with Corsair fan curves active, the fans audibly spin up the
  moment the Preferences window appears, and again on essentially every keystroke or arrow-key
  press inside it. Confirmed in `C:\SensorReadout\Logs\SIMSTATION.log`, 17:20-17:32 on
  2026-08-07: six complete `returning iCUE LINK hub ... to hardware mode` -> `worker thread is
  exiting` -> `Loaded` -> `taking software control` cycles in eleven minutes, each one triggered
  by a Preferences interaction and each one audible.
- **The chain:** `PreferencesForm`'s `Shown` handler ends with an unconditional
  `SaveLivePreferences()` (`src/PreferencesForm.cs:1609-1614`); so do 78 further call sites across
  `PreferencesForm.cs`, `.Panels.cs`, `.TraySearch.cs`, `.SpokenHotKeys.cs`, `.FanProfiles.cs`,
  `.Alarms.cs` and `.Core.cs` — every `CheckedChanged`, `SelectedIndexChanged`, `ValueChanged` and
  `TextChanged` in the dialog — plus `CommitPreferences()` on `FormClosing` and the
  `DialogResult.OK` apply.
  `SaveLivePreferences` raises `LivePreferencesSaved` (`src/PreferencesForm.Core.cs:749-753`) ->
  `ApplyLivePreferencesFromOpenDialog` -> `ApplyPreferencesFromDialog`, whose line 283
  (`src/SensorReadoutForm.PreferencesAndCommands.cs`) is an unconditional
  `DisposePlugInManager()`. That disposes every loaded plug-in — calling
  `IPluginLifecycle.Shutdown()` on each (`src/SensorReadoutForm.PlugIns.cs:183`) — and, because
  `EnsurePlugInManager` is only ever reached from inside a sensor collection
  (`src/SensorReadoutForm.PlugIns.cs:21-31, 48-56`), leaves the process with **no plug-in
  instance at all** until the next refresh completes.
- **Why it hurts any plug-in that owns hardware state:** `Shutdown()` is the plug-in's only
  signal to release what it owns, and the SDK (`src/PluginSdk/PluginSdk.cs:22-25`) gives it no
  way to tell "you are being disabled / the app is closing" from "I am rebuilding my manager and
  will load you again in a second". The MSI and ASUS plug-ins get away with it because they hold
  no persistent device state. Anything that takes ownership of hardware — a hub in software mode,
  a PSU in manual fan mode, a future EC-control plug-in — has to give it back and take it again
  on every preference keystroke.
- **Change made:** `ApplyPreferencesFromDialog` now calls `DisposePlugInManagerIfEnabledSetChanged()`
  instead of `DisposePlugInManager()`. The guard compares the enabled-plug-in signature the live
  manager was **built with** — recorded in `EnsurePlugInManager` — against the signature of the
  settings after the apply, using the existing `GetOemProviderRowsCacheSignature`
  (`src/SensorReadoutForm.OemProviders.cs:106-115`). `ClearOemProviderRowsCache()` on the next line
  stays unconditional: it is cheap and only affects reading freshness.
- **Why not the before/after capture this proposal originally suggested.** It would never fire.
  `PreferencesForm` holds the host's own `AppSettings` instance (`liveSettings = settings`,
  `src/PreferencesForm.cs:265`) and writes the new enabled set into it *before* it raises
  `LivePreferencesSaved` (`SavePlugInCheckChange`, `src/PreferencesForm.Panels.cs:997-1003`), so by
  the time the apply runs, "before" and "after" are the same value. The same aliasing is why
  `ApplyLivePreferencesFromOpenDialog`'s existing `plugInsChanged`
  (`src/SensorReadoutForm.PreferencesAndCommands.cs:189, 195`) is always false for a plug-in toggle
  today; that is left alone here, because the unconditional `RefreshSensors` on the OK path and the
  next auto-refresh already pick the change up, and changing it would change refresh behaviour
  rather than fix a defect. Comparing against the manager's own signature sidesteps the aliasing
  entirely and states the actual question: is this manager still the right one?
- **Verified before making it conditional:** nothing else about a plug-in's behaviour depends on the
  manager being recreated. `PlugInManager` reads the enabled set exactly once, in `EnsureLoaded`
  (`src/SensorReadoutForm.PlugIns.cs`), and never re-reads it; everything a plug-in sees per call —
  `PlugInContext` with machine identity, plug-in directory, diagnostics flag and logger — is rebuilt
  inside `GetRows` on every refresh, so no plug-in state goes stale when the manager survives.
- **Not done — the second, smaller half.** The unconditional `SaveLivePreferences()` in
  `PreferencesForm`'s `Shown` handler still saves settings nothing changed. It is now harmless for
  plug-ins (the apply it triggers no longer tears anything down), so it is left for a separate
  change rather than folded in here.
- **Why generic:** every `IPluginLifecycle` implementation is shut down and re-instantiated by
  this path, and the plug-in contract has no other way to survive it.
- **Files changed:** `src/SensorReadoutForm.PlugIns.cs` (one field, one line in
  `EnsurePlugInManager`, one new guard method) and
  `src/SensorReadoutForm.PreferencesAndCommands.cs` (one call swapped in
  `ApplyPreferencesFromDialog`). Plug-in imports (`ImportPlugInFromZip`) and settings imports
  (`src/SensorReadoutForm.SettingsTransfer.cs:598`) keep their unconditional dispose: those change
  what is on disk, not just which plug-ins are enabled.
- **Test:** new self-test step "Plug-in manager rebuild guard"
  (`src/SensorReadoutForm.SelfTest.cs`). It builds a real `PreferencesForm`, creates the manager,
  and drives the real `ApplyPreferencesFromDialog` three times: an unrelated save must leave the
  same manager instance in place, enabling a plug-in must dispose it, and disabling one must dispose
  it. Verified by mutation — restoring the unconditional `DisposePlugInManager()` fails the step on
  its first assertion. No plug-in is instantiated by the step, because the manager loads lazily on
  its first `GetRows` call. Full `Build.ps1` green, Corsair plug-in self-test still 65 checks.
- **Residual risk:** a manager is now kept across preference saves, so a plug-in that misbehaves is
  no longer implicitly restarted by opening Preferences. That was never a documented recovery path,
  and disabling and re-enabling the plug-in still rebuilds it. The guard is conservative in the safe
  direction: it compares against the set captured when the manager was constructed, which is at or
  before the moment `EnsureLoaded` reads that set, so it can only ever rebuild more often than
  strictly necessary, never less.

## Proposal 5 — `refreshInProgress` needs a watchdog: one wedged collection kills the refresh loop for good

- **Symptom that produced it:** at 17:31:53 on 2026-08-07 the app stopped refreshing entirely and
  never recovered. The log goes silent — no `CollectSensorRows`, no `WMI provider processes` line
  (written unconditionally at the end of **every** collection,
  `src/SensorReadoutForm.FanControls.cs:397`, and present every ~5 s until that moment), no
  plug-in load block — from 17:31:53 until the user restarted the app at 17:43:09. No crash-log
  entry, no Windows Application-log hang or error event in that window, and the settings the app
  had just written show `AutoRefreshEnabled: true`, `RefreshIntervalSeconds: 5`,
  `LoggingLevel: Debug`. The refresh timer was armed and Debug logging was on; the app simply
  never collected again. Because plug-ins are only instantiated from inside a collection, the
  Corsair plug-in was never loaded again either, so the hub stayed in the hardware mode the
  preceding teardown had put it in — the fans stayed loud until restart.
- **The mechanism:** `refreshInProgress` is set on the UI thread in `RefreshSensors`
  (`src/SensorReadoutForm.FanControls.cs:210`) and cleared in exactly one place, the continuation
  of that collection's task (`:275-284`). While it is set, `RefreshSensors` early-outs into
  `QueuePendingRefresh` (`:203-207`) and `RunPendingRefreshIfNeeded` early-outs on the same flag
  (`:301-306`). So if a collection never completes — for any reason, in any phase — every later
  timer tick silently queues a pending refresh that can never run, for the remaining life of the
  process. There is no timeout on the outer collection and no watchdog. The inner
  LibreHardwareMonitor phase *is* bounded (`AddTimedRowsWithTimeout`, `:434-482`); nothing else
  is.
- **What this narrows the search to.** The clear sits in a `finally` and the continuation handles
  `task.IsFaulted` before it, so an *exception* anywhere in `CollectSensorRows` cannot wedge the
  flag — it is cleared and a pending refresh is re-armed as normal. Only two things can: a
  collection that genuinely never returns (a hang inside one of the phases), or a UI thread that
  stops pumping so the continuation never runs at all, since it is scheduled via
  `TaskScheduler.FromCurrentSynchronizationContext()` (`:285`). The plug-in phase had already
  completed and released everything it holds before the stall, so it is one of the later,
  WMI-heavy phases or the message pump.
- **Why it is worth fixing even though the wedge cause is unknown:** the failure is silent,
  total, and unrecoverable without a restart, and it takes fan control down with it. Whatever
  wedged — the collection had already passed the plug-in phase (`returned 36 rows` at 17:31:53)
  and the plug-in's own teardown had completed and released everything it holds, so the stall was
  in a later, WMI-heavy phase — a stuck phase should degrade one refresh, not the app.
- **Change made — a watchdog, not a timeout.** The outer collection is *not* given a bounded wait:
  it runs on a thread-pool thread that cannot be cancelled, so a "timeout" could only mean
  abandoning it, which is exactly what the watchdog does, more cheaply and without a second waiter.
  `RefreshSensors` records when the in-flight collection started
  (`refreshInProgressSinceAwakeMs`). When a refresh is requested while one is already in flight, it
  now asks `TrySupersedeStalledRefresh()` before queueing: if the in-flight collection has been
  running past the threshold, one Error line is logged, `refreshInProgress` is released, and the new
  collection starts. No new thread and no new timer — the check rides the refresh request that
  would otherwise have been queued and dropped.
- **The elapsed time is measured on the machine's *awake* clock**
  (`NativeMethods.TryGetAwakeMilliseconds`, i.e. `QueryUnbiasedInterruptTime`), not the wall clock.
  The first version used `DateTime.UtcNow` and every firing it ever produced in the field was a
  hibernation rather than a stall — see the production-evidence bullet below. `DateTime.UtcNow` is
  not alone in this: `GetTickCount64` and `QueryPerformanceCounter`, and therefore `Stopwatch` and
  every phase timing this app logs, all count suspended time as elapsed. Measured on the reference
  machine: wall clock 22,750.6 s, `GetTickCount64` 22,750.7 s, QPC/Frequency 22,750.9 s,
  `QueryUnbiasedInterruptTime` 9,108.6 s. There is deliberately no fallback to a biased clock when
  the unbiased one is unavailable — mixing bases between the two readings either fires instantly or
  never fires — so the watchdog simply stands down on a Windows that cannot answer. (For the
  avoidance of doubt: this app does **not** require Windows 10. `IsWindows10OrLater` has exactly one
  call site and it gates the Prism speech backend. The reason availability is a non-issue is that
  the export is Windows 7+ and .NET Framework 4.x does not install below Vista. There is also no
  `WM_POWERBROADCAST` / `PBT_APM*` handling anywhere in this app; the `WndProc` override handles
  hotkey messages only.)
- **Threshold — `max(120 s, 6 × the user's refresh interval)`, doubled for the first collection of
  the process.** Six intervals is far past anything a healthy pass takes. The floor exists because
  the interval can be as low as 1 s, where six intervals would supersede a merely slow collection on
  a busy machine; it started at 60 s and was raised after the field logs showed how little headroom
  that left. The worst genuine collection observed was 20,371 ms
  (`LibreHardwareMonitorFull=5876; SlowRowsRefresh=8440; Tasks=1941; Battery=1318;
  OemProviders=1134`) — and only the LibreHardwareMonitor phase is bounded at all, by its own 20 s
  guard, while the other thirteen phases are unguarded. The doubling covers the first collection
  specifically: it is the expensive one (cold LibreHardwareMonitor, uncached slow rows, cold WMI,
  every plug-in's first device scan), it is the one the check is guaranteed to run against at a 5 s
  interval, and a false positive there tells the user to restart an app they have just launched. A
  slow-but-completing refresh can never trip any of this: the check only runs when a *new* refresh
  is requested while one is in flight, and the flag is cleared the moment the in-flight one
  completes.
- **Bounded recovery.** `CollectSensorRows` serializes on `sensorCollectionLock`, so if the wedged
  pass never returns, the replacement blocks behind it. Firing on every tick would therefore leave
  one blocked thread-pool thread per interval behind for the life of the process. A latch
  (`refreshStallReported`) permits exactly one replacement, and exactly one Error line, per stall
  episode; the continuation of whichever collection completes clears it. A generation counter makes
  the flag belong to the newest collection only, so a superseded pass that finally returns cannot
  declare the pipeline idle underneath its replacement — without it, every later tick would start
  another collection behind the same lock. It also keeps stale rows from driving fans: the abandoned
  pass's `ApplyFanCurvesAsync` early-outs on `refreshInProgress`, which is still set because the
  replacement owns it. That needs the stale continuation to run *while* the replacement is in
  flight, and it does: the replacement cannot start collecting until the stalled pass releases
  `sensorCollectionLock` — so the stalled pass has already posted its continuation — and the
  WinForms synchronisation context dispatches posted continuations in order. If that ordering ever
  failed, stale rows would drive fan curves.
- **A superseded pass publishes nothing at all.** The generation check above started life in the
  continuation's `finally`, covering only the flag reset, which left every publishing call above it
  running for an abandoned generation: `SetLatestRows`, `LogTrendRows`,
  `TryApplySavedFanControlsOnStartupAsync`, the Preferences row update, the reading tree,
  `UpdateTrayStatus`, the status line — and `CheckAlarms`, which ends in
  `SpeakTextWithScreenReaderPolite`. A superseded pass is by construction an old one, and across a
  sleep it can be hours old, so that was an abandoned collection showing, logging and *speaking* an
  alarm derived from readings the machine no longer has, to a screen-reader user, seconds after they
  wake it. The continuation now returns before any of that when `generation != refreshGeneration`,
  and logs one Debug line naming both generations. The `finally` is unchanged and still runs for the
  superseded pass, because its two remaining calls (`ApplyFanCurvesAsync`,
  `RunPendingRefreshIfNeeded`) both early-out on `refreshInProgress` and are the bookkeeping a
  superseded pass should still take part in.
- **The Error line names the stall duration**, says refreshes had stopped and that readings, alarms
  and fan curves went unapplied, says a replacement is starting and that it is waiting on the
  stalled collection's lock, and tells the user what to do if refreshes do not resume. It is the
  half of this change that matters most: today the failure is completely invisible.
- **Also fixed here, two lines:** `GetPlugInRows` and `TryPlugInFanControl`
  (`src/SensorReadoutForm.PlugIns.cs`) each read the `plugInManager` field twice without
  synchronisation, while `DisposePlugInManager()` sets it to `null` from the UI thread. A dispose
  landing between the two statements is a `NullReferenceException` on the collection thread —
  recoverable, since `task.IsFaulted` is handled, but it turns a plug-in toggle into a failed
  refresh. Both now read the field into a local and treat `null` as "no rows" / "not handled".
  `GetPlugInRows` also reports whether a live manager served the call, and
  `GetOemProviderRows` skips its cache write when one did not: an empty result caused by a
  concurrent teardown says nothing about the plug-ins, and caching it would suppress every plug-in
  reading for a whole cache interval — up to five minutes with the app in the tray.
- **Why generic:** nothing about this is plug-in-specific; it is the app's whole refresh pipeline.
- **Files changed:** `src/SensorReadoutForm.cs` (three fields next to `refreshInProgress`),
  `src/SensorReadoutForm.FanControls.cs` (the guard in `RefreshSensors`, the generation check at the
  top of the continuation and in its `finally`, and the private decision methods),
  `src/NativeInterop.cs` (the `QueryUnbiasedInterruptTime` P/Invoke, its cached probe and
  `TryGetAwakeMilliseconds`), `src/SensorReadoutForm.PlugIns.cs` (the two-line null-safety fix).
- **Test:** self-test step "Stalled refresh watchdog" (`src/SensorReadoutForm.SelfTest.cs`).
  The decision is a pure function of elapsed time, the refresh interval and whether this is the
  first collection of the process, so it is asserted directly with no real stall: false for a
  collection that just started, false for a slow but recent one, false for the worst genuine
  collection ever observed (20,371 ms), false at 61 s and 119 s (the raised floor), true at 121 s,
  false at 121 s for a first collection and true at 241 s, and both sides of the six-intervals rule
  at a 300 s interval. The clock is asserted separately: it must answer, it must never read ahead of
  the biased tick count, a collection ten *awake*-seconds old must not be a stall while a
  ten-awake-minute one must be, a machine without the clock must never report one, and a start stamp
  ahead of "now" must not be one either. It then drives the real `TrySupersedeStalledRefresh` with
  synthetic clock readings — including the production shape, hours of wall clock with no awake time
  — to assert that a healthy in-flight collection keeps the flag, that a ten-minute-old one releases
  it, and that the latch refuses a second recovery attempt. That real method's Error line is
  suppressed under test and the step proves the suppression by counting the line in the log before
  and after: written from a test it would be indistinguishable from a genuine firing in the one file
  users are asked to send in for support. Full `Build.ps1` green, Corsair plug-in self-test 74
  checks.
- **What the test does *not* pin, stated so it is not mistaken for coverage:** the generation guard
  in the continuation — both the publish skip and the flag reset — and the latch's
  clear-on-completion are not exercised. Deleting `if (generation != refreshGeneration)` still
  passes the suite, because reaching either needs a collection that actually stalls and later
  returns, over a live form with real rows and the real alarm speaker. They are argued in the
  comments at the call site instead. The call-site wiring is likewise unpinned: removing
  `&& !TrySupersedeStalledRefresh()` from `RefreshSensors` would not fail the step.
- **Production evidence — 3.5 days of ordinary use, 2026-08-18 00:22 → 2026-08-21 17:27, 122k log
  lines at Debug.** The watchdog fired **five times, and all five were false positives**: each was a
  hibernation with a collection in flight from before the machine slept, measured with the
  wall clock the first version used. The clearest of them reported "has not completed for 13642 s"
  at a moment when the machine's accumulated sleep bias was 13,642 s — the whole of the reported
  duration and none of it real. Reconstructed on an unbiased clock the five were 2, 2, 3, 2 and 0 s
  old. **Zero genuine stalls were observed in that corpus**, so the recovery path this proposal
  exists for has still never run in the field; what has been demonstrated is the false-positive
  rate, and the fix for it. The cost of each false firing was one Error telling the user to restart
  the app, an abandoned collection, and a replacement queued behind its lock.
- **Scope, stated plainly.** What this watchdog actually recovers is a **starved continuation** —
  a message pump that stopped dispatching, or a lost continuation — where the replacement runs and
  the app comes back on its own. It does **not** recover a phase hung inside `sensorCollectionLock`:
  `CollectSensorRows` wraps `CollectSensorRowsCore` in that lock, only the LibreHardwareMonitor
  phase has a timeout guard, and the other thirteen phases use unguarded `AddTimedRows`, so for the
  headline scenario in its own comment — a phase that hangs — the replacement blocks on the same
  lock, `refreshStallReported` stays latched, and the pipeline is as dead as before plus one
  permanently blocked thread-pool thread. The benefit in that case is the loud, single, duration-
  naming Error line where there was previously complete silence. Which of the two the 2026-08-07
  incident was is still unknown.
- **The watchdog only fires when something asks for a refresh.** With auto-refresh paused, a wedged
  collection stays silent until the user unpauses or presses Refresh — at which point the check runs
  and the stall is logged. That is self-resolving rather than a hole: with no refreshes wanted,
  nothing is being missed. In the observed incident auto-refresh was on at a 5 s interval, so the
  watchdog would have fired within a minute of the stall.
- **Residual risk — this recovers the pipeline; it does not fix whatever wedged the collection.**
  If the stall is a genuine hang inside `CollectSensorRows`, the wedged pass still holds
  `sensorCollectionLock` forever and the single permitted replacement blocks on it, so refreshes
  still do not resume — but the failure is now logged loudly and once, with the duration, instead of
  being silent. If the stall is a starved message pump or a lost continuation, the replacement runs
  and the app recovers on its own. The root cause of the observed 2026-08-07 stall is still unknown
  (the log ends mid-collection after the plug-in phase, in one of the later WMI-heavy phases), which
  is why the change is a recovery mechanism and a loud log line rather than a claimed fix.
- **Note for the Corsair plug-in:** its deferred hand-back means a wedged refresh loop leaves the
  fans on their last curve values for the grace period instead of dropping them to the hub's own
  loud profile immediately. That is independent of this change and stays as it is.
