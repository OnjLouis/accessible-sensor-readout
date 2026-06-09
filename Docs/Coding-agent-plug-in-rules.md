# Coding Agent Rules For Plug-In Work

This file is for coding agents and contributors working on Sensor Readout hardware plug-ins.

Sensor Readout uses plug-ins so machine-specific hardware support can grow without turning the main application into a collection of vendor special cases. If the task is to add, fix, or improve support for an existing bundled plug-in, keep the work inside that plug-in unless Andre explicitly asks for a core application change.

## Hard Boundary

Do not modify Sensor Readout core application code when the requested work is plug-in-specific.

For existing bundled plug-ins, stay inside the matching folder:

```text
PlugIns\AsusRog
PlugIns\DellLatitude
PlugIns\Framework
PlugIns\HP
PlugIns\LenovoThinkPad
PlugIns\MsiLaptop
```

Allowed plug-in-scope files include:

- The plug-in's `src` files.
- The plug-in's `plugin.json`.
- Plug-in-local notices, licenses, test notes, or bundled helper files.

Do not touch these for plug-in-only work:

- `src\SensorReadoutForm*.cs`
- `src\Program*.cs`
- `src\Models.cs`
- `src\PreferencesForm*.cs`
- `src\PluginSdk\PluginSdk.cs`
- `Build.ps1`
- `Release.ps1`
- shared language/manual/source-map files

Those files are core app or release infrastructure. Changing them for one laptop brand risks breaking every user.

## When Core Changes Are Allowed

Core app changes are acceptable only when the plug-in work proves that the SDK or host app lacks a required generic capability. Examples:

- A new `SensorReading` field is needed by all plug-ins.
- The plug-in loader rejects a valid safe manifest shape.
- The host cannot display a generic reading type that multiple plug-ins need.
- A host bug prevents any plug-in from loading or reporting safely.

If a core change seems necessary, stop and write down:

- What the plug-in cannot do with the current SDK.
- Why the need is generic rather than vendor-specific.
- Which core file would need to change.
- What test proves the change does not affect unrelated plug-ins or core readings.

Do not make the core change without explicit approval.

## Expected Workflow

1. Identify the exact plug-in folder for the target hardware.
2. Read that plug-in's `plugin.json` and source file before editing.
3. Keep changes narrowly scoped to the plug-in.
4. Keep probes read-only unless the plug-in already has tested write/control behavior.
5. Add diagnostic details inside the plug-in so tester reports explain what Windows exposed.
6. Build Sensor Readout so the plug-in DLL is regenerated.
7. Run the self-test when possible.
8. Ask for tester diagnostics when hardware is not locally available.

## Safety Rules

- Do not invent readings. Report only what Windows, a vendor interface, or a bundled helper actually exposes.
- Do not copy GPL or other incompatible implementation code into the MIT core app.
- If GPL-derived research is used, keep it isolated in the relevant plug-in folder with its own notices.
- Do not enable a plug-in by default.
- Do not add write/control behavior unless tester diagnostics prove it works and restores safely.
- Do not make a plug-in depend on inaccessible vendor software unless that dependency is optional and clearly documented.

## Documentation

If the plug-in behavior changes in a user-visible way, update the plug-in's description or notes where appropriate. User-facing changelog/manual updates belong to release work and should be handled deliberately, not as a side effect of exploratory plug-in edits.
