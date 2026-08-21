# Corsair Plug-In — Supervised Hardware Validation Plan

Purpose: record exactly what has been exercised on real Corsair hardware before the fan-control
write path is offered to users, and what still has to be exercised. Fan control was withheld from
the 5.2.0 monitoring release pending "broader hardware validation"; this document is the plan for
that validation and the running record of its results.

## Status at a glance (2026-08-21)

**Proven on hardware today**, in a supervised bench session on this branch's build (freshly
rebased onto upstream 6.0.0):

- Clean-exit hand-back, hub only: **PASS**. The full restore-line sequence appeared, in order,
  terminated correctly. See P0, run 1.
- Clean-exit hand-back, hub **and** PSU under manual control: **PASS**. This is the first PSU
  control write ever recorded on this project; the restore was confirmed both in the log, in the
  required PSU-before-hub order, and on the wire by a post-exit read-only probe. See P0, run 2.
  This also closes out the PSU restore path in section 2.3, previously the highest-consequence
  untested path in the change.
- Marker-file auto-resume: re-confirmed on a clean restart. Still n = small, still restarts only
  — see below and section 2.3.

**What 3.5 days of production logs separately proved**, unsupervised (full detail in section 2.3):
every duty write reached the hardware (13,066/13,066), the 10 s fan-curve rate limit and the
`MinimumChangePercent` gate held with no exceptions, the pump floor never read below 50 %, plug-in
row integrity held for 14,557/14,558 polls, and in-memory intent replay across sleep/resume
recovered 5/5 times (1/5 with a slow first attempt — since fixed).

**Deliberately not tested — a decision, not a gap (section 2.4):** surprise USB unplug/replug of
the hub or the PSU. The hardware owner has ruled this out: reaching either connector means opening
a powered, running system to work at an internal header, which risks physical damage and personal
injury and is not a hot-plug event either device is designed to absorb, and if an AIO hub genuinely
vanished at runtime no fan-control action could help anyway. The defensive code for this (device-gone
detection, session teardown, reconnect/backoff) stays in the plug-in, checked by inspection and by
the ordinary teardown at every clean app exit, not by an induced disconnect.

**Still pending, and why:**

- Marker-file auto-resume **across a sleep** — every observation so far (two, plus the one in the
  production corpus) is a restart, never a sleep/resume.
- A forced collection wedge — the watchdog is only known to recover a starved continuation, not a
  phase genuinely hung inside the collection lock (section 2.3), so this test would characterise
  the failure shape rather than validate a fix for it.
- Per-device backoff (5 failures / 30 s) — still never exercised, but it does not need hardware to
  be unplugged: it fires when an already-open session's reads keep failing, not when a device
  disappears. See section 2.3 for how it could be reached.
- P1's deliberate sleep/resume pass, P3 (process kill), P4 (multi-hour soak), P5 (second machine),
  P6/P7 (Preferences round-trip and plug-in disable) — untouched by today's session; see section 2.2.

Reference system for everything marked ALREADY VALIDATED below:

- Corsair iCUE LINK Hub, firmware 3.12.650 (product id 0x0C3F), 11 attached devices: one TITAN AIO
  pump with liquid temperature, seven QX fans (RPM + in-fan temperature), three RX RGB fans (RPM
  only), plus the AIO's LCD cap on its own port, which the hub does not separately identify.
- Corsair HX1200i (2025) digital power supply.
- Windows 11, with Debug logging enabled during the runs.
- Date of the validated runs: 2026-08-07.
- Date of the follow-up bench session (P0 re-run, V9 re-confirmation): 2026-08-21, same reference
  system, this branch's build, after rebasing onto upstream 6.0.0.

**What "ALREADY VALIDATED" means here, precisely.** Those rows were observed on 2026-08-07 on
builds from the *predecessor* branch, not from this one. The control code they exercise was
carried onto this branch unchanged: a line-by-line drift audit found the device-layer control
paths (duty writes, mode changes, pump clamp, PSU mode/duty writes, per-device restores) and the
worker's control routing byte-identical between the two branches. What differs on this branch is
the *orchestration* around them — when the teardown runs and who drives it — which is why the
items that depend on that orchestration are marked PENDING or PENDING RE-RUN below rather than
validated. V8 and V9 are the exception as of 2026-08-21: both were independently re-observed on
this branch's own build (see P0 and section 2.3), so they no longer rely on the drift-audit
argument at all.

