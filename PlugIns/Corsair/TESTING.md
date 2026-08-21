# Corsair Plug-In Test Guide

The Corsair plug-in provides opt-in monitoring and fan control for supported
iCUE LINK Hub cooling devices and HXi/RMi digital power supplies. Monitoring is passive;
nothing is written to the hardware until a fan control is used.

For the supervised hardware-validation matrix that accompanies the fan-control release, see
`docs/hardware-validation-plan.md` in this folder.

## Before testing

- Close Corsair iCUE. iCUE does not use the shared Corsair device guard and cannot be
  used safely alongside this plug-in.
- HWiNFO or Fan Control may continue running for monitoring. Sensor Readout uses the
  standard `Global\CorsairLinkReadWriteGuardMutex` with bounded waits. Only one program
  should *drive* the fans at a time, so quit Fan Control before testing fan control here.
- Enable Debug logging before collecting a diagnostic if readings are absent or stale.

## Monitoring test

1. Open Preferences > Plug-Ins and enable **Corsair iCUE Link and PSU Support
   (experimental)**.
2. Supported iCUE LINK Hub hardware can provide pump and fan speeds plus liquid or
   device temperatures where the attached device reports them.
3. Supported HXi/RMi power supplies can provide fan speed, VRM and case temperatures,
   input voltage, and output power. A PSU fan speed of 0 RPM can be normal at low load.
4. Generate a normal report and a diagnostic ZIP. Corsair rows should be present in
   both when supported hardware is available.
5. Disable the plug-in. Its background worker keeps running for a short grace period and
   then stops, closing its HID sessions and handing the hub and the PSU fan back --
   typically about 30 seconds, at most about 90. The Debug log says "the Corsair devices
    are being handed back now" when it happens. The delay is deliberate: changing the enabled
    plug-in set rebuilds the shared plug-in manager, and the lifecycle call cannot tell whether
    this plug-in was disabled or an unrelated plug-in changed.
   Re-enable it to confirm monitoring starts again without restarting Sensor Readout.
6. Open and close Preferences while fans are under Corsair control. Nothing should change
   audibly, and the Debug log should contain no "returning iCUE LINK hub ... to hardware
   mode" line at all.

## Fan control test (quit Fan Control or any other controlling program first)

1. Open the Fan Controls dialog. Every Corsair control starts at
   "automatic or firmware managed".
2. **The first control change re-baselines every hub channel**: the pump goes to 100 %
   and all fans to 50 % before the requested value is applied, so the loop gets audibly
   louder for a moment. This is normal and matches how other Corsair software
   initializes the hub.
3. Set one fan to a clearly audible value (e.g. 80 %), verify the RPM row follows within
   a few seconds, then set it lower (e.g. 40 %) and verify it drops.
4. Press the automatic/default action for that fan: it returns to the 50 % default. The
   hub stays under Sensor Readout's control until the app exits, so "default" means
   50 % fans / 100 % pump, not the hub's own curve.
5. Pump test: the pump control never accepts below 50 %. Setting 30 % must land at 50 %.
6. PSU fan: the control stays visible even while the fan reads 0 RPM (the row carries the
   `Zero RPM capable` marker). Set 40 % and listen for the PSU fan; watch the Debug log --
   if every PSU write logs an acknowledgement mismatch, stop and report it. A reset, or
   any value below 30 %, hands the fan back to the PSU's own zero-RPM curve.
   Recommendation: leave the PSU fan automatic in daily use; the control exists for
   sustained-load situations.
7. "All fans reset" in the Fan Controls dialog must audibly return manually-set Corsair
   fans to their defaults immediately.

## Fan curve test

1. Preferences > Fan curves: create a curve using a Corsair liquid temperature as input
   and a Corsair fan control as target, or use CPU temperature as input.
2. Curve changes apply at most once per 10 seconds per control.
3. Plug-in rows are normally served from a 5-minute cache while the app is minimized to
   the tray. When an enabled fan curve uses a plug-in reading, that cache falls back to
   the normal foreground interval, so liquid-temperature curves stay responsive in the
   tray. CPU/GPU-temperature curves are unaffected either way.

## Exit, restart, and diagnostics

