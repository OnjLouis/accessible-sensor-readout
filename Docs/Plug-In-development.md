# Sensor Readout Plug-In Development

Sensor Readout Plug-Ins let hardware-specific integrations live outside the main app. The goal is to keep Sensor Readout stable, accessible, and small while still making it possible to support machines whose fans, temperatures, batteries, or firmware data are exposed through vendor-specific tools.

This document describes the 1.6.0 Plug-In model.

## Design Goals

- Keep the main app independent of laptop-brand special cases.
- Let users enable only the Plug-Ins that matter for their machine.
- Let third parties add support without editing Sensor Readout itself.
- Make Plug-In readings behave like normal Sensor Readout readings, so they work in the tree, reports, hotkeys, alarms, hidden items, fan curves, and fan profiles wherever the reading type makes sense.
- Fail safely. A missing, disabled, corrupt, or crashing Plug-In must not crash Sensor Readout.

## Trust Model

Plug-Ins are .NET Framework DLLs loaded into the Sensor Readout process. That means enabled third-party Plug-Ins are trusted code.

Only install Plug-Ins from people or projects you trust. Sensor Readout will catch common Plug-In failures, but it cannot sandbox arbitrary .NET code inside the same process.

## Folder Layout

Plug-Ins live under the app folder:

```text
SensorReadout
  Sensor Readout.exe
  SensorReadout.PluginSdk.dll
  Plug-Ins
    Framework
      plugin.json
      FrameworkPlugIn.dll
```

Each Plug-In must have its own subfolder. The folder name is for humans; Sensor Readout identifies the Plug-In by the `id` inside `plugin.json`.

## User Settings

Users enable or disable Plug-Ins from:

```text
Options > Preferences > Plug-Ins
```

The enabled/disabled choice is stored in the per-machine config because hardware Plug-Ins are machine-specific.

Installed Plug-Ins always start disabled. The user must explicitly enable a Plug-In from Preferences before Sensor Readout loads or queries it.

Changes take effect after closing Preferences. A full app restart should not be required.

Users can import a Plug-In ZIP from the same Preferences tab. The ZIP must contain exactly one `plugin.json`. Importing copies the Plug-In into the `Plug-Ins` folder, but it does not enable it.

## Manifest

Every Plug-In must include a `plugin.json` file:

```json
{
  "id": "example.vendor.mylaptop",
  "name": "My Laptop",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Adds optional readings for My Laptop firmware.",
  "assembly": "MyLaptopPlugIn.dll",
  "type": "Example.MyLaptopPlugIn"
}
```

Fields:

- `id`: Stable unique ID. Use lowercase reverse-domain style if possible, such as `com.example.mylaptop`.
- `name`: Friendly Plug-In name shown in Preferences.
- `version`: Plug-In version.
- `author`: Person or project responsible for the Plug-In.
- `description`: Short user-facing description.
- `assembly`: DLL file name inside the Plug-In folder.
- `type`: Fully qualified class name implementing `ISensorReadoutPlugin`.

Do not change `id` between versions unless you intentionally want Sensor Readout to treat it as a different Plug-In.

## SDK Reference

Plug-Ins reference:

```text
SensorReadout.PluginSdk.dll
```

The SDK namespace is:

```csharp
using SensorReadout.PluginSdk;
```

The main interface is:

```csharp
public interface ISensorReadoutPlugin
{
    PluginInfo Info { get; }
    IEnumerable<SensorReading> GetReadings(IPluginContext context);
}
```

`GetReadings` is called during Sensor Readout refresh. Keep it fast.

## Minimal Plug-In Example

```csharp
using System;
using System.Collections.Generic;
using SensorReadout.PluginSdk;

namespace Example
{
    public sealed class MyLaptopPlugIn : ISensorReadoutPlugin
    {
        public PluginInfo Info
        {
            get
            {
                return new PluginInfo
                {
                    Id = "example.vendor.mylaptop",
                    Name = "My Laptop",
                    Version = "1.0.0",
                    Author = "Your Name",
                    Description = "Adds optional readings for My Laptop firmware."
                };
            }
        }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (context.Machine.Manufacturer.IndexOf("Example", StringComparison.OrdinalIgnoreCase) < 0)
            {
                yield break;
            }

            yield return new SensorReading
            {
                Type = "Temperature",
                Hardware = "Example firmware",
                Name = "CPU",
                Identifier = "example/temperature/cpu",
                Value = 42,
                DisplayValue = "42.0 C",
                Source = "Example Plug-In"
            };
        }
    }
}
```

