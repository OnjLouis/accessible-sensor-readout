# Corsair iCUE Link / HXi PSU Plug-In Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An optional Sensor Readout plug-in (`PlugIns\Corsair`) that monitors and fan-controls the Corsair iCUE LINK Hub and HXi/RMi PSUs through original protocol code, so Sensor Readout's Fan Curves can replace Fan Control.

**Architecture:** A background worker thread owns all HID I/O (1–2 s ticks, like Fan Control's timer); `GetReadings` serves cloned snapshots instantly; control calls write synchronously under a device lock. Every wire transaction holds the ecosystem mutex `Global\CorsairLinkReadWriteGuardMutex`. Protocol sessions live in per-family device classes over a shared P/Invoke HID transport.

**Tech Stack:** C# 5 (legacy csc.exe), .NET Framework 4.x, x64, P/Invoke (hid.dll, setupapi.dll, kernel32.dll), SensorReadout.PluginSdk.

**Spec:** `Docs/superpowers/specs/2026-08-07-corsair-plugin-design.md` — read it first.
**Protocol annexes (committed in Task 1):** `PlugIns/Corsair/docs/icuelink-protocol.md`, `PlugIns/Corsair/docs/hidpsu-protocol.md`, `PlugIns/Corsair/docs/crosscheck-protocol-notes.md`, `PlugIns/Corsair/docs/host-conventions.md`. Byte-level details cited as "annex §N" are normative; copy constants from there, never guess.

## Global Constraints

- **C# 5 only** (legacy `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`): no `$"..."`, no `?.`, no `nameof`, no expression-bodied members, no auto-property initializers/getter-only autoprops, no `out var`, no pattern matching, no tuples, no dictionary `["k"]=` initializers, no local functions, no exception filters. `var`, LINQ, lambdas, `??`, object/collection initializers are fine. `CultureInfo.InvariantCulture` for all number formatting/parsing.
- All plug-in sources **flat** in `PlugIns\Corsair\src\` (build does not recurse). Each file **< 2000 lines** (build fails ≥ 3000).
- References available: System.dll, System.Core.dll, System.Management.dll, Newtonsoft.Json.dll, SensorReadout.PluginSdk.dll, mscorlib. Nothing else. **No assembly attributes in source** (build injects them).
- Namespace `SensorReadout.CorsairPlugIn`; plug-in class `CorsairPlugIn` (sealed, partial), manifest id `sensorreadout.corsair.experimental`, assembly `CorsairPlugIn.dll`.
- Reading Types used: only `Fan`, `Temperature`, `Performance`, `Fan Control`. Identifiers: lowercase, no leading `/`, no `|`, `/fan/N` ↔ `/control/N` pairing.
- **Never copy code from FanControl.CorsairLink** (unlicensed) — protocol facts from the annex docs only.
- Safety: pump channels (hub models 0x07, 0x11, 0x0C, 0x19) floor at **50 %**, reset default **100 %**; fans reset default **50 %**. PSU manual duty valid range 30–100; percent < 30 means "return fan to PSU automatic". No hub mode change and no duty write unless a control action requires it.
- Every wire transaction inside `Global\CorsairLinkReadWriteGuardMutex` (bounded 2000 ms wait; abandoned-mutex = acquired). Drain stale input reports **inside the mutex, immediately before each command write** — other tools' response traffic lands in our HID queue too.
- Do not touch core app files or `Build.ps1` (`Docs/Coding-agent-plug-in-rules.md`).
- Fast compile loop for tasks 2–8 (run from repo root; requires one prior full `Build.ps1` run so the SDK DLL exists):

```bash
powershell -NoProfile -Command "& $env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:library /platform:x64 /out:$env:TEMP\CorsairPlugIn.dll /reference:System.dll,System.Core.dll,System.Management.dll,'portable\Resources\Newtonsoft.Json.dll','portable\Resources\SensorReadout.PluginSdk.dll' PlugIns\Corsair\src\*.cs"
```

- Test harnesses are single-file console programs in the session scratchpad (`C:\Users\robin\AppData\Local\Temp\claude\C--Claude-accessible-sensor-readout\b15f8421-ccf9-4984-a9a1-d760ff475c54\scratchpad\harness\`), compiled with the same csc referencing the freshly built plug-in DLL. They are never committed. Live-hardware harnesses may READ from the hub/PSU under the mutex but must **never** send mode changes or duty/mode writes (Fan Control is running and owns control).

---

### Task 1: Plug-in scaffold, manifest, notices, protocol annexes

**Files:**
- Create: `PlugIns/Corsair/plugin.json`
- Create: `PlugIns/Corsair/NOTICE.txt`
- Create: `PlugIns/Corsair/GPL-3.0.txt` (copy of `PlugIns/AsusRog/GPL-3.0.txt`)
- Create: `PlugIns/Corsair/docs/icuelink-protocol.md`, `hidpsu-protocol.md`, `crosscheck-protocol-notes.md`, `host-conventions.md` (copied from `C:\Users\robin\AppData\Local\Temp\claude\C--Claude-accessible-sensor-readout\b15f8421-ccf9-4984-a9a1-d760ff475c54\scratchpad\research\` — same names except `sensor-readout-plugin-conventions.md` → `host-conventions.md`)
- Create: `PlugIns/Corsair/src/CorsairPlugIn.cs` (minimal skeleton)

**Interfaces:**
- Produces: manifest identity; class `SensorReadout.CorsairPlugIn.CorsairPlugIn : ISensorReadoutPlugin` with `PluginInfo Info` and `GetReadings` returning one status row. Later tasks extend this class as `sealed partial`.

- [ ] **Step 1: Write plugin.json**

```json
{
  "id": "sensorreadout.corsair.experimental",
  "name": "Corsair iCUE Link and PSU Support (experimental)",
  "version": "0.1.0",
  "author": "Robin Kipp, Claude Code, and Sensor Readout contributors",
  "description": "Experimental, opt-in support for the Corsair iCUE LINK Hub (pump, fans, liquid temperature, fan control) and Corsair HXi/RMi digital power supplies (temperatures, fan, input voltage, output power, fan control). Do not run together with Corsair iCUE. Safe to run alongside HWiNFO or Fan Control for monitoring, but only one program should control fan speeds.",
  "assembly": "CorsairPlugIn.dll",
  "type": "SensorReadout.CorsairPlugIn.CorsairPlugIn",
  "helpLinks": [
    { "label": "Corsair i&CUE Link protocol research (FanControl.CorsairLink)", "url": "https://github.com/EvanMulawski/FanControl.CorsairLink" },
    { "label": "&liquidctl project", "url": "https://github.com/liquidctl/liquidctl" }
  ]
}
```

- [ ] **Step 2: Write NOTICE.txt**

```text
Corsair iCUE Link and PSU Support Plug-In — Third-Party Notices
===============================================================

This plug-in contains an original implementation written for Sensor Readout.
No source code was copied from any third-party project.

The USB HID wire-protocol facts it implements (command identifiers, packet
layouts, numeric encodings, timing and safety semantics) were learned by
studying and cross-checking the following public research projects:

1. FanControl.CorsairLink — Evan Mulawski
   https://github.com/EvanMulawski/FanControl.CorsairLink
   (No open-source license is published for this project; it was used as a
   reference for uncopyrightable protocol facts only.)

2. liquidctl — the liquidctl contributors (GPL-3.0-or-later)
   https://github.com/liquidctl/liquidctl

3. OpenLinkHub — Nikola Jurkovic (GPL-2.0-or-later)
   https://github.com/jurkovic-nikola/OpenLinkHub

Because part of this protocol research derives from GPL-licensed projects,
this optional plug-in is offered under GPL-3.0-or-later (see GPL-3.0.txt
in this folder). The main Sensor Readout application remains MIT-licensed.
This notice applies only to the optional Corsair plug-in in this folder.

Interoperability: this plug-in synchronizes hardware access through the
standard Global\CorsairLinkReadWriteGuardMutex, like HWiNFO, SIV, SignalRGB
and Fan Control. Corsair iCUE does not use this mutex and must not run at
the same time.

Safety notes:
- iCUE Link pump channels are never driven below 50 percent duty.
- Sensor Readout's one-click diagnostics may briefly set exposed fan
  controls to 100 percent and then restore the previous state.
- On clean exit the plug-in returns the hub to its autonomous hardware mode
  and the PSU fan to automatic control if it ever took control of them.
```

- [ ] **Step 3: Copy GPL-3.0.txt and the four annex docs into place** (plain file copies; create `PlugIns/Corsair/docs/`).

- [ ] **Step 4: Write the minimal plug-in class** `PlugIns/Corsair/src/CorsairPlugIn.cs`:

```csharp
using System;
using System.Collections.Generic;
using SensorReadout.PluginSdk;

namespace SensorReadout.CorsairPlugIn
{
    public sealed partial class CorsairPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.corsair.experimental",
            Name = "Corsair iCUE Link and PSU Support (experimental)",
            Version = "0.1.0",
            Author = "Robin Kipp, Claude Code, and Sensor Readout contributors",
            Description = "Experimental, opt-in support for Corsair iCUE LINK Hub cooling devices and Corsair HXi/RMi digital power supplies."
        };

        public PluginInfo Info
        {
            get { return info; }
        }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            var rows = new List<SensorReading>();
            rows.Add(new SensorReading
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Corsair Plug-In",
                Identifier = "corsair/status",
                DisplayValue = "Corsair support is starting up",
                Source = "Corsair Support Plug-In"
            });
            return rows;
        }
    }
}
```

- [ ] **Step 5: Full build** — `powershell -NoProfile -File Build.ps1`. Expected: success; `portable\Plug-Ins\Corsair\CorsairPlugIn.dll`, `plugin.json`, `NOTICE.txt`, `GPL-3.0.txt` exist; `docs\` subfolder is NOT copied to portable (top-level files only).
- [ ] **Step 6: Commit** — `git add PlugIns/Corsair Docs/superpowers/plans/2026-08-07-corsair-plugin.md && git commit -m "Add Corsair plug-in scaffold, notices, and protocol annexes"`.

---

### Task 2: HID transport (`CorsairHidTransport.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairHidTransport.cs`
- Test: scratchpad `harness\HidEnumTest.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class CorsairHidDeviceInfo
{
    public string Path;           // \\?\hid#vid_1b1c&pid_0c3f...
    public ushort VendorId;
    public ushort ProductId;
    public int InputReportLength;   // caps.InputReportByteLength (includes report id)
    public int OutputReportLength;  // caps.OutputReportByteLength (includes report id)
    public string Product;          // may be ""
    public string SerialNumber;     // may be ""
}

