# Sensor Readout Plug-In Conventions (for a new PlugIns/Corsair plug-in)

Repo: `C:/Claude/accessible-sensor-readout` (MIT, our own project — quoting is allowed).
All host quotes below are verbatim from the repo at commit `8c13534` (4.14.1).

---

## 0. The SDK surface (entire contract)

`src/PluginSdk/PluginSdk.cs` is the whole SDK — 52 lines:

```csharp
public interface ISensorReadoutPlugin
{
    PluginInfo Info { get; }
    IEnumerable<SensorReading> GetReadings(IPluginContext context);
}

public interface IFanControllablePlugin
{
    bool TrySetFanPercent(string identifier, int percent);
    bool TryResetFan(string identifier);
}

public interface IPluginContext
{
    MachineInfo Machine { get; }
    string PluginDirectory { get; }
    bool DiagnosticsMode { get; }
    void Log(string level, string message);
}
```

`SensorReading` fields: `Type, Hardware, Name, Identifier` (strings), `float? Value`, `DisplayValue, Source` (strings), `Dictionary<string,string> Details` (OrdinalIgnoreCase). `MachineInfo` has `Manufacturer` and `Model` from `Win32_ComputerSystem`.

The manager maps `SensorReading` 1:1 to the host's `SensorRow` (`SensorReadoutForm.PlugIns.cs`, `ToSensorRow`):
- `Source` falls back to the **manifest** `name` when blank: `Source = string.IsNullOrWhiteSpace(reading.Source) ? plugIn.Descriptor.Name : reading.Source`.
- Empty `Details` becomes `null` (row shows no details section).
- Everything else copies verbatim. There is no `WindowsSettingsUri` on `SensorReading` (host-only field).

Plug-in loading (`PlugInManager.EnsureLoaded`): manifests are `Plug-Ins/*/plugin.json` (one directory level only), loaded lazily on first refresh, `Assembly.LoadFrom(assemblyPath)`, `Activator.CreateInstance(type) as ISensorReadoutPlugin`. Duplicate ids are ignored with a warning. **Plug-ins always start disabled**; `IsEnabled` returns `false` unless `settings.PlugInsEnabled[id] == true`.

---

## 1. The fan-control contract

### 1.1 What makes a row a writable fan control

The host recognizes exactly the Type string `"Fan Control"`. Every consumer filters on it, e.g.:

```csharp
// SensorReadoutForm.FanControls.cs (UpdateFanControlBox)
var controls = latestRows
    .Where(r => r.Type == "Fan Control")
    .OrderBy(r => ControlSortKey(r.Identifier))
    .ToList();
```

and diagnostics (`SensorReadoutForm.Diagnostics.cs`, `RunFanDiagnostics`):

```csharp
var controls = (rows ?? new List<SensorRow>())
    .Where(r => r.Type == "Fan Control" && !string.IsNullOrWhiteSpace(r.Identifier))
```

Required fields on a control row: `Type = "Fan Control"`, a **non-empty, stable Identifier**, a `Name`, `DisplayValue`, `Source`, and usually `Details`. `Hardware` groups the rows (MSI uses `"MSI fan controls"`, Asus uses `"Fan controls"`).

CRITICAL: `CollectSensorRowsCore` (`SensorReadoutForm.FanControls.cs:383`) drops any row whose Type is not in this whitelist:

```csharp
.Where(s => s.Type == "Temperature" || s.Type == "Fan" || s.Type == "SMART" || s.Type == "Performance" || s.Type == "Battery" || s.Type == "Network" || s.Type == "Bluetooth" || s.Type == "Tasks" || s.Type == "USB" || s.Type == "Audio" || s.Type == "Display" || s.Type == "Devices" || s.Type == "Firmware Security" || s.Type == "Fan Control")
```

Use only `Temperature`, `Fan`, `Performance`, `Fan Control` (plus `Battery` etc. if genuinely relevant). Any invented Type silently disappears.

### 1.2 Value / DisplayValue semantics on control rows

