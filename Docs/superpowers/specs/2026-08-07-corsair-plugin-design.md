# Corsair iCUE Link / HXi PSU Plug-In — Design

Date: 2026-08-07. Author: Claude (for Robin Kipp). Branch: `feature/corsair-plugin`.

## 1. Goal

Add an optional Sensor Readout plug-in (`PlugIns\Corsair`) that reads and controls Corsair
iCUE LINK Hub cooling devices (pump, fans, liquid temperature) and Corsair HXi/RMi digital
power supplies (temperatures, fan, input voltage, output power, fan control), so that
Sensor Readout's accessible Fan Curves and Fan Controls features can replace Fan Control
for screen-reader users. Robin's machine has an iCUE LINK Hub (`1B1C:0C3F`) and an
HX1200i 2025 (`1B1C:1C27`); those two device families are the v1 scope.

Out of scope for v1: Commander PRO/CORE/XT, Hydro Platinum/Asetek AIOs, Coolit,
AXi (SiUsbXpress) PSUs, XC7 standalone, and all lighting control. The architecture keeps a
per-family device module list so these can be added later.

## 2. Legal basis

FanControl.CorsairLink is **unlicensed** (GitHub `license: null`) — no code is copied from
it. The implementation is original C# written from wire-protocol facts (byte layouts,
command IDs, timings, safety semantics) extracted into research documents and
independently cross-checked against liquidctl (GPL-3.0) and OpenLinkHub (GPL) — all three
sources agree byte-for-byte on every overlapping fact. Following the AsusRog precedent,
the plug-in folder ships `NOTICE.txt` crediting all three research sources plus
`GPL-3.0.txt`, and the plug-in (not the core app) is offered under GPL-3.0-or-later out of
caution because protocol research derives in part from GPL projects.

## 3. Host contract (fixed by Sensor Readout 4.14.1)

- Plug-in DLL `CorsairPlugIn.dll` from flat `PlugIns\Corsair\src\*.cs`, compiled by legacy
  csc.exe → **C# 5 only**, x64, references limited to System, System.Core,
  System.Management, Newtonsoft.Json, PluginSdk. No added assembly attributes. Files < 2000
  lines each (hard fail at 3000).
- Class `SensorReadout.CorsairPlugIn.CorsairPlugIn : ISensorReadoutPlugin,
  IFanControllablePlugin`; manifest id `sensorreadout.corsair.experimental`; starts
  disabled; user enables it under Options > Preferences > Plug-Ins.
- `GetReadings` runs on a worker thread, called at most every ~10 s foreground / 5 min
  minimized; **no host timeout** — it must return in milliseconds from an internal cache.
- Control rows: `Type = "Fan Control"`, identifier pattern `corsair/.../control/N` paired
  with `corsair/.../fan/N`; manual state → `Value` = percent and DisplayValue `"NN% ..."`;
  automatic → `Value = null`, DisplayValue "automatic or firmware managed". Host parses the
  percent out of DisplayValue.
- `TrySetFanPercent`/`TryResetFan` may be called concurrently with `GetReadings` from other
  worker threads; must gate cheaply on identifier ownership, never throw, and round-trip
  safely (diagnostics sets every control to 100 % for 1.5 s and restores). Fan curves
  write at most once per control per 10 s.
- Reading Types restricted to the host whitelist; this plug-in uses only `Fan`,
  `Temperature`, `Performance`, and `Fan Control`.

## 4. Architecture

```
PlugIns\Corsair
  plugin.json
  NOTICE.txt            (research credits, license statement, safety notes)
  GPL-3.0.txt
  src\
    CorsairPlugIn.cs          plug-in entry: Info, GetReadings, TrySetFanPercent,
                              TryResetFan, worker lifecycle, row snapshot cache
    CorsairPlugIn.Rows.cs     SensorReading row building from device snapshots
    CorsairWorker.cs          background poll thread, device scan/reconnect/backoff,
                              suspend/resume handling, exit restore
    CorsairHidTransport.cs    HID P/Invoke: enumeration (SetupDi*/Hid*), open/close,
                              overlapped read/write with timeouts, drain-input helper
    CorsairDeviceGuard.cs     Global\CorsairLinkReadWriteGuardMutex acquire/release
    CorsairLinkHubDevice.cs   iCUE LINK Hub protocol session
    CorsairLinkHubData.cs     known-device table, packet parse/build helpers
    CorsairHidPsuDevice.cs    HXi/RMi PMBus-over-HID protocol session
```

### 4.1 Threading model

A single background thread (`CorsairWorker`, IsBackground = true) owns routine device
I/O, exactly like Fan Control's 1 s timer — this keeps `GetReadings` instant, keeps the
hub's software mode alive regardless of the host's refresh cadence (the hub firmware
reverts to hardware mode when idle), and survives the host's 5-minute minimized cache.

- Tick cadence: 1000 ms while any hub channel is under software control, else 2000 ms.
- Each tick per device: (re-assert requested duties if dirty) → read speeds → read temps
  (hub) / handshake + read temps, fan, mode, VIN, POUT (PSU). Snapshot results into a
  lock-guarded state store.