public static class CorsairHidEnumerator
{
    // All present HID interfaces with VID 0x1B1C and OutputReportLength > 1.
    public static List<CorsairHidDeviceInfo> FindCorsairDevices(Action<string> logDebug);
}

public sealed class CorsairHidStream : IDisposable
{
    public static CorsairHidStream Open(CorsairHidDeviceInfo info); // null on failure
    public CorsairHidDeviceInfo Info { get; }
    public bool Write(byte[] buffer, int timeoutMs);   // buffer.Length == OutputReportLength
    public bool Read(byte[] buffer, int timeoutMs);    // buffer.Length == InputReportLength
    public void DrainInput();                          // read with 3 ms timeout until timeout
    public bool IsDeviceGone { get; }                  // last error was 1167/ERROR_DEVICE_NOT_CONNECTED
    public void Dispose();
}
```

- [ ] **Step 1: Implement enumeration.** P/Invoke skeleton (complete signatures — x64-correct):

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct HiddAttributes { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

[StructLayout(LayoutKind.Sequential)]
internal struct HidpCaps
{
    public ushort Usage; public ushort UsagePage;
    public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

[DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid hidGuid);
[DllImport("hid.dll")] internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);
[DllImport("hid.dll")] internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);
[DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
[DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps caps);
[DllImport("hid.dll", CharSet = CharSet.Unicode)] internal static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);
[DllImport("hid.dll", CharSet = CharSet.Unicode)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

[DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags); // flags = DIGCF_PRESENT(0x2) | DIGCF_DEVICEINTERFACE(0x10)
[DllImport("setupapi.dll")] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
[DllImport("setupapi.dll", CharSet = CharSet.Unicode)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);
[DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
```

  Detail-buffer quirk: allocate `requiredSize` bytes with `Marshal.AllocHGlobal`, write `cbSize` = **8** at offset 0 on x64 (`Marshal.WriteInt32(buffer, 0, 8)`), path string starts at offset 4 (`Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4))`). Enumerate loop: `memberIndex` 0.. until `SetupDiEnumDeviceInterfaces` returns false. For each path: open a **metadata handle** with `CreateFile(path, 0, FILE_SHARE_READ|FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero)` (access 0 lets us query attributes/caps even for devices opened elsewhere), read attributes → keep VID 0x1B1C; caps via preparsed data; product/serial strings via 256-byte buffers (`Encoding.Unicode.GetString`, trim at first `\0`); close handle. Keep only `OutputReportByteLength > 1`.

