# Corsair Plug-In — Supervised Hardware Validation Plan

Purpose: record exactly what has been exercised on real Corsair hardware before the fan-control
write path is offered to users, and what still has to be exercised. Fan control was withheld from
the 5.2.0 monitoring release pending "broader hardware validation"; this document is the plan for
that validation and the running record of its results.

Reference system for everything marked ALREADY VALIDATED below:

- Corsair iCUE LINK Hub, firmware 3.12.650 (product id 0x0C3F), 11 attached devices: one TITAN AIO
  pump with liquid temperature, seven QX fans (RPM + in-fan temperature), three RX RGB fans (RPM
  only), plus the AIO's LCD cap on its own port, which the hub does not separately identify.
- Corsair HX1200i (2025) digital power supply.
- Windows 11, with Debug logging enabled during the runs.
- Date of the validated runs: 2026-08-07.

**What "ALREADY VALIDATED" means here, precisely.** Those rows were observed on 2026-08-07 on
builds from the *predecessor* branch, not from this one. The control code they exercise was
carried onto this branch unchanged: a line-by-line drift audit found the device-layer control
paths (duty writes, mode changes, pump clamp, PSU mode/duty writes, per-device restores) and the
worker's control routing byte-identical between the two branches. What differs on this branch is
the *orchestration* around them — teardown now runs through the host's `IPluginLifecycle.Shutdown`
rather than a process-exit handler — which is why the one item that depends on that orchestration
is marked PENDING RE-RUN below rather than validated.

The Debug logs from the 2026-08-07 runs were **not retained**. Every validated row's procedure is
re-runnable and cheap, and the weekend test pass **should** re-run all of them with Debug logging
on so that a retained log becomes the evidence of record. Until then, each row states what was
observed, not what can be produced on demand.

**Tester: keep `Logs\<machine>.log` from the whole validation session** (copy it out before it
rotates) and attach it to the results. That file is the evidence for both the re-runs and the
pending items.

Anything not listed as ALREADY VALIDATED has not been observed on hardware in this form and is
marked PENDING (or PENDING RE-RUN) with the procedure to run and the outcome to expect.

## 1. Scope and safety invariants

These are the properties the validation exists to protect. Any observation that contradicts one of
them is a stop-and-report result, not a retry.

1. **Nothing is written unless this machine has asked.** Enumerating, connecting and polling never
   send a mode change or a duty on their own. There are exactly four kinds of write:
   1. a fan-control action from the host (the user moved a control, a curve applied, diagnostics
      ran, or the host re-applied a saved setting);
   2. the resume of a control *this process* already recorded taking — after a reconnect or a
      sleep/wake, where a freshly constructed device object would otherwise start blank;
   3. the **marker-file resume**: a hub running its own hardware profile refuses sub-device
      enumeration outright, so a hub that was handed back on a previous clean exit cannot be read,
      controlled, or recovered from by anything except a program taking software control. The
      plug-in takes it back on connect — enter software mode, enumerate, baseline, write the duty
      set — but *only* when `Plug-Ins\Corsair\corsair-hub-<serial>.controlled` exists, which is
      written the first time fan control is used for that hub on this machine and deleted by the
      user (or cleared by an app update) to switch the behaviour off. So the authorization is "this
      machine has used Sensor Readout to drive this hub's fans", carried across processes on disk;
      it is never "this hub happens to be takeable". Without the marker the hub is left exactly as
      found and a status row explains how to start. This is the behaviour V9 validates and the one
      TESTING.md documents under "Exit, restart, and diagnostics";
   4. the restores on shutdown.
2. **Pump floor.** iCUE LINK pump channels (models 0x07, 0x11, 0x0C, 0x19) are clamped to a minimum
   of 50 % duty. A lower request is raised, never passed through, never refused silently. A stalled
   AIO pump reads as a pump failure, so this clamp is not negotiable.
3. **PSU hand-back below 30 %.** The PSU's manual duty range is 30–100. A requested percent below 30
   means "give the fan back to the PSU's own curve" (which is how zero-RPM operation returns), not
   "run the fan at that duty".
4. **Fail-loud, never fail-silent.** Every failure mode leaves the cooling running: a hub that stops
   being driven falls back to its own profile (possibly loud), a PSU keeps its last duty. No path
   may end with a fan or pump stopped.
5. **Exit restores.** On a clean exit or a plug-in disable/reload, power supplies are restored
   first, then hubs. The PSU is first because a PSU left in manual mode keeps that duty until
   something writes mode 0x00 or it is power-cycled; a hub reverts on its own once nothing drives
   it.
