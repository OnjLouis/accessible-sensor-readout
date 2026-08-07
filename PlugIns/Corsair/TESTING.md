# Corsair Plug-In — Supervised Test Guide

This guide walks through the first supervised test of fan control. Reading and monitoring
were validated on this machine (iCUE LINK Hub firmware 3.12.650, HX1200i 2025); every
*write* path below runs against real hardware for the first time, so follow the order.

## Before you start

- Corsair iCUE must not be installed/running (it does not share the device safely).
- HWiNFO or other monitoring tools may keep running — they share the standard mutex.
- Turn on Debug logging first (Preferences > General > Logging: Debug). The first control
  test doubles as the wire-protocol confirmation for PSU writes, and the Debug log is the
  evidence if anything misbehaves.

## Step 1 — Monitoring only (Fan Control still running)

1. Build or use the portable copy, start Sensor Readout normally.
2. Options > Preferences > Plug-Ins: enable "Corsair iCUE Link and PSU Support
   (experimental)", close Preferences.
3. Within a few seconds the tree should show, under Fan and Temperature:
   - "Port 4 TITAN AIO pump" (~2500 RPM) and "Port 4 TITAN AIO liquid temperature",
   - seven "Port N QX Fan" rows with temperatures, three "Port N RX RGB Fan" rows
     (RPM only — RX RGB fans have no temperature sensor),
   - "PSU fan" (0 RPM is normal: zero-RPM mode), "PSU VRM temperature", "PSU case
     temperature", "PSU input voltage", "PSU output power".
   - Port 6 (the TITAN's LCD cap) intentionally shows nothing.
4. Fan Control keeps working during all of this. Both programs only read.

## Step 2 — Take over fan control (stop Fan Control first)

1. **Quit Fan Control** (tray icon > Exit). Also disable its autostart for the test.
2. In Sensor Readout, open the Fan Controls dialog.
3. Expect all Corsair controls to read "automatic or firmware managed".
4. **The first control change re-baselines every hub channel**: the pump goes to 100%
   and all fans to 50% before your requested value is applied — the loop gets audibly
   louder for a moment. This is normal and matches how Fan Control initializes.
5. Set one QX fan to a clearly audible value (e.g. 80%), verify the RPM row follows
   within a few seconds, then set it lower (e.g. 40%) and verify it drops.
6. Press the automatic/default action for that fan: it returns to the 50% default
   (the hub stays under Sensor Readout's control until the app exits — "default" means
   50% fans / 100% pump, not the hub's own curve).
7. Pump test: the pump control never accepts below 50%. Try setting 30% — it must
   land at 50%. Do not attempt to stop the pump; the plug-in will refuse.
8. PSU fan (optional): on hosts that support the zero-RPM marker the PSU control is
   always visible; on older hosts it is hidden while the fan is at 0 RPM unless
   "Show stopped" is ticked. Set 40% and listen for the PSU
   fan; watch the Debug log — if every PSU write logs an acknowledgement mismatch, stop
   and report (write framing was unverifiable until this moment). Reset returns the fan
   to the PSU's own zero-RPM logic. Values below 30% also mean "give it back to the PSU".
   Recommendation: leave the PSU fan automatic in daily use; the control exists for
   sustained-load situations.

## Step 3 — Fan curves

1. Preferences > Fan curves: create a curve using "Port 4 TITAN AIO liquid temperature"
   as input and a QX fan control as target; or use CPU temperature as input.
2. Note: curve changes apply at most once per 10 seconds per control.
3. **Caveat**: on hosts without the fan-curve cache exemption, plug-in readings (including
   the liquid temperature) refresh only every 5 minutes while Sensor Readout is minimized
   to the tray, so a liquid-temperature curve reacts slowly when minimized. Hosts with the
   exemption keep plug-in readings on the normal ~10-second interval whenever an enabled
   fan curve uses one, so liquid-temperature curves stay responsive in the tray.
   CPU/GPU-temperature curves are unaffected either way.

## Step 4 — Exit behavior and diagnostics

1. With fans under Sensor Readout control, quit the app normally. Within a few seconds
   the hub returns to its own hardware profile (fans may change pitch). If you ever set
   the PSU fan manually, it returns to automatic on exit too.
2. **Start the app again.** A hub in hardware mode will not even list the devices plugged
   into it, so there is nothing to read until something takes software control. Because
   fan control has already been used on this machine, the plug-in resumes it by itself:
   the hub goes back into software mode within a few seconds of start-up, all the Port N
   rows reappear, and the fans re-baseline (pump 100%, fans 50%) before your saved
   fan-control settings are re-applied. The Debug log says "resuming fan control of hub
   ...". If instead the tree shows a single "Corsair Plug-In" row saying the hub is
   running its own hardware fan profile, the marker file below is missing — set any
   Corsair fan to a manual percent once and the rows appear immediately.
3. Help > One-click diagnostics: this briefly sets every visible fan control to 100%
   for about 1.5 seconds and restores it — loud but harmless, and by design.
4. After a crash/kill (not normal exit): the hub reverts to its own profile on its own
   after a short idle timeout (fail-loud: possibly full fans, never stopped fans). A
   manually-set PSU fan keeps its last duty until AC power-cycle — reset it via the app.

## Going back to Fan Control

Disable the Corsair plug-in in Preferences (or quit Sensor Readout), then start
Fan Control again. Do not run both with active control at the same time — they would
fight over duties (monitoring together is fine).

## Known limitations

- A hub in hardware mode refuses to enumerate its sub-devices (measured on firmware
  3.12.650), so after a clean exit there is nothing at all to read until some program takes
  software control. The plug-in resumes control by itself only on a machine that has used it
  for fan control before; that fact is recorded in a marker file next to the plug-in,
  `Plug-Ins\Corsair\corsair-hub-<serial>.controlled` (one per hub, written the first time a
  Corsair fan control is used). Deleting the file returns the plug-in to strictly
  read-only-until-touched behaviour: the hub is then left in hardware mode at start-up and
  its rows stay hidden until a fan control is used again. The file records only that fan
  control was used — no duties, no percentages; those live in Sensor Readout's own settings.
- Minimized-tray refresh of plug-in rows is 5 minutes — except on hosts with the
  fan-curve cache exemption, where rows refresh at the normal 10-second interval
  whenever an enabled fan curve uses a plug-in temperature (so liquid-temperature curves
  stay responsive in the tray).
- iCUE cannot run alongside (no shared-mutex support in iCUE).
- On hosts without the Details-preserving fan-row rebuild, the Fan row loses its Details
  entries while a control percent is attached to it; newer hosts preserve them.
- Legacy Corsair devices (Commander PRO/CORE, Hydro AIOs, AXi PSUs) are not yet
  supported — the plug-in is structured so families can be added later.