- [ ] **Step 2: Implement CorsairHidStream.** Open with `CreateFile(path, GENERIC_READ|GENERIC_WRITE (0xC0000000), FILE_SHARE_READ|FILE_SHARE_WRITE (0x3), IntPtr.Zero, OPEN_EXISTING (3), FILE_FLAG_OVERLAPPED (0x40000000), IntPtr.Zero)`. Overlapped I/O per operation:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlapped2 { public IntPtr Internal; public IntPtr InternalHigh; public uint OffsetLow; public uint OffsetHigh; public IntPtr EventHandle; }

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, IntPtr bytesRead, ref NativeOverlapped2 overlapped);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint bytesToWrite, IntPtr bytesWritten, ref NativeOverlapped2 overlapped);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle handle, ref NativeOverlapped2 overlapped, out uint bytesTransferred, bool wait);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, IntPtr name);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
[DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
```

  Pattern per Read/Write: create manual-reset event, set `overlapped.EventHandle`; call Read/WriteFile; if `false` and `Marshal.GetLastWin32Error() == 997` (ERROR_IO_PENDING) → `WaitForSingleObject(event, timeoutMs)`; on `WAIT_TIMEOUT (0x102)` → `CancelIoEx` + `GetOverlappedResult(wait: true)` → return false; on signaled → `GetOverlappedResult` and require full-length transfer. If last error is `1167` set `IsDeviceGone = true` and return false. Always close the event. `DrainInput()` loops `Read(buffer, 3)` until it returns false.

- [ ] **Step 3: Harness `HidEnumTest.cs`** — console app: call `FindCorsairDevices`, print one line per device (`path | vid:pid | in/out lengths | product | serial`), then `Open` each, `DrainInput()`, `Dispose()`. **No writes.** Compile & run:

```bash
powershell -NoProfile -Command "& $env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /platform:x64 /out:$env:TEMP\HidEnumTest.exe /reference:$env:TEMP\CorsairPlugIn.dll 'C:\Users\robin\AppData\Local\Temp\claude\C--Claude-accessible-sensor-readout\b15f8421-ccf9-4984-a9a1-d760ff475c54\scratchpad\harness\HidEnumTest.cs'; & $env:TEMP\HidEnumTest.exe"
```

  Expected on this machine: a `0c3f` entry with in=512/out=513 and a 32-char serial, and a `1c27` entry with in=64 or 65/out=65 (record actual values — the PSU annex assumes 64/65). The `0c4e` device must be absent (no writable vendor output report) or ignorable.
- [ ] **Step 4: Fast-compile the plug-in sources** (Global Constraints command) — must succeed with zero warnings about language version.
- [ ] **Step 5: Commit** — `git add PlugIns/Corsair/src/CorsairHidTransport.cs && git commit -m "Corsair plug-in: HID enumeration and overlapped transport"`.

---

### Task 3: Global mutex guard (`CorsairDeviceGuard.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairDeviceGuard.cs`
- Test: scratchpad `harness\GuardTest.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class CorsairDeviceGuard : IDisposable
{
    public CorsairDeviceGuard();                 // creates/opens the named mutex once
    public bool TryEnter(int timeoutMs);         // false on timeout; abandoned counts as acquired
    public void Exit();
    public void Dispose();
}
```

- [ ] **Step 1: Implement.** Mutex name `Global\CorsairLinkReadWriteGuardMutex`, created unowned with Everyone/FullControl:

```csharp
var security = new MutexSecurity();
security.AddAccessRule(new MutexAccessRule(
    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
    MutexRights.FullControl, AccessControlType.Allow));
bool createdNew;
mutex = new Mutex(false, "Global\\CorsairLinkReadWriteGuardMutex", out createdNew, security);
```

  Wrap construction in try/catch: on `UnauthorizedAccessException` fall back to `Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify)`. `TryEnter`: `try { return mutex.WaitOne(timeoutMs); } catch (AbandonedMutexException) { return true; }`. `Exit`: `mutex.ReleaseMutex()` in try/catch (swallow `ApplicationException`).
- [ ] **Step 2: Harness `GuardTest.cs`** — acquire (expect true fast — Fan Control holds it only per-transaction), release, then two threads: A holds 500 ms, B measures `TryEnter(2000)` wait ≥ ~400 ms and succeeds. Print PASS/FAIL lines.
- [ ] **Step 3: Run harness + fast-compile plug-in.** Expected: both PASS.
- [ ] **Step 4: Commit** — `"Corsair plug-in: CorsairLink guard mutex"`.

---

### Task 4: Link Hub data layer (`CorsairLinkHubData.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairLinkHubData.cs`
- Test: scratchpad `harness\LinkDataTest.cs`

**Interfaces:**
- Produces (all parsing on RAW 512-byte input reports, report id included — offsets per annex `icuelink-protocol.md` §4.5/§5/§6/§7):

```csharp
public sealed class LinkKnownDevice { public byte Model; public byte Variant; public string Name; public bool IsPump; public bool HasTemp; public bool HasRpm; public bool HasControl; }
public static class LinkKnownDevices { public static LinkKnownDevice Find(byte model, byte variant); } // full table from annex §6.2
public sealed class LinkSubDevice { public int Channel; public byte Model; public byte Variant; public string DeviceId; }
public sealed class LinkSensorRecord { public int Channel; public bool Available; public short RawValue; }