6. **Interoperability.** Every wire transaction runs inside the shared
   `Global\CorsairLinkReadWriteGuardMutex` with a bounded 2000 ms wait, so monitoring alongside
   HWiNFO, SIV, SignalRGB or Fan Control is safe. Corsair iCUE does not use that mutex and must not
   run at the same time. Only one program should *drive* the fans.
7. **Bounded everywhere.** Host-facing calls never inherit the device layer's worst case: control
   calls wait at most 5 s for the worker's device lock, HID transfers time out at 500 ms, and
   shutdown joins the worker under a bounded budget.

## 2. Validation matrix

### 2.1 Validated on the reference system

| # | Item | Status | What was observed |
| --- | --- | --- | --- |
| V1 | Live monitoring alongside another controlling program | ALREADY VALIDATED (2026-08-07, predecessor-branch build, hub fw 3.12.650 + HX1200i 2025; observed in the Debug log, log not retained) | With Fan Control running and owning the hub, Sensor Readout read pump/fan RPM, liquid and in-fan temperatures, and all PSU rows through the shared mutex without either program losing a transaction. |
| V2 | First-take re-baseline | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and audibly, log not retained) | The first manual percent put the hub into software mode, baselined every channel (pump 100 %, fans 50 %) and wrote the whole set before applying the requested duty. Audible step change, RPM rows followed. |
| V3 | Manual set and reset round trips (hub) | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and the RPM rows, log not retained) | Setting a fan to 80 % then 40 % moved the RPM row both ways within the poll interval; the automatic/default action returned it to the 50 % default with the hub still in software mode. |
| V4 | Pump floor clamp | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and the RPM row, log not retained) | A 30 % request against the pump channel landed at 50 %; the pump never dropped below its floor. |
| V5 | PSU write acknowledgement framing | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log, log not retained) | PSU fan-mode and duty writes were acknowledged with the expected framing; no acknowledgement-mismatch fallback was logged during the run. |
| V6 | PSU manual duty and reset to zero-RPM | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and audibly, log not retained) | A 40 % manual duty spun the PSU fan up; reset (and any value below 30 %) handed it back to the PSU's own curve and it returned to 0 RPM. |
| V7 | Diagnostics 100 % sweep and restore | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the diagnostic ZIP and the Debug log, neither retained) | One-click diagnostics briefly drove every exposed control to 100 % and then restored: hub channels to their defaults (fans 50 %, pump 100 %), PSU fan to automatic. |
| V8 | Clean-exit hardware-mode restore | **PENDING RE-RUN** — see P0 below | Observed working on 2026-08-07, but under the predecessor branch's process-exit teardown, not this branch's `IPluginLifecycle.Shutdown` path. |
| V9 | Restart into a hardware-mode hub (marker auto-resume) | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log, log not retained) | After a clean exit the hub refused sub-device enumeration, so the next start had nothing to read. The marker file recorded prior fan control on this machine, the plug-in took software control on connect, all port rows reappeared, and the saved fan-control settings were re-applied. |
| V10 | "All fans reset" routing | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and audibly, log not retained) | The Fan Controls dialog's bulk reset reached the plug-in's controls and returned manually-set Corsair fans to their defaults immediately, instead of leaving them to the curve engine. |

### 2.2 Pending — must be run before the write path is considered validated

Each item lists the procedure and the single expected outcome that decides pass or fail.

#### P0 — Clean-exit hardware-mode restore under the new teardown path (re-run of V8)

- **Status:** PENDING RE-RUN.
- **Why it is not simply carried over:** the device-level restore transactions are unchanged from
  the build V8 was observed on — same hand-back commands, same per-device `Disconnect(restore)`
  decisions, same PSU-before-hub ordering, verified byte-identical by the drift audit. What changed
  is who drives them and on what budget: teardown now runs through the host's
  `IPluginLifecycle.Shutdown` (called on app close, plug-in disable, and plug-in reload) with the
  worker-thread cleanup and its join budget, instead of the predecessor branch's process-exit
  handler. That is orchestration, so it needs one observation rather than a full re-validation.
- **Procedure (about 30 seconds):** set a fan and the pump to clearly audible manual percents and
  set the PSU fan to 40 %. Quit Sensor Readout normally (File > Exit, not a kill). Listen for the
  hub's own profile taking over, and read the tail of `Logs\<machine>.log`.
- **Expected outcome:** within a few seconds of the quit, the log shows the PSU restore before the
  hub restore and then "the Corsair worker has stopped and every device session is closed"; the
  hub's own hardware profile is audibly back in charge; and the PSU fan has returned to automatic
  (it spins down rather than holding 40 %). No shutdown-timeout warning about the worker thread
  failing to stop.
- **Also worth one look while there:** repeat with Options > Preferences > OK instead of quitting,
  which the host also treats as a plug-in reload. Expect the same restore followed by an automatic
  marker-file re-take a few seconds later, i.e. an audible re-baseline (pump 100 %, fans 50 %)
  before the saved settings are re-applied. This is known and documented behaviour, not a failure;
  the point of the look is to confirm it self-heals rather than leaving the hub unread.