The convention (both MSI and Asus): when a manual percent is active, `Value` = that percent and `DisplayValue` contains `"NN% ..."`; when automatic, `Value = null` and DisplayValue is descriptive text.

MSI (`MsiLaptopPlugIn.cs`, `MakeFanControlRow`):

```csharp
Value = isManual ? (float?)manualPercent : null,
DisplayValue = isManual ? manualPercent.ToString(CultureInfo.InvariantCulture) + "% manual test" : "automatic or firmware managed",
```

The host derives the control's percent with `ExtractPercent(SensorRow)` (`SensorReadoutForm.ReadingTree.cs:388`): it first parses the number immediately before the first `%` in `DisplayValue`; only rows whose Name contains "usage/load/..." fall back to `Value`. So **the percent must appear in DisplayValue as `NN%`** for the host to attach "1200 RPM 43%" to the matching Fan row (`AttachFanControlPercentsToFanRows`).

Mode-style controls: `FormatFanControlPercentOrMode` (`FanControls.cs:770`) checks whether any Details value contains the phrase "thermal mode"; if so, UI status says `"75% / performance mode"`. Only add that phrase to Details if the control is really a quiet/balanced/performance mode mapping (Asus pattern: 0-33 quiet, 34-66 balanced, 67-100 performance via `FanControlModeName`).

### 1.3 Identifier rules

- Identifiers are persisted in settings: fan labels (`FanLabels`), saved manual state (`FanControlSettings`), fan curves (`FanCurveSetting.FanControlKey`), fan profiles, hidden rows (`"row|" + RowSettingsKey(row)` where `RowSettingsKey = Type|Hardware|Name|Identifier`). Docs: "It must be stable because Sensor Readout uses it for saved hotkeys, hidden items, alarms, fan profiles, and labels." **Never embed changing values.**
- `IdentifierFromSettingsKey` (`Formatting.cs:94`) returns the text after the **last `|`** — so identifiers must not contain `|`.
- **Must NOT start with `/`.** `SetLibreHardwareMonitorControl` (`ReportsAndLogging.cs:577`):

```csharp
controlIdentifier = IdentifierFromSettingsKey(controlIdentifier);
if (TryPlugInFanControl(controlIdentifier, percent, manual))
{
    return;
}
if (!controlIdentifier.StartsWith("/", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Plug-in fan control did not accept the requested change: " + controlIdentifier);
}
// ...falls through to a real LibreHardwareMonitor sensor.Control.SetSoftware()/SetDefault()
```

  `/`-prefixed identifiers are LibreHardwareMonitor's namespace (`/lpc/nct6798d/control/0`). Plug-in identifiers use a vendor slug prefix: `msi/acpi/control/cpu`, `framework/control/fan/0/rpm`, `asus/...`. For Corsair use e.g. `corsair/<device>/control/0`.
- Fan-to-control pairing: `GuessControlIdentifier` replaces `"/fan/"` with `"/control/"`; `GuessFanIdentifier` does the reverse. `FanControlPercentForFanRow` first matches the control whose Identifier equals `GuessControlIdentifier(fanRow.Identifier)`, else matches `BaseFanControlName(control.Name) == BaseFanReadingName(fan.Name)` (name up to the first comma). **Emit paired identifiers** — `corsair/x/fan/0` (Type "Fan", RPM) and `corsair/x/control/0` (Type "Fan Control") — so the UI links RPM, labels, and "Show stopped" filtering automatically.
- `ShouldShowFanControl` hides a control unless it is a GPU control or the matching Fan row has RPM > 0. If a header can legitimately be at 0 RPM the user must tick "Show stopped"; nothing for the plug-in to do, but each control should have a matching Fan row where possible.
- `ControlSortKey` parses the integer after the last `/control/` for ordering (`.../control/0` sorts before `.../control/1`); non-numeric suffixes sort at 80000. Numeric suffixes give deterministic ordering.

### 1.4 How TrySetFanPercent / TryResetFan are invoked

`PlugInManager.TrySetFanControl` (`SensorReadoutForm.PlugIns.cs:115`):