public static class LinkHubData
{
    public static string ParseFirmwareVersion(byte[] raw);                  // raw[5], raw[6], LE16 raw[7..8] -> "2.9.488"
    public static List<LinkSensorRecord> ParseSensorRecords(byte[] raw);    // count raw[7], 3-byte records from raw[8]
    public static List<LinkSubDevice> ParseSubDevices(byte[] rawFirst, byte[] rawContinuation); // stream = rawFirst[7..] + rawContinuation[5..]; tolerant of truncation
    public static byte[] BuildCommandPacket(int outLength, byte[] command, byte[] data);        // [0]=0, [1]=0, [2]=1, cmd at 3, data after, zero-padded
    public static byte[] BuildWriteBlock(byte[] dataType, byte[] innerData);                    // LE16 len=inner+2, 00 00, type, inner
    public static byte[] BuildDutyInnerData(List<KeyValuePair<int, int>> channelPercents);      // [count] + per channel [ch,0,pct,0], ascending channel
    public static byte ResponseStatus(byte[] raw);                          // raw[4]
    public static bool ResponseTypeMatches(byte[] raw, byte[] dataType);    // raw[5..6]
}
```

  Command constants (annex §4.2/§4.3) as `static readonly byte[]`: `EnterSoftwareMode = {0x01,0x03,0x00,0x02}`, `EnterHardwareMode = {0x01,0x03,0x00,0x01}`, `ReadFirmwareVersion = {0x02,0x13}`, `OpenEndpoint = {0x0D,0x01}`, `CloseEndpoint = {0x05,0x01,0x01}`, `ReadEndpoint = {0x08,0x01}`, `WriteEndpoint = {0x06,0x01}`; endpoints `Speeds=0x17`, `Temperatures=0x21`, `DutyWrite=0x18`, `SubDevices=0x36`; data types `Speeds={0x25,0x00}`, `Temperatures={0x10,0x00}`, `Duty={0x07,0x00}`, `SubDevices={0x21,0x00}`. Status: `0=OK`, `3=WrongMode`.

- [ ] **Step 1: Write the harness first** (`LinkDataTest.cs`) with the annex worked vectors — it will not compile yet; that is the failing state:
  - FW: raw report `00 00 00 02 00 02 09 E8 01` (+ padding to 512) → `"2.9.488"`.
  - Sensor records: raw with `raw[7]=0x03`, records `00 E2 01`, `01 00 00`, `00 D0 00` → ch0 available 482, ch1 unavailable, ch2 available 208.
  - Duty inner data for {1→50, 8→100} → exactly `02 01 00 32 00 08 00 64 00`.
  - Write block for type `07 00` + that inner data → starts `0B 00 00 00 07 00` then the 9 bytes.
  - Command packet: `BuildCommandPacket(513, EnterSoftwareMode, null)` → `[0]=0,[1]=0,[2]=1,[3..6]=01 03 00 02`, rest zero, length 513.
  - Sub-devices: build a synthetic `rawFirst` whose stream is: `[0]=2` (last channel), channel 1 record with model 0x01/variant 0x00/idLen 4/id "AB12", channel 2 empty (8 zero bytes) → expect one device (ch 1, QX Fan). Also: continuation split mid-record must not throw and must recover the device list.
  - `LinkKnownDevices.Find(0x07, 0x02)` → "H150i", IsPump true; `Find(0x13,0x00)` → "RX Fan", HasTemp false; `Find(0x0E, 0x00)` → null.
- [ ] **Step 2: Run — expect compile failure** (types missing).
- [ ] **Step 3: Implement `CorsairLinkHubData.cs`** with the full known-device table from annex §6.2 (24 entries incl. TITAN variants 0x00–0x05, HXi SHIFT 0x00–0x02, XC7 temp-only). Parse defensively: any index beyond buffer → stop and return what was parsed.
- [ ] **Step 4: Run harness — all vectors PASS.** Fast-compile plug-in sources.
- [ ] **Step 5: Commit** — `"Corsair plug-in: iCUE Link data layer with test vectors"`.

---

### Task 5: Link Hub device session (`CorsairLinkHubDevice.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairLinkHubDevice.cs`
- Test: scratchpad `harness\LinkLiveReadTest.cs` (READ-ONLY live test)

**Interfaces:**
- Consumes: `CorsairHidStream`, `CorsairDeviceGuard`, `LinkHubData`, `LinkKnownDevices`.
- Produces:

```csharp
public sealed class LinkChannelState
{
    public int Channel; public LinkKnownDevice Device; public string DeviceId;
    public int? Rpm; public float? TemperatureC;
    public int RequestedPercent;      // meaningful only when hub owns software control
    public bool PercentIsDefault;     // true until a manual set, and again after reset
}

