# Cross-check: iCUE LINK Hub + Corsair HXi HID PSU protocol facts

Sources compared (independent of each other):

- **A. OpenLinkHub** (jurkovic-nikola, GPL, Go) — `src/devices/lsh/lsh.go`, `database/external/lsh.json`, `src/devices/devices.go` (facts only, no code reused)
- **B. liquidctl** — `liquidctl/driver/corsair_hid_psu.py`, `liquidctl/driver/commander_core.py`, `docs/developer/protocol/commander_core.md`
- **C. FanControl.CorsairLink** (EvanMulawski) — README, `src/devices/icue_link/ICueLinkHubDevice.cs`, `src/devices/hid_psu/HidPsuDevice.cs`, `src/CorsairLink.Synchronization/*`
- **D.** Corsair forum / HWiNFO forum / OpenRGB issue #605 (web search)

Local copies of all fetched sources (`lsh.go`, `lsh.json`, `corsair_hid_psu.py`, `commander_core.md`, `commander_core.py`, `icuelink-device.cs`, `hidpsu-device.cs`, `guard.cs`, `fancontrol-readme.md`) were kept in the uncommitted research workspace used while writing this document. They are not part of this repository.

---

## 1. iCUE LINK Hub — identity and framing (A ⇔ C AGREE)

| Fact | OpenLinkHub (A) | FanControl (C) |
|---|---|---|
| VID/PID | registered at product id 3135 = `0x0C3F` (VID 1B1C) | `1b1c:0c3f` |
| HID report size | 512 in / 512+1 out (`bufferSize=512`, `bufferSizeWrite=513`) | `PACKET_SIZE=512`, `PACKET_SIZE_OUT=513` |
| Out header | `[0]=0x00` (report id), `[1]=0x00`, `[2]=0x01`, command at offset 3 | same (3-byte header + command, HANDLE_ID `0x01`) |
| Response status byte | checked at offset 3 of stripped response (= offset 4 raw) | error byte = `readBuf[4]`, `0x00` = OK |
| Error `0x03` | (retries write) | `IncorrectModeError` → must re-enter software mode |
| Response data type | `[4:6]` of stripped read (= raw `[5..6]`) | `readBuf.Slice(5,2)` |

### Command set (A ⇔ C byte-for-byte AGREE)

- Enter **software mode**: `0x01 0x03 0x00 0x02`
- Enter **hardware mode**: `0x01 0x03 0x00 0x01`
- Read firmware version: `0x02 0x13`
- Open endpoint: `0x0d 0x01`; Close endpoint: `0x05 0x01 0x01`
- Endpoint read: `0x08 0x01`; endpoint write: `0x06 0x01`
- Endpoints: get speeds `0x17` (data type `0x25 0x00`), get temps `0x21` (type `0x10 0x00`), set speed `0x18` (type `0x07 0x00`), get sub-devices `0x36` (type `0x21 0x00`)
- OpenLinkHub extras: get LEDs `0x20`, set color `0x22` (type `0x12 0x00`), Link-attached-PSU volts `0x28` / amps `0x29` (with `0x19` at header byte 1), LED device codes `0x1e`/`0x1d`

**Cross-family confirmation (B):** liquidctl's Commander Core doc/driver uses the *identical* `0x01 0x03 0x00 0x02` as "wake" and `...0x01` as "sleep", and identical open/close/read/write endpoint opcodes (`0x0d`, `0x05`, `0x08`, `0x06`/`0x07`) with 96-byte packets. The Link Hub is the same protocol family scaled to 512-byte reports. Temperature encoding also matches (int16 LE, tenths of °C, e.g. `3b:01` = 31.5 °C, with per-channel connected-status byte).

## 2. Software vs hardware mode semantics / failsafe

