# Corsair iCUE LINK Hub — Wire Protocol Reference

> **Editor's note (2026-08-07, local measurement addendum):** Windows-measured report
> lengths on an iCUE LINK System Hub (PID `0x0C3F`): `in=513/out=513`. Allocate
> report buffers from live `HidP_GetCaps` output, not from this document's `512` wording below.

Protocol facts extracted from study of the FanControl.CorsairLink source tree (unlicensed; no
code reproduced — this document describes byte layouts, command IDs, timings, and semantics as
uncopyrightable protocol facts). Source tree studied:
`.../scratchpad/FanControl.CorsairLink` — primarily the iCUE LINK device implementation, the
HID transport layer, the cross-process synchronization layer, and the plugin lifecycle host.

---

## 1. USB / HID identification and interface selection

| Item | Value |
|---|---|
| USB Vendor ID | `0x1B1C` (Corsair) |
| USB Product ID | `0x0C3F` (iCUE LINK Hub, "System Hub") |
| Transport | HID input/output reports only (no feature reports used for this device) |
| HID report ID | `0x00` (single unnumbered report; first buffer byte is always `0x00`) |
| Output (host→hub) buffer | 513 bytes total = 1 report-ID byte + 512 payload bytes |
| Input (hub→host) buffer | 512 bytes total, report-ID byte included as byte 0 |

Interface selection is **not** done by interface number (MI_00/MI_01) or usage page. The
reference implementation enumerates all HID collections with VID `0x1B1C` (via HidSharp,
which on Windows enumerates top-level collections), filters to the supported PID list, and
keeps only the collection whose **maximum output report length is greater than zero**. In
practice this selects the hub's vendor-defined read/write collection and skips any read-only
or keyboard-emulation collections. If you implement this yourself: pick the HID interface of
`VID_1B1C&PID_0C3F` that exposes a writable output report of ~512 bytes.

Feature reports: the transport layer has a get-feature capability but the iCUE LINK code path
never uses it. Everything is interrupt IN/OUT reports.

Serial number: read from the USB string descriptor; if the device refuses, an MD5 hash of the
device path is used as a stand-in identifier. Display name is built as
`<product name> (<serial>)`; unique ID is the Windows device path.

---

## 2. Session model: software mode vs. hardware mode

The hub has two operating modes:

- **Hardware mode** — the hub runs its own stored cooling profile autonomously.
- **Software mode** — the host controls duty cycles; the hub obeys the last written values.

Mode-control command (see framing in §4): command family `0x01 0x03 0x00` with a final mode
byte:

| Mode byte | Meaning |
|---|---|
| `0x02` | Enter software mode |
| `0x01` | Enter hardware mode |

### Handshake / initialization order (as performed by the reference implementation)

1. Open the HID device (read timeout 500 ms, write timeout 500 ms).
2. Read firmware version (command `0x02 0x13`).
3. Decide enumeration strategy from firmware version: firmware **≥ 2.5** (or major ≥ 3)
   supports the two-read sub-device enumeration needed for up to 24 connected devices;
   older firmware gets a single-read enumeration.
4. Send **enter software mode** (`0x01 0x03 0x00 0x02`), under the global mutex.
5. Initial refresh: enumerate sub-devices, initialize every discovered channel to a default
   duty (**fans 50 %, pumps 100 %**), write duties, read speeds, read temperatures.
6. Register a reconnect callback that re-sends *enter software mode* whenever the HID device
   is re-opened after surprise removal.

### Keepalive

There is **no dedicated keepalive command**. The 1-second polling loop (speed read +
temperature read every second, duty write only on change) doubles as the keepalive.

### Returning to hardware mode — CRITICAL SAFETY FACTS

- The *enter hardware mode* command (`0x01 0x03 0x00 0x01`) is **defined but never sent** by
  the reference implementation. On plugin shutdown it simply closes the HID stream, leaving
  the hub in software mode with the last written duties latched.