```csharp
foreach (var plugIn in loaded.Where(p => p.Enabled && p.Instance is IFanControllablePlugin))
{
    var controllable = (IFanControllablePlugin)plugIn.Instance;
    var success = manual
        ? controllable.TrySetFanPercent(identifier, percent)
        : controllable.TryResetFan(identifier);
    ...
    if (success) { return true; }
}
return false;
```

Consequences:
- **Every** enabled fan-controllable plug-in gets offered **every** identifier. Return `false` fast (a dictionary/prefix check, no I/O) for identifiers you do not own — MSI's `TryGetFanTableSubfeature` and Asus's `TryGetCurveId` gate at the top before touching hardware.
- `true` means "accepted and applied"; the loop stops. `false`/exception falls through to the next plug-in and finally to LibreHardwareMonitor (or the thrown `InvalidOperationException` above, which surfaces as a status-bar error).
- Exceptions are caught and logged (`"Plug-In X fan control failed: ..."`) — but do your own try/catch and return false; that is the established style.

**Threading:** all writes come from `Task.Factory.StartNew` worker threads, never the UI thread:
- Manual UI actions: `RunFanAction(..., Task.Factory.StartNew(worker)...)` (`FanControls.cs:827`).
- Fan curves: `ApplyFanCurvesAsync` runs actions in `Task.Factory.StartNew(delegate { ... SetLibreHardwareMonitorControl(action.Key, action.Value, true); ... })` after every completed refresh.
- Saved manual settings re-applied once per app run on startup (`TryApplySavedFanControlsOnStartupAsync`, also `Task.Factory.StartNew`).
- Diagnostics (`RunFanDiagnostics`) sets **every visible control to 100% manual**, sleeps 1500 ms, then restores each to its saved percent/automatic. So writes must be safe, idempotent, and reversible.

Writes can run **concurrently with `GetReadings`** (which runs on a different Task). Guard shared state with your own lock — MSI uses `fanWriteLock` for writes plus per-field state, Asus wraps everything in one `stateLock`.

**Frequency / rate limiting done by the host:** fan curves change a control at most once per 10 seconds and only when the target percent moved by at least `MinimumChangePercent` (`FanCurves.cs:322-338`, `TimeSpan.FromSeconds(10)`). Manual actions are user-paced. Still keep a single write cheap (sub-second): the UI blocks its status line on the worker completing.

**"Automatic/default" semantics:** `TryResetFan` must return the fan to firmware/auto control, restoring pre-manual state when possible. The capture-original-state pattern (MSI, `EnsureFanSnapshot`): before the **first** manual write, read and store the original fan table and mode; `TryResetFan` writes those back, and only falls back to clearing the manual-enable bit when no snapshot exists. MSI's Details document it:

```csharp
{ "Safety", "Exposed only after the user enables the MSI Laptop Support plug-in. Original fan table and AP mode are captured before the first manual write and restored on automatic/default." }
```

After a successful write, both MSI and Asus do `manualPercents[identifier] = p; cachedRows.Clear();` (and `manualPercents.Remove` on reset) so the next `GetReadings` re-reads hardware and the control row's Value/DisplayValue reflect reality immediately (`RefreshSensorsAfterFanAction` triggers a refresh right after each action).

---

## 2. GetReadings: thread, cadence, host caching, timeout budget

### 2.1 Thread and call path

`RefreshSensors` → `Task.Factory.StartNew(() => CollectSensorRows(...))` — a **ThreadPool worker thread**, serialized by `lock (sensorCollectionLock)`. Inside, the plugin phase is:

```csharp
AddTimedRows(rows, "OemProviders", () => GetOemProviderRows(diagnosticsMode, backgroundRefresh), timings);
```

`GetOemProviderRows` → `GetPlugInRows(diagnosticsMode)` → `plugInManager.GetRows(...)` → each enabled plug-in's `GetReadings(context)` sequentially with a fresh `PlugInContext` per refresh. UI updates happen afterwards via `TaskScheduler.FromCurrentSynchronizationContext()`. **Never touch UI, never assume STA.** Exceptions from `GetReadings` are caught per plug-in (status becomes `"Failed: " + ex.Message`, logged as Error), and the plug-in is retried on the next refresh.