1. With fans under Sensor Readout control, quit the app normally. Within a few seconds
   the hub returns to its own hardware profile (fans may change pitch). A manually set
   PSU fan returns to automatic control too. Quitting is the immediate path; disabling the
   plug-in instead takes the grace period described under "Known limitations".
2. **Start the app again.** A hub in hardware mode does not even list the devices plugged
   into it, so there is nothing to read until something takes software control. Because
   fan control has already been used on this machine, the plug-in resumes it by itself:
   the hub goes back into software mode within a few seconds of start-up, the port rows
   reappear, and the fans re-baseline (pump 100 %, fans 50 %) before the saved
   fan-control settings are re-applied. The Debug log says "resuming fan control of hub
   ...". If instead the tree shows a single "Corsair Plug-In" row saying the hub is
   running its own hardware fan profile, the marker file below is missing -- open the Fan
   Controls dialog, set the hub's "Take fan control" entry to any manual percent once, and
   the rows appear on the next refresh.
3. Help > One-click diagnostics briefly sets every visible fan control to 100 % for about
   1.5 seconds and restores it -- loud but harmless, and by design.
4. After a crash or a killed process (not a normal exit): the hub reverts to its own
   profile on its own after a short idle timeout (fail-loud -- possibly full fans, never
   stopped fans). A manually set PSU fan keeps its last duty until an AC power cycle;
   reset it from the app.

## Going back to another control program

**Quit Sensor Readout, then start the other program.** Quitting restores the hub and the
PSU fan immediately, so there is no window in which both programs could drive the fans.

Disabling the plug-in in Preferences also works, but it is *not* immediate: for up to
about 90 seconds afterwards the plug-in is still driving the hub. If you take that route,
wait for the Debug log line "the Corsair devices are being handed back now" (or just wait
a couple of minutes) before starting the other program.

Do not run two programs with active control at the same time -- they would fight over
duties. Monitoring together is fine.

## Known limitations

- A hub in hardware mode refuses to enumerate its sub-devices (measured on firmware
  3.12.650), so after a clean exit there is nothing at all to read until some program takes
  software control. While the hub is in that state the plug-in shows one Fan Control entry
  for it, "Take fan control": set it to any manual percent and the hub goes into software
  mode and its real controls appear in its place. The plug-in resumes control by itself only
  on a machine where a hub channel was last left off its default -- a manual setting or a
  fan curve; that fact is recorded in a marker file next to the plug-in,
  `Plug-Ins\Corsair\corsair-hub-<serial>.controlled` (one per hub). The marker is written
  when a channel is set and removed again when every channel of the hub is returned to its
  default, so "All fans reset" -- or one-click diagnostics, which restores every control
  afterwards -- leaves none behind. Deleting the file returns the plug-in to strictly
  read-only-until-touched behaviour: the hub is then left in hardware mode at start-up and
  its rows stay hidden until a fan control is used again. Sensor Readout preserves this marker
  through app updates without treating it as a user-modified plug-in file. The file records only that fan
  control is in use -- no duties, no percentages; those live in Sensor Readout's own
  settings.
- **Disabling the plug-in hands the hardware back after a delay, not instantly.** Sensor
  Readout rebuilds the shared plug-in manager when the enabled plug-in set changes, and gives
  each plug-in no way to tell whether it was disabled or another plug-in changed. So this plug-in defers the
  hand-back and cancels it if the app asks for readings again -- which is what keeps opening
  Preferences from dropping the hub to its own loud profile. The wait is three of the app's
  observed refresh intervals, clamped to between 20 and 90 seconds (refresh intervals above
  90 seconds count as 90; after a genuine plug-in reload the app asks for readings again at
  once, which cancels the wait, and so does any fan-control action). Quitting Sensor Readout
  is unaffected and restores immediately.
- Legacy Commander PRO/CORE, Hydro AIO, and AXi device families are not supported.
- Some hub readings are unavailable while the hub is in hardware mode or controlled by
  a program that does not expose compatible read responses.
- Fan control is experimental. The supervised validation matrix in
  `docs/hardware-validation-plan.md` records what has been verified on real hardware and
  what is still pending.