- The hub can and does drop out of software mode on its own (sleep/wake, hub-side reset,
  another program taking over). Evidence in the protocol: a dedicated response status code
  (`0x03`, "incorrect mode") is returned when a software-mode-only operation is attempted
  while the hub is in hardware mode, and the implementation reacts by re-sending
  *enter software mode* and retrying on the next cycle.
- Practical implication (inference, consistent with the recovery logic and community
  knowledge): if the host stops polling or crashes, the hub eventually reverts to its
  autonomous hardware-mode profile, so fans do not stay stuck at a dangerous low duty
  forever. However, this is hub-firmware behavior, not guaranteed by anything the host does.
  A safety-conscious implementation should either send *enter hardware mode* on clean
  shutdown, or ensure duties are left at safe values before exiting.
- After a write or read **timeout**, the reference implementation assumes the hub silently
  fell back to hardware mode: it re-sends *enter software mode*, and if that succeeds it
  swallows the timeout (that polling cycle's data is simply lost).

---

## 3. Cross-process synchronization (global mutex)

All Corsair tools in this ecosystem coordinate via a named Windows mutex:

- **Name:** `Global\CorsairLinkReadWriteGuardMutex`
- **ACL:** created with a world (Everyone) full-control access rule so processes running as
  different users/elevation can share it.
- **Acquire:** infinite wait (no timeout). If another tool holds the mutex, the caller blocks
  indefinitely; at the plugin layer this shows up as skipped refresh ticks (see §8).
- **Abandoned mutex:** if a wait completes with an abandoned-mutex condition (another process
  died while holding it), ownership is released and the wait retried in a loop, which
  self-heals the lock.
- **Release:** immediately after each transaction (RAII/disposable pattern).

**Granularity:** the mutex is held for one full endpoint transaction at a time — e.g. the
whole close→open→read→close sequence for a sensor read, or a whole mode change, or a whole
firmware-version read. It is *not* held across the entire refresh.

Interop note: SignalRGB (2.3.13+ for iCUE LINK) honors the same mutex; Corsair iCUE itself
does not and must not run concurrently.

---

## 4. Command framing

### 4.1 Host → hub command packet (513 bytes, zero-padded)

| Offset | Size | Content |
|---|---|---|
| 0 | 1 | `0x00` HID report ID |
| 1 | 1 | `0x00` |
| 2 | 1 | `0x01` (constant frame marker) |
| 3 | n | command bytes (see table below) |
| 3+n | m | command-specific data (optional) |
| … | — | zero padding to 513 bytes total |

There is **no sequence number and no CRC** anywhere in this protocol.

### 4.2 Command set

All commands below use a fixed **handle ID of `0x01`** (the hub supports the notion of an
endpoint "handle"; the implementation only ever uses handle 1).

| Command bytes | Name | Data | Notes |
|---|---|---|---|
| `01 03 00 02` | Enter software mode | — | |
| `01 03 00 01` | Enter hardware mode | — | defined, never sent |
| `02 13` | Read firmware version | — | |
| `0D 01` | Open endpoint (into handle 1) | 1 endpoint byte | |
| `05 01 01` | Close endpoint (handle 1) | 1 endpoint byte | |
| `08 01` | Read from handle 1 | — | one read command can yield multiple response reports |
| `06 01` | Write to handle 1 | framed write block (§4.4) | |

### 4.3 Endpoint addresses and data types

| Endpoint byte | Purpose | Response/write "data type" (2 bytes) |
|---|---|---|
| `0x17` | Read speeds (RPM) | `25 00` |
| `0x21` | Read temperatures | `10 00` |
| `0x18` | Write software fixed-percent duty | `07 00` |
| `0x36` | Read sub-device enumeration | `21 00` |

Careful: the *temperature endpoint address* (`0x21`) is numerically identical to the first
byte of the *sub-device data type* (`21 00`). They are unrelated.

Continuation reads (second response report of a multi-report payload) have **no** data type;
their payload continues the raw byte stream.

### 4.4 Write data block (payload of the `06 01` write command)

| Offset | Size | Content |
|---|---|---|
| 0–1 | 2 | little-endian length = (inner data length + 2) |
| 2–3 | 2 | `00 00` |
| 4–5 | 2 | data type (e.g. `07 00` for duty) |
| 6… | n | inner data |

### 4.5 Hub → host response report (512 bytes)

| Offset | Size | Content |
|---|---|---|
| 0 | 1 | `0x00` report ID |
| 1–2 | 2 | `00 00` |
| 3 | 1 | echo of the first command byte (`0x08` for endpoint reads, `0x02` for FW read, …) |
| 4 | 1 | status: `0x00` = OK; `0x03` = incorrect mode (hub is in hardware mode); any other non-zero = error |
| 5–6 | 2 | data type (endpoint responses only; for direct commands like FW read, data starts at offset 5) |
| 7… | — | payload |

**Response matching:** there is no transaction ID. After issuing a read command, the host
polls HID input reports until one arrives whose data-type field (offsets 5–6) matches the
expected type, with an overall budget of **500 ms** (each individual HID read also has a
500 ms timeout). Reports with the wrong type are discarded. Additionally, before *every*
command write, the transport **drains all stale queued input reports** (reads with a 1 ms
timeout until a timeout occurs) so a response can't be matched to a previous command.

### 4.6 Endpoint transaction patterns

Read transaction (mutex held throughout):
1. Close endpoint (defensive — the hub tolerates closing an endpoint that isn't open)
2. Open endpoint
3. Read command; poll until expected data type (500 ms budget)
4. (two-read enumeration only) issue a second read command; its single response is the
   continuation report, taken as-is without type matching
5. Close endpoint

Write transaction (mutex held throughout):
1. Close endpoint
2. Open endpoint
3. Write command carrying the framed write block
4. Close endpoint

Any non-zero status at any step throws/aborts the transaction.

---

## 5. Firmware version

Command `02 13`. Response (offsets per §4.5, no data type): offset 5 = major, offset 6 =
minor, offsets 7–8 = little-endian 16-bit patch. Example from captured test data: bytes
`02 09 E8 01` → version 2.9.488.

Version gate: two-read sub-device enumeration (24-device support) requires
major = 2 ∧ minor ≥ 5, or major ≥ 3.

---

## 6. Sub-device enumeration

Endpoint `0x36`, expected data type `21 00`. On firmware ≥ 2.5, two consecutive read
commands are issued inside one open/close bracket; the first response carries the data type
`21 00`, the second is a raw continuation. The usable stream is: first response from offset 7
onward, concatenated with the continuation response from offset 5 onward. (A source comment
notes a suspected firmware bug: the continuation report may be missing one leading byte.)

### 6.1 Enumeration stream layout

| Offset (in stream) | Content |
|---|---|
| 0 | last channel index (highest channel number to parse) |
| 1… | per-channel records, channels numbered **1..lastChannel** (channel 0 does not exist) |

Per-channel record:

| Offset (in record) | Size | Content |
|---|---|---|
| 0–1 | 2 | unknown/reserved |
| 2 | 1 | **device model code** |
| 3 | 1 | **device variant code** |
| 4–6 | 3 | unknown (byte 6 often `0x05`) |
| 7 | 1 | device-ID length L (`0x00` ⇒ channel empty; record is exactly 8 bytes, next channel follows) |
| 8…8+L−1 | L | device ID: ASCII characters (serial-like string, e.g. 26 chars), NUL-trimmed |

Occupied-channel records are `8 + L` bytes; empty channels consume 8 zero bytes. The stream
may end mid-record at a report boundary (hence the continuation read).

### 6.2 Known model/variant database (complete as of the studied tree)

| Model code | Variant | Marketing name | Pump? | Capabilities (temp / RPM / duty control) |
|---|---|---|---|---|
| `0x01` | `0x00` | QX Fan | no | temp + RPM + control |
| `0x02` | `0x00` | LX Fan | no | RPM + control |
| `0x03` | `0x00` | RX MAX RGB Fan | no | RPM + control |
| `0x04` | `0x00` | RX MAX Fan | no | temp + RPM + control |
| `0x07` | `0x00` | H100i AIO (black) | yes | temp + RPM + control |
| `0x07` | `0x01` | H115i AIO (black) | yes | temp + RPM + control |
| `0x07` | `0x02` | H150i AIO (black) | yes | temp + RPM + control |
| `0x07` | `0x03` | H170i AIO (black) | yes | temp + RPM + control |
| `0x07` | `0x04` | H100i AIO (white) | yes | temp + RPM + control |
| `0x07` | `0x05` | H150i AIO (white) | yes | temp + RPM + control |
| `0x09` | `0x00` | XC7 CPU water block (stealth gray) | no | temp only |
| `0x09` | `0x01` | XC7 CPU water block (white) | no | temp only |
| `0x0A` | `0x00` | XG3 GPU water block | no | temp + RPM + control |
| `0x0B` | `0x00`–`0x02` | HXi SHIFT PSU (1000/1200?/1500?) | no | temp + RPM + control |
| `0x0C` | `0x00` | XD5 pump/res (stealth gray) | yes | temp + RPM + control |
| `0x0C` | `0x01` | XD5 pump/res (white) | yes | temp + RPM + control |
| `0x0F` | `0x00` | RX RGB Fan | no | RPM + control |
| `0x10` | `0x00` | VRM Fan CapSwap Module | no | RPM + control |
| `0x11` | `0x00`–`0x04` | TITAN AIO (models/colors TBD) | yes | temp + RPM + control |
| `0x11` | `0x05` | TITAN 360 RX RGB AIO (white) | yes | temp + RPM + control |
| `0x13` | `0x00` | RX Fan | no | RPM + control |
| `0x19` | `0x00` | XD6 pump/res (stealth gray) | yes | temp + RPM + control |
| `0x19` | `0x01` | XD6 pump/res (white) | yes | temp + RPM + control |
| `0x1B` | `0x00` | COMMANDER DUO | no | temp + RPM + control |

"Pump?" = models the implementation treats as pumps for minimum-duty purposes: `0x07`
(H-series AIO), `0x11` (TITAN AIO), `0x0C` (XD5), `0x19` (XD6). Note the LCD variants of the
AIOs are **not** distinguished — the hub reports the same model/variant regardless of LCD cap;
there is no LCD-specific handling in this protocol layer.

Unknown model codes are logged and the channel is ignored (test captures show e.g. model
`0x0E`, not yet in the database). Note the XC7 water block reports temperature only — its
channel produces no RPM and accepts no duty.

**Channel assignment:** the channel number from enumeration is the same channel index used in
the speed array, temperature array, and duty write. There is no per-type remapping.

---

## 7. Sensor reads

Both sensor endpoints share one payload shape. In the 512-byte response report:

| Offset | Content |
|---|---|
| 7 | sensor count N (observed `0x0F` = 15 on smaller configurations; larger with fw ≥ 2.5 and many devices) |
| 8… | N records × 3 bytes, record *i* belongs to channel *i* |

Per-record layout:

| Byte | Content |
|---|---|
| 0 | status: `0x00` = sensor present/valid, `0x01` = absent/invalid |
| 1–2 | little-endian signed 16-bit value (only meaningful when status is `0x00`) |

- **Speeds** (endpoint `0x17`, type `25 00`): value = RPM, no scaling.
- **Temperatures** (endpoint `0x21`, type `10 00`): value = tenths of a degree Celsius
  (divide by 10; e.g. `0xD0 0x00` = 208 → 20.8 °C).

Absent-sensor marker is the status byte, not a sentinel value. Channel 0's record exists in
the array but is always unavailable (devices occupy channels 1+). Consumers should intersect
the sensor array with the enumerated channel map: only report RPM for channels whose model
has the RPM capability, and temperature for channels with the temperature capability.

---

## 8. Writing duty (fan/pump speed)

Endpoint `0x18`, data type `07 00`, via the write transaction of §4.6. Inner data:

| Offset | Content |
|---|---|
| 0 | number of channel entries K |
| 1… | K × 4-byte entries |

Entry layout: `[channel, 0x00, percent, 0x00]`. Percent is a plain integer 0–100 (no 0–255
fractional scaling for this device).

Worked example (from test vectors): channels 1→50 % and 8→100 % encode as inner data
`02 01 00 32 00 08 00 64 00`.

Semantics and rules:

- **Batch write:** the implementation always writes **all enumerated channels in one packet**
  whenever *any* channel's requested duty changed since the last write (change detection via a
  queued store; unchanged cycles write nothing). There is no per-channel/broadcast
  distinction on the wire — the packet simply lists every channel explicitly.
- **Clamping:** fan channels clamp to 0–100. Pump channels (models flagged as pumps, §6.2)
  clamp the *lower* bound to the minimum pump duty.
- **Minimum pump duty:** default **50 %**. Override via environment variable
  `FANCONTROL_CORSAIRLINK_MIN_PUMP_DUTY` (integer percent, read from process then machine
  scope; also applies to Commander Core devices). The override itself is clamped to 0–100 —
  so a user *can* set 0 and allow pump stalls; the default exists to prevent the AIO "pump
  failure" state and low-RPM resonance noise.
- **Zero percent:** legal for fans (fan stop). For pumps it is raised to the minimum.
- **Defaults / reset:** when a channel is reset (control released) or at initialization,
  fans go to **50 %**, pumps to **100 %**.
- Duty takes effect only in software mode; in hardware mode the write fails with status
  `0x03`.

---

## 9. Timing rules

| Parameter | Value |
|---|---|
| Polling cadence | 1000 ms timer; each tick: (duty write if dirty) → speed read → temperature read per device |
| HID read timeout | 500 ms |
| HID write timeout | 500 ms |
| Wait-for-data-type budget | 500 ms of repeated reads |
| Stale-report drain | before every command write, read with 1 ms timeout until timeout |
| Inter-command delay | none for this device (no sleeps between commands) |
| Overlap protection | a non-blocking semaphore skips a refresh tick if the previous one is still running; ticks are never queued |
| Refresh-skip alarm | 10 consecutive skipped ticks → user-facing error dialog ("device may be unresponsive"), suppressible via `FANCONTROL_CORSAIRLINK_ERROR_NOTIFICATIONS_DISABLED` |
| Error alarm | 10 logged errors within a 30-tick window → error dialog |

When another process holds the global mutex, acquisition blocks with **no timeout**; the
running refresh simply stalls, subsequent ticks are skipped, and after 10 skips the user is
notified. Nothing is aborted — when the mutex frees up, operation resumes.

Devices refresh **in parallel** (one task per device per tick), but the global mutex
serializes actual bus transactions across devices and processes.

---

## 10. Error handling, device loss, and recovery

- **Protocol error (status ≠ 0):** the transaction throws; the error is logged with full
  write/read buffer hex dumps. Status `0x03` additionally schedules a software-mode re-entry
  which happens at the start of the next refresh cycle.
- **I/O timeout:** interpreted as "hub silently left software mode"; responds by re-sending
  *enter software mode* (under the mutex). If that succeeds, the timeout is swallowed and the
  cycle's operation abandoned; if it also fails, the timeout propagates.
- **Surprise removal / sleep-wake:** a Windows I/O failure with native error `0x48F`
  (1167, device not connected) triggers: reopen the HID device → on success, run the
  reconnect hook (*enter software mode* again) → retry the failed read/write once. On
  reopen failure, the original exception propagates and the refresh logs an error.
- **System suspend:** a power-mode listener cancels in-flight refreshes and skips all
  refreshes while suspending; polling resumes after resume (recovery then rides on the
  timeout / wrong-mode / reconnect paths above).
- **Re-entrancy guard:** mode changes are flag-guarded so a mode change triggered during a
  mode change is refused (logged) rather than nested.
- **Plugin close:** cancel refreshes, stop the timer, close HID streams. **No hardware-mode
  restore is issued** (see safety notes in §2).

---

## 11. Quirks and gotchas

1. **Firmware < 2.5** only supports single-read enumeration; large device counts (up to 24,
   e.g. two daisy-chained hub ports fully populated) require fw ≥ 2.5 and the two-read scheme.
2. **Continuation report bug:** source comments flag that the continuation enumeration report
   may arrive missing its first byte on some firmware; parsers should be tolerant (the device-
   ID length field and NUL-trimming provide resynchronization).
3. **Endpoint/data-type numeric collision:** endpoint `0x21` (temperatures) vs. data type
   `21 00` (sub-devices) — unrelated values that are easy to confuse.
4. **Defensive close-before-open:** every transaction closes the endpoint before opening it;
   the hub accepts closing an endpoint that is not open. Implementations should replicate
   this, since a crashed predecessor may have left an endpoint open.
5. **No CRC/sequence** anywhere; correctness relies on the drain-before-write plus
   data-type polling.
6. **Duty writes are full-set:** you cannot meaningfully write one channel; always send every
   enumerated channel's duty in a single packet.
7. **Pump minimum duty is host-side policy**, not firmware-enforced — a third-party
   implementation must apply its own floor (50 % is the field-proven default) or risk the
   AIO pump-failure state.
8. **LCD AIO variants** are indistinguishable at this layer; no special handling exists or is
   needed for fan/pump/temp functions.
9. **Concurrent Corsair software:** iCUE / Corsair Service will fight over the device (no
   mutex participation) — documented as unsupported; SignalRGB ≥ 2.3.13 cooperates via the
   `Global\CorsairLinkReadWriteGuardMutex` mutex.
10. **Unknown models** appear in the wild (e.g. code `0x0E` in captures); skip them rather
    than fail enumeration.
11. The AIO coolers (`0x07`, `0x11`) expose a *liquid* temperature sensor on their channel;
    XC7/XG3 blocks report block/liquid temperature; QX and RX MAX fans carry an in-fan
    temperature sensor.

---

## 12. Worked example transactions (hex, 513/512-byte frames truncated; `..` = zero padding)

Enter software mode:
`00 00 01 01 03 00 02 ..` → response `00 00 00 01 00 ..` (offset 4 = 0x00 OK)

Read firmware version:
`00 00 01 02 13 ..` → `00 00 00 02 00 02 09 E8 01 ..` → v2.9.488

Read speeds:
1. `00 00 01 05 01 01 17 ..` (close 0x17) → status OK
2. `00 00 01 0D 01 17 ..` (open 0x17) → status OK
3. `00 00 01 08 01 ..` (read) → poll until `00 00 00 08 00 25 00 0F <15×3 bytes> ..`
   e.g. record `00 E2 01` = available, 482 RPM; record `01 00 00` = no device
4. `00 00 01 05 01 01 17 ..` (close)

Write duties (channel 1 → 50 %, channel 8 → 100 %):
1. close `0x18`, open `0x18` as above
2. `00 00 01 06 01 0B 00 00 00 07 00 02 01 00 32 00 08 00 64 00 ..`
   (length field `0B 00` = 9 data bytes + 2 type bytes)
3. close `0x18`

Read temperatures: same bracket with endpoint `0x21`, expect type `10 00`; record
`00 D0 00` = 20.8 °C, `01 00 00` = none.