- Device scan: on start and every 30 s while a supported device is absent; disappeared
  devices get closed and re-scanned (reconnect on Win32 error 1167 mid-I/O).
- `GetReadings` clones rows from the snapshot store; never touches the wire.
- `TrySetFanPercent`/`TryResetFan` validate identifier → update the requested-duty store →
  perform the write synchronously (device lock + global mutex) so success/failure is real;
  the worker re-asserts on later ticks.
- Suspend/resume via `SystemEvents.PowerModeChanged` (defensively wrapped): pause ticks on
  suspend; on resume wait ~3 s, re-handshake PSU, re-enter hub software mode if owned,
  re-assert duties.
- `AppDomain.CurrentDomain.ProcessExit` + `DomainUnload`: stop worker, then best-effort
  restore — hub → enter hardware mode (only if we ever took software control), PSU → duty
  0 + fan mode automatic (only if we ever set manual), close handles. Mutex waits during
  shutdown are bounded (2 s).

### 4.2 Global mutex

All wire transactions (a full close→open→read/write→close bracket, a mode change, a PSU
handshake+command exchange) hold `Global\CorsairLinkReadWriteGuardMutex` (created unowned,
Everyone/FullControl DACL). Unlike Fan Control we wait a bounded 2000 ms, skip the tick on
timeout, and reuse the previous snapshot; `AbandonedMutexException` is treated as acquired
(release and retry loop). This keeps Sensor Readout responsive if another tool wedges.

Coexistence: HWiNFO, SIV, SignalRGB, Fan Control's CorsairLink honor the same mutex —
concurrent *monitoring* is safe. Corsair iCUE does not honor it and is unsupported. Two
programs *controlling* duties simultaneously (e.g. Fan Control + this plug-in) fight by
design; the user must stop one. Both READMEs and our Details rows document this.

## 5. iCUE LINK Hub behavior

Protocol per `icuelink-protocol.md` (research doc): 513/512-byte HID reports, frame
`00 00 01 <cmd>`, status at raw offset 4 (0x00 OK, 0x03 wrong mode), endpoint
close→open→read/write→close brackets, data-type-matched response polling (500 ms), drain
stale input reports before every write.

### 5.1 Mode policy (safety-first, differs deliberately from Fan Control)

- On connect: read firmware version, enumerate sub-devices, read sensors. **Do not enter
  software mode and do not write duties** — pure monitoring leaves whatever is currently
  driving the fans (hub hardware profile or another tool) untouched.
- Software mode is entered only when the first control action arrives (host re-applies
  saved manual fans / fan curves / user action). At that moment all enumerated channels
  get their tracked duty: the requested channel its percent, every other channel its
  default (fans 50 %, pumps 100 %) because duty writes are full-set on the wire.
- Status 0x03 on a read while we own software control ⇒ hub reverted (sleep, replug):
  re-enter software mode, re-assert duties. Status 0x03 while not owning ⇒ leave mode
  alone, mark hub rows "hub is in hardware mode" via a Details note if sensor reads fail
  (empirically reads work in software mode under Fan Control; hardware-mode read behavior
  gets verified in testing — if reads fail in hardware mode, GetReadings shows one status
  row explaining that readings appear once Sensor Readout controls the fans).
- Clean exit while owning ⇒ enter hardware mode so the hub's own curves resume (safer than
  Fan Control's leave-latched behavior; hub also auto-reverts on idle, fail-loud).
- `TryResetFan(channel)` ⇒ tracked duty returns to the default (fan 50 % / pump 100 %),
  still in software mode (matches CorsairLink; per-channel hardware handoff does not exist
  on the wire). DisplayValue then shows `"50% default"` rather than claiming "automatic".
- **Pump floor: 50 %** (models 0x07 H-series AIO, 0x11 TITAN, 0x0C XD5, 0x19 XD6), always
  clamped plugin-side; reset default for pumps is 100 %. Field-proven values; below 50 %
  risks the AIO pump-failure state.

### 5.2 Rows

Hub Hardware string: `"Corsair iCUE LINK Hub"`. Identifiers use the hub HID serial
(lowercased; fallback `"hub0"`). Per enumerated channel N with a known model:

| Row | Type | Identifier | Name example | DisplayValue |
|---|---|---|---|---|
| RPM (if capability) | Fan | `corsair/link/<serial>/fan/<N>` | `Port 1 H150i pump`, `Port 3 QX Fan` | `1180 RPM` |
| Temperature (if capability) | Temperature | `corsair/link/<serial>/temperature/<N>` | `Port 1 H150i liquid temperature` | `31.4 C` |
| Control (if capability) | Fan Control | `corsair/link/<serial>/control/<N>` | same as fan row | `35% manual` / `50% default` / `automatic or firmware managed` (never controlled yet) |

Unknown model codes: log Debug, expose RPM/temp if the sensor arrays mark them available,
name `Port N Corsair device (model 0xNN)`, no control row (safety: never drive unknown
hardware). Details on rows include model/variant, device id string, firmware version, and
a Safety note describing diagnostics behavior and the Fan Control conflict.

