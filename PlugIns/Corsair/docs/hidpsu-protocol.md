# Corsair HXi/RMi HID PSU Protocol (incl. HX1200i 2025, PID 0x1C27)

> **Editor's note (2026-08-07, local measurement addendum):** Windows-measured report
> lengths on this machine's HX1200i (PID `0x1C27`): `in=65/out=65`. Allocate report
> buffers from live `HidP_GetCaps` output, not from this document's `64`/`65` wording below.

Protocol facts extracted from study of the FanControl.CorsairLink source tree (unlicensed; no code reproduced — this document describes wire-level and behavioral facts only). Source areas studied: the HID PSU device driver, the HidSharp-based HID transport, the global-mutex synchronization component, and the FanControl plugin host layer.

---

## 1. Device identification

- **Vendor ID:** `0x1B1C` (Corsair).
- The HXi/RMi family ("HID PSU" driver group) product IDs:

| Model | PID |
|---|---|
| HX550i | `0x1C03` |
| HX650i | `0x1C04` |
| HX750i | `0x1C05` |
| HX850i | `0x1C06` |
| HX1000i | `0x1C07` |
| HX1200i | `0x1C08` |
| RM550i | `0x1C09` |
| RM650i | `0x1C0A` |
| RM750i | `0x1C0B` |
| RM850i | `0x1C0C` |
| RM1000i | `0x1C0D` |
| HX1000i (2021) | `0x1C1E` |
| HX1500i (2021) | `0x1C1F` |
| HX1200i (2023) | `0x1C23` |
| **HX1200i (2025)** | **`0x1C27`** |

- **There are no per-model code paths inside the HID PSU driver.** All fifteen PIDs above (including `0x1C27`) share the identical protocol, framing, commands, and behavior. The 2025 HX1200i is simply a new PID added to the same driver group. (AXi units use a different, non-HID transport and are out of scope.)
- The human-readable model name is **not** derived from the PID: it is read from the device itself via the handshake command (see §4.1), then displayed as `"<model name> (<serial>)"`. The USB serial number string is used as the identity suffix; if the OS cannot read a serial, an MD5 hash of the device path is substituted.

## 2. HID interface selection and report lengths

- Enumeration takes all HID devices with VID `0x1B1C`, filters to known PIDs, and additionally requires the interface's **maximum output report length to be greater than 0**. That single check is the interface selector — the PSU exposes one vendor HID interface with 64-byte input and output reports and no report IDs (report ID 0).
- **Write (host→device):** a 65-byte buffer — byte 0 is the report ID `0x00` (Windows HID framing prerequisite for unnumbered reports), followed by the 64-byte report payload. Only the first few payload bytes are meaningful; the rest are zero-filled.
- **Read (device→host):** a 64-byte buffer. Under the Windows HID API the first byte of a received report is again the report ID (`0x00`), so meaningful payload starts at offset 1 of the read buffer. (On hidraw-style platforms without the report-ID prefix, the same fields appear one byte earlier.)
- Interrupt IN/OUT transfers via ordinary HID read/write; **no feature reports** are used by the PSU driver (the transport supports GET_FEATURE but the PSU path never calls it).

## 3. Framing — byte layout

All offsets below are **Windows buffer offsets** (report-ID byte included).

**Request (65 bytes total):**

| Offset | Meaning |
|---|---|
| 0 | Report ID, always `0x00` |
| 1 | Length/mode byte: `0x03` = read, `0x02` = write (one data byte) |
| 2 | Command / PMBus register code |
| 3 | Data byte (for writes; `0x00` for reads) |
| 4–64 | Zero padding |

This is a PMBus-style SMBus bridge: byte 1 is effectively the transfer length/direction, byte 2 the PMBus command code, byte 3 the write payload.

**Response (64 bytes read):**

| Offset | Meaning |
|---|---|
| 0 | Report ID `0x00` |
| 1 | Echo of the request's mode byte |
| 2 | Echo of the request's command byte |
| 3.. | Data (little-endian words for numeric values, ASCII for strings) |

**Response validation rules** (a response is treated as an error if any of these hold):

1. Byte 1 == `0xFE` **and** byte 3 == `0xFE` → error (failed handshake-style reply).
2. Byte 2 == `0xFE` → error (device signaled failure in the command-echo slot).
3. Otherwise the response is valid **only if** byte 1 and byte 2 exactly echo the request's bytes 1 and 2; any mismatch is an error.

`0xFE` is thus the device's error/invalid marker as well as the handshake command code (§4.1).

## 4. Command set