## Context

`IPluginContext` provides:

- `Machine.Manufacturer`: Windows computer manufacturer.
- `Machine.Model`: Windows computer model.
- `PluginDirectory`: The folder containing the Plug-In.
- `Log(level, message)`: Send diagnostics to Sensor Readout logging.

Use `context.PluginDirectory` when calling tools bundled with the Plug-In.

Use `context.Log("Debug", "...")` for diagnostic detail. Use `Error` only for actionable failures.

## Reading Fields

`SensorReading` supports:

- `Type`
- `Hardware`
- `Name`
- `Identifier`
- `Value`
- `DisplayValue`
- `Source`
- `Details`

### Type

Use existing Sensor Readout sections where possible:

- `Temperature`
- `Fan`
- `Performance`
- `Battery`
- `Network`
- `USB`

Avoid inventing new `Type` names unless the main app also knows how to display them.

### Hardware

Use the device or provider name, such as:

```text
Framework Control
Framework EC
HP OMEN firmware
```

### Name

Use a short reading name:

```text
CPU
Fan 1
Battery charge rate
```

### Identifier

This is critical. It must be stable because Sensor Readout uses it for saved hotkeys, hidden items, alarms, fan profiles, and labels.

Good:

```text
framework/control/fan/0/rpm
framework/control/temperature/cpu
```

Bad:

```text
fan-3467-rpm
cpu-43.2c
```

Never include changing values in an identifier.

### Value

Use `Value` for numeric readings. This lets Sensor Readout use progress meters, alarms, and comparisons.

Examples:

- Celsius temperatures: `42.5`
- Fan speed RPM: `1200`
- Percent values: `75`

### DisplayValue

Use a human-readable value:

```text
42.5 C
1200 RPM
75%
```

### Source

Use a source users can understand:

```text
Framework Control API
Framework EC ectool
```

### Details

Use `Details` for extra fields that belong in the details dialog and reports, but would make the tree too noisy.

```csharp
reading.Details["Firmware source"] = "Vendor API";
reading.Details["Raw value"] = rawValue;
```

## Performance Rules

Sensor Readout is designed to feel responsive with a screen reader. Plug-Ins must respect that.

- Keep normal refresh calls short.
- Use timeouts measured in milliseconds, not tens of seconds.
- Cache expensive calls inside the Plug-In.
- Do not query USB, WMI, HTTP, EC, or external processes repeatedly if the data is static.
- If a vendor service is not present, fail fast and wait before trying again.
- Prefer returning no rows over blocking the app.

## Safety Rules

- Prefer read-only data collection.
- Do not control fans, power, battery charging, or firmware unless the Plug-In is explicitly designed and documented for that.
- Never guess values. If a value cannot be read reliably, omit it.
- Do not assume a model is supported just because a manufacturer string matches.
- Keep writes behind explicit user action if write support is added in a future SDK.

## Error Handling

Throwing exceptions from `GetReadings` is allowed but discouraged. Sensor Readout catches Plug-In exceptions and logs them, but a Plug-In should still handle expected failures itself.

Good behavior:

- Vendor tool not installed: return no rows or one clear status row.
- Vendor API timed out: log debug detail and return cached rows or no rows.
- Unknown JSON shape: omit invalid rows and log debug detail.

## Updates and Custom Plug-Ins

Sensor Readout updates merge folders rather than deleting the whole app folder.

That means third-party Plug-Ins in their own folders should survive normal updates. A shipped Plug-In folder may be updated by Sensor Readout if the release contains files with the same names.

## Current Shipped Plug-In

Sensor Readout currently ships:

```text
Plug-Ins\Framework
```

It reads Framework Control API data when available and falls back to optional Framework EC helper tools if present.

Framework users should install the required Framework helper software before expecting Framework-specific readings.

## Future SDK Direction

The current 1.6.0 SDK is intentionally small. Future versions may add explicit APIs for:

- Writable fan controls.
- Fan curve providers.
- Plug-In settings panels.
- External-process Plug-Ins for stronger isolation.

Until then, Plug-Ins should focus on adding read-only sensor rows.