- **Hardware mode = device autonomous.** The hub (like Commander Core) runs its stored hardware profile for fans/pump; software mode = host-commanded fixed duties.
- **Inactivity failsafe (IMPORTANT, indirect):** liquidctl's Commander Core doc states wake-up "needs to be run every time the device has not been sent any data for a predefined number of seconds" — i.e. this device family **drops back to autonomous hardware mode after an idle timeout**. No source documents the exact timeout for the Link Hub. Supporting evidence for Link Hub: FanControl treats response error `0x03` (wrong mode) as "device left software mode; re-enter", re-enters software mode after USB reconnect and after read/write timeouts (sleep/resume path). Corsair forum reports of fans ramping to loud/100% "when switching profiles or logging on" are consistent with hardware-mode fallback being *loud but safe* (fans spin, pump runs — fail-loud, not fail-stop).
- **CONTRADICTION in handoff practice (fact, not protocol):**
  - liquidctl commander_core: wakes before **every** operation and returns the device to sleep/hardware mode after **every** operation (most conservative).
  - OpenLinkHub: enters software mode once at init (then sleeps 500 ms), polls device data every 1 s / applies speed profiles every 3 s, sends **hardware mode on Stop()** before closing the handle. On its "exit" path it writes without waiting for a response.
  - FanControl.CorsairLink: enters software mode at connect/reconnect; **never sends EnterHardwareMode on disconnect** — just closes the handle, implicitly relying on the hub's own revert-to-hardware behavior.
  - Safest original-implementation policy: explicit hardware-mode command on every clean shutdown *and* rely on the firmware fallback for dirty exits; re-assert software mode after resume/reconnect; treat status `0x03` as a trigger to re-enter software mode.

## 3. Speed write mechanics + pump minimums (A ⇔ C AGREE)

- Write to endpoint `0x18`, data type `0x07 0x00`. Payload: `[count]` then per device 4 bytes: `[channel, mode, duty%, 0x00]`, channels sorted ascending; `mode 0x00` = fixed percent. Duty is an integer percent (0–100).
- OpenLinkHub validates the response and **retries up to 20 times** (100 ms apart) if status != 0.
- **Pump minimum 50%:** OpenLinkHub hard-codes "Minimal pump speed should be 50%" for any `containsPump` channel (and floors everything at 20%). FanControl: default minimum pump power 50% (user-overridable), default/reset pump power **100%**; README warns <50% can cause a "pump failure" state, noise/resonance. Both AGREE: never drive Link pumps below 50, default to 100 when releasing control.
- Pump-bearing model codes (both sources agree): AIO `0x07` (H100i/H115i/H150i/H170i variants 00–05), XD5 `0x0c`, XD6 `0x19`, TITAN `0x11` (variants 00–05). LCD pump caps show up as type 6 / 14.
- Sub-device enumeration: endpoint `0x36`, type `0x21 0x00`; channel count at payload[6], then 8-byte records `[.., type@2, variant@3, .., idLen@7]` + variable-length id. Firmware ≥ 2.5 (or ≥ 3.x) requires a **second continuation read** to enumerate up to 24 devices (C). Model tables in A (`lsh.json`) and C (README) match: 01 QX, 02 LX, 03 RX RGB MAX, 04 RX MAX, 05 adapter, 07 AIO, 09 XC7, 0a XG3 hybrid, 0b **HXi SHIFT PSU (variants 00–02 = HX1000i/HX1200i/HX1500i)**, 0c XD5, 0d XG7, 0f RX RGB, 10 VRM cooler, 11 TITAN, 13 RX, 19 XD6, 1b Commander Duo.

## 4. HXi/RMi HID PSU protocol (B ⇔ C AGREE on everything checked)