## 6. HXi/RMi PSU behavior

Protocol per `hidpsu-protocol.md`: 64/65-byte reports, `[mode, command, data]` with 0x03
read / 0x02 write, `FE 03` handshake before every transaction (response = model name,
e.g. "HX1200i"), LINEAR11 numeric encoding, response echo validation.

Reads per tick: temp1 0x8D (VRM), temp2 0x8E (case), fan RPM 0x90, fan mode 0xF0,
VIN 0x88, total output power 0xEE. All read-only except explicit fan control.

Fan control (zero-RPM semantics, matching the ecosystem):

- Requested percent ≥ 30 ⇒ write duty 0x3B then mode 0xF0 = 0x01 (manual).
- Requested percent < 30 ⇒ mode 0xF0 = 0x00 (PSU automatic, zero-RPM capable). The
  control's DisplayValue then reads `"automatic (PSU zero-RPM control)"` — an honest
  description of what low percentages mean on this hardware, documented in Details.
- `TryResetFan` ⇒ duty 0 + mode automatic. Same on clean exit if we ever set manual.
- Reconciliation: each tick reads mode 0xF0; if we believe manual but the PSU reports
  automatic (AC cycle, iCUE interference), re-assert the requested duty and mode.
- Risk note (documented): if the process is killed while the PSU fan is manual, the PSU
  stays at the last duty until AC power-cycle or another tool resets it — identical
  exposure to Fan Control.

Rows: Hardware `"Corsair <model> PSU"` (model from handshake). Identifiers
`corsair/psu/1c27/temperature/0|1`, `.../fan/0`, `.../control/0`, `.../voltage/in`,
`.../power/out` (last two `Type = "Performance"`). PID table covers all 15 documented
HXi/RMi PIDs (1c03–1c1f, 1c23, 1c27) — same protocol, only the connected model differs.

Known overlap: LibreHardwareMonitor also has Corsair PSU support and does not honor the
guard mutex. During testing we check whether the host's LHM pass already exposes the
HX1200i; if it does, the PSU module's Details include a note recommending one source and
the morning summary flags it for Robin/Andre.

## 7. Error handling and performance

- HID open/read/write timeouts 500 ms; per-transaction bounded mutex wait 2 s; worker tick
  skips (never queues) when a previous tick is still running.
- Device absent: no rows except one status row
  (`Performance/Overview: "No supported Corsair device was found"`), rescan every 30 s.
- Transaction failure: log Debug with hex context, keep last snapshot, retry next tick;
  after 5 consecutive failed ticks per device, back off to 30 s ticks and set a status
  Details note until a tick succeeds.
- `GetReadings` cost: clone of ≤ ~25 cached rows — microseconds. DiagnosticsMode adds
  Details (firmware, enumeration table, raw last-transaction hex, worker statistics) and
  forces a fresh tick with a 3 s wait bound so support bundles carry live data.
- The worker performs ~4–6 HID transactions/second worst case — the same order as Fan
  Control's own 1 s loop; no WMI, no registry polling.

## 8. Known limitations (documented, not fixed here)

1. While Sensor Readout is minimized, plug-in *rows* refresh at the host's 5-minute
   background cache. Fan curves keyed on the hub liquid temperature therefore react slowly
   when minimized; curves keyed on CPU/GPU temperatures are unaffected (host sources stay
   live) and are the recommended configuration for now. A core-side cache exemption for
   plug-in temperature rows used by active fan curves would fix this generically —
   proposal written up for Andre per Coding-agent rules, no core change made.
2. Duty control while Fan Control (with its CorsairLink plugin) runs is a deliberate
   conflict; monitoring alongside it is safe via the shared mutex.
3. iCUE coexistence is unsupported (iCUE ignores the mutex).
4. Crash-kill while controlling: hub self-heals to hardware mode (fail-loud); PSU fan
   stays at last manual duty until AC cycle (ecosystem-wide limitation).

## 9. Testing plan

1. `Build.ps1` compiles the plug-in (C# 5, x64); `Build.ps1 -SelfTest` passes (manifest
   identity + bundled-hash tests pick the new folder up automatically).
2. Tonight (Fan Control running, hub in software mode): disposable app copy with the
   plug-in enabled → command-line report must show hub RPM/temperature rows and PSU rows
   with sane values; mutex contention with Fan Control must not stall either app.
   **No duty writes, no mode changes** (read-only paths only — control paths gate on user
   action, which nobody takes tonight).
3. Morning (Robin): stop Fan Control → enable curves/manual control in Sensor Readout →
   verify duty changes, reset behavior, diagnostics round-trip, exit restore (fans return
   to hub hardware profile after quitting), and screen-reader UX of the new rows.

## 10. Open questions for Robin (non-blocking, defaults chosen)

1. PSU fan control ships enabled-but-opt-in (it only acts when you use the control). If
   you would rather have the PSU strictly read-only, say so and the control row disappears.
2. Hub sensor reads while the hub is in *hardware* mode are unverified until Fan Control
   is stopped; if they fail, monitoring requires taking over control (documented above).
3. plugin.json helpLinks default to the three research projects' pages; happy to change.