| Command byte | Direction | Purpose | Data |
|---|---|---|---|
| `0xFE` | special | Handshake / model-name read | Response data = ASCII model name |
| `0xD4` | read | Firmware version | 4 bytes at data[0..3], rendered as `a.b.c.d` |
| `0x8D` | read | Temperature 1 (PMBus READ_TEMPERATURE_1) | LINEAR11, °C |
| `0x8E` | read | Temperature 2 (PMBus READ_TEMPERATURE_2) | LINEAR11, °C |
| `0x90` | read | Fan speed (PMBus READ_FAN_SPEED_1) | LINEAR11, RPM |
| `0x3B` | write | Fan duty (PMBus FAN_COMMAND_1) | 1 byte, percent 0–100 |
| `0xF0` | write | Fan control mode (vendor/MFR range) | `0x00` = Normal (PSU/automatic), `0x01` = Manual |

No PMBus PAGE selection, voltage/current/power rail commands, OCP mode, or any other registers are used by this implementation.

### 4.1 Handshake

The handshake **swaps the first two bytes** relative to a normal read: byte 1 = `0xFE` (the handshake code), byte 2 = `0x03` (the read-mode value). The response echoes those two bytes and carries the model name (e.g. "HX1200i") as a NUL-terminated ASCII string in the data area; non-printable bytes are replaced with `?` when parsed, and the result is trimmed.

The handshake serves two roles:

1. **Session/name read at connect time** — the name is parsed once and must succeed (an invalid handshake response at init raises a device error).
2. **Wake/sync preamble** — a handshake write+read is performed **before every single command transaction** (reads and writes alike), inside the same mutex hold. Its response is discarded (not validated) in this preamble role.

### 4.2 Transaction sequence

Every logical operation is: acquire global mutex → handshake write → handshake read → command write → command read → release mutex. Before each HID write, the transport drains any stale enqueued input reports (see §8).

## 5. Numeric encodings

### 5.1 LINEAR11 (all sensor reads)

Sensor values (both temperatures, fan RPM) are 16-bit little-endian words at response data offsets 0–1 (Windows buffer offsets 3–4), decoded as PMBus LINEAR11:

- `raw = data[1] << 8 | data[0]`
- mantissa = low 11 bits, two's-complement: if mantissa > 1023, subtract 2048.
- exponent = high 5 bits, two's-complement: if exponent > 15, subtract 32.
- **value = mantissa × 2^exponent**

Temperatures are used as °C floats; RPM is truncated to an integer. LINEAR16 is not used anywhere in the HID PSU path.

### 5.2 Fan duty (write)

The fan duty is written as a **plain integer percent byte (0–100)** in the single data byte, with the following byte left zero. (Because a 0–100 value with exponent 0 is also a valid LINEAR11 low byte, this is wire-compatible with a LINEAR11 FAN_COMMAND_1 interpretation.) The value is clamped to 0–100 before sending. No 0–255 fractional scaling is applied for PSUs.

### 5.3 Strings and version

- Model name: ASCII, NUL-terminated, printable subset, trimmed.
- Firmware version: four raw bytes at data offsets 0–3 formatted as a dotted quad. If the read fails validation, the version is reported as "UNKNOWN" rather than an error.

## 6. Sensors exposed

- **Temperatures: exactly 2 channels**, polled via `0x8D` and `0x8E`, labeled generically "Temp #1" and "Temp #2". (The source does not name the physical locations; external convention for this family is that sensor 1 is the VRM/internal hot-spot and sensor 2 the case/ambient sensor — treat that as unverified by this codebase.)
- **Fan: exactly 1 channel** (channel 0), RPM via `0x90`, labeled "Fan #1", marked controllable — so FanControl surfaces one fan-speed sensor plus one paired control.
- **Nothing else.** This implementation reads no input/output voltages, currents, power, energy, efficiency, or uptime — even though the underlying PMBus device supports them. A sensor read that fails validation yields a null reading for that cycle (last good value retained by the sensor object; the plugin surfaces whatever the sensor holds).
- Fan RPM read errors likewise produce a null RPM, not an exception.

## 7. Fan control and zero-RPM semantics (safety-critical)

### 7.1 Two control modes

The mode register (`0xF0`) selects:

- **Normal (`0x00`)** — the PSU's own firmware controls the fan from its internal temperature/load curve, **including zero-RPM (fan-stop) operation**. This is the power-on default state and the "hardware control" state.
- **Manual (`0x01`)** — the fan runs at the last duty written to `0x3B`.

### 7.2 Zero-RPM duty threshold behavior

The driver deliberately never runs the fan manually at low duties. A **zero-RPM duty threshold** governs every set-power request:

- Requested percent **below** the threshold → mode is set to Normal (`0x00`): the PSU takes over, allowing fan stop. The requested duty is still stored/written but is inert while in Normal mode.
- Requested percent **at or above** the threshold → mode Manual (`0x01`) with the requested duty.

Threshold values:

- **Default: 30%** for the entire HXi/RMi family (historically the family's "minimum duty").
- User override via environment variable `FANCONTROL_CORSAIRLINK_PSU_ZERO_RPM_DUTY` (read from process or machine scope), **clamped to 1–99**. The same value applies to all HID PSUs present.
- (For comparison, the AXi family default is 15%; not applicable to `0x1C27`.)

Rationale (documented project behavior since v1.8.0): setting 0% — or any value under the threshold — "returns control to the PSU," i.e. re-enables automatic/zero-RPM mode, while values ≥ threshold take manual control.

### 7.3 Write ordering and deduplication

Set-power requests are queued and applied on the next refresh cycle, not immediately (except on reset, §7.4):

1. If the requested duty **changed** since last applied value: write duty (`0x3B`) first.
2. Then, if the mode changed **or** a duty was just written: write mode (`0xF0`). The mode is intentionally rewritten after every duty write even if unchanged.

Unchanged values are not re-sent (change-tracking store), so a steady FanControl curve produces no writes — only the three sensor reads per cycle.

### 7.4 Returning the fan to automatic control (restore path)

- **Reset of the control channel** (user disables the control in FanControl, or plugin shutdown): the driver sets the requested power to **0%**, which falls below any permissible threshold, forcing mode Normal — and flushes it to the device **immediately and synchronously** rather than waiting for the next refresh.
- **Disconnect/close**: the same reset is attempted first, with any exception swallowed, then the HID handle is closed. So the intended terminal state after the software exits is always **mode `0xF0` = `0x00` (PSU automatic control)**.
- **Connect/initialize** also starts the channel at 0%/Normal and pushes that state on the first refresh — the driver never leaves a stale manual duty in place from a previous session.
- **Failure mode to be aware of:** if the process is killed or the machine loses power mid-session, no reset is sent; the PSU remains in Manual mode at the last duty until reset by another writer or a PSU power cycle. Any reimplementation must treat "write mode `0x00` on exit and on control-disable" as a hard safety requirement.

## 8. Transport behavior, timing, retries, busy handling

- **HID read timeout: 500 ms. HID write timeout: 500 ms** (set on the open stream).
- **Stale-report draining:** before every normal write, the transport drains the input queue by reading with a **1 ms** timeout in a loop until a timeout occurs. This discards leftover responses from aborted or interleaved transactions so request/response pairing stays aligned. (A separate "direct write" path skips draining; the PSU driver does not use it.)
- **Surprise-removal recovery:** if a read/write fails with a Windows I/O error whose code is `0x48F` (ERROR_DEVICE_NOT_CONNECTED, 1167), the transport closes and reopens the device once and retries the same operation. The PSU driver registers no post-reconnect re-initialization callback (none is needed — every transaction re-handshakes anyway).
- **No other retry logic and no inter-command delays** exist in the PSU path (no sleeps at all); pacing comes from the 1-second refresh cadence and the per-transaction handshake.
- **Refresh cadence (plugin host):** a 1000 ms timer triggers a refresh of all devices in parallel. A non-blocking semaphore skips the tick if the previous refresh is still running; 10 consecutive skips trigger a "device may be unresponsive" user dialog (suppressible via `FANCONTROL_CORSAIRLINK_ERROR_NOTIFICATIONS_DISABLED`). 10 accumulated device errors likewise raise a dialog; the error counter is reset roughly every 30 ticks if under the limit. Refreshes are suppressed while the system is suspending; refresh resumes after resume.
- Each refresh performs, in order: apply pending fan writes → read temp 1 → read temp 2 → read fan RPM. With per-transaction handshakes that is up to 5 mutex-guarded transactions (10 HID transfers) per second in steady state, 3 transactions when no fan change is pending.

## 9. Synchronization / interoperability (mutex)

- All device I/O (the handshake+command pair) is wrapped in an exclusive hold of the named global mutex **`Global\CorsairLinkReadWriteGuardMutex`**, created with a world-full-control ACL so any process/session can share it. This is the de-facto standard mutex also honored by HWiNFO (5.34+), SIV (5.17+), and others; holding it during each transaction prevents interleaving with those tools.
- An abandoned mutex (holder crashed) is handled by releasing and re-acquiring in a loop.
- The mutex is **per-transaction**, not per-refresh — other tools can interleave between transactions, which is safe because every transaction begins with its own handshake.
- One process-wide mutex covers **all** Corsair devices, not just the PSU.
- Corsair iCUE does **not** honor this mutex and is documented as incompatible (must not run concurrently). Citrix Workspace is also documented as breaking device recognition.

## 10. Quirks and error handling summary

- `0xFE` triples as handshake command, error marker, and (in the handshake) sits in the mode-byte position — the validation rules in §3 encode this overlap.
- Handshake responses in the per-command preamble are ignored entirely; only the connect-time handshake (name read) is validated.
- Firmware-version failure degrades to the string "UNKNOWN" instead of an error.
- Per-sensor read failures degrade to null readings for that cycle; the refresh continues with the remaining sensors, and exceptions from a device refresh are caught and logged by the host without stopping the other devices.
- Sensor labels are static ("Temp #1", "Temp #2", "Fan #1"); channel IDs are 0-based (temps 0–1, fan 0).
- The set-power API accepts any percent and clamps to 0–100; the threshold comparison happens on the raw requested value before clamping (values <0 behave as 0 → Normal mode).
- FanControl users must disable LibreHardwareMonitor's "PSU (Corsair)" source to avoid double-driving the device (documented requirement, not a protocol feature).
- Device open failures at connect simply mark the device unavailable; the plugin continues with other devices.

## 11. Minimal safe reimplementation checklist (derived)

1. Open the VID `0x1B1C` / PID `0x1C27` HID interface that has a nonzero output report length; 64-byte reports, report ID 0.
2. Take `Global\CorsairLinkReadWriteGuardMutex` around every transaction; tolerate abandonment.
3. For each transaction: drain stale input (1 ms timeout loop) → write handshake (`FE 03`) → read → write command → read → validate echo bytes and `0xFE` markers.
4. Read `0x8D`/`0x8E`/`0x90` as LINEAR11 (mantissa low 11 bits, exponent high 5 bits, both two's-complement).
5. To drive the fan: write duty percent to `0x3B`, then `0x01` to `0xF0`. Never hold manual mode below ~30% duty; below the threshold write `0x00` to `0xF0` instead.
6. **Always** write `0x00` to `0xF0` on shutdown, control-disable, and before closing the handle.

---

> **Editor's note (2026-08-07, Task 6 live-measurement addendum):** measured against this machine's
> HX1200i (PID `0x1C27`, `in=65/out=65`) while implementing `CorsairHidPsuDevice`. Corrections and
> additions to the text above.
>
> **(a) The handshake *reply* is framed like every other reply — §3's table applies unchanged.**
> §4.1's "swaps the first two bytes" describes the **request** only. The measured reply is
>
> ```
> 00 fe 03 48 58 31 32 30 30 69 20 50 6f 77 65 72 ...
>  ^  ^  ^  H  X  1  2  0  0  i     P  o  w  e  r
>  |  |  +-- 0x03, the echoed handshake argument, in the ordinary command slot
>  |  +----- 0xfe, the handshake echo, in the ordinary mode slot
>  +-------- report id
> ```
>
> so the NUL-terminated ASCII model name starts at **buffer offset 3** (the ordinary data offset),
> not at offset 2. Validating the reply may therefore require both echo bytes (`[1] == 0xFE`,
> `[2] == 0x03`) exactly as §3 rule 3 states for any other command; that stricter check was verified
> to hold on live hardware.
>
> **(b) This unit reports its marketing string, not a bare model name.** The handshake answers
> `"HX1200i Power Supply"` where §1 quotes `"HX1200i"` — identical to the USB product string.
> Consumers that use the name as a label may want to strip the `" Power Supply"` suffix.
>
> **(c) Three registers work here that §4 does not list.** All read cleanly and repeatedly with
> mode `0x03`: `0x88` (PMBus READ_VIN, LINEAR11) measured **230.0 V**; `0xEE` (total output power,
> LINEAR11) measured **~120 W** at idle; and `0xF0` (fan control mode), which §4 lists as write-only,
> is **readable**, answering `0x00` (automatic) at buffer offset 3. Treat all three as unverified
> outside PID `0x1C27` — no other HXi/RMi model was available — so a consumer should degrade
> gracefully (null reading) rather than treat their absence as a device fault.
>
> **(d) Write-response framing for `0x3B` and `0xF0` is still UNVERIFIED.** No control write has
> ever been sent to this PSU: Fan Control owns the fans on this machine, so every measurement above
> comes from read-style traffic only (constraints.md §9). The assumption in the reimplementation is
> that a write (mode `0x02`) is acknowledged with the same echo framing as a read. **The first
> supervised control run must capture the Debug logs of both write transactions** and confirm the
> echo bytes before the write path is trusted.
