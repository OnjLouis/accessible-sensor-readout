# OEM provider research for Sensor Readout 1.6

This is a development note, not user-facing release documentation. The goal is to support more laptop hardware without bundling vendor control suites or making inaccessible OEM apps part of the main workflow.

## Design rules

- Keep LibreHardwareMonitor as the first provider.
- Add OEM providers only as optional fallback or enrichment layers.
- Prefer documented local APIs, command line tools, or clean-room calls to Windows interfaces.
- Do not copy GPL implementation code into Sensor Readout.
- Keep probes quiet unless they return real sensor rows or a targeted diagnostic is genuinely useful.
- Keep slow OEM probes out of fast refresh unless an alarm, fan curve, hotkey, or visible view actually needs them.

## Current provider seam

`SensorReadoutForm.OemProviders.cs` now owns the OEM entry point:

- `GetOemProviderRows()` aggregates OEM-specific rows.
- `GetMachineIdentity()` caches `Win32_ComputerSystem` manufacturer/model.
- `IsFrameworkComputer()`, `IsHpOmenComputer()`, and `IsAsusComputer()` centralize targeting.
- Framework EC and Framework Control API now sit behind the OEM provider seam.
- HP OMEN and ASUS currently perform debug-only capability probes and return no sensor rows yet.

## Framework

Status: implemented for Framework Control API and optional `ectool.exe`.

Sources:

- Framework Control API observed from tester: `http://127.0.0.1:30912/api/thermal`
- Framework EC fallback via `ectool.exe`

Notes:

- The API gives temperatures and fan RPMs when Framework Control is running.
- The old loose model check for `"Laptop 16"` caused false diagnostics on HP OMEN laptops and has been removed.

## HP OMEN / Victus

Status: research-ready, not implemented as control/readout yet.

Sources:

- OmenMon: https://github.com/OmenMon/OmenMon
- OmenMon CLI docs: https://omenmon.github.io/cli
- OmenMon GUI docs: https://omenmon.github.io/gui
- OmenHwCtl: https://github.com/GeographicCone/OmenHwCtl
- Linux HP WMI fan-control work indicates newer OMEN/Victus fan reporting/control is possible at the firmware layer.

Observed Windows interface:

- WMI namespace: `root\wmi`
- Classes: `hpqBDataIn`, `hpqBIntM`
- Method: `hpqBIOSInt128`
- Shared signature used by open-source tools: ASCII `SECU`

Useful commands seen in OmenHwCtl/OmenMon:

- Fan count: command type `0x10`
- Fan type: command type `0x2C`
- Fan level: command type `0x2D`
- Fan table: command type `0x2F`
- Thermal sensor: command type `0x23`
- Set fan level: command type `0x2E`
- Set fan mode: command type `0x1A`

Open questions:

- Whether fan level bytes map to exact RPM on each model or are only internal levels.
- Whether OMEN MAX 16-ak0xxx uses the same WMI methods.
- Whether direct WMI calls require administrator rights on all affected systems.
- Whether using the WMI command constants directly is acceptable for this project without GPL contamination. Implementation should be clean-room and narrowly based on interface behavior, not copied source.

Next safe step:

- Add a debug/test build command that probes `hpqBIntM` read-only calls on HP/OMEN/Victus machines and logs raw output only when debug logging is enabled.

## ASUS ROG / TUF / Zephyrus

Status: research-ready, not implemented as control/readout yet.

Sources:

- G-Helper: https://github.com/seerge/g-helper
- G-Helper describes fan, power, GPU, battery, and lighting control for ASUS laptops and ROG Ally.

Observed G-Helper implementation surface:

- ASUS ACPI device methods expose CPU, GPU, and middle fan RPMs.
- Fan curve and fan range setters exist inside the app.
- No stable machine-readable CLI/API was found in the first pass.

Risk:

- G-Helper is GPL. Sensor Readout should not copy its ACPI implementation.
- If G-Helper adds a stable CLI/API, Sensor Readout can call it as an installed optional tool.
- Direct ASUS ACPI support would need a clean-room provider and careful test hardware coverage.

Next safe step:

- Track G-Helper releases and docs for a stable CLI/API.
- Add a quiet debug probe for installed G-Helper path only; do not show UI rows from that probe.

## Lenovo Legion

Status: candidate for later research.

Likely sources:

- Lenovo Legion Toolkit and related open-source Legion fan-control tools.
- Some tools use low-level drivers or EC access, which can create anti-cheat and safety concerns.

Next safe step:

- Identify whether Lenovo Legion Toolkit exposes a stable command line, IPC, or documented API before considering direct EC access.

## Dell / Alienware

Status: candidate for later research.

Notes:

- Existing community fan-control tools often use low-level EC methods.
- Prefer installed-tool integration if a safe CLI exists.

## 1.6 implementation target

1. Keep the new OEM provider seam.
2. Add read-only debug probes for HP OMEN/Victus first.
3. If HP read-only fan levels work on tester hardware, expose them as `Fan` rows.
4. Only after readout is proven, consider writable `Fan Control` rows with explicit safety limits and manual testing.
5. Add ASUS support only if a stable installed-tool interface exists or a clean-room direct provider can be built safely.
6. Update docs around “OEM helper support” only when a provider is actually user-facing.
