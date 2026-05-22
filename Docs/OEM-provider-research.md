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
- Framework, HP, Dell, ASUS, Lenovo, and MSI hardware support lives in optional Plug-Ins, disabled by default.
- Framework EC and Framework Control API now sit behind the Plug-In seam.
- HP OMEN/Victus, Dell, and Lenovo Plug-Ins are read-only probes for tester feedback. MSI is opt-in and can expose fan-table controls on compatible models.
- ASUS support is experimental, opt-in, and documented separately because it includes G-Helper-derived ACPI research and limited fan-control attempts.

## Framework

Status: implemented for Framework Control API and optional `ectool.exe`.

Sources:

- Framework Control API observed from tester: `http://127.0.0.1:30912/api/thermal`
- Framework EC fallback via `ectool.exe`

Notes:

- The API gives temperatures and fan RPMs when Framework Control is running.
- The old loose model check for `"Laptop 16"` caused false diagnostics on HP OMEN laptops and has been removed.

## HP OMEN / Victus

Status: experimental Plug-In shipped for tester feedback. It is disabled by default and read-only.

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

Current Plug-In behavior:

- Ships as `HP Hardware Support (experimental)`.
- Enables only when the user opts in from Preferences, Plug-Ins.
- Targets HP, Hewlett-Packard, OMEN, and Victus machine identifiers.
- Probes `root\wmi` for `hpqBDataIn` and `hpqBIntM`.
- Adds only a diagnostic status row for now; it does not set fans or call firmware commands.

Next safe step:

- Ask HP/OMEN/Victus testers to enable the Plug-In, send a debug log and report, and confirm which WMI classes are present.
- If read-only fan/thermal commands are confirmed on tester hardware, expose them as real `Fan` or `Temperature` rows.

## ASUS laptops

Status: experimental Plug-In prepared for tester feedback. It is disabled by
default. It can read ATKACPI temperature and fan duty-cycle values where
available, falls back to ASUS WMI read probes on systems that expose
`AsusAtkWmi_WMNB`, and attempts fan control only on models that accept the
known ATKACPI fan curve or fan range calls.

Sources:

- G-Helper: https://github.com/seerge/g-helper
- G-Helper describes fan, power, GPU, battery, and lighting control for ASUS laptops and ROG Ally.

Observed G-Helper implementation surface:

- ASUS ACPI device methods can expose CPU, GPU, and middle fan duty-cycle percentages.
- Fan curve and fan range setters exist inside the app.
- No stable machine-readable CLI/API, named pipe, socket, or local HTTP endpoint was found in the source or docs.
- Direct ATKACPI calls can expose useful read-only data on some machines, but
  fan writes are model-sensitive and may be rejected even when reads work.
- Some non-ROG systems expose an ASUS WMI `DSTS` method through `root\WMI\AsusAtkWmi_WMNB`; Sensor Readout treats that as read-only until write behavior is proven safe.

Risk:

- G-Helper is GPL. The optional Asus Laptop Support Plug-In is marked separately and
  ships with its own GPL notice and GPL text because it uses G-Helper-derived
  ACPI research.
- If G-Helper adds a stable CLI/API, Sensor Readout can call it as an installed optional tool.
- Direct ASUS ACPI support needs careful test hardware coverage.

Decision:

- Ship as an experimental, opt-in Plug-In so Asus owners can provide logs and
  confirm which models accept fan writes.
- Keep this research note so ASUS can be revisited if G-Helper adds a supported interface or if a cleaner provider becomes practical.

Next safe step:

- Track G-Helper releases and docs for a stable CLI/API.
- Use tester logs to identify model-specific fan-control requirements.

## Lenovo Legion

Status: experimental Lenovo Laptop Plug-In shipped for tester feedback. It is disabled by default and read-only.

Likely sources:

- Lenovo Legion Toolkit and related open-source Legion fan-control tools.
- Some tools use low-level drivers or EC access, which can create anti-cheat and safety concerns.

Next safe step:

- Identify whether Lenovo Legion Toolkit exposes a stable command line, IPC, or documented API before considering direct EC access.

## MSI laptops

Status: experimental MSI Laptop Plug-In prepared for opt-in tester feedback. It is disabled by default and exposes MSI ACPI fan-table controls on compatible models.

Sources:

- MSI support directs fan setup through MSI Center / Cooling Wizard.
- Linux `msi-wmi-platform` documentation describes a Windows ACPI WMI class named `MSI_ACPI`, with a read method `Get_Fan` returning up to four fan tachometer readings.

Observed Windows interface:

- WMI namespace: `root\WMI`
- Class: `MSI_ACPI`
- Package class: `Package_32`
- Method: `Get_Fan`
- Output: firmware status byte followed by up to four 16-bit fan tachometer readings.
- RPM formula from the public WMI platform notes: `RPM = 480000 / raw tachometer reading`.
- Candidate write methods from public Linux platform-driver work: `Set_Fan` writes CPU/GPU fan tables through subfeatures `0x01` and `0x02`; `Set_AP` changes fan-table mode through subfeature `0x01` and bit `0x80`.

Risk:

- MSI fan control and EC writes are model-specific. Sensor Readout only exposes MSI fan-table controls after the user enables the MSI plug-in, and manual writes capture the current table/mode so automatic/default can restore it when available.
- The tester write path writes a flat six-point CPU/GPU fan table, enables MSI fan-table mode, and attempts to restore the original table and AP mode. It must stay out of public/default behavior until tester diagnostics prove that 100% and restore both work safely.
- If `MSI_ACPI` is absent or returns no usable readings, the Plug-In should report a clear diagnostic status row rather than inventing fan rows.

Next safe step:

- Ask MSI testers to enable `MSI Laptop Support (experimental)`, send diagnostics, and confirm whether `MSI_ACPI` exists and returns sane RPM values.
- For HAMISHMSI-style controlled tests, ask the tester to enable the MSI plug-in, run diagnostics, and review whether the diagnostic fan-control test reports `Set ... to 100%: OK` and restore `OK`.
- Ship MSI fan control publicly only after tester reports show both accepted writes and successful restore.

## Dell / Alienware

Status: candidate for later research.

Notes:

- Existing community fan-control tools often use low-level EC methods.
- Prefer installed-tool integration if a safe CLI exists.

## 1.6 implementation target

1. Keep the new OEM provider seam.
2. Ship the HP experimental Plug-In disabled by default and collect tester logs/reports.
3. If HP read-only fan levels work on tester hardware, expose them as `Fan` rows.
4. Only after readout is proven, consider writable `Fan Control` rows with explicit safety limits and manual testing.
5. Do not ship ASUS/G-Helper detection-only support.
6. Add ASUS sensor/control support only if a stable installed-tool interface exists or a clean-room direct provider can be built safely.
7. Update docs around OEM helper support only when a provider is actually user-facing.