**Deferred hand-back — read this before running P0, P6 or P7.** `IPluginLifecycle.Shutdown` no
longer hands the hardware back at all. It arms a hand-back that the next `GetReadings` cancels;
the worker runs it itself once the grace period elapses with no host contact (three observed host
refresh intervals, clamped to 20–90 s); and `AppDomain.ProcessExit` ignores the grace and runs it
immediately. The reason is that Sensor Readout disposes and re-creates its entire plug-in manager
on every live preference save — including one fired the instant the Preferences window appears —
so restoring inside `Shutdown` returned the hub to its own loud firmware profile every time the
user touched Preferences, six times in eleven minutes in the field log that prompted the change.

This is a **deliberate deviation from the documented plug-in contract.**
`Docs\Plug-In-development.md` says `Shutdown` "should stop background work and release hardware
handles"; at call time this plug-in now does neither. It is disclosed to Andre up front as the
preamble to proposals 4 and 5 in `Docs\superpowers\specs\2026-08-07-corsair-core-proposals.md`,
and it is the reason for the two new pending items P6 and P7: the properties that used to fall out
of the old design now have to be observed.

Practical consequences for the tester:

- Quitting Sensor Readout restores **immediately**, through the ProcessExit path and its
  1500 ms budget (not the 15 s budget the old `Shutdown` path had).
- Disabling the plug-in restores **after the grace period**, typically ~30 s.
- Opening, using and closing Preferences must produce **no hardware traffic at all**.

The Debug logs from the 2026-08-07 runs were **not retained**. Every validated row's procedure is
re-runnable and cheap, and the weekend test pass **should** re-run all of them with Debug logging
on so that a retained log becomes the evidence of record. Until then, each row states what was
observed, not what can be produced on demand.

**Tester: keep `Logs\<machine>.log` from the whole validation session** (copy it out before it
rotates) and attach it to the results. That file is the evidence for both the re-runs and the
pending items.

