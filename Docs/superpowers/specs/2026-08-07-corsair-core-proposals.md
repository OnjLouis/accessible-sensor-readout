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