public sealed class CorsairLinkHubDevice
{
    public CorsairLinkHubDevice(CorsairHidDeviceInfo info, CorsairDeviceGuard guard, Action<string, string> log);
    public bool Connect();                        // open + fw + enumerate + first sensor read; NO mode change
    public void Disconnect(bool restoreHardwareMode);
    public string Serial { get; }                 // lowercased HID serial, fallback "hub0"
    public string FirmwareVersion { get; }
    public bool OwnsSoftwareControl { get; }
    public bool LastReadWrongMode { get; }        // sensor read returned status 3 while not owning
    public bool IsGone { get; }
    public List<LinkChannelState> Channels { get; }  // stable after Connect
    public bool RefreshSensors();                 // speeds + temps under mutex; false on failure
    public bool SetChannelPercent(int channel, int percent);  // clamp (pump floor 50); ensures software mode; full-set duty write
    public bool ResetChannel(int channel);        // default 50/100, PercentIsDefault=true; stays in software mode
    public bool ReassertControl();                // re-enter software mode + full-set write (resume path)
}
```

- [ ] **Step 1: Implement the transaction core.** Private `byte[] SendCommand(byte[] command, byte[] data, byte[] waitForType)`: build packet (out length 513), inside an **already-held** mutex: `stream.DrainInput()` → `Write(packet, 500)` → `Read(response, 500)`; poll further reads until `ResponseTypeMatches` when `waitForType != null` with a 500 ms deadline (`Environment.TickCount` based). Status byte ≠ 0 → return null and record status (3 → `lastStatusWrongMode = true`). Endpoint read bracket (annex §4.6): guard.TryEnter(2000) → close, open, read(+continuation for sub-devices when fw ≥ 2.5), close → guard.Exit() in finally. Endpoint write bracket likewise. Firmware gate: parse "major.minor.patch", two-read enumeration when (major == 2 && minor >= 5) || major >= 3.
- [ ] **Step 2: Implement Connect/Refresh.** Connect: open stream, read serial from `info.SerialNumber`, fw version, enumerate sub-devices → `Channels` (skip unknown models with a Debug log, but keep a channel entry when the sensor arrays later mark it available — Name `"Corsair device (model 0x" + model.ToString("x2") + ")"`, `HasControl=false`). RefreshSensors: read speeds → for each record with matching channel and `Device.HasRpm` set `Rpm`; read temps similarly (`RawValue / 10f`). Wrong-mode on read: if `OwnsSoftwareControl` → `ReassertControl()` once; else set `LastReadWrongMode`.
- [ ] **Step 3: Implement control path.** `SetChannelPercent`: validate channel exists and `HasControl`; clamp 0–100, pump floor 50; if `!OwnsSoftwareControl` → send EnterSoftwareMode (mutex-bracketed), set all channels' `RequestedPercent` to defaults (fan 50/pump 100, `PercentIsDefault=true`) first, mark `OwnsSoftwareControl=true`; set the target channel (`PercentIsDefault=false`); full-set duty write (`BuildDutyInnerData` over all channels ascending). On status-3 during the write: EnterSoftwareMode once, retry the write once. `ResetChannel`: same write path with default percent. `Disconnect(restoreHardwareMode=true)`: if `OwnsSoftwareControl` → EnterHardwareMode (bounded 2 s mutex wait, best effort), then dispose stream.
- [ ] **Step 4: Live READ-ONLY harness** (`LinkLiveReadTest.cs`): find `0c3f`, Connect, print serial + firmware + enumerated channels (port, model name, pump?), RefreshSensors ×3 at 1 s intervals, print RPM/temps, Disconnect(**false**). Assert: ≥ 1 channel enumerated; at least one RPM > 0; any temperature between 15 and 60 °C. **This harness must never call SetChannelPercent/ResetChannel/ReassertControl, and Disconnect must be called with restoreHardwareMode=false** — Fan Control currently owns the hub.
- [ ] **Step 5: Run live harness with Fan Control running.** Expected: enumeration matches Robin's loop (pump/AIO + fans), plausible RPM/temps, no errors in Fan Control (spot-check `CorsairLink.log` in Fan Control's folder for new errors afterwards if accessible).
- [ ] **Step 6: Fast-compile plug-in; commit** — `"Corsair plug-in: iCUE Link hub session with live read validation"`.

---

### Task 6: HXi PSU device session (`CorsairHidPsuDevice.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairHidPsuDevice.cs`
- Test: scratchpad `harness\PsuTest.cs` (LINEAR11 vectors + live read-only)

**Interfaces:**
- Consumes: `CorsairHidStream`, `CorsairDeviceGuard`.
- Produces:

```csharp
public sealed class CorsairHidPsuDevice
{
    public CorsairHidPsuDevice(CorsairHidDeviceInfo info, CorsairDeviceGuard guard, Action<string, string> log);
    public bool Connect();                       // open + handshake (model name)
    public void Disconnect(bool restoreAutomatic);
    public string ModelName { get; }             // "HX1200i" etc., from handshake
    public string PidHex { get; }                // "1c27"
    public bool IsGone { get; }
    public float? Temperature1C { get; }         // 0x8D VRM
    public float? Temperature2C { get; }         // 0x8E case
    public int? FanRpm { get; }                  // 0x90
    public bool FanIsManual { get; }             // 0xF0 readback
    public float? InputVoltage { get; }          // 0x88
    public float? OutputPowerW { get; }          // 0xEE
    public int RequestedPercent { get; }         // last requested manual percent, -1 = none
    public bool RefreshSensors();                // all reads + mode reconcile under one mutex hold per transaction
    public bool SetFanPercent(int percent);      // >=30: duty 0x3B then mode 0xF0=1; <30: mode 0xF0=0
    public bool ResetFan();                      // duty 0 then mode 0xF0=0; clears RequestedPercent
    public static float FromLinear11(ushort raw);
}
```

- [ ] **Step 1: Harness first — LINEAR11 vectors** (annex `hidpsu-protocol.md` §numeric encoding): `FromLinear11(0x0350) == 848f`, `FromLinear11(0xE1E0) == 30.0f` (±0.001), `FromLinear11(0xE2A8) == 42.5f`, `FromLinear11(0x0000) == 0f`. Expect compile failure first, then implement:

```csharp
public static float FromLinear11(ushort raw)
{
    var exponent = raw >> 11;
    var mantissa = raw & 0x07FF;
    if (exponent > 15) { exponent -= 32; }
    if (mantissa > 1023) { mantissa -= 2048; }
    return (float)(mantissa * Math.Pow(2.0, exponent));
}
```

- [ ] **Step 2: Implement the transaction core.** Buffers: out = `Info.OutputReportLength` (65), in = `Info.InputReportLength` (64 or 65 — use measured value from Task 2, offsets below are for the 64/no-report-id layout; if the measured input length is 65 with a leading 0x00, shift read offsets by one — decide from the Task 2 harness output and annex §framing). Transaction (mutex held): drain → write handshake `[0]=0x00,[1]=0xFE,[2]=0x03` → read (validate `[1]==0xFE` echo; bytes from `[2]` = ASCII model name, NUL-trimmed) → write `[0]=0x00,[1]=mode,[2]=command,[3..]=data` → read → validate `[1]==mode && [2]==command` and no `0xFE` at `[1]` — mismatch = interference (log Debug, return null). Reads use mode 0x03, writes mode 0x02. Word values LE16 at `[3..4]` of the response.
- [ ] **Step 3: Implement sensors + control.** RefreshSensors: temps 0x8D/0x8E (LINEAR11, sane range −10..150 else null), fan 0x90 (LINEAR11, 0..10000), mode read 0xF0 (`[3] == 1` manual), VIN 0x88 (LINEAR11, 80..260 for mains — outside → null), POUT 0xEE (LINEAR11, 0..2000). Reconcile: if `RequestedPercent >= 30` but readback says automatic → re-send duty+manual once per refresh. SetFanPercent/ResetFan per interface docs (duty = single byte percent at data[0], write mode 0x02). `Disconnect(true)`: if we ever set manual → duty 0 + mode 0.
- [ ] **Step 4: Live read-only run** (Fan Control running — mutex protects): Connect → expect ModelName "HX1200i"; Refresh ×3; print all values. Assert temps 10–80, fan RPM 0–3000 (0 valid: zero-RPM), VIN 200–250 (EU mains), POUT 30–1200. **No SetFanPercent/ResetFan calls; Disconnect(false).**
- [ ] **Step 5: Fast-compile; commit** — `"Corsair plug-in: HXi PSU session with LINEAR11 vectors and live read validation"`.

---

### Task 7: Worker thread (`CorsairWorker.cs`)

**Files:**
- Create: `PlugIns/Corsair/src/CorsairWorker.cs`
- Test: scratchpad `harness\WorkerTest.cs`

**Interfaces:**
- Consumes: `CorsairHidEnumerator`, `CorsairLinkHubDevice`, `CorsairHidPsuDevice`, `CorsairDeviceGuard`.
- Produces:

```csharp
public sealed class CorsairSnapshot
{
    public DateTime CapturedUtc;
    public string Status;                        // "" when devices found; else human message
    public List<HubSnapshot> Hubs;
    public List<PsuSnapshot> Psus;
}
public sealed class HubSnapshot
{
    public string Serial; public string FirmwareVersion; public bool OwnsSoftwareControl; public bool WrongModeReadFailure;
    public List<HubChannelSnapshot> Channels;
}
public sealed class HubChannelSnapshot
{
    public int Channel; public string DeviceName; public string DeviceId; public bool IsPump;
    public bool HasRpm; public bool HasTemp; public bool HasControl;
    public int? Rpm; public float? TemperatureC; public int RequestedPercent; public bool PercentIsDefault;
}
public sealed class PsuSnapshot
{
    public string ModelName; public string PidHex;
    public float? Temperature1C; public float? Temperature2C; public int? FanRpm;
    public bool FanIsManual; public float? InputVoltage; public float? OutputPowerW; public int RequestedPercent;
}

