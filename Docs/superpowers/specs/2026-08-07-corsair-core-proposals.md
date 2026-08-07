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
was restarted. Both are core-side and **neither has been implemented**; the plug-in-side fix
that ships on this branch works around proposal 4 entirely and merely softens proposal 5. Full
evidence: `.superpowers/sdd/2026-08-07-corsair-plugin/preferences-teardown-fix.md`.

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

## Proposal 4 (NEW, not implemented) — Preferences should not rebuild the plug-in manager unless the plug-in set actually changed

- **Symptom that produced it:** with Corsair fan curves active, the fans audibly spin up the
  moment the Preferences window appears, and again on essentially every keystroke or arrow-key
  press inside it. Confirmed in `C:\SensorReadout\Logs\SIMSTATION.log`, 17:20-17:32 on
  2026-08-07: six complete `returning iCUE LINK hub ... to hardware mode` -> `worker thread is
  exiting` -> `Loaded` -> `taking software control` cycles in eleven minutes, each one triggered
  by a Preferences interaction and each one audible.
- **The chain:** `PreferencesForm`'s `Shown` handler ends with an unconditional
  `SaveLivePreferences()` (`src/PreferencesForm.cs:1609-1614`); so do about sixty control event
  handlers, plus `CommitPreferences()` on `FormClosing` and the `DialogResult.OK` apply.
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
- **Proposed change (one file, one condition):** in `ApplyPreferencesFromDialog`, dispose the
  plug-in manager only when the enabled-plug-in set actually changed. The comparison already
  exists and is already computed on this path — `GetOemProviderRowsCacheSignature(settings)`
  (`src/SensorReadoutForm.OemProviders.cs:106-115`), which `ApplyLivePreferencesFromOpenDialog`
  captures before and after the apply (`src/SensorReadoutForm.PreferencesAndCommands.cs:189,
  195`). Capture it inside `ApplyPreferencesFromDialog` instead, and make line 283 conditional on
  it having changed. Nothing else about the method changes; `ClearOemProviderRowsCache()` on the
  next line can stay unconditional (it is cheap and only affects freshness).
- **Second, smaller half:** the unconditional `SaveLivePreferences()` in the `Shown` handler
  saves settings that nothing changed, purely as a side effect of the window opening. Guarding it
  (only save if `FinishInitialPreferenceLoad` actually altered something) removes a settings-file
  write and a full preference apply from every Preferences open, independent of the above.
- **Why generic:** every `IPluginLifecycle` implementation is shut down and re-instantiated by
  this path, and the plug-in contract has no other way to survive it.
- **Risk:** low and local. The only behaviour that depends on the dispose is picking up a changed
  plug-in enablement set, which is exactly what the guard tests for. Plug-in imports
  (`ImportPlugInFromZip`, `src/SensorReadoutForm.PreferencesAndCommands.cs:344`) and settings
  imports (`src/SensorReadoutForm.SettingsTransfer.cs:598`) keep their unconditional dispose.
- **Status if not accepted:** the Corsair plug-in on this branch no longer restores hardware
  inside `Shutdown()` — it arms a hand-back that the next `GetReadings` cancels — so the audible
  symptom is gone without this change. The churn itself (an assembly-loading, manifest-parsing
  manager rebuild per keystroke) remains.

## Proposal 5 (NEW, not implemented) — `refreshInProgress` needs a watchdog: one wedged collection kills the refresh loop for good

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
- **Why it is worth fixing even though the wedge cause is unknown:** the failure is silent,
  total, and unrecoverable without a restart, and it takes fan control down with it. Whatever
  wedged — the collection had already passed the plug-in phase (`returned 36 rows` at 17:31:53)
  and the plug-in's own teardown had completed and released everything it holds, so the stall was
  in a later, WMI-heavy phase — a stuck phase should degrade one refresh, not the app.
- **Proposed change:** give the outer collection the same treatment the LHM phase already gets —
  a bounded wait with a logged fallback — or, more cheaply, a watchdog that notices
  `refreshInProgress` has been set for more than N times the refresh interval, logs it loudly
  with the reason string, clears the flag and re-arms. Either shape is a few lines in
  `src/SensorReadoutForm.FanControls.cs`. Logging the stall is the important half: today it is
  completely invisible.
- **Related, one line, independent:** `GetPlugInRows` and `TryPlugInFanControl`
  (`src/SensorReadoutForm.PlugIns.cs:21-31`) each read the `plugInManager` field twice without
  synchronisation:

  ```csharp
  EnsurePlugInManager();
  return plugInManager.GetRows(diagnosticsMode);
  ```

  `DisposePlugInManager()` runs on the UI thread and sets that field to `null` (`:58-66`), while
  these two run on the collection thread. A dispose landing between the two statements is a
  `NullReferenceException` on the collection thread. It is recoverable (`task.IsFaulted` is
  handled), but it turns a Preferences keystroke into a failed refresh, and with proposal 4's
  churn the window is not rare. Assigning the field to a local once fixes it.
- **Why generic:** nothing about this is plug-in-specific; it is the app's whole refresh
  pipeline.
- **Status if not accepted:** the Corsair plug-in's deferred hand-back means a wedged refresh
  loop now leaves the fans on their last curve values for the grace period instead of dropping
  them to the hub's own loud profile immediately — a better failure mode, but the app still stops
  refreshing and still needs a restart.