### 2.2 Cadence and host-side caching

- Refresh timer: `private const int RefreshIntervalMs = 5000;` default, user-configurable 1–300 s. Minimized/hidden refreshes are throttled (`HiddenAutoRefreshMinimumInterval = TimeSpan.FromSeconds(15)`).
- The host caches plug-in rows in `SensorReadoutForm.OemProviders.cs`:

```csharp
private static readonly TimeSpan ForegroundOemProviderRowsMinimumInterval = TimeSpan.FromSeconds(10);
private static readonly TimeSpan BackgroundOemProviderRowsMinimumInterval = TimeSpan.FromMinutes(5);
```

  So `GetReadings` is called at most every ~10 s foreground, every 5 min when minimized. The cache is invalidated when the enabled-plug-in set changes (signature of enabled ids) and **bypassed entirely in diagnostics mode**.
- Because the host already caches for 10 s, the plug-in's own cache exists to survive diagnostics bursts, fan-action refreshes (`RefreshSensorsAfterFanAction` clears nothing but re-runs collection), and to throttle genuinely expensive probes. Existing plug-in cache durations: MSI 30 s; Framework 5 s; Lenovo 5 s normal / 2 min diagnostics (`NormalCacheDuration` / `DiagnosticCacheDuration`); Huawei 30 s plus a 6-hour "SDK not installed" backoff.

### 2.3 Timeout budget

There is **no watchdog around the OemProviders phase** (only the LibreHardwareMonitor phase has `AddTimedRowsWithTimeout`, 2 s live / 20 s full). A slow plug-in stalls the entire refresh and the status line. Docs (`Plug-In-development.md`): "Keep normal refresh calls short. Use timeouts measured in milliseconds, not tens of seconds. ... Prefer returning no rows over blocking the app."

Concrete budgets used by shipped plug-ins:
- Framework HTTP probe: `request.Timeout = 200; request.ReadWriteTimeout = 200;` and a 60 s retry backoff when the API is absent.
- Framework `ectool` child process: `process.WaitForExit(2000)` then kill.
- Huawei helper process: `WaitForExit(3000)` then kill, `Task.WaitAll(..., 1000)` for output.
- Lenovo WMI: `searcher.Options.Timeout = TimeSpan.FromSeconds(5)` plus a static backoff map — missing WMI class → 6 h backoff, transient failure → 30 min (`MissingWmiProbeBackoff` / `FailedWmiProbeBackoff` in `LenovoThinkPadPlugIn.Helpers.cs`).

Also: gate on machine identity first (`IsMsiComputer`, `IsFrameworkComputer`, ...) using `context.Machine` so non-matching machines pay nothing. Return cloned copies of cached rows (`CloneReading` in every plug-in) so host-side mutation cannot corrupt the cache.

---

## 3. C# 5 language restrictions (legacy csc.exe)

`Build.ps1:9`: `$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'` — the in-box .NET Framework compiler. Maximum language version: **C# 5**. Everything C# 6+ is a hard compile error.

### DON'T (C# 6/7+ — will not compile)