#### P1 — Sleep and resume under an active fan curve

- **Status:** PENDING.
- **Procedure:** Configure an enabled fan curve using a Corsair liquid temperature as input and a
  Corsair fan control as target. Confirm the curve is driving the fan (the control shows a manual
  percent that tracks the temperature). Put the machine into sleep (S3/Modern Standby) for at least
  two minutes, then resume. Watch the Debug log and the Fan Controls dialog for the next three
  minutes. Repeat three times, including once with the app minimized to the tray.
- **Expected outcome:** After each resume the plug-in logs the resume path, re-opens the devices,
  re-asserts software control it previously held, re-applies the recorded duties, and the curve
  resumes driving within roughly 30 seconds — with no interval in which a fan or the pump is
  stopped and no duplicate/queued mode changes in the log.

#### P2 — Surprise USB disconnect and reconnect while under control

- **Status:** PENDING.
- **Procedure:** With a fan set to a manual percent, unplug the iCUE LINK Hub's USB cable (the hub
  keeps its own power). Wait two minutes, watching the rows and the Debug log. Reconnect the cable
  and wait a further two minutes. Repeat for the PSU's USB cable with the PSU fan set manually.
- **Expected outcome:** The disappearance is detected as "device gone" rather than as a run of
  transaction errors, the rows are replaced by the status explanation, no restore write is attempted
  against the vanished device, and the reconnect re-enumerates the device and replays the recorded
  intent (hub: software control plus the saved duties; PSU: the last manual duty). Fans keep
  spinning throughout — the hub falls back to its own profile while unplugged.

#### P3 — Process kill while under control (hub self-heal observation)

- **Status:** PENDING.
- **Procedure:** With the pump and at least one fan under manual control at a clearly audible duty,
  end the Sensor Readout process from Task Manager (End task, not Exit). Do not restart the app.
  Listen and watch a hardware monitor for 15 minutes, noting when and how the hub's behaviour
  changes. Then repeat with the PSU fan set to 40 % and note whether the PSU fan keeps that duty.
- **Expected outcome:** No restore runs (the process is gone), and the hub returns to its own stored
  profile on its own within the firmware's idle timeout — audibly, possibly at full speed, but never
  stopped. The PSU fan is expected to keep its last manual duty until the app is started again (or
  AC power is cycled); this is the known fail-loud state that item 4 of the invariants describes,
  and the point of the test is to confirm it is loud rather than silent.

#### P4 — Multi-hour curve soak

- **Status:** PENDING.
- **Procedure:** Run an enabled fan curve against a Corsair control for at least 8 hours of ordinary
  use, with the app minimized to the tray for most of it and at least one sustained-load period.
  Afterwards, inspect the Debug log for: duty-write retries (`re-sending the duty set`),
  acknowledgement-mismatch fallbacks, wrong-mode read failures, device backoffs, and worker tick
  durations.
- **Expected outcome:** The curve keeps applying for the whole period; no unbounded growth in retry
  or mismatch counts; no device stuck in backoff at the end; tick durations stay in the same range
  as at the start; and the hub is still under software control with the expected duties when the
  soak ends.

#### P5 — Second machine and other supported models

- **Status:** PENDING.
- **Procedure:** Repeat V1–V8 and P1 on at least one further system with different hardware —
  ideally another iCUE LINK Hub firmware revision, a different attached-device mix (for example a
  non-TITAN AIO or an XD5/XD6 pump), and a different HXi/RMi power supply from the supported product
  id list. Record the firmware version, the enumerated model/variant bytes from the diagnostics
  bundle, and any device the plug-in reports as unknown.
- **Expected outcome:** Enumeration names every attached device or degrades to a clearly-labelled
  unknown-model row with duty control withheld; the pump floor applies to whichever pump model is
  present; the PSU accepts manual duty and hand-back at the same thresholds; and no model-specific
  crash, hang, or unacknowledged write appears in the log.

## 3. Reporting

For each pending item, attach to the result: the Sensor Readout Debug log covering the run
(`Logs\<machine>.log`, copied out of the Logs folder before it rotates), the one-click diagnostics
ZIP taken immediately afterwards, and a one-line pass/fail against the expected-outcome sentence
above. A failure against any invariant in section 1 is reported as-is without a retry, because the
interesting evidence is the state the hardware was left in.

The same applies to the re-runs of the section 2.1 rows: once a row has been re-observed on a build
from this branch with the log retained, replace its "log not retained" note with the retained log's
file name and the new date. When every row in section 2.1 and every item in section 2.2 carries a
retained log, the write path is validated.
