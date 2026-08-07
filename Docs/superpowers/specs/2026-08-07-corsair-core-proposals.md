# Core Change Proposals Accompanying the Corsair Plug-In

Date: 2026-08-07. Branch: `feature/corsair-core-proposals` (based on `feature/corsair-plugin`,
rebased onto v5.0.0). Author: Robin Kipp with Claude Code.

Per `Docs/Coding-agent-plug-in-rules.md`, core changes require a written case and explicit
approval from Andre. These three small changes were identified while building the Corsair
plug-in; each is generic (any plug-in benefits), isolated, and separately revertible — one
commit per proposal. None changes behavior for users without plug-ins or fan curves.

## Proposal 1 — Foreground cache interval for plug-in readings that feed enabled fan curves

- **What the plug-in cannot do today:** while the app is minimized, plug-in rows are served
  from a 5-minute host cache (`BackgroundOemProviderRowsMinimumInterval`). A fan curve keyed
  on a plug-in temperature (e.g. the Corsair hub's liquid temperature) therefore reacts up
  to 5 minutes late exactly when the machine is under load with the app in the tray. The
  plug-in cannot influence the host cache from its side of the SDK.
- **Why generic:** any plug-in exposing a temperature that a user selects as a fan-curve
  input has the same problem (Framework EC temperatures, future OEM plug-ins). The check is
  data-driven — no vendor names anywhere.
- **File changed:** `src/SensorReadoutForm.OemProviders.cs` only. When `backgroundRefresh`
  is requested, the background interval is used unless an enabled, non-suspended
  `FanCurveSetting`'s `TemperatureReadingKey` resolves to an identifier present in the
  cached plug-in rows — then the foreground interval (10 s) applies. Curves suspended by a
  manual fan action are skipped, matching `ApplyFanCurvesAsync`'s definition of a live
  curve. With no curves, no plug-ins, or curves keyed on host readings, behavior is
  byte-identical to before.
- **Test:** full self-test passes; manual verification on live hardware (Corsair plug-in +
  liquid-temperature curve responds at foreground cadence while minimized; with the curve
  disabled, the 5-minute background cache is used as before). Plug-ins that are disabled or
  return no rows never match, so the exemption cannot wake fragile vendor providers by
  itself — a curve must explicitly reference one of their readings.

## Proposal 2 — Fan rows keep `Details` and `WindowsSettingsUri` when a control percent attaches

- **What the plug-in cannot do today:** `AttachFanControlPercentsToFanRows`
  (`src/SensorReadoutForm.FanControls.cs`) rebuilds each Fan row to append "NN%" to the
  display value, but the rebuilt row drops `Details` and `WindowsSettingsUri`. As soon as a
  fan is under manual/curve control, its Details dialog content disappears — for plug-in
  fans that includes device identity, firmware, and safety notes.
- **Why generic:** affects any provider that supplies `Details` (or a settings link) on a
  Fan row — today that means plug-ins; nothing in the row contract says Fan rows cannot
  carry Details, and the rebuild silently assumed they never do.
- **File changed:** `src/SensorReadoutForm.FanControls.cs` — the rebuilt row deep-copies
  `Details` (matching `CloneSensorRow` and `ApplyFanLabelsToReadings` in the same file)
  and carries `WindowsSettingsUri`.
- **Test:** full self-test passes; live check that a controlled Corsair fan keeps its
  Details entries. No behavior change for rows without details.

## Proposal 3 — Opt-in "Zero RPM capable" marker keeps semi-passive fan controls visible

- **What the plug-in cannot do today:** `ShouldShowFanControl` hides any control whose
  paired Fan row reads 0 RPM (correct for unused motherboard headers). Semi-passive PSU
  fans (Corsair HXi zero-RPM mode) legitimately sit at 0 RPM most of the time, so their
  control is invisible unless the user finds "Show stopped fans". The plug-in cannot
  distinguish itself from an unused header through the current row contract.
- **Why generic:** any provider of semi-passive fans (PSUs, GPU-style hybrid coolers
  surfaced by future plug-ins) needs the same distinction. The marker is a Details key
  (`"Zero RPM capable"`), so it needs no SDK change and is ignored by older hosts. Worst
  case for a plug-in that marks controls indiscriminately is dialog clutter the user could
  already produce with "Show stopped fans" — the marker is opt-in per control.
- **Files changed:** `src/SensorReadoutForm.FanControls.cs` — one early-return in
  `ShouldShowFanControl` when the control's Details contain the key, and the visibility
  filter now runs before `EnrichFanControlRow` (which strips Details) at both call sites
  (`FanControls.cs` and `FanCurves.cs`) — behavior-identical for existing rows because
  the filter only reads `Identifier` and `Details`. `src/SensorReadoutForm.SelfTest.cs`
  gains a "Zero-RPM fan control visibility" step asserting a marked stopped control stays
  visible and an unmarked one stays hidden. `Docs/Plug-In-development.md` documents the
  key as part of the plug-in contract. The Corsair plug-in adopts the marker in the same
  commit (plug-in-side change only adds a Details entry).
- **Test:** the new self-test step (would have caught the filter/enrich ordering — it was
  found by review); full self-test passes; live check that the PSU fan control is visible
  at 0 RPM without "Show stopped fans", and that ordinary 0-RPM header controls remain
  hidden.