- 64-byte HID reports, **no report number** (out buffer 65 with `[0]=0`, data from `[1]`).
- Framing: `[1]` = 0x02 write / 0x03 read (B models it as slave address `0x02 | RW-bit`; C calls it "command mode" — same bytes), `[2]` = PMBus command code, `[3..]` = data.
- Handshake/init: write `0xFE 0x03` (command mode and command swapped). B: required after boot/resume/replug or all replies read `<addr> 0xfe 00 00...`. C: performs the handshake **before every guarded transaction** and parses the response as the model-name string ("HX1200i" etc.).
- Reads: fan RPM `0x90` (READ_FAN_SPEED_1), temp1 `0x8D` (VRM), temp2 `0x8E` (case), VIN `0x88`, rail select PAGE `0x00`, VOUT/IOUT/POUT `0x8B/0x8C/0x96`, total output power `0xEE`, uptime `0xD2`/total `0xD1`, fw version `0xD4` (C). Values are **LINEAR11** (`linear_to_float`), uptime = LE uint32 seconds.
- **Fan control:** register `0xF0` (MFR_SPECIFIC_F0) = fan control mode: `0x00` hardware/automatic (PSU's own curve incl. **zero-RPM**), `0x01` software/manual. Duty = write percent byte to `0x3B` (FAN_COMMAND_1). AGREEMENT B⇔C.
- **Return fan to PSU control = write `0x00` to `0xF0`.** liquidctl resets to hardware mode during `initialize`; FanControl switches to Normal whenever requested duty < zero-RPM threshold (default 30, configurable 1–99) and on Disconnect (ResetChannel → power 0 → Normal mode). C writes duty first, then mode.
- **Minimum manual duty 30%** for HXi/RMi (B clamps 30–100; C README table: HXi/RMi 30%, AXi 15% — AXi is a different, SiUsbXpress-based protocol, out of scope).
- OCP: register `0xD8`, `0x01` single-rail / `0x02` multi-rail (B only; C doesn't touch it).
- Conflict detection: B asserts the response echoes request bytes `[0..1]` ("possible conflict with another program"); C validates `Response[1..2] == Request[1..2]` and treats `0xFE` bytes as errors. Same mechanism.
- **HX1200i generations:** `1c08` = original HXi; `1c23` = HX1200i (2023) / "ATX 3.1" CP-9020281; **`1c27` = HX1200i (2025) / "ATX 3.1 #2" CP-9020307**. Both B and C drive 1c23/1c27 with the *unchanged* HXi protocol — only the efficiency-curve coefficients differ (B). No special init, no reports of bricking found; fan-mode/duty writes are volatile (revert on PSU AC cycle / handshake re-init).
- Linux note (B): kernel `corsair-psu` hwmon driver (5.11+) may own the device; racing it corrupts OCP/fan-mode reads. On Windows the analogous conflict is LibreHardwareMonitor's "PSU (Corsair)" source (C README requires disabling it).

## 5. `Global\CorsairLinkReadWriteGuardMutex` convention

- Name: `Global\CorsairLinkReadWriteGuardMutex` (introduced by Corsair Link 4.2.4.25 replacing the `CorsairLinkReadWriteGuard` semaphore; per HWiNFO/SIV forums).
- Implementers: HWiNFO ≥ 5.34, SIV ≥ 5.17, FanControl.CorsairLink, SignalRGB (Commander PRO/CORE/XT 2.2.29+, iCUE LINK 2.3.13+, Hydro Platinum 2.3.45+), OpenRGB ≥ 1.0 only for Commander PRO/CORE/XT + Hydro Platinum (**not** iCUE Link — OpenRGB issue #605). **Modern Corsair iCUE does NOT honor it** — coexistence with iCUE is unsupported.
- Exact usage per FanControl source (`CorsairDevicesGuard`): create the mutex **unowned**, in the `Global\` namespace, with an explicit DACL granting `MutexRights.FullControl` to Everyone (WorldSid) so elevated and non-elevated processes interoperate. Acquire = `WaitOne()` (infinite); on `AbandonedMutexException` the mutex *is* owned — FanControl releases and re-acquires in a loop (i.e., abandoned = recoverable, don't crash). Scope: **one global mutex for all Corsair devices**, held per transaction (each write+read exchange or small command sequence), released immediately after.
- Hold-time convention: SIV logs a diagnostic if it cannot acquire within **≤ 20 s** — holders must keep transactions short (hundreds of ms). One long-poll transaction (e.g. Link Hub endpoint read with 500 ms data-type wait, as in C) is the practical upper bound.

## 6. Coexistence notes

- iCUE + its "Corsair Service" must be stopped; they do not use the guard mutex and will interleave transactions (C README, HWiNFO forum). PSU response-echo validation and Link status-byte checking exist specifically to detect this.
- Citrix Workspace breaks Corsair device enumeration (known Corsair KB issue).
- iCUE Link Hub firmware has known freeze/crash reports under iCUE itself (Corsair forums); keep polling modest (OpenLinkHub: 1 s device data, 3 s speed application; FanControl: 500 ms data-type read timeout, one transaction at a time) and always close/reopen endpoints around each read (both A and C close-then-open defensively before every endpoint operation).
- After system resume: re-run PSU handshake (B explicitly documents init is required after resume) and re-enter Link software mode (C does this on reconnect/timeout; OpenLinkHub ships a `resumeDelay` config for re-initialization after sleep).