public sealed class CorsairWorker
{
    public static CorsairWorker Instance { get; }        // lazy singleton (plug-in instance may be recreated)
    public void EnsureStarted(Action<string, string> log);
    public CorsairSnapshot GetSnapshot();                // deep clone under lock
    public bool ForceRefresh(int waitMs);                // diagnostics: wake worker, wait for a completed tick
    public bool SetHubChannelPercent(string serial, int channel, int percent);
    public bool ResetHubChannel(string serial, int channel);
    public bool SetPsuFanPercent(string pidHex, int percent);
    public bool ResetPsuFan(string pidHex);
    public void StopAndRestore();                        // exit hook body; idempotent
}
```

  PID sets: hub = `{0x0C3F}`; PSU = `{0x1C03,0x1C04,0x1C05,0x1C06,0x1C07,0x1C08,0x1C09,0x1C0A,0x1C0B,0x1C0C,0x1C0D,0x1C1E,0x1C1F,0x1C23,0x1C27}` (annex crosscheck §4; HXi/RMi HID protocol family).

- [ ] **Step 1: Implement.** Background thread (`IsBackground = true`, name "CorsairPlugInWorker") + `AutoResetEvent` for wake. Loop: scan (initially and every 30 s while nothing found, or when a device `IsGone`); per device tick: hub `RefreshSensors` (+ duty reassert happens inside control calls, not the tick; tick calls `ReassertControl()` only when `OwnsSoftwareControl && LastReadWrongMode`); PSU `RefreshSensors`. Snapshot rebuild under `snapshotLock` after each tick. Tick interval: 1000 ms if any hub owns control, else 2000 ms; after 5 consecutive tick failures per device → that device polls every 30 s until success. Control methods: locate device under `deviceLock`, call through synchronously (they take the mutex themselves), then rebuild snapshot immediately. `SystemEvents.PowerModeChanged` in try/catch: Suspend → set `paused=true`; Resume → after 3000 ms delay flag reconnect (dispose devices, rescan; hub `ReassertControl()` if it owned control; PSU re-applies via reconcile). `AppDomain.CurrentDomain.ProcessExit += StopAndRestore; AppDomain.CurrentDomain.DomainUnload += StopAndRestore` — StopAndRestore: stop loop (join ≤ 3 s), hub `Disconnect(restoreHardwareMode: OwnsSoftwareControl)`, PSU `Disconnect(restoreAutomatic: RequestedPercent >= 0)`.
- [ ] **Step 2: Harness `WorkerTest.cs`** — `EnsureStarted`, poll `GetSnapshot()` once per second for 8 s: assert hub snapshot appears within 6 s, has channels with RPM values, PSU snapshot has ModelName; assert `OwnsSoftwareControl == false` throughout and every `RequestedPercent` untouched (hub: PercentIsDefault true / RPM present; PSU: RequestedPercent == -1). Then `StopAndRestore()` (safe: nothing owned → no restore writes) and assert it returns < 3 s.
- [ ] **Step 3: Run harness; fast-compile; commit** — `"Corsair plug-in: background worker with snapshots"`.

---

### Task 8: Plug-in integration (`CorsairPlugIn.cs` full + `CorsairPlugIn.Rows.cs`)

**Files:**
- Modify: `PlugIns/Corsair/src/CorsairPlugIn.cs`
- Create: `PlugIns/Corsair/src/CorsairPlugIn.Rows.cs`
- Test: scratchpad `harness\PluginRowsTest.cs`

**Interfaces:**
- Consumes: `CorsairWorker.Instance`, snapshot types (Task 7).
- Produces: final `CorsairPlugIn : ISensorReadoutPlugin, IFanControllablePlugin`.

- [ ] **Step 1: Identifier scheme (Rows.cs helpers).**

```csharp
// hub:  corsair/link/<serial>/fan/<N>   corsair/link/<serial>/temperature/<N>   corsair/link/<serial>/control/<N>
// psu:  corsair/psu/<pid>/fan/0  /temperature/0  /temperature/1  /control/0  /voltage/in  /power/out
internal static string HubIdentifier(string serial, string kind, int channel)
{
    return "corsair/link/" + serial + "/" + kind + "/" + channel.ToString(CultureInfo.InvariantCulture);
}
internal static bool TryParseControlIdentifier(string identifier, out bool isHub, out string deviceKey, out int channel)
```

  `TryParseControlIdentifier` accepts only `corsair/link/<key>/control/<n>` and `corsair/psu/<key>/control/0`; anything else → false (cheap ownership gate).
- [ ] **Step 2: Row building from snapshot.** Hub rows per channel: Fan row (`Type="Fan"`, Name `"Port N <device>"` or `"Port N <device> pump"` for pumps, DisplayValue `FormatRpm` = `"1180 RPM"`, Value = rpm); Temperature row (Name `"Port N <device> liquid temperature"` for pumps/blocks, `"Port N <device> temperature"` otherwise, DisplayValue `"31.4 C"` via `"0.0"` format, Value = temp); Control row when HasControl (`Type="Fan Control"`, same Name as fan row, and: owns && !PercentIsDefault → Value=percent, DisplayValue `percent + "% manual"`; owns && PercentIsDefault → Value=percent, DisplayValue `percent + "% default"`; !owns → Value=null, DisplayValue `"automatic or firmware managed"`). Hardware `"Corsair iCUE LINK Hub"`, Source `"Corsair Support Plug-In"`. Details per row: `"Port"`, `"Device"`, `"Device id"`, `"Model code"`, `"Firmware"`, `"Safety"` (diagnostics + pump-floor + conflict note), `"Interoperability"` (mutex/iCUE note). PSU rows: analogous (`"PSU fan"`, `"PSU VRM temperature"`, `"PSU case temperature"`, `"PSU input voltage"` DisplayValue `"231.0 V"` Type Performance, `"PSU output power"` DisplayValue `"312 W"` Type Performance; control DisplayValue when RequestedPercent in 1..29 → `"automatic (PSU zero-RPM control)"`). Hub `WrongModeReadFailure` and empty-scan cases → single status row (`Performance`/`Overview`) with explanatory DisplayValue. DiagnosticsMode: call `ForceRefresh(3000)` first and append a `"Corsair worker"` Details bundle (tick counts, last errors, firmware, raw enumeration summary).
- [ ] **Step 3: Wire the SDK surface.** `GetReadings`: `EnsureStarted(log)`, snapshot → rows (always < 1 ms; no I/O). `TrySetFanPercent(identifier, percent)`: parse gate → route to `SetHubChannelPercent`/`SetPsuFanPercent`. `TryResetFan` → reset counterparts. All wrapped try/catch return false, log Error with vendor prefix `"Corsair plug-in: "`.
- [ ] **Step 4: Harness `PluginRowsTest.cs`** — fake `IPluginContext` (Machine = new MachineInfo(), PluginDirectory = temp, DiagnosticsMode = false, Log → console). Call `GetReadings` twice 3 s apart; assert: a `corsair/link/.../fan/N` row with `Value > 0` exists; its paired control identifier differs only by `/fan/` → `/control/`; control DisplayValue is exactly `"automatic or firmware managed"`; a PSU temperature row exists; no row's Type outside {Fan, Temperature, Performance, Fan Control}; no identifier contains `|` or starts with `/`. Then `TrySetFanPercent("corsair/link/bogus/control/1", 50)` → false quickly; `TrySetFanPercent("lhm/whatever", 50)` → false. **Do not call TrySetFanPercent with a real identifier.**
- [ ] **Step 5: Run harness; fast-compile; commit** — `"Corsair plug-in: SDK integration and row model"`.

---

### Task 9: Full build + self-test

- [ ] **Step 1:** `powershell -NoProfile -File Build.ps1` — fix any size-audit warnings ≥ 2000 lines by splitting (partials), any compile errors (usual culprits: C# 6 syntax that slipped in).
- [ ] **Step 2:** `powershell -NoProfile -File Build.ps1 -SelfTest` — expected PASS incl. plug-in manifest identity and bundled-hash tests. Self-test runs on a disposable copy and does NOT enable the Corsair plug-in, so no device I/O happens.
- [ ] **Step 3: Commit** — `"Corsair plug-in: full build and self-test pass"`.

---

### Task 10: Live app smoke test (read-only) + morning handoff docs

- [ ] **Step 1: Disposable app copy.** Copy `portable\` to scratchpad `apptest\`. Create `apptest\Config\` settings enabling only the Corsair plug-in: run the app once with `--report` style CLI? No — instead: copy an existing minimal config if present, else write `apptest\Config\settings.json` with `PlugInsEnabled = { "sensorreadout.corsair.experimental": true }` matching the AppSettings JSON shape (inspect a real config from a self-test run first — `SensorReadoutForm.SettingsAndFiles.cs` documents the location; do NOT touch any real user config).
- [ ] **Step 2: Run report mode** (command line, no UI): `& "apptest\Sensor Readout.exe" --report "apptest\report.html"` (check `Program.cs` for the exact report-only switch name before running; logging on). Expected: report contains rows `Corsair iCUE LINK Hub` with RPM/temperature values and `Corsair HX1200i PSU` rows; log contains no Corsair Error lines; **log must show no "software mode" / duty writes**. Fan Control keeps running normally throughout.
- [ ] **Step 3: Check LHM overlap.** Search the report for a pre-existing LibreHardwareMonitor PSU section (Corsair PSU rows not from the plug-in). Record the finding for the morning summary (duplication + mutex-less access note for Robin/Andre).
- [ ] **Step 4: Write `PlugIns/Corsair/TESTING.md`** — morning test script for Robin: how to enable the plug-in, verify rows with a screen reader, stop Fan Control, test manual fan control + reset, set up a fan curve on liquid temperature, run one-click diagnostics, quit the app and confirm fans return to the hub profile, and how to revert to Fan Control. Include the caveats from spec §8 verbatim.
- [ ] **Step 5: Commit** — `"Corsair plug-in: live read-only smoke test artifacts and testing guide"`. Update the plan file checkboxes; write the morning summary in chat.

## Self-Review Notes

- Spec coverage: §4 architecture → Tasks 2/3/7; §5 hub → Tasks 4/5; §6 PSU → Task 6; §5.2/§6 rows → Task 8; §7 errors/perf → Tasks 5–8; §9 testing → Tasks 5/6/9/10; §2 legal → Task 1. Spec §8 limitation 1 (background cache) needs no task — documented in TESTING.md (Task 10).
- Type consistency: snapshot/device member names match between Tasks 5/6/7/8 (checked).
- No placeholders: byte-level details delegated to committed annex docs by explicit section reference, which the implementer must read (annexes are normative, not TBD).