Anything not listed as ALREADY VALIDATED has not been observed on hardware in this form and is
marked PENDING (or PENDING RE-RUN) with the procedure to run and the outcome to expect, unless it
has been deliberately ruled out and marked NOT APPLICABLE in section 2.4 instead.

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
5. **Exit restores, and only where a restore is actually meant.** A plug-in *reload* — which is
   what the host performs on every preference change — restores nothing at all and must not
   disturb the hardware in any way; see the deferred hand-back note below. A clean exit and a
   genuine plug-in disable both do restore, and there power supplies are restored first, then
   hubs. The PSU is first because a PSU left in manual mode keeps that duty until something
   writes mode 0x00 or it is power-cycled; a hub reverts on its own once nothing drives it.
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
| V8 | Clean-exit hardware-mode restore | ALREADY VALIDATED (2026-08-21, this branch's build, hub fw 3.12.650 + HX1200i 2025; observed directly in the Debug log) — see P0 | Re-run twice under this branch's `AppDomain.ProcessExit` teardown: a hub-only pass and a PSU-under-manual-control pass both produced the full restore-line sequence in the required order, and the PSU pass's restore was independently confirmed at the hardware level by a post-exit read-only probe. |
| V9 | Restart into a hardware-mode hub (marker auto-resume) | ALREADY VALIDATED (2026-08-07, predecessor-branch build; log not retained) — re-confirmed 2026-08-21, this branch's build | After a clean exit the hub refused sub-device enumeration, so the next start had nothing to read. The marker file recorded prior fan control on this machine, the plug-in took software control on connect, all port rows reappeared, and the saved fan-control settings were re-applied. Re-confirmed on 2026-08-21's bench session: `resuming fan control of hub … (this machine previously used Sensor Readout for fan control)` followed by `is now in software mode with 12 channel(s) enumerated`, with curves writing within ~20 s. Still n = small and still only across a restart, never across a sleep — see section 2.3. |
| V10 | "All fans reset" routing | ALREADY VALIDATED (2026-08-07, predecessor-branch build; observed in the Debug log and audibly, log not retained) | The Fan Controls dialog's bulk reset reached the plug-in's controls and returned manually-set Corsair fans to their defaults immediately, instead of leaving them to the curve engine. |

### 2.2 Pending — must be run before the write path is considered validated

Each item lists the procedure and the single expected outcome that decides pass or fail.

#### P0 — Clean-exit hardware-mode restore under the new teardown path (re-run of V8)

- **Status: PASS, twice** (2026-08-21, this branch's build, hub fw 3.12.650 + HX1200i 2025 PSU).
  This closes out what had been, until today, the **highest-priority** pending item. Section 2.3's
  "The one clean exit" shows the only clean exit in the production corpus produced **no restore
  line at all**, which left it possible this path had never run on this branch. Both runs below
  produced the full restore-line sequence, in the required order, terminated correctly — see
  "Result (2026-08-21)" below for the actual log evidence.
- **Why it is not simply carried over:** the device-level restore transactions are unchanged from
  the build V8 was observed on — same hand-back commands, same per-device `Disconnect(restore)`
  decisions, same PSU-before-hub ordering, verified byte-identical by the drift audit. What changed
  is *when* they run and on what budget. On this branch `IPluginLifecycle.Shutdown` deliberately
  touches no hardware, so a clean exit restores through the `AppDomain.ProcessExit` handler and its
  `ProcessExitJoinMs = 1500` budget — the same handler the predecessor branch used, but now as the
  primary path for a normal quit rather than a fallback, and with a tighter budget than the 15 s the
  old `Shutdown` path had.
- **What this run has to discriminate.** Two hypotheses explain the production observation, and the
  hardware's end state cannot tell them apart because the hub's own firmware idle failsafe reaches
  hardware mode on its own: **H1**, the restore ran and left no log line (implausible — see 2.3 —
  and the null hypothesis to exclude); **H2**, the restore never ran. **Only the log lines separate
  them**, so "the hub was in hardware mode afterwards" is *not* a pass.
- **Procedure (about a minute), run twice — once for the hub, once with the PSU as well:**
  1. Set a fan and the pump to clearly audible manual percents. For the PSU variant, also set the
     PSU fan to 40 % **and confirm from the log that the manual write landed** — the PSU restore is
     gated on `everSetManual`, so without this step its absence afterwards would prove nothing.
  2. Note the wall-clock second, then quit Sensor Readout normally (File > Exit, not End task).
  3. **Restart within about ten seconds**, so the hub's own idle failsafe cannot plausibly have
     acted in the gap. In production the gap was 72 s, which is ample for the failsafe and is
     precisely why that observation is uninformative.
  4. Listen throughout, and read `Logs\<machine>.log` across the whole exit window.
- **Pass criterion — explicit, and all of it inside the exit window:**
  - the hub line **`returning iCUE LINK hub <serial> to hardware mode.`** appears; and
  - for the PSU variant, **`returning the fan of Corsair PSU <id> to automatic control.`** appears
    **and** the `0xF0 = 0x00` write is observably effective — the PSU fan spins down rather than
    holding 40 %, with no `the automatic-control restore … did not complete` Error; and
  - the PSU line precedes the hub line (PSU-before-hub ordering); and
  - **`the Corsair worker has stopped and every device session is closed.`** appears as the
    terminator — it is the last line `CleanupOnWorkerThread` writes, so its absence means cleanup
    did not finish.

- **Result (2026-08-21).**

  **Run 1 — hub only under control.** Exit at 19:05:29–30. `Logs\<machine>.log` shows, in order,
  inside the exit window:

  1. `Corsair plug-in: the host released the Corsair plug-in; its devices stay under this
     plug-in's control for up to … ms in case the host is only reloading it.`
  2. `Plug-In sensorreadout.corsair.experimental shut down cleanly.`
  3. `Corsair plug-in: returning iCUE LINK hub 0f14…e46f to hardware mode.`
  4. `Corsair plug-in: the Corsair worker has stopped and every device session is closed.`
  5. `Corsair plug-in: the Corsair worker thread is exiting.`

  All five lines are present, in the right order, inside the exit window. **PASS.**

  **Run 2 — the consequential variant, PSU under manual control.** A manual PSU duty (45 %) had
  been saved to settings and was re-applied by the app at start-up —
  `Applied saved fan control corsair/psu/1c27/control/0: 45% manual.` at 19:08:28 — which is both
  the first PSU control write ever recorded on this project and confirmation that the PSU
  restore's `everSetManual` gate was actually armed, so its absence afterwards would have meant
  something. A read-only probe confirmed the hardware had taken the write: PSU fan mode `manual`,
  fan speed 1084 rpm, against 0 rpm / automatic beforehand.

  Exit at 19:10:17–18. The log shows, in order:

  1. `Corsair plug-in: returning the fan of Corsair PSU HX1200i [1c27] to automatic control.`
     (PSU first)
  2. `Corsair plug-in: returning iCUE LINK hub 0f14…e46f to hardware mode.` (hub second)
  3. `Corsair plug-in: the Corsair worker has stopped and every device session is closed.`

  The PSU-before-hub ordering — which exists precisely because the PSU has no firmware failsafe to
  fall back on — held under real conditions. A post-exit read-only probe confirmed the restore on
  the wire, not just in the log: PSU fan mode `automatic (PSU curve, zero-RPM capable)`, fan speed
  376 rpm. **PASS, and the restore is verified at the hardware level, not merely in the log.**

  **What this resolves, and what it does not.** Both runs exclude H2 for a normal exit on this
  build: the restore lines are present, in the required order, terminated correctly, and — for the
  PSU — independently confirmed on the wire. That does not retroactively prove the *historical*
  2026-08-21 11:18:28 exit (section 2.3) took the same path. The current reading, given that a
  normal exit on this build now reliably restores and logs correctly twice, is that the historical
  exit is better explained by a **different termination path** — most plausibly a kill (Task
  Manager End task, a crash, or anything that tore the process down before
  `CleanupOnWorkerThread` reached its restore writes) rather than a normal `File > Exit` — than by
  a restore path that runs but fails silently. This is the current reading of the evidence, not a
  settled fact: the historical exit was not instrumented closely enough at the time (no
  process-list or event-log correlation was captured) to say with certainty which termination path
  it actually took.

- **Anything less is a fail, and is reported as-is** — this remains the standard for any future
  re-run, for example under P5 on a second machine. In particular: the release line followed by
  nothing (the production shape — that is H2 confirmed, and it means the clean-exit hand-back does
  not work and, more seriously, that **a PSU left in manual would not be restored either**, since
  the PSU has no failsafe to cover it); a hub line but no PSU line; or the PSU fan still holding
  40 % after the app is gone. If the lines are absent, the next thing to establish is *why* —
  whether the 1500 ms join elapsed, whether cleanup was killed before its first write, or whether
  `RestoreHubsAtShutdown` computed `owned && !gone` as false — because each has a different fix.

#### P1 — Sleep and resume under an active fan curve

- **Status:** PARTIALLY SATISFIED BY PRODUCTION EVIDENCE — see section 2.3. Stated honestly:
  *in-memory intent replay across sleep/resume: 5/5 eventual recovery, 1/5 first-attempt
  enumeration failure with a 301 s worst-case recovery.* Not "PROVEN": n = 5 on one machine, one
  hub, one firmware, one user, over three calendar days; all five exercised the **in-memory** intent
  replay only, because the process survived every sleep, so neither the marker-file path nor the
  clean-exit hand-back was exercised across a sleep at all. The deliberate run below is still worth
  doing, on a build carrying the post-resume rescan fix.
- **Procedure:** Configure an enabled fan curve using a Corsair liquid temperature as input and a
  Corsair fan control as target. Confirm the curve is driving the fan (the control shows a manual
  percent that tracks the temperature). Put the machine into sleep (S3/Modern Standby) for at least
  two minutes, then resume. Watch the Debug log and the Fan Controls dialog for the next three
  minutes. Repeat three times, including once with the app minimized to the tray.
- **Expected outcome:** After each resume the plug-in logs the resume path, re-opens the devices,
  re-asserts software control it previously held, re-applies the recorded duties, and the curve
  resumes driving within roughly 20 seconds — with no interval in which a fan or the pump is
  stopped and no duplicate/queued mode changes in the log. (Observed in production on the four
  clean wakes: 19 s, 12 s, 9 s, 19 s from wake to the first duty write.)
- **What governs recovery when the first attempt fails — corrected.** Earlier drafts attributed
  post-resume recovery to "the 30 s hardware-mode retry". That is wrong, and the field log shows
  exactly why. `ScanIntervalMs = 30000` only applies when *nothing at all* is connected. On
  2026-08-21 the hub failed to re-open after a resume but the PSU session came back in the same
  scan, so the worker was in the "devices present" cadence and scheduled its next attempt at
  `PresentRescanMs = 300000` — recovery took **301 s**, to the second. Since then `ScanDevices`
  distinguishes a third state, "this process is short a session it should have", and retries such a
  scan at 30 s, doubling back up to the 5 min cadence. **A re-run of P1 should now show a failed
  first attempt recovering in about 30 s rather than about 300**, and that is the specific thing to
  look for if a resume does fail to enumerate.
- **One extra pass, cheap and worth it:** disable the plug-in and put the machine to sleep
  immediately, i.e. inside the hand-back grace window, then resume. The hand-back must run *after*
  the resume has re-opened the devices, so expect the resume lines first and the hand-back
  ("the Corsair devices are being handed back now") after them, with the PSU restore actually
  landing. A hand-back logged before the resume lines — or one with no PSU restore, leaving the PSU
  fan holding its manual duty — is a failure.

#### P2 — Surprise USB disconnect and reconnect while under control

**Reclassified, 2026-08-21.** This item is no longer pending. It has been moved to section 2.4,
"Not applicable — not to be attempted", on the hardware owner's decision; see there for the
reasoning and what stays untested as an accepted consequence.

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

#### P6 — A Preferences round-trip produces no hardware traffic at all

- **Status:** PENDING. **This is the item the deferred hand-back exists for**, and it is the
  regression the user reported: before the change, opening Preferences dropped the hub to its own
  loud profile within a second and closing it did not bring the fans back down.
- **Procedure (about two minutes):** Configure an enabled fan curve on a Corsair fan control so the
  fans are audibly under Sensor Readout's control, and confirm from the Debug log that curve
  updates are landing ("Fan curve set corsair/link/.../control/N to NN%"). Then, listening the
  whole time: open Options > Preferences, wait five seconds, arrow through a couple of settings and
  type a character into one text box, wait five seconds, and close with OK. Repeat once closing
  with Escape/Cancel instead. Read the section of `Logs\<machine>.log` covering the whole
  round-trip.
- **Expected outcome:** No audible change at any point — no spin-up when the window appears, no
  re-baseline afterwards — and the fan curve keeps applying throughout, with "Fan curve set ..."
  lines continuing across the whole Preferences session. In the log:
  - one or more "the host released the Corsair plug-in; its devices stay under this plug-in's
    control for up to NNNNN ms in case the host is only reloading it" lines,
  - each followed within a few seconds by "the host asked for Corsair readings again, so it was
    reloading the plug-in rather than shutting it down; the pending hand-back was cancelled and fan
    control was never interrupted",
  - and **no** "returning iCUE LINK hub ... to hardware mode", **no** "taking software control of
    iCUE LINK hub", **no** "resuming fan control of hub", and **no** "the Corsair worker thread has
    started" anywhere in the round-trip.
- **Any one of those four lines appearing is a failure**, because each of them means the hardware
  was disturbed by a preference change.

#### P7 — A genuine plug-in disable still hands the devices back, within the grace period

- **Status:** PENDING. This is the safety valve for P6: the deferred hand-back is the only thing
  standing between a disabled plug-in and a hub left in software mode with frozen duties for the
  rest of the session. It must be observed, not assumed.
- **Procedure (about three minutes):** With the pump and at least one fan under clearly audible
  manual control and the PSU fan set to 40 %, open Options > Preferences > Plug-Ins, untick
  **Corsair iCUE Link and PSU Support (experimental)**, and close Preferences. Do **not** quit the
  app. Note the wall-clock time. Listen and watch `Logs\<machine>.log` for the next three minutes.
  Then re-tick the plug-in and confirm it comes back.
- **Expected outcome:** Within about 90 seconds plus one poll interval — typically around 30
  seconds — the log shows "nothing has asked the Corsair plug-in for readings since the host
  released it, so it was disabled rather than reloaded; the Corsair devices are being handed back
  now", followed by the PSU restore, the hub restore, and "the Corsair worker has stopped and every
  device session is closed". Audibly, the hub's own profile takes over and the PSU fan spins down.
  Re-enabling brings the plug-in back with the marker-file resume as in V9.
- **Failure modes to report as-is:** no hand-back line at all after three minutes (the hub would be
  held in software mode indefinitely); the hand-back line without the PSU restore; or the PSU fan
  still holding 40 % afterwards.

### 2.3 What 3.5 days of ordinary production use did and did not establish

Source: the reference machine's own Debug logs, `SIMSTATION.log.old` (2026-08-18 00:22 →
2026-08-20 16:44) and `SIMSTATION.log` (2026-08-20 16:44 → 2026-08-21 17:27), 122k lines, analysed
2026-08-21 and independently re-derived line by line. This is *unsupervised* evidence — the user
simply used the machine — so it is strong for the happy paths that ran thousands of times and says
nothing at all about paths that never ran. **Every "0 occurrences" row below is an untested branch,
not a passing test.** Five clean resumes are evidence that the happy path works, not evidence that
the failure paths do.

Satisfied by production evidence:

| Property | Evidence |
| --- | --- |
| Duty writes reach hardware | 13,066 of 13,066 synchronous `WriteAllDuties()` successes; 0 `returned False`, 0 exceptions, 0 `Fan curve could not set` |
| 10 s fan-curve rate limit | 13,055 transitions, none below 9 s (the eight 9 s gaps are decision-time stamping) |
| `MinimumChangePercent` gate | a duty delta of 1 never occurs in 13,055 transitions |
| Pump floor at or above 50 % | minimum 60 % across 321 pump writes, maximum 96 % |
| A live curve change reaches the fan | the exhaust port's curve matches a closed-form model on 1,000 of 1,000 paired samples within rounding |
| Plug-in row integrity | 14,557 of 14,558 polls returned the full 36 rows |
| Sleep/resume, **in-memory** intent replay | 5 of 5 cycles recovered; the hub was re-taken with all 12 channels every time |
| Hub fail-safe reverts to hardware mode on wake | `status 0x03` on 6 of 6 re-opens |
| Read-path stability | exactly 1 HID timeout and 0 `IsGone` events in 3.5 days |

Not met, or never exercised by this passive corpus — most still need the deliberate runs in
section 2.2; two have since been resolved by the 2026-08-21 bench session and one has been
reclassified, as their rows note:

| Property | Status | Basis |
| --- | --- | --- |
| Post-resume enumeration reliability | **NOT MET** | 1 failure in 5 resumes (20 %), 301 s recovery. Addressed by the rescan-cadence fix; needs a re-run to confirm |
| Marker-file auto-resume (V9) | **NOT PROVEN — n = 2, restarts only** | one clean restart in the production corpus (2026-08-21) plus one deliberate restart on the bench (2026-08-21, this branch's build, same session as P0); still never across a sleep |
| Clean-exit hand-back to hardware mode (P0/V8) | **RESOLVED BY BENCH TEST, 2026-08-21 — PASS x2, see P0** | Still **0** occurrences of `returning … to hardware mode` in this passive 3.5-day corpus (that fact about the unsupervised logs does not change — see "The one clean exit" below). What changed is that the deliberate, supervised P0 re-run on 2026-08-21 produced the full restore sequence twice, in the required order, so the question this corpus could not answer is now answered: the restore path works on a normal exit |
| PSU restore path | **EXERCISED AND CONFIRMED, 2026-08-21 — see P0 run 2** | Still zero `corsair/psu` control lines in this passive production corpus — this was the highest-consequence untested path in the change. Today's deliberate P0 run 2 exercised it directly: a manual PSU duty was set, saved, and re-applied at start-up (the first `corsair/psu/…/control/0` write ever recorded on this project), and the shutdown restore was confirmed not just in the log but on the wire by a read-only probe (fan mode `automatic (PSU curve, zero-RPM capable)`, 376 rpm, down from 1084 rpm manual) |
| USB disconnect / reconnect (P2) | **RECLASSIFIED — NOT APPLICABLE, see section 2.4** | 0 `IsGone`, 0 disconnects in this corpus either way; the hardware owner has ruled out inducing one (physical risk to a powered, running system), rather than this remaining an open gap |
| Per-device backoff (5 failures / 30 s) | **STILL PENDING** | 0 occurrences. Unlike P2, this does not need an induced disconnect: it fires when an *already open* device session's reads keep failing five times running (`NoteDeviceResult`, `CorsairWorker.Devices.cs:144-178`) — e.g. a device that is busy, wedged, or held exclusively by a program that does not share the read/write mutex — not when the device goes fully `IsGone`, which is the separate path P2 covered. It is also distinct from the post-resume reconnect cadence fix 4 addressed (`NextScanDelayMs` retries a missing *session*; this counts failing *reads* on a session that is still open). A hardware-safe way to reach it would be running Corsair iCUE alongside Sensor Readout for a few polls (already discouraged in normal use, for this exact reason) or catching a hub in a genuinely marginal state; neither has been attempted |
| Preferences round-trip produces no traffic (P6) | **NOT COVERED HERE** | the corpus predates a deliberate Preferences exercise |
| Genuine collection wedge → host watchdog recovery | **STILL PENDING** | 0 real stalls in 122k lines. The host watchdog fired 5 times in this corpus and all five were hibernations counted as elapsed time, since fixed. Worth flagging before this is attempted: the watchdog only recovers a *starved continuation* (one that never got scheduled), not a phase genuinely hung inside the collection lock — in the latter case the replacement collection blocks on the same lock and only the Error line helps. A forced-wedge test would therefore characterise which failure shape actually occurs, not validate that the watchdog fixes it, since it is not designed to fix the second one |

#### The one clean exit, and what it does and does not tell us

The corpus contains exactly one clean exit, at 2026-08-21 11:18:28, with the hub under software
control and driving curves right up to it. What the log shows:

```
11:18:28  the host released the Corsair plug-in; its devices stay under this plug-in's control for up to NNNNN ms …
11:18:28  Plug-In sensorreadout.corsair.experimental shut down cleanly.
          (nothing further — no "returning iCUE LINK hub … to hardware mode",
           and no "the Corsair worker has stopped and every device session is closed")
11:19:40  (the app is restarted — 72 s after the exit)
11:19:46  … answered with status 0x03 (the hub is in hardware mode), 0 channels
```

Note that "shut down cleanly" is the *host's* line, written once `IPluginLifecycle.Shutdown`
returns — and on this branch `Shutdown` only *arms* the deferred hand-back. It says nothing about
whether any hardware was handed back.

**An earlier draft of this section explained the missing restore line as a logging problem and told
the tester to "flush the log writer inside `OnProcessExit` first". That was wrong, and it would have
sent the bench run looking in the wrong place.** `LogMessage` is a bare
`File.AppendAllText` (`src/SensorReadoutForm.ReportsAndLogging.cs:868`) — it opens, writes and
closes on every call, so there is no buffered writer to flush and nothing in flight to lose. And
`returning iCUE LINK hub … to hardware mode` is an ordinary `Log("Debug", …)`
(`PlugIns/Corsair/src/CorsairLinkHubDevice.cs:341`) emitted **before** the hand-back command goes on
the wire, with Debug logging on for the whole corpus. A restore that ran and then failed, timed out
or threw would still have left that line.

So there are two hypotheses, and **the point of P0 is to discriminate between them**:

- **H1 — the restore ran and produced no log line.** Given synchronous per-call logging and a log
  line that precedes the write, this is implausible. Treat it as the null hypothesis P0 must
  *exclude*, not as the explanation.
- **H2 — the restore did not run.** Candidates: the `ProcessExitJoinMs = 1500` budget elapsed before
  cleanup got that far; the CLR tore the process down before cleanup reached its first log write; or
  cleanup ran but asked for no restore (`RestoreHubsAtShutdown` passes `owned && !gone`). The
  absence of `the Corsair worker has stopped and every device session is closed` — the last line
  `CleanupOnWorkerThread` writes — points the same way. Under H2 the hub reached hardware mode
  **only via its own firmware idle failsafe**, and the 72 s between the exit and the restart is ample
  for that.

**The hub's end state on the next start therefore proves nothing.** Both hypotheses predict
`status 0x03`. The only thing that separates them is whether the restore *lines* appear inside the
exit window.

If H2 holds, two things follow and both matter: the clean-exit hand-back is effectively unverified
in every form, and — far more consequential — **a PSU left in manual duty would not be restored
either.** The PSU has no failsafe. Per annex §7.4 it holds the last manual duty until something
writes `0xF0 = 0x00` or it is power-cycled, so there is no equivalent of the hub's firmware fallback
to quietly cover a restore that never ran. That is why P0's PSU variant below is not optional.

**Update, 2026-08-21 bench session.** P0 has since been re-run twice on this branch's build and
passed both times (see P0 above): a normal exit produces the full restore sequence, in order,
terminated correctly, with the PSU leg additionally confirmed on the wire. That means H2 — "the
restore did not run" — is no longer the default explanation for *this build's* normal-exit
behaviour; a `File > Exit` now reliably runs and logs the restore. Given that, the current reading
of the 11:18:28 exit is that it is best explained by a **different termination path** than the one
P0 exercises — most plausibly a kill (Task Manager End task, a crash, or anything that tore the
process down before `CleanupOnWorkerThread` reached its restore writes) rather than a normal
`File > Exit` — rather than by a restore path that runs but fails silently on this build. This is
the current reading of the evidence, not a settled fact: the historical exit was not instrumented
closely enough at the time (no process-list or event-log correlation was captured) to say with
certainty which termination path it actually took.

### 2.4 Not applicable — not to be attempted

These items are not part of the outstanding validation work. Each has been ruled out deliberately,
and the reasoning is recorded here so the decision is not later mistaken for an oversight.

#### Surprise USB disconnect and reconnect while under control (formerly P2)

- **Status: NOT APPLICABLE — NOT TO BE ATTEMPTED.** This is a decision by the hardware owner, not
  a gap in the plan.
- **Reasoning.** The iCUE LINK Hub is an internal device: its USB connection is a header inside the
  case, not an external port. Unplugging it while the system is powered means reaching into a
  running, powered machine to work at a motherboard header, which risks physical damage to the
  hardware and personal injury, and it is not a hot-plug scenario either the hub or the motherboard
  is designed to absorb. The PSU's Corsair Link connection is the same kind of internal header and
  carries the same risk, so both legs of the original P2 procedure (hub and PSU) are ruled out on
  the same grounds.
- **The test would not have told us anything actionable anyway.** If an AIO cooling hub genuinely
  disappears at runtime, there is no fan-control action that could help: the fans either revert to
  the hub's own onboard control or simply stop being reachable by anything, Sensor Readout
  included. The property worth confirming — that the plug-in fails safe rather than silently — is
  already covered by the invariants in section 1 and by code inspection, without needing to induce
  the failure physically.
- **What stays untested, and that this is accepted.** The surprise-removal code paths — `IsGone`
  detection, the per-device session teardown (`DropHub`/`DropPsu`), and the reconnect/backoff
  cadence built for fix 4 — remain in the plug-in as defensive handling. They are exercised only by
  code inspection and by the ordinary device-session teardown that already happens at every clean
  app exit, not by an induced mid-session disconnect. That is a documented limitation, accepted by
  the hardware owner, not an outstanding task.

## 3. Reporting

For each pending item, attach to the result: the Sensor Readout Debug log covering the run
(`Logs\<machine>.log`, copied out of the Logs folder before it rotates), the one-click diagnostics
ZIP taken immediately afterwards, and a one-line pass/fail against the expected-outcome sentence
above. A failure against any invariant in section 1 is reported as-is without a retry, because the
interesting evidence is the state the hardware was left in.

The same applies to the re-runs of the section 2.1 rows: once a row has been re-observed on a build
from this branch with the log retained, replace its "log not retained" note with the retained log's
file name and the new date. V8 and P0 were re-observed this way on 2026-08-21; attach and name the
retained log for that session here once it is copied out. When every row in section 2.1 and every
remaining item in section 2.2 carries a retained log — section 2.4's item is out of scope by the
hardware owner's decision and does not gate this — the write path is validated.