- String interpolation `$"..."` → use `"..." + x` or `string.Format`.
- Null-conditional `?.` / `?[]` → explicit `x == null ? null : x.Y`.
- `nameof(...)` → string literals.
- Expression-bodied members `int X => 5;` / `public string F() => ...;` → full `{ get { return ...; } }` bodies (see every plug-in's `public PluginInfo Info { get { return info; } }`).
- Auto-property initializers `public int X { get; set; } = 5;` and getter-only auto-properties `{ get; }` → readonly fields or `{ get; private set; }` set in the constructor (Lenovo's `CandidateClass` does exactly this).
- `out var x` → declare first: `int value; if (int.TryParse(s, out value))` — this pattern is everywhere in the repo.
- Pattern matching (`if (x is Foo f)`, `case string s:`, `switch` expressions) → `var f = x as Foo; if (f != null)`.
- Tuples `(a, b)` and deconstruction → small sealed classes (`CommandResult`, `MsiFanSnapshot`, `CandidateClass`) or `KeyValuePair<,>` (fan curves use `List<KeyValuePair<string,int>>`).
- `using static` → fully qualify.
- Dictionary **index** initializer `new Dictionary<string,string> { ["k"] = "v" }` → the C# 3 collection form `{ { "k", "v" } }` (used throughout MSI).
- Local functions, `??=`, ranges `^`/`..`, `Span<T>`/`Memory<T>`, `in` parameters, readonly structs, default literals, discards `_`, `async Main`, string interpolation in any form.
- Exception filters `catch (X e) when (...)`.

### DO (C# 5 and earlier — matches existing style)

- `var`, LINQ, lambdas, anonymous `delegate { }` handlers, ternary, `??`, `params`, generics.
- Object/collection initializers: `new SensorReading { Type = "Fan", ... }`, `new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { { "Mode", "..." } }`.
- `async`/`await` compiles (C# 5 feature) — **but no existing plug-in declares an async method**. They use synchronous code, `Task.Factory.StartNew`, and at most `ReadToEndAsync()` + `Task.WaitAll(new Task[] { ... }, timeout)` (Huawei). Follow that: synchronous `GetReadings` with internal timeouts.
- P/Invoke is fine: AsusRog uses `DllImport`/`Marshal` (`System.Runtime.InteropServices`) against ATKACPI. `unsafe` is NOT used anywhere (and no `/unsafe` flag is passed — avoid).
- `CultureInfo.InvariantCulture` on every `ToString`/`Parse` of numbers.

### Available references (plug-in compile line, `Build.ps1:272-278`)

```powershell
$plugInReferences = @(
    'System.dll',
    'System.Core.dll',
    'System.Management.dll',
    (Join-Path $resources 'Newtonsoft.Json.dll'),
    $sdkOutput            # SensorReadout.PluginSdk.dll
) -join ','
```

So: WMI (`System.Management`), `HttpWebRequest` (System.dll), `Process` (System.dll), Regex, LINQ, JSON via `Newtonsoft.Json.Linq` (`JObject`/`JArray`). NOT available: `System.Net.Http`, WinForms, Drawing, `System.IO.Compression`, any NuGet package, `Microsoft.Win32.Registry` — wait, `Registry` lives in mscorlib (auto-referenced), so registry access IS possible. `kernel32`/`DeviceIoControl` P/Invoke needs no reference.

---

## 4. File layout and build integration

Plug-in compile loop (`Build.ps1:249-284`), key lines:

```powershell
foreach ($plugIn in Get-ChildItem -LiteralPath $plugInRoot -Directory) {
    $sourceFolder = Join-Path $plugIn.FullName 'src'
    ...
    $plugInSources = Get-ChildItem -Path $sourceFolder -Filter '*.cs' | Sort-Object Name | ...
    $plugInTarget = Join-Path (Join-Path $portable 'Plug-Ins') $plugIn.Name
    ...
    $plugInOutput = Join-Path $plugInTarget ($plugIn.Name + 'PlugIn.dll')
    ...
    & $csc /nologo /target:library /platform:x64 /out:$plugInOutput /reference:$plugInReferences @(@($plugInSources) + @($plugInAssemblyInfo))
```

Rules that follow:

1. **Folder = DLL name.** `PlugIns/Corsair/src/*.cs` compiles to `portable/Plug-Ins/Corsair/CorsairPlugIn.dll`. The manifest must say `"assembly": "CorsairPlugIn.dll"`.
2. **Flat src folder** — `-Filter '*.cs'` is non-recursive. No subdirectories under `src`. Files compile sorted by name.
3. **Partial-class split** for size (Lenovo pattern): `CorsairPlugIn.cs` (`public sealed partial class CorsairPlugIn : ISensorReadoutPlugin, IFanControllablePlugin` holding `Info`, `GetReadings`, control entry points) plus `CorsairPlugIn.Helpers.cs`, `CorsairPlugIn.Fans.cs`, etc., each `public sealed partial class CorsairPlugIn` in namespace `SensorReadout.CorsairPlugIn`.
4. **File size audit** (whole repo incl. PlugIns): warn at ≥2000 lines, **build fails at ≥3000 lines** per file (`Measure-SourceFileSize`, `Build.ps1:48-97`). Aim well under 2000; split with partials.
5. **Do not add AssemblyVersion/AssemblyTitle attributes** — the build injects a generated AssemblyInfo (`New-GeneratedAssemblyInfo`) carrying the app version; duplicates cause CS0579. `Assert-BinaryVersion` then requires every `*PlugIn.dll` / `*Helper.exe` to have FileVersion == app build version.
6. **plugin.json** at `PlugIns/Corsair/plugin.json`. Fields (loader reads exactly these, `ReadDescriptor`): `id, name, version, author, description, assembly, type, helpLinks[].label, helpLinks[].url`. `type` = fully qualified class name (`SensorReadout.CorsairPlugIn.CorsairPlugIn`). helpLinks must be http/https (`IsSafeHelpUrl`) and labels should carry a `&` accelerator. AsusRog manifest is the model:

```json
{
  "id": "sensorreadout.asus.rog.experimental",
  "name": "Asus ROG Support (experimental)",
  "version": "0.4.0",
  "author": "Jason Fayre, Claude Code, and Sensor Readout contributors",
  "description": "Experimental, opt-in ASUS laptop probe. ...",
  "assembly": "AsusRogPlugIn.dll",
  "type": "SensorReadout.AsusRogPlugIn.AsusRogPlugIn",
  "helpLinks": [ { "label": "&G-Helper page", "url": "https://g-helper.com/" } ]
}
```

   Id convention for bundled plug-ins: `sensorreadout.<vendor>.<qualifier>.experimental` (e.g. `sensorreadout.corsair.experimental`). Never change `id` between versions.
7. **NOTICE/LICENSE copying.** `Build.ps1:264-268` copies **all top-level files** in the plug-in folder (everything except `plugin.json`, which is copied explicitly) into the portable folder. AsusRog precedent for research-derived work: `NOTICE.txt` naming the upstream project, its license, the derivation ("This plug-in is based in part on ACPI constants and behavior documented in G-Helper"), the resulting license obligation ("distribute this plug-in under GPL-3.0-or-later"), and the statement "The main Sensor Readout application remains MIT-licensed. This notice applies to the optional ... plug-in." — plus the full license text file (`GPL-3.0.txt`) beside it. If Corsair work derives from a GPL project (e.g. OpenLinkHub/liquidctl protocol research), replicate this exactly; keep GPL-derived material out of `src/` for the core app.

---

## 5. Style conventions from existing plug-ins

- Namespace `SensorReadout.<Name>PlugIn`, single `public sealed class <Name>PlugIn` (or `sealed partial`), implementing `ISensorReadoutPlugin` (+ `IFanControllablePlugin` when controlling fans).
- `private readonly PluginInfo info = new PluginInfo { ... };` + `public PluginInfo Info { get { return info; } }`. `Info.Id` must equal the manifest id.
- First thing in `GetReadings`: machine gate via `context.Machine` (`IsMsiComputer` / `IsLenovoComputer` style, `ContainsAny(manufacturer, "MSI", "Micro-Star", ...)`). Lenovo shows the alternative when the plug-in is enabled but the machine doesn't match: return **one** status row ("Enabled, but this computer was not detected as Lenovo") instead of silence. MSI/Framework return `Enumerable.Empty<SensorReading>()`.
- Cache pattern: `cachedRows`/`cachedRowsUtc` guarded by a private `object cacheLock` (Lenovo/Huawei; MSI is lock-free on reads but single-threaded in practice), return `cachedRows.Select(CloneReading).ToList()`. `DiagnosticsMode` gets its own cache/duration when diagnostics collect much more (Lenovo: 5 s normal, 2 min diagnostics).
- Logging: `context.Log(level, message)`; levels used are `"Debug"` (almost everything — probe results, backoffs, write outcomes) and `"Error"` (actionable failures only, e.g. an exception during a fan write). Docs: "Use `Error` only for actionable failures." Null-guard the context (`private static void Log(IPluginContext context, string level, string message)` wrapper in MSI). Prefix messages with the vendor ("MSI fan write ...", "Asus ROG plug-in: ...").
- DisplayValue formats (InvariantCulture always):
  - Fans: `FormatNumber(roundedRpm) + " RPM"` → `"1200 RPM"` (whole numbers).
  - Temperatures: `"42.5 C"` — Framework uses `Format(Math.Round(value, 1), "0.0") + " C"`; MSI uses `"0.##"`. Plain `C`, no degree sign.
  - Control rows: `"57% manual test"` / `"automatic or firmware managed"`.
- `Value` is the raw number: Celsius for Temperature, RPM for Fan, percent for controls. Omit (`null`) when unknown — "Never guess values."
- `Details`: `new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)`; conventional keys: `"Mode"`, `"Interface"`, `"Safety"`, `"Source note"`, `"Namespace"`, `"WMI class"`, `"Formula"`, plus raw diagnostic dumps (`"MSI_ACPI fan raw bytes"`). MSI/Lenovo append a shared probe summary onto every row (`"MSI probe " + key`) so tester reports explain what the machine exposed.
- Zero-rows fallback: one status row `Type = "Performance", Hardware = "Overview", Name = "<Vendor> Plug-In"` with a human DisplayValue ("No extra MSI fan or temperature values found", "ectool.exe not found").
- `Source`: a constant like `"Corsair Support Plug-In"` (MSI: `"MSI Laptop Support Plug-In"`; Framework distinguishes `"Framework Control API"` vs `"Framework EC ectool"` per data path).
- Identifier slugs: lowercase, `/`-separated, vendor prefix, index-stable: `framework/ec/fan/0/rpm`, `msi/acpi/control/cpu`. For name-derived identifiers use a slug/hash helper (`Slugify` in Framework, `StableIdentifier` hashing in MSI/Huawei) — never raw changing values.
- Plain-English comments only where behavior is surprising (Asus's reset rationale comment); otherwise self-describing names, no regions.

---

## 6. Self-test and build enforcement gotchas

- **Self-test does not run your plug-in's GetReadings**, but two steps touch plug-in infrastructure (`SensorReadoutForm.SelfTest.cs`):
  - `SelfTestPlugInPreferenceIdentity` (line 1326) loads all manifests via `LoadPlugInPreferenceInfos`, so `plugin.json` must parse and have a unique non-empty `id`. It flips HP/Huawei checkboxes and asserts enable-state follows the **stable id**, not list position — a new plug-in just changes list ordering, which the test is specifically designed to tolerate.
  - `SelfTestBundledPlugInManifestRepair` (line 2156) exercises `Data/BundledPlugInHashes.json` repair. The build regenerates that manifest hashing **every file** under `portable/Plug-Ins` (`Build.ps1:384-398`), so a new Corsair folder is automatically included; nothing to hand-edit.
- **Diagnostics fan test** (`RunFanDiagnostics`) will set every `"Fan Control"` row your plug-in exposes to **100% manual**, wait 1.5 s, and restore. MSI's Details warn users about this ("Diagnostics may briefly set exposed controls to 100% and restore automatic/original state."). Your TrySetFanPercent/TryResetFan pair must round-trip safely.
- **x64 only**: `/platform:x64` on app, SDK, plug-ins, and helpers. P/Invoke signatures must be 64-bit correct.
- Version assert: plug-in DLL FileVersion must equal app version — again, don't add assembly attributes.
- Mnemonic-uniqueness self-tests apply to core UI menus, not plug-ins; only helpLinks labels carry `&` accelerators (pick letters unlikely to clash within the Help menu).
- Hard boundary from `Docs/Coding-agent-plug-in-rules.md`: for plug-in work, do not touch `src\SensorReadoutForm*.cs`, `src\Program*.cs`, `src\Models.cs`, `src\PreferencesForm*.cs`, `src\PluginSdk\PluginSdk.cs`, `Build.ps1`, `Release.ps1`, or shared language/manual/source-map files. Also: "Do not enable a plug-in by default", "Do not add write/control behavior unless tester diagnostics prove it works and restores safely", "Do not copy GPL or other incompatible implementation code into the MIT core app."
- Build from repo root: `powershell -File Build.ps1` (optionally `-SelfTest`). The plug-in is compiled after the main exe; a compile error in `PlugIns/Corsair/src` fails the whole build with "Plug-In build failed for Corsair".

---

## 7. Helper-process pattern (HuaweiMateBook precedent)

When vendor access is too risky to run in-process (loading vendor SDK DLLs, elevated device I/O), put a console program in `PlugIns/<Name>/helper/*.cs`. Build (`Build.ps1:286-299`):

```powershell
$helperSourceFolder = Join-Path $plugIn.FullName 'helper'
...
$helperOutput = Join-Path $plugInTarget ($plugIn.Name + 'Helper.exe')
...
& $csc /nologo /target:exe /platform:x64 /out:$helperOutput @(@($helperSources) + @($helperAssemblyInfo))
```

Notes: helper name is fixed (`CorsairHelper.exe` for us), `/target:exe` (console), x64, and **no `/reference:` list** — it gets only csc's default response-file references (System.dll, System.Core.dll, etc. — no Newtonsoft, no PluginSdk). It ships beside the plug-in DLL.

Plug-in side (`HuaweiMateBookPlugIn.cs`, `RunHelper`): resolve `Path.Combine(context.PluginDirectory, "HuaweiMateBookHelper.exe")`; if missing, report a status row. Launch with `UseShellExecute = false, CreateNoWindow = true`, redirected stdout/stderr, `WorkingDirectory = pluginDirectory`; `WaitForExit(3000)` then kill; read output via `ReadToEndAsync()` + `Task.WaitAll(..., 1000)`. Protocol is line-oriented `KEY=VALUE` on stdout: `STATUS=OK`, `SDK=<path>`, `FAN0=3200`, `TEMP0=42`, `ERROR=...`; exit code 0 + `STATUS=OK` means success, anything else discards parsed values. Sanity-clamp parsed values (`speed == 0 || speed >= 30000` rejected). Cache results (30 s) and back off hard (6 h) when the vendor SDK is absent so a missing dependency costs one probe per session, not one per refresh.

---

## Quick checklist for PlugIns/Corsair

1. `PlugIns/Corsair/plugin.json` — id `sensorreadout.corsair.experimental`, `"assembly": "CorsairPlugIn.dll"`, `"type": "SensorReadout.CorsairPlugIn.CorsairPlugIn"`, helpLinks with `&` accelerators.
2. `PlugIns/Corsair/src/CorsairPlugIn.cs` (+ `CorsairPlugIn.<Area>.cs` partials), C# 5 only, flat folder, each file < 2000 lines.
3. `PlugIns/Corsair/NOTICE.txt` (+ license text file) if protocol research derives from GPL or other-licensed projects.
4. Rows: Types only from the host whitelist; Fan rows `"<n> RPM"`; Temperature `"42.5 C"`; control rows `Type = "Fan Control"`, Identifier `corsair/<dev>/control/<i>` paired with `corsair/<dev>/fan/<i>`, `Value`=percent+`DisplayValue` `"NN% ..."` when manual, `Value=null`+"automatic..." otherwise.
5. `IFanControllablePlugin`: cheap ownership check, own lock, capture-original-state before first write, restore on `TryResetFan`, `cachedRows.Clear()` after successful writes, return false (never throw) on failure.
6. `GetReadings`: machine/device gate first, internal cache (~5-30 s), millisecond timeouts, backoff for missing hardware/services, `CloneReading` on cache reads, one Overview status row when nothing is found.
7. Build with `Build.ps1`; verify `portable/Plug-Ins/Corsair/CorsairPlugIn.dll` exists and self-test passes.
