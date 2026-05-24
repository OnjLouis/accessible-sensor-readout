# Sensor Readout

Current version: 3.9.0.

Sensor Readout is a Windows utility for reading hardware sensors, checking connected devices, creating support reports, and controlling supported fans with a keyboard-first, screen-reader-friendly interface.

It shows high-level categories on the left, readings grouped by device in a tree view on the right, and common commands in a standard menu bar. The goal is to make useful system information quick to reach without forcing blind users through inaccessible vendor tools or verbose Windows dialogs.

Project page: [https://github.com/OnjLouis/accessible-sensor-readout](https://github.com/OnjLouis/accessible-sensor-readout)

Contributor source overview: [SOURCE-MAP.md](SOURCE-MAP.md)

## Why Sensor Readout Exists

All that information about your own computer that used to be hard to find, hidden behind Device Manager or locked away in expensive or inaccessible apps, should be easier to reach.

Sensor Readout aims to bring as much of that information as possible into one easy-to-use, portable or installable, free app so you do not have to hunt for it. I was sick of the state of accessibility around the technical side of Windows computing, so I set out to get something made that I wanted to use. Apparently, many others want that too.

I hope you discover just as much about your computer with this app as I have about mine, and I hope you are pleasantly surprised by what you find.

My wish is that Sensor Readout becomes the gold standard in its class: an accessibility-first tool that blind and visually impaired people in tech support jobs can give to clients, so those clients can generate reports and get help troubleshooting issues. I strive for excellence, and I hope you feel that when you use it.

[Andre Louis](https://onj.me), creator of Sensor Readout.

## What It Does

- Reads temperatures, fan RPM, storage health, storage capacity, connected-device information, and selected hardware counters.
- Shows a Performance/Overview category for uptime, BIOS details, GPU details, CPU usage, CPU model/core/thread/cache information, memory usage, and storage read/write activity, grouped so related information stays together.
- Shows Windows printer information in Performance/Overview, including default printer, status, driver, port, configuration, queued jobs, and ink or toner percentages when the printer driver exposes them to Windows.
- Opens the main UI immediately while the first sensor refresh continues in the background.
- Shows a Network category for adapter status, IP addresses, link speed, send/receive rates, total traffic, and Wi-Fi details such as connection state, SSID, signal strength, RSSI, channel, frequency, radio type, link speeds, and security where Windows provides them.
- Shows network adapter MAC addresses and OUI vendor names when the bundled OUI data contains the prefix.
- Shows a USB category for connected devices, hubs, controllers, connection speed, power draw where Windows exposes it, drive letters, safe-to-unplug status, USB network adapter MAC details and storage hardware IDs where available, and detailed copyable device fields.
- Shows an Audio category grouped by device/interface, with playback and recording endpoints underneath, including vendor, status, direction, and default channel/sample-rate/bit-depth format where Windows exposes it.
- Shows a Display category for graphics adapters and monitors, including resolution, refresh rate, adapter memory, driver information, monitor vendor, and monitor identifiers where Windows exposes them.
- Links back to the project page from the README, Help menu, and About dialog.
- Uses bundled LibreHardwareMonitor libraries for sensor access.
- Uses the PawnIO driver for low-level motherboard sensors and fan controls where hardware support is available.
- Lets you label fan headers with friendly names.
- Hides stopped or unpopulated motherboard fan headers by default.
- Applies manual fan percentages to one selected fan or to all visible fans.
- Returns one fan or all fans to automatic/default control.
- Supports simple fan curves that set a fan control from a selected temperature reading.
- Supports fan profiles that apply several fan controls at once, with optional global hotkeys and optional toggle-back-to-automatic behavior.
- Saves TXT or HTML sensor reports.
- Opens saved report files directly, including TXT or HTML reports sent inside a ZIP file.
- Shows a Devices category with Windows PnP inventory, including PCI/system devices, storage controllers, input devices, Bluetooth, printers, security devices, and concise cross-linked entries for USB, audio, display, and network hardware.
- Puts devices with Windows problem codes, driver failures, or non-OK status into a clear Non-working devices group at the top of Devices, while keeping them in their normal hardware group too.
- Adds a Category summary row at the top of each main section, giving a quick count of readings, groups, rows with Details, and section-specific health signals where useful.
- Gives clearer empty-section guidance when a category has no visible readings, so users know whether to refresh, enable a plug-in, connect hardware, or check hidden items.
- Keeps the main tree readable while putting deeper technical fields behind Details, including PnP identifiers, driver data, CPU WMI fields, cache records, and memory module information where Windows exposes them.
- Lets Details users copy only matching lines from a detailed tree, useful when a support person asks for a specific driver, ID, or firmware field.
- Exports a portable copy ZIP of the current app, including settings, Plug-Ins, language files, custom sounds, and optional reports or logs.
- Can run one-click diagnostics from the Help menu or command line, collecting TXT and HTML reports, a debug log, sensor summaries, and a safe fan-control exercise into a ZIP file in `Reports`.
- Adds a support-readiness summary to diagnostics so support helpers can quickly see row counts, data sources, plug-in rows, detail coverage, fan-control rows, and empty key categories.
- Supports configurable automatic refresh.
- Defaults to a 5-second refresh interval on new configurations.
- Can run at Windows startup and start minimized to the notification area.
- Uses `Config\Shared.json` for portable preferences that should follow the app between machines, while keeping hardware-specific setup in `Config\<ComputerName>.json`.
- Saves preference changes as they are made, so hotkey, tray, hidden-item, and similar setup work survives crashes better.
- Writes diagnostic logs in `Logs` as `<ComputerName>.log` when logging is enabled.
- Can show temperatures in Celsius, Fahrenheit, Celsius then Fahrenheit, or Fahrenheit then Celsius.
- Supports optional user-defined global hotkeys for show/hide and speaking the notification area status.
- Can speak the notification area status through the active screen reader using bundled 64-bit Tolk screen reader library DLLs.
- Supports simple user-editable language files in the `Langs` folder.
- Logging is off by default and can be enabled from Preferences when troubleshooting.
- Can show selected readings in the notification area tooltip.
- Can run opt-in alarms for selected readings, speaking through the active screen reader and/or playing a chosen WAV file when a threshold is reached.
- Includes alarm presets for common cases such as low battery, Wi-Fi disconnect, weak Wi-Fi signal, high CPU/GPU temperature, disk health warnings, and printer problems when matching readings are available.
- Includes spoken hotkey presets for common starter profiles such as system status, network status, disk activity, GPU status, battery status, and fan/temperature status. Spoken hotkey profiles are listed by assigned key so configured shortcuts stay easy to scan.
- Includes a Help menu accessibility setup check for screen reader output, notification area speech, show/hide keys, report/log folder availability, and common startup-minimized pitfalls.
- Can play optional startup and shutdown sounds from the `Sounds` folder.
- Supports opt-in hardware plug-ins from the `Plug-Ins` folder for extra machine-specific hardware support. Installed plug-ins stay disabled until you enable them in Preferences.
- Enabled plug-ins can add related support pages to the Help menu, such as vendor utility pages for laptop-specific sensor support.
- Ships with optional plug-ins for selected laptop/vendor-specific hardware where useful community or vendor interfaces are available.
- Can import a plug-in ZIP from Preferences without automatically enabling it.
- Can show laptop battery charge, status, capacity, health, cycle count, voltage, and power rate where Windows exposes them.

## Prerequisites

- Windows 10 or Windows 11, 64-bit.
- Microsoft .NET Framework 4.8 or newer.
- PawnIO driver installed for motherboard sensors and fan controls.
- Administrator rights when reading motherboard sensors or controlling fans.

LibreHardwareMonitor is not required as a running app because this folder ships the needed library files. Installing the standalone app can still be useful for troubleshooting.

## Official Links

- [PawnIO](https://github.com/namazso/PawnIO)
- [PawnIO setup releases](https://github.com/namazso/PawnIO.Setup/releases)
- [LibreHardwareMonitor](https://librehardwaremonitor.net/)
- [LibreHardwareMonitor releases](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases)
- [Newtonsoft.Json](https://www.newtonsoft.com/json)
- [HidSharp](https://github.com/IntergatedCircuits/HidSharp)
- [DiskInfoToolkit](https://github.com/LibreHardwareMonitor/DiskInfoToolkit)
- [RAMSPDToolkit](https://github.com/LibreHardwareMonitor/RAMSPDToolkit)
- [BlackSharp.Core on NuGet](https://www.nuget.org/packages/BlackSharp.Core/)
- [Tolk screen reader library](https://github.com/dkager/tolk)
- [.NET Framework install notes](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)

## Prerequisite Installer

Run `Install-Prerequisites.cmd` from this folder first. It calls `Install-Prerequisites.ps1`, asks for administrator rights if needed, and installs PawnIO. It tries winget first, then Chocolatey if it is already installed, then downloads the official PawnIO.Setup release from GitHub if neither package manager is available. When winget exists, it can also install .NET Framework Runtime.

To also install the standalone LibreHardwareMonitor app with winget for troubleshooting, run:

```powershell
.\Install-Prerequisites.ps1 -IncludeLibreHardwareMonitor
```

## Manual Install Commands

If the prerequisite installer does not work on your system, open Windows Terminal or PowerShell as administrator and run:

```powershell
winget install --id Microsoft.DotNet.Framework.Runtime -e --source winget
winget install --id namazso.PawnIO -e --source winget
```

Optional troubleshooting tool:

```powershell
winget install --id LibreHardwareMonitor.LibreHardwareMonitor -e --source winget
```

## First Run

1. Start `Sensor Readout.exe`.
2. Accept the Windows administrator prompt.
3. If PawnIO is missing, Sensor Readout offers to run the prerequisite installer.
4. Press `F5` to refresh if values are still loading.
5. Open `Options` > `Fan controls...` or press `Ctrl+L` if you want to adjust fan control values.

If motherboard fans or controls are missing, check that PawnIO is installed and that the app was started as administrator.

You can rerun the prerequisite installer later from `Help` > `Install prerequisites...`.

For support, use `Help` > `Run diagnostics...`. It creates a diagnostic ZIP in the `Reports` folder, opens that folder, and can optionally speak each step and play sounds at the start and end of the test. For unattended testing, `--diagnostics [path]` creates the same ZIP and exits.

## Accessibility Setup

Use `Help` > `Check accessibility setup...` after first run or after changing startup and hotkey preferences. It checks whether Sensor Readout can reach screen reader speech, whether notification-area speech is configured, whether report and log folders can be opened, and whether a minimized startup setup could leave you without an obvious way back to the main window.

If you use `Start minimized to notification area`, set a show/hide hotkey in `Options` > `Preferences` > `Hotkeys`. Without that key, Sensor Readout can still be opened from its notification-area icon, but Windows may hide new tray icons until you allow them in Windows notification area or system tray settings. A show/hide hotkey gives keyboard and screen-reader users a reliable way to bring the main window back.

The same Hotkeys tab can set a key for `Speak notification area status`. That key reads the configured notification-area readings without opening the main window, and it is often the fastest way to check important values such as memory, temperatures, uptime, network status, or GPU memory.

## Keyboard Shortcuts

| Shortcut Group | Shortcut | Action |
| --- | --- | --- |
| Main window | `F5` | Refresh current readings. |
| Main window | `F3` | Find a reading across all categories. Type to narrow results, Tab to the list, Enter to choose, Esc to close, or Alt+L to clear. |
| Main window | `F4` | Review the selected reading or tree branch in a read-only text box. |
| Main window | `Enter` / `Alt+Enter` | Open Details for the selected reading when extra details are available. |
| Main window | `Ctrl+C` | Copy the selected reading or tree branch. |
| Main window | `Ctrl+Shift+C` | Copy value only: copy the selected reading value without the reading name. On a branch, copies one value per reading. |
| Main window | `F2` | Rename the selected fan reading, edit the selected spoken label in Preferences, or jump to the fan label field in Fan Controls. |
| Main window | `Del` | Hide the selected reading or tree branch. |
| Main window | `Ctrl+Shift+H` | Add the selected reading to notification area status or a spoken hotkey profile, remove it again, or create a spoken hotkey profile from the same dialog. |
| Main window | `Ctrl+Shift+G` | Add or remove the selected reading from the optional reading history CSV log. |
| Main window | `Ctrl+Shift+Right` / `Ctrl+Shift+Left` | Expand or collapse the full reading tree for the current category. |
| Main window | `Ctrl+0` to `Ctrl+9` | Switch directly to a main reading category. |
| File commands | `Ctrl+S` | Save a report. |
| File commands | `Ctrl+O` | Open a saved Sensor Readout report as a static view. |
| File commands | `Ctrl+Shift+M` | Compare two saved Sensor Readout reports. |
| File commands | `Ctrl+Shift+A` | Save an anonymized report for sharing. |
| File commands | `Ctrl+R` | Return from a static report to live readings. |
| File commands | `Ctrl+E` | Export selected settings and profiles to a transfer package. |
| File commands | `Ctrl+Shift+E` | Export a portable copy ZIP of the current app and settings. |
| File commands | `Ctrl+I` | Import selected settings and profiles from a transfer package. |
| File commands | `Ctrl+Shift+I` | Import a Plug-In ZIP. |
| File commands | `Ctrl+Shift+O` | Open the Reports folder. |
| File commands | `Ctrl+Shift+L` | Open the Logs folder. |
| Tools and help | `Ctrl+L` | Open fan controls. |
| Tools and help | `Ctrl+U` | Open fan curves. |
| Tools and help | `Ctrl+,` | Open Preferences. |
| Tools and help | `F1` | Open the manual. |
| Tools and help | `Shift+F1` | Check GitHub Releases for a newer version, check PawnIO, and offer update installation when available. If quiet update installs are enabled, an available update installs without showing release notes first. |
| Tools and help | `Ctrl+F1` | Open the project page. |
| Tools and help | `Alt+F1` | Run diagnostics and create a support ZIP. |
| Fan controls dialog | `Esc` | Close the Fan Controls dialog. |
| Fan controls dialog | `Alt+L` | Save the label for the selected fan control. |
| Fan controls dialog | `Alt+M` | Apply the manual percentage to the selected fan control. |
| Fan controls dialog | `Alt+A` | Return the selected fan control to automatic/default. |
| Fan controls dialog | `Alt+R` | Return all fan controls to automatic/default. |
| Fan controls dialog | `Alt+7` | Set all visible fan controls to 75%. |
| Fan controls dialog | `Alt+X` | Set all visible fan controls to maximum. |
| Fan controls dialog | `Alt+S` | Show or hide stopped fan headers. |
| Fan controls dialog | `Alt+P` | Pause automatic updates. |
| Details dialog | `Ctrl+M` | Copy only detail lines matching search text. |
| Details dialog | `Ctrl+C` / `Ctrl+Shift+C` | Copy the selected Details branch, or copy value-only lines from the selected branch. |
| Details dialog | `F3` / `F4` | Find within Details, or review the selected Details branch in a read-only text box. |
| Details dialog | `Ctrl+Shift+Right` / `Ctrl+Shift+Left` | Expand or collapse all Details groups. |
| Preferences | `Ctrl+1` to `Ctrl+8` | Jump to General, Startup and Install, Hotkeys, Fan profiles, Alarms, Plug-Ins, Hidden items, or Language editor. |
| Preferences | `F3` | Find a reading or fan control before adding it to notification area status, a spoken hotkey, or a fan profile. |
| Preferences | `F2` | Jump to the name or rename field where applicable. |
| Preferences | `Enter` | Jump to the main value field where applicable. |
| Preferences | `Ctrl+Right` | Add the selected available reading to the tray order or selected spoken hotkey. |
| Preferences | `Ctrl+Left` | Remove the selected tray or spoken-hotkey reading. |
| Preferences | `Ctrl+Up` / `Ctrl+Down` | Move the selected tray or spoken-hotkey reading earlier or later. |
| Preferences | `Alt+I` | In Startup and Install, install to this PC. |
| Preferences | `Alt+U` | In Startup and Install, uninstall from this PC when running from the installed copy. |
| Preferences | `Alt+I` | In Plug-Ins, import a plug-in ZIP. |
| Preferences | `Alt+N` | In Hotkeys, create a new spoken hotkey profile. |
| Preferences | `Alt+I` | In Hotkeys, import spoken hotkey profiles from another machine config. |
| Preferences | `Alt+P` | In Hotkeys or Alarms, open presets. |
| Preferences | `Alt+R` | In Hotkeys, remove the selected spoken hotkey profile. |
| Preferences | `Alt+A` | In Hotkeys, add the selected reading to the selected spoken hotkey. |
| Preferences | `Alt+M` | In Notification area or Hotkeys, remove the selected reading. |
| Preferences | `Alt+U` / `Alt+W` | Move the selected spoken-hotkey reading up or down. |
| Preferences | `Alt+R` | Rename the selected spoken label for notification area status or a spoken hotkey. |
| Preferences | `Alt+D` | Reset the selected spoken label to default. |
| Preferences | `Delete` | Remove the selected tray reading, spoken hotkey profile, spoken hotkey reading, or alarm where applicable. |

## Command-Line Options

Sensor Readout can also be started with a few command-line options:

| Option | Action |
| --- | --- |
| `--minimized` or `--tray` | Start minimized to the notification area. |
| `--close` | Close any running Sensor Readout instance and exit. |
| `--report-txt [path]` | Save a text report and exit. If no path is supplied, a timestamped report is created in the `Reports` folder. |
| `--report-html [path]` | Save an HTML report and exit. If no path is supplied, a timestamped report is created in the `Reports` folder. |
| `--anonymized-report-txt [path]` | Save an anonymized text report and exit. If no path is supplied, a timestamped report is created in the `Reports` folder. |
| `--anonymized-report-html [path]` | Save an anonymized HTML report and exit. If no path is supplied, a timestamped report is created in the `Reports` folder. |
| `--compare-reports before after [output]` | Compare two Sensor Readout reports and save the comparison text. If no output path is supplied, a timestamped comparison is created in the `Reports` folder. |
| `--diagnostics [path]` | Run diagnostics, save a ZIP, and exit. If no path is supplied, a computer-named timestamped ZIP is created in the `Reports` folder. If a folder is supplied, the ZIP is created there. |
| `--diagnostics-quiet` | Do not speak diagnostic progress or play diagnostic sounds when used with `--diagnostics`. |
| `--no-diagnostics-speech` | Run command-line diagnostics without spoken progress, while keeping diagnostic sounds enabled if preferences allow them. |
| `--no-diagnostics-sounds` | Run command-line diagnostics without start or completion sounds, while keeping spoken progress enabled if preferences allow it. |
| --log off\|error\|normal\|debug | Set the logging level before continuing. |

## Preferences

Use `Options` > `Preferences` or `Ctrl+,` to configure Sensor Readout. The dialog remembers the tab you used last during the current session. Press `Esc` or the Close button to leave Preferences.

### General (`Ctrl+1`)

The General tab controls the main reading experience.

- Language: choose one of the installed language files. The interface refreshes immediately after a language change.
- Automatic refresh: turn background refresh on or off.
- Refresh while Sensor Readout has focus: choose whether readings continue updating while you are navigating the main window.
- Refresh interval: set the normal refresh interval in seconds.
- Show status in notification area: show or hide the tray icon and its tooltip.
- Show as many readings as possible in notification area tooltip: use the longer Windows tooltip text where available; turn this off if you prefer long tray tooltips to show only `Sensor Readout`.
- Temperature unit: choose Celsius, Fahrenheit, Celsius then Fahrenheit, or Fahrenheit then Celsius.
- Decimal separator: use the language default, period, or comma.
- Logging level: Off, Error, Normal, or Debug.
- Update checks: choose whether Sensor Readout checks GitHub Releases at startup, hourly, every 6 or 12 hours, daily, weekly, or never.
- Quiet update installs: download, install, close, and reopen automatically when an update is available. Normal update installs ask before closing unless you clear that confirmation from the update prompt or turn it off in `Hidden items`. Use `Help` > `Version history...` to review the latest release notes later.
- Notification area items: choose up to eight readings for the tray tooltip and spoken tray status.

When notification area status is enabled, minimizing Sensor Readout hides it from the taskbar and Alt+Tab list. Open it again from the notification area icon. `Alt+F4` exits the app completely.

Notification area readings are selected from an Available readings list and moved into a Tray order list. A reading appears in only one list at a time. Use Add, Remove, Up, and Down, or `Ctrl+Right`, `Ctrl+Left`, and `Ctrl+Up` / `Ctrl+Down`, to choose exactly which readings appear first. Available readings are listed as device first, then reading name and category, such as `Ethernet - Rx: Network`, so type-ahead can jump to a device name. Sensor Readout uses shortened tray labels such as `CPU`, `GPU`, `Rx`, and `Tx`.

### Startup and Install (`Ctrl+2`)

The Startup and Install tab controls installation plus what happens when Sensor Readout starts and exits.

- Install to this PC: copy the current portable folder to the Windows programs folder for this user, optionally create a desktop shortcut and enable Windows startup, close the current copy, and start the installed copy.
- Uninstall from this PC: when running from the installed copy, remove the installed app files and shortcuts. The uninstall dialog can also remove `Config`, `Logs`, and `Reports` when you want a complete cleanup.
- Run at Windows startup: create or remove a Windows logon task for the current user. Sensor Readout uses a scheduled task so it can start reliably with the administrator rights it needs for hardware access.
- Create desktop shortcut: create or remove a `Sensor Readout.lnk` shortcut on the desktop.
- Start minimized to notification area: open directly to the tray instead of showing the main window.
- Startup speech: choose whether Sensor Readout speaks when it starts and edit the spoken message.
- Startup and shutdown sounds: choose WAV files from the `Sounds` folder.
- Diagnostics feedback: choose whether diagnostics speak progress and play start/completion sounds.

The install flow is for people who started from a portable or synced folder but want Sensor Readout in the normal programs location on this PC. It copies the app and existing settings, reports, logs, language files, sounds, data, docs, and plug-ins. During installation, you can choose whether to add a desktop shortcut and whether Sensor Readout should run at Windows startup. If Run at Windows startup is enabled, Sensor Readout creates a Windows logon task for the installed copy.

If startup is enabled, Sensor Readout also enables start-minimized behavior so configured tray readings are available after sign-in without leaving the main window in Alt+Tab.

If Sensor Readout restarts after install or update and the main window does not appear, check whether Start minimized to notification area is enabled. Windows may also hide the tray icon until you allow it in Windows notification area or system tray settings. Showing the Sensor Readout tray icon lets you open the main window from that icon.

### Hotkeys (`Ctrl+3`)

The Hotkeys tab controls global speech and visibility keys. Spoken hotkey profiles are sorted by assigned key, with unassigned profiles after assigned profiles.

- Show/hide hotkey: toggle the main window from anywhere.
- Speak notification area status hotkey: speak the configured tray readings.
- Include device names in spoken feedback: choose between fuller output such as `Ethernet Rx` and shorter output such as `Rx`.
- Double-press copy timeout: copy the same spoken output to the clipboard when a speech hotkey is pressed twice quickly.
- Spoken hotkey profiles: create extra global hotkeys, each with its own name, key combination, and ordered reading list.
- Spoken hotkey presets: create starter profiles with useful reading groups. Presets are created without key assignments, so assign the key you want before using them. Use `Alt+P` for Presets here and in the Alarms tab.
- Spoken labels: rename selected readings for shorter speech, such as changing `Receive rate` to `Rx`.

Spoken hotkey profiles can be imported from another machine's `Config\<ComputerName>.json`. Imported profiles do not keep their old key assignments, so they cannot steal keys already used on the current machine. Readings are kept only when Sensor Readout can match them safely on the current computer.

### Fan Profiles (`Ctrl+4`)

The Fan profiles tab builds named groups of fan actions. The layout matches the Hotkeys tab: create a profile, assign an optional global hotkey, add fan controls to the profile, then choose whether each fan should be set to a manual percentage or returned to automatic/default control.

Fan profiles can be toggles. With that option enabled, pressing the profile hotkey once applies the profile, and pressing the same hotkey again returns those fans to automatic/default control. Each profile can speak a custom message and/or play a chosen sound when it is applied.

### Alarms (`Ctrl+5`)

The Alarms tab lets you monitor readings without watching the main window.

- Use `Presets...` to add common alarms for available readings, such as low battery, Wi-Fi disconnect, weak Wi-Fi signal, high CPU or GPU temperature, disk health warnings, or printer problems.
- Choose a reading.
- Choose Above or equal, Below or equal, or Equal.
- Set the threshold and unit.
- Set a cooldown so repeated alarms do not fire too often.
- Choose whether the alarm speaks, plays a sound, or both.

Alarms are best for values that naturally change, such as temperature, fan speed, CPU load, disk activity, battery charge, or network speed. Static information such as BIOS version is usually not useful as an alarm.

### Plug-Ins (`Ctrl+6`)

The Plug-Ins tab lists installed hardware plug-ins and describes what each one does. Plug-ins are trusted code, so only enable plug-ins from people or projects you trust. Imported plug-in ZIP files are copied into the `Plug-Ins` folder but stay disabled until you enable them yourself.

### Hidden Items (`Ctrl+7`)

The Hidden items tab restores readings or groups hidden from the main window. Checked items are hidden. Clear a checkbox to make that item visible again. It also includes confirmation preferences for update installs and spoken hotkey profile removal, so you can bring those safety prompts back after choosing not to show them again.

### Language Editor (`Ctrl+8`)

The Language editor tab edits installed language files without opening a separate text editor. English is the primary fallback language. Missing translated strings fall back to English, and corrupt language files are skipped rather than crashing the app.

## Alarms And Sounds

Preferences > Alarms lets you create reading alarms. Choose a reading, set Above or equal, Below or equal, or Equal, then choose the threshold and cooldown. Each alarm can speak through the active screen reader, play a WAV file, or both.

The `Presets...` button offers practical starter alarms when matching readings exist on the current machine. Presets start unchecked so you can choose only the ones you want. Check the threshold, sound, and cooldown before relying on them.

Preferences > Startup and Install includes optional startup and shutdown sound choices. Sensor Readout loads WAV files from the `Sounds` folder. The bundled sounds use neutral names such as `SR01.wav`, `SR02.wav`, and so on, but user-added sounds can use any normal `.wav` file name. Any sound in the folder can be selected anywhere Sensor Readout offers a sound, including alarms, startup or shutdown, diagnostics, update alerts, and fan profile actions.

## Plug-Ins

Preferences > Plug-Ins lists installed hardware plug-ins and describes what each one does. Plug-ins are trusted code, so only enable plug-ins from people or projects you trust.

Installed plug-ins are disabled by default. Importing a plug-in ZIP copies it into the `Plug-Ins` folder but still leaves it disabled until you check it yourself.

When an enabled plug-in includes related support pages, those links appear in the Help menu. For example, the bundled laptop plug-ins can show the Dell Command | Monitor, G-Helper, HP Support Assistant, OMEN Gaming Hub, Lenovo Vantage, Lenovo System Update, Framework BIOS and Drivers, or Framework Control pages.

The bundled Framework Laptop plug-in is optional and disabled by default. It can add Framework-specific temperature and fan RPM rows when Framework Control is installed and running.

Experimental HP, Dell Latitude, Lenovo Laptop, MSI Laptop, and Asus Laptop Support plug-ins are also bundled for opt-in tester feedback. They are disabled by default. The Lenovo plug-in is read-only and probes Lenovo fan WMI, Windows fan, ACPI thermal-zone, and Lenovo WMI interfaces. The MSI plug-in can expose MSI ACPI fan-table controls on compatible models after the user enables it. The Asus plug-in is based in part on G-Helper ACPI research and includes its own GPL notice in its plug-in folder.

For developers, the GitHub source tree includes `Docs\Plug-In-development.md`.

## Reading Sensors

The readings pane is a tree view. Choose a category from the list on the left, then review that category's readings and details on the right. Categories group readings by device or purpose first, then list individual readings underneath, so screen readers do not have to announce a long device name before every value.

Each category starts with a Category summary row. Open Details on that row to see a compact count of groups, readings, numeric values, rows with Details, and section-specific items such as non-working devices or storage health rows.

Use the left section list to move between broad areas. This changes the view only; it does not enable, disable, or permanently select devices.

Sensor Readout opens on Performance/Overview by default. There is no combined all-readings page; each reading belongs to its own section so navigation stays predictable and device names are not repeated endlessly.

If a category has no visible readings, Sensor Readout now gives a category-specific hint instead of a generic blank tree. Depending on the section, it may suggest refreshing, connecting hardware, enabling a plug-in, checking hidden items, or waiting for slow Windows-backed inventory data.

The Performance section summarizes live system counters and storage activity. It groups CPU usage with CPU model, vendor, core/thread count, clock, socket, architecture, instruction sets such as SSE and AVX, and virtualization information, then groups memory and storage activity by device.

Windows reports hardware virtual-machine memory translation as SLAT. Intel documentation often calls the same class of feature EPT, while AMD documentation often calls it NPT or RVI. Sensor Readout spells this out as `CPU hardware VM memory translation (SLAT/EPT/NPT)` so the reading is less cryptic.

The Network section shows each adapter under one common tree, including status, IP address, link speed, send and receive rates, and total traffic counters. Wi-Fi adapters can also show connection state, network name, access point, signal strength, RSSI in dBm, channel, frequency, radio type, receive/transmit link speeds, and security details where Windows provides them. Wi-Fi connection state, signal, RSSI, channel, frequency, and link speeds can be used in notification area status, spoken hotkeys, and alarms where numeric thresholds make sense.

The USB section shows connected devices, hubs, controllers, connection speed, capable speed where Windows exposes it, requested power, drive letters, safe-to-unplug status, USB network adapter MAC address/OUI vendor and storage hardware ID/OUI vendor where available, and other copyable details. USB data is partly dependent on what Windows and the device driver expose, so some devices may report less detail than others.

The Audio section groups related endpoints under their device or interface name where possible, then separates playback and recording entries. It shows vendor, status, direction, and default format details such as channels, sample rate, and bit depth where Windows exposes them.

The Display section shows graphics adapters and monitors, including adapter memory, resolution, refresh rate, driver details, monitor vendor, product code, serial, and manufacture date where Windows exposes them.

When the selected reading is a percentage, Sensor Readout also exposes it through the progress bar below the tree. This is useful visually and lets screen readers use their existing progress bar feedback without navigating many separate progress controls.

Use `Edit` > `Find reading...` or `F3` to search readings across all categories. The search narrows as you type. Tab to the results list, press Enter to move to the selected reading, press `Alt+L` to clear the search, or press `Esc` or Close to return to the main window.

Use the `Edit` menu, Application key, or right-click on a reading or group to copy it, review the exact text in a read-only edit box, open Details where available, rename a fan, or hide it. In Details, `Copy matching...` or `Ctrl+M` asks for search text and copies only matching lines from the detailed tree. Hidden items can be restored from `Options` > `Preferences` > `Hidden items`; checked items in that tab are hidden.

## Fan Control Workflow

Open fan controls from `Options` > `Fan controls...` or press `Ctrl+L`.

1. Choose a fan in the fan control target box.
2. Optional: type a friendly label and press `Alt+L`.
3. Type a percentage from 0 to 100.
4. Press `Alt+M` to apply it to the selected fan.
5. Press `Alt+A` to return the selected fan to automatic/default.
6. Press `Alt+R` to return all visible fan controls to automatic/default.

The all-fan buttons apply only to visible fan controls. Stopped or unpopulated motherboard headers are hidden unless `Show stopped` is enabled.

Fan labels are saved in the machine-specific configuration file in `Config`. Labels only change friendly names shown in Sensor Readout.

## Fan Curves

Open fan curves from `Options` > `Fan curves...`, press `Ctrl+U`, or open them from the Fan Controls dialog.

Each curve chooses one writable fan control and one temperature reading. Sensor Readout sets the fan to the low percentage at the low temperature, ramps between the low and high points, and uses the emergency percentage once the emergency temperature is reached. Fan curves depend on LibreHardwareMonitor exposing a writable control; if the hardware only exposes a fan RPM reading, Sensor Readout can read it but cannot control it.

## Fan Profiles

Preferences > Fan profiles lets you build named groups of fan actions. The layout matches the Hotkeys tab: create a profile, assign an optional global hotkey, add fan controls to the profile, then choose whether each fan should be set to a manual percentage or returned to automatic/default control.

Fan profiles can also be marked as toggles. With that option enabled, pressing the profile hotkey once applies the profile, and pressing the same hotkey again returns the fans in that profile to automatic/default control.

Each fan profile can speak when it is applied, play a sound, both, or neither. The spoken message can be customized, and `{0}` is replaced with the profile name.

Useful examples:

- Everyday: set case fans to a quiet fixed percentage and leave the CPU fan on automatic.
- Gaming or rendering: set case, CPU, and GPU fan controls higher before starting a heavy workload.
- Reset to automatic: create one profile that returns every writable fan control to automatic/default control.

Fan profiles are machine-specific because fan control keys depend on the hardware in the current computer. New installs start with a few empty starter profiles, such as everyday use, heavier workloads, and reset to automatic. Add the fans that make sense on your machine, rename the profiles if needed, or delete the starters.

If a fan curve is enabled for the same fan, Sensor Readout temporarily pauses that curve while a manual fan action or profile controls the same fan. Returning the fan to automatic/default control resumes the curve.

## Example Setups

- Quick status hotkey: create a spoken hotkey for CPU usage, CPU temperature, GPU temperature, and the most important network rates.
- Disk activity hotkey: create a spoken hotkey for read and write rates on the drives you care about most, then double-press it when you want the same values copied to the clipboard.
- Quiet desktop: use a fan curve for the CPU fan, a quiet fan profile for case fans, and an alarm that warns if CPU or GPU temperature rises above your chosen limit.
- Troubleshooting USB: open the USB category, press Enter on a device for details, select the useful fields, and copy them for sharing.

## Diagnostics

Use `Help` > `Run diagnostics...` or press `Alt+F1` when you need to collect support information. Sensor Readout creates a ZIP file in `Reports` with the computer name in the file name, opens the folder when finished, and removes the temporary staging folder after the ZIP is created.

The diagnostic ZIP contains a TXT report, an HTML report, a debug log, a diagnostic summary, and timing information. The run briefly tests writable fan controls at 100%, then restores each fan to the previous manual, automatic/default, or fan-curve state.

The diagnostic summary includes a support-readiness section with row counts, data sources, plug-in rows, rows with Details, fan-control rows, non-working devices, and empty key categories. This is meant to help support helpers quickly decide whether a report contains enough data before asking the user for another run.

Diagnostics can speak progress and play start/completion sounds. Configure this in `Options` > `Preferences` > `Startup and Install`. Command-line diagnostics use the same preferences unless you pass `--diagnostics-quiet`, `--no-diagnostics-speech`, or `--no-diagnostics-sounds`.

For unattended testing, run `Sensor Readout.exe --diagnostics [path]`. If `[path]` is a folder, the diagnostic ZIP is created there. If no path is supplied, Sensor Readout creates the ZIP in `Reports`.

## Reports

Reports are useful in two different situations: keeping a snapshot of your own machine, or sending a snapshot to someone else so they can inspect it in Sensor Readout.

### Saving A Report

Press `Ctrl+S` or use `File` > `Save report...`.

The save dialog offers:

- TXT report.
- HTML report.

TXT reports group values by reading type and device, so long hardware names are written once as headings instead of being repeated on every line. HTML reports use formatted tables with sensor, value, and source columns.

The first save creates the `Reports` folder if it does not already exist. Use `File` > `Open Reports folder` or `Ctrl+Shift+O` to jump to saved reports, and use `File` > `Open Logs folder` or `Ctrl+Shift+L` when support needs the log folder. To share a report, send the saved `.html` or `.txt` file. HTML is usually easier to read in a browser, while TXT is convenient for pasting into messages. If someone needs a fuller support bundle, use `Help` > `Run diagnostics...` instead; diagnostics include both report formats, logs, and a summary in one ZIP file.

Use `File` > `Save anonymized report...` or `Ctrl+Shift+A` when you want a shareable report with common private identifiers masked. The anonymized report replaces the computer name and masks IP addresses, MAC addresses, serial numbers, UUIDs, GUIDs, PnP IDs, hardware IDs, compatible IDs, and location paths where Sensor Readout recognises them.

### Opening Someone Else's Report

Press `Ctrl+O` or use `File` > `Open report...` to load a saved Sensor Readout TXT or HTML report. If someone sends a report inside a ZIP file, open the ZIP directly and Sensor Readout will use the first readable report inside it.

The report opens as a static snapshot in the normal category and tree layout, so you can inspect another user's machine as if it were your own current view. Static report mode does not refresh live values, run alarms, or control hardware. It is only a viewer for the data saved in the report.

Notification-area status and spoken hotkeys read from the loaded report while a report is open. Sensor Readout announces that the values are from a static report, and if a configured reading is not present in that report it says so instead of suggesting that live sensor data is still loading.

Details and copy commands still work where the report contains the relevant fields. This is useful when someone sends a USB, audio, display, network, fan, battery, or SMART report and you want to inspect specific rows without reading the whole file manually.

Use `File` > `Return to live readings` or `Ctrl+R` to go back to the current computer. HTML reports saved by Sensor Readout 2.0 or later contain a hidden machine-readable snapshot for the best import experience. TXT reports include the same reopen data after a clearly labelled internal-data line, wrapped into short lines so the readable report stays practical in text editors.

### Comparing Reports

Use `File` > `Compare reports...` or `Ctrl+Shift+M` to choose two Sensor Readout reports. The comparison shows added readings, removed readings, changed values, and changed Details fields. This is useful for before-and-after troubleshooting, checking whether a driver install changed hardware state, or comparing two similar machines.

### Support Reports

Use `Help` > `Prepare support report...` when you need to send information to the project or a support person. Sensor Readout creates the same diagnostic ZIP as `Run diagnostics...`, then shows the ZIP path with buttons to copy the path, open the containing folder, or open the GitHub issue page.

### Reading History

Use `Options` > `Enable reading history logging` to turn short-term CSV logging on or off. Select a reading and use `Edit` > `Add to history log` or `Ctrl+Shift+G` to include it. Sensor Readout writes the selected readings to `Logs\ReadingHistory-<ComputerName>-<Date>.csv` during normal refreshes. This is intended for temporary troubleshooting rather than permanent background monitoring.

## Settings Transfer

Use `File` > `Export settings and profiles...` or `Ctrl+E` to create a settings package. Sensor Readout shows a checklist first, so you can choose exactly what to include: general preferences, notification area readings, spoken hotkey profiles, fan profiles, fan curves, alarms, hidden items, or plug-in choices.

Use `File` > `Import settings and profiles...` or `Ctrl+I` to bring settings back in. Import shows the same kind of checklist, limited to the sections found in the selected package. Portable readings such as memory, CPU, System uptime, and many overview values are matched when possible. Hardware-specific items that could be unsafe or wrong on another computer are imported cautiously: global hotkeys are left blank, fan profile actions are not bound, fan curves are disabled until a fan control is chosen, and alarms are disabled for review.

Use `File` > `Export portable copy...` or `Ctrl+Shift+E` when you want the whole customized app as a ZIP. It includes Sensor Readout, `Config`, Plug-Ins, language files, custom sounds, and the other files needed to run from another folder or another PC. Reports and logs are optional checkboxes because they can be large or private.

## Files

Portable build:

- `Sensor Readout.exe`

Configuration and logging created by the app:

- `Config\Shared.json`: portable preferences shared across machines, such as language, refresh behavior, temperature unit, startup/shutdown sounds, startup speech, notification-area visibility, update checks, and global hotkey choices.
- `Config\<ComputerName>.json`: machine-specific preferences, such as run-at-Windows-startup, selected notification-area readings, spoken-hotkey reading lists, alarms, hidden readings, fan labels, fan control settings, custom spoken reading labels, logging level, and first-run prerequisite prompts.
- `*.srsettings.json`: optional settings transfer packages created by `File` > `Export settings and profiles...`.
- `Logs\<ComputerName>.log`: optional diagnostics, fan actions, hotkey registration, and screen reader speech messages, created only when logging is enabled.
- `Logs\ReadingHistory-<ComputerName>-<Date>.csv`: optional short-term reading history created only when reading history logging is enabled and at least one reading has been selected for it.
- `Reports`: default folder for reports saved from the command line, from the first save dialog, or from `Help` > `Run diagnostics...`.
- `Sounds\SR01.wav`, `Sounds\SR02.wav`, and similar: optional alarm/startup/shutdown sounds. Users can add their own `.wav` files.
- `Data\usb.ids`: bundled USB vendor/product lookup data.
- `Data\oui.csv`: bundled MAC/OUI vendor lookup data for network adapters.
- `Plug-Ins\Framework`: optional Framework Laptop plug-in.
- `Plug-Ins\HP`: experimental optional HP/OMEN/Victus plug-in.
- `Plug-Ins\DellLatitude`: experimental optional Dell Latitude plug-in.
- `Plug-Ins\AsusRog`: experimental optional Asus laptop plug-in. This plug-in includes its own notice and GPL text because it uses G-Helper-derived ACPI research.
- `Plug-Ins\LenovoThinkPad`: experimental optional Lenovo/ThinkPad read-only plug-in.
- `Plug-Ins\MsiLaptop`: experimental optional MSI laptop plug-in for MSI ACPI temperature, fan, and fan-control support where exposed.
- Users can add third-party plug-ins in their own subfolders.

Language files:

- Put `.txt` or `.ini` files in the `Langs` folder beside `Sensor Readout.exe`.
- On first run, Sensor Readout chooses a bundled language from the Windows display language when one is available; otherwise it falls back to English.
- Use simple `key=value` lines. Lines starting with `#` or `;` are comments.
- Set `language.name=Display name` so the language has a clear name in menus.
- Set `manual.file=README-xx.html` so `F1` opens the matching manual for the selected language in the user's browser. Type only a file name, not a folder path. Sensor Readout looks in the `Docs` folder first. If this entry is missing, Sensor Readout tries `<language-file-name>.html`, then falls back to `README-en.html`.
- Set `number.decimalSeparator=,` if the language should display decimal values with a comma, such as `1,1 TB`.
- Sensor Readout checks the folder every 15 seconds, so newly added or edited files appear in `Options` > `Language` without restarting.
- `Langs\English.txt` is the primary/default language file. Copy it or use the Language editor's New button to start another language.
- The decimal separator can also be changed from Preferences without editing a language file.
- Bundled manuals live in the `Docs` folder and use `README-en.html`, `README-de.html`, `README-es.html`, `README-fr.html`, and `README-it.html`.

Optional screen-reader speech:

- Spoken hotkeys and alarms use Tolk to talk to the active screen reader where possible.
- `Tolk.dll`, `SAAPI64.dll`, and Tolk support DLLs are bundled beside `Sensor Readout.exe` for screen-reader and SAPI speech.
- If Tolk is missing or no speech target accepts the message, the action fails safely and shows a status message when the main window is visible.
- When Sensor Readout starts minimized to the notification area, it can speak a configurable startup message through the active screen reader. The default comes from `speech.startupActive` in the selected language file.
- Preferences includes a simple language editor tab for editing existing language-file entries without opening a separate text editor.

## Notes

Fan support depends on LibreHardwareMonitor, PawnIO, the motherboard sensor chip, and what the BIOS exposes. Some systems may provide readings but not writable fan controls.

The app must run as administrator for motherboard Super I/O access on many systems. GPU and storage readings may still appear without elevation, but motherboard fans and controls often will not.

If CPU temperature or CPU load readings are missing, installing and running Core Temp may help. Sensor Readout can read Core Temp's shared-memory data when Core Temp is already running. Core Temp is optional and is only used as a fallback for CPU readings. Use `Help` > `Install Core Temp support...` to open the official Core Temp page or start installation with winget or Chocolatey when those tools are available.

If fan controls appear to be missing, open `Options` > `Fan controls...` and enable `Show stopped`. Some boards report controllable headers as stopped or undefined until they begin spinning, and this option makes those hidden entries visible for testing.

### Laptop Brand Plug-Ins

Framework, Dell, Lenovo, MSI, Asus, HP, OMEN, and Victus laptop users may get better fan or temperature visibility by enabling the matching hardware plug-in from `Options` > `Preferences` > `Plug-Ins`. Plug-ins are disabled until you enable them, and changes apply after closing Preferences.

Framework Laptop users should also prepare Framework Control before reporting missing Framework readings:

1. Update the laptop BIOS from Framework's BIOS and drivers page: <https://knowledgebase.frame.work/bios-and-drivers-downloads-rJ3PaCexh>
2. Install and run Framework Control: <https://github.com/ozturkkl/framework-control>
3. Confirm Framework Control can show its fan/temperature controls in its browser interface.
4. Start Sensor Readout.
5. Open `Options` > `Preferences` > `Plug-Ins`, enable the Framework Laptop plug-in, and close Preferences.
6. Framework readings should appear as `Framework Control` temperature and fan rows when the local API is available.

Dell, Lenovo, MSI, Asus, HP, OMEN, and Victus plug-ins do not guarantee fan readings on every model, because laptop makers decide what Windows can see. They are still worth enabling on matching laptops before sending diagnostics about missing fans or temperatures.

Optional vendor tools can also help expose or verify laptop-specific data. Dell users can try [Dell Command | Monitor](https://www.dell.com/support/kbdoc/en-us/000177080/dell-command-monitor). Lenovo users can try [Lenovo Vantage](https://www.lenovo.com/us/en/software/vantage) or [Lenovo System Update](https://pcsupport.lenovo.com/us/en/solutions/ht003029-lenovo-system-update-update-drivers-bios-and-applications). MSI users can try [MSI Center](https://www.msi.com/Landing/MSI-Center). Asus users can try [G-Helper](https://g-helper.com/). HP, OMEN, and Victus users can try [HP Support Assistant](https://support.hp.com/us-en/help/hp-support-assistant) and [OMEN Gaming Hub](https://www.hp.com/us-en/gaming-pc/omen-gaming-hub.html). These tools are outside Sensor Readout; use the vendor or project pages and only install what makes sense for your machine.

Sensor Readout only reads these optional support paths unless a plug-in clearly says otherwise. It does not flash firmware or replace the laptop maker's own setup tools.

## Changelog

### 3.9.0

- Added: Each main category now starts with a Category summary row that gives a compact overview of readings, groups, detail coverage, and section-specific health signals.
- Added: Empty categories now explain the most likely next step, such as refreshing, connecting hardware, enabling a plug-in, checking hidden items, or waiting for slower Windows-backed data.
- Added: The Alarms tab now has practical presets for common available readings such as low battery, weak or disconnected Wi-Fi, high CPU/GPU temperature, disk health warnings, printer problems, CPU usage, memory usage, uptime, GPU memory, disk free space, and disk activity. Presets start unchecked so users only create the alarms they choose.
- Added: The Hotkeys tab now has spoken hotkey presets for common starter groups such as system status, network status, disk activity, GPU status, battery status, and fan/temperature status. Presets are created without assigned keys so they cannot steal existing shortcuts.
- Improved: Spoken hotkey profiles are now sorted by assigned key, Hotkeys and Alarms both use `Alt+P` for Presets, and removing a spoken hotkey profile asks for confirmation unless you turn that prompt off.
- Added: Help now includes an accessibility setup check for screen reader output, notification area speech, show/hide keys, report/log folder availability, and start-minimized setup.
- Improved: Details now includes Copy matching, also available with `Ctrl+M`, which searches the detailed tree and copies only matching lines for easier support sharing.
- Improved: Diagnostics now includes a support-readiness summary with data-source, plug-in, detail, fan-control, non-working-device, and empty-category counts.

### 3.8.1

- Improved: `File` > `Import settings and profiles...` now uses `Ctrl+I`, matching `Ctrl+E` for export. The less common Plug-In ZIP importer now uses `Ctrl+Shift+I`.

### 3.8.0

- Fixed: Live refresh now avoids repeated WMI queries for network adapter details, GPU memory totals, and battery metadata, reducing sustained `WmiPrvSE.exe` CPU use on lower-powered systems while keeping those details available.
- Fixed: When elevated, Sensor Readout can now make one recovery attempt if core Windows WMI gets stuck with the specific "paging file is too small" failure that can otherwise leave USB, Devices, Battery, and other Windows-backed sections blank.
- Fixed: Plug-ins can now load correctly from extracted downloaded ZIP builds when Windows marks the files as downloaded from the internet.
- Added: Experimental MSI Laptop Support plug-in for opt-in tester feedback. It checks MSI ACPI WMI, Windows fan, and ACPI thermal-zone interfaces for extra fan, fan-control, or temperature data where a laptop exposes it.
- Improved: Saved TXT and HTML reports now show the Sensor Readout version near the top of the report, making support builds easier to identify.
- Improved: Notification-area status and spoken hotkeys now clearly announce when they are reading from a loaded static report, and report categories or hotkey readings that are absent from the report no longer imply that live data is still loading.

### 3.7.1

- Fixed: Automatic updates now replace shipped folders such as Plug-Ins, Docs, Langs, and Data cleanly instead of merging new files into old folders. This prevents duplicated plug-ins and stale shipped files after updating.
- Fixed: Update backups now go into a top-level `Backups` folder as ZIP files, while `Config`, `Logs`, and `Reports` remain reserved for live settings, logs, and saved reports.
- Fixed: Plug-In loading now ignores duplicate manifests and only scans the root Plug-Ins folder plus one folder per plug-in, preventing nested stale copies from being loaded twice.
### 3.7.0

- Fixed: Manual refresh with `F5` now rebuilds USB and device inventory immediately, so newly plugged or unplugged USB devices can appear without restarting Sensor Readout.
- Improved: The EXE, plug-in SDK, and bundled plug-in DLLs now carry matching Sensor Readout file and product version metadata, and the build fails if those versions drift.
- Added: Experimental Lenovo Laptop Support plug-in for opt-in tester diagnostics on Lenovo systems. It checks Lenovo fan WMI, Windows fan, ACPI thermal-zone, and Lenovo WMI interfaces for extra read-only fan or temperature data.

### 3.6.2

- Fixed: Preferences now has a tab-reachable `Rename...` button for notification-area spoken labels, with a cleaner tab order and `Alt+R` accelerator.
- Fixed: Spoken hotkey and notification-area speech changes made in Preferences can be tested immediately before closing Preferences, including newly created spoken hotkeys and renamed labels.
- Improved: Display adapter Details now include additional GPU registry fields for NVIDIA, AMD, and Intel adapters, plus NVIDIA driver data from `nvidia-smi` where available, including CUDA compute capability, VBIOS, clocks, power limit, temperature, PCI IDs, and memory figures.
- Improved: Printer rows now include richer Details from Windows printer WMI, printer configuration WMI, and print registry/driver data, while keeping binary registry blobs short enough for screen-reader review.
- Improved: Audio device and endpoint Details now include raw Windows sound-device WMI fields and endpoint registry properties, with binary blobs summarized instead of expanded.

### 3.6.1

- Fixed: Localized manuals no longer include corrupted oversized text, and the release checks now block stale visible manual versions and bad manual encoding before packaging.

### 3.6.0

- Added: `File` > `Compare reports...` compares two saved Sensor Readout reports, including added, removed, changed, and detail-level differences for before-and-after troubleshooting.
- Added: `File` > `Save anonymized report...` creates a shareable report with common private identifiers masked before sending it outside your own machine.
- Added: `Help` > `Prepare support report...` creates a diagnostic ZIP and gives a guided support flow with the file path ready to copy or attach to a GitHub issue.
- Added: Performance/Overview includes a Data sources group that shows which Windows, LibreHardwareMonitor, database, plug-in, and Sensor Readout sources contributed to the current view.
- Added: Optional reading history logging can write selected readings to a short-term CSV log for troubleshooting trends during normal refreshes.
- Added: Command-line options can now create anonymized reports and compare two saved reports for unattended testing or scripted support workflows.
- Improved: Spoken hotkey and notification-area speech changes made in Preferences now apply immediately, so a hotkey can be tested before closing Preferences.
- Improved: The hotkey and tray assignment dialog now shows the notification area status hotkey and selected reading count, matching the spoken hotkey entries.
- Improved: Update downloads now use the same Sensor Readout user-agent label as update checks.

### 3.5.0

- Added: Performance/Overview can now show Windows GPU memory usage where available, including dedicated GPU memory total, used, free, and shared GPU memory used. These readings can be used in notification area status, spoken hotkeys, reports, and alarms.
- Improved: Refresh now lives in the View menu, and F5 refresh handling is more reliable from the main window.
- Improved: The refresh interval preference can now be set as low as 1 second for users who want faster live readouts.

### 3.4.0

- Improved: SMART and disk Details now include more Windows physical-drive information where available, including bus type, sector sizes, firmware/software version, serial and unique IDs, physical location, slot/enclosure fields, storage usage, pool eligibility, and spindle speed for drives that report it.
- Improved: Asus laptop support can now fall back to ASUS WMI sensor readings and ASUS thermal-mode fan control on models that reject direct fan-curve writes.
- Improved: `Ctrl+Shift+H` can now add or remove the selected reading from notification area status as well as spoken hotkey profiles, and it can create a new spoken hotkey profile without opening Preferences first.

### 3.3.1

- Fixed: Run at Windows startup now uses a Windows logon task with highest privileges instead of a Startup folder shortcut, so installed copies can start reliably even though Sensor Readout needs administrator rights for hardware access.

### 3.3.0

- Improved: Performance/Overview now uses clearer group names for CPU and memory readings and printer summary rows, avoiding duplicate-looking System and Printers branches in the tree.
- Improved: CPU and baseboard Details now include deeper Windows/SMBIOS fields such as cache associativity and write policy, chassis type, memory slot counts, expansion slots, and raw Windows fields where available.
- Improved: Windows and firmware Details now include additional OS, registry, licensing, system product, BIOS, Secure Boot, and TPM fields where Windows exposes them. Product keys are shown only as masked endings so shared reports do not leak full keys.
- Improved: Disk Details now include physical disk, partition, hidden/no-letter partition, and logical volume information where Windows exposes it.
- Improved: Network Details now include gateway, DNS, DHCP, IP, MTU, adapter statistics, and raw Windows adapter/configuration fields where available.
- Improved: Details now open as a grouped tree instead of a two-column list, so screen readers read each field and value as one item, raw WMI or registry fields can be skipped by group, and F3/F4 work inside Details for search and text review.
- Added: `Ctrl+Shift+C` copies the selected reading value only, without the reading name, making model numbers, device strings, UUIDs, and similar fields easier to reuse.
- Improved: Screen-reader users now hear "Has Details" after tree items that support the Details command, without adding that hint to copied text or reports.

### 3.2.1

- Fixed: The Windows startup option now refreshes Sensor Readout's Startup Apps approval state as well as the startup-folder shortcut, so Run at startup is more reliable on systems where Windows has kept a stale approval entry.

### 3.2.0

- Improved: Performance/Overview is now split into clearer subgroups for System, Windows, firmware and board, graphics, printer summary, printers, and storage instead of one long flat overview.
- Improved: Printer rows now appear under a Printers subtree with one branch per printer, so screen readers no longer hear the full printer name before every field.
- Added: Printers with offline, paused, error, stopped, or queued-job states are also shown in a Printer issues group when Windows reports a problem.
- Added: Printer supply percentages, offline state, and queued job counts can be used in notification area readings, spoken hotkeys, and alarms when Windows exposes those values.
- Added: The new Hotkeys menu shows the configured key for speaking tray status, show/hide, spoken hotkey profiles, and fan profile hotkeys, and it provides the quick command for adding the current reading to a spoken hotkey.

### 3.1.1

- Fixed: SMART lifetime `Data read` and `Data written` totals are shown again when LibreHardwareMonitor exposes them.
- Improved: Sensor Readout now tries additional Windows NVMe SMART paths for storage devices, including standard NVMe health-log data and ASMedia-style USB NVMe bridge data where Windows permits access.
- Added: Performance/Overview now includes Windows printer status, configuration, and ink or toner level percentages when the printer driver exposes them.
- Added: Devices now places problem devices in a clear Non-working devices group, making Device Manager-style driver or hardware failures easier to find with a screen reader.
- Fixed: `Alt+F4` now closes Sensor Readout from the reading tree even when the selected row also supports Details.

### 3.1.0

- Added: `Edit` > `Add/remove from spoken hotkey...` (`Ctrl+Shift+H`) lets you add the selected reading to a spoken hotkey profile, or remove it again, without opening the full Preferences workflow.
- Added: `Edit` > `Expand all` (`Ctrl+Shift+Right`) and `Edit` > `Collapse all` (`Ctrl+Shift+Left`) make large categories easier to browse or reset.
- Improved: Notification area tooltips can now use Windows' longer native tooltip text where available, so more selected readings can be shown directly before falling back to `...`. This closes [GitHub issue #3](https://github.com/OnjLouis/accessible-sensor-readout/issues/3).
- Improved: Preferences includes an option to show as many readings as possible in the notification area tooltip, or keep long notification tooltips to the simple `Sensor Readout` label.
- Fixed: Showing and hiding the main window no longer collapses the reading tree unexpectedly.
- Fixed: Installing to this PC now applies the selected Windows startup option during the install flow.

### 3.0.1

- Fixed: Updating Sensor Readout no longer copies bundled `Config`, `Logs`, or `Reports` folders over an existing installation, preserving hotkeys, notification area readings, spoken hotkey profiles, fan profiles, alarms, reports, and logs during updates.
### 3.0.0

- Added: `Devices` gives a Device Manager-style Windows hardware inventory with grouped sections, a compact tree, and detailed PnP, PCI, driver, registry, and resource fields available through Details and reports.
- Improved: Details for CPU and memory rows now include deeper Windows hardware fields such as processor identifiers, WMI CPU properties, cache records, memory array limits, module manufacturer/part/serial information, speeds, widths, voltages, and memory type where Windows exposes them.
- Improved: `Download and install` can now remember when you do not want to see the close/install/reopen confirmation again, and `Preferences` > `Hidden items` can turn that confirmation back on.
- Improved: `Uninstall from this PC` now offers an explicit option to also delete `Config`, `Logs`, and `Reports` for a complete cleanup.
- Added: `File` > `Export portable copy...` (`Ctrl+Shift+E`) creates a ZIP of the customized app for use on another PC, including settings and custom app files with optional Reports and Logs.
- Improved: TXT reports now keep exact reopen data in a labelled, wrapped block after the readable report content, avoiding one very long screen-reader-hostile line.
- Added: Performance/Overview now shows CPU cache sizes, including L1, L2, and L3 where Windows reports them.
- Improved: The title bar for an opened report now shows the report computer directly, such as `Report: Example-PC`, instead of repeating the full report title.
- Fixed: Notification area tooltips now use the same configured labels as spoken tray status when they fit, and show a clear summary instead of a clipped partial list when Windows' tooltip limit is reached. This closes [GitHub issue #2](https://github.com/OnjLouis/accessible-sensor-readout/issues/2).

### 2.3.0

- Added: Optional quiet update installs can download, install, close, and reopen without showing release notes first. `Help` > `Version history...` opens the latest release notes on demand.
- Improved: Startup and Install now shows Uninstall from this PC when Sensor Readout is already running from the installed location, and can create a desktop shortcut directly from the same tab.
- Improved: Install to this PC now offers desktop and Windows startup shortcut choices in one dialog before copying and reopening the installed copy.
- Improved: The title bar shows the current computer name after the Sensor Readout version, matching the extra context shown while viewing a saved report.
- Improved: Automatic updates only preserve changed or custom language files instead of backing up the whole language folder on every update.
- Improved: `File` > `Open report...` can open a ZIP file that contains a Sensor Readout TXT or HTML report, so users do not have to extract a report manually before viewing it.

### 2.2.1

- Fixed: System uptime now refreshes continuously with the normal sensor update cycle instead of staying on an older cached Overview value.

### 2.2.0

- Added: `Edit` > `Find reading...` (`F3`) searches readings across all categories. The same F3 search dialog is available in Preferences reading lists when choosing notification area readings, spoken hotkey readings, or fan-profile controls.
- Added: `Edit` > `Review text...` (`F4`) opens the selected reading or tree branch in a read-only edit box, making it easier to inspect exact spelling or punctuation after screen-reader speech. The Copy command confirms through the active screen reader after copying.
- Added: `File` > `Export settings and profiles...` (`Ctrl+E`) and `File` > `Import settings and profiles...` (`Ctrl+I`) create selective transfer packages for general preferences, notification area readings, spoken hotkey profiles, fan profiles, fan curves, alarms, hidden items, and plug-in choices. Hardware-specific items are imported unassigned, disabled, or ready for review so they can be bound safely on the current computer.
- Added: Wi-Fi adapters can show connection state, SSID, access point, signal strength percentage, RSSI in dBm, channel, frequency, radio type, receive/transmit link speeds, and security details when Windows exposes them. Wi-Fi connection state, signal, RSSI, channel, frequency, and link speeds can also be used for notification area status, spoken hotkeys, and alarms where numeric thresholds make sense. This closes [GitHub issue #1](https://github.com/OnjLouis/accessible-sensor-readout/issues/1).
- Improved: Notification area status can include up to eight selected readings, making room for Wi-Fi signal, System uptime, and other key readings alongside CPU, GPU, fan, or network values.
- Improved: Startup and Install warns when minimized startup is enabled without a show/hide hotkey, and the manual explains how to reveal the tray icon if Windows hides it.
- Improved: Context menus hide unavailable Details or Rename commands for the selected reading.

### 2.1.0

- Added: Startup and Install adds an `Alt+I` Install to this PC command that can move a portable or synced-folder copy into the normal Windows programs folder for this user. It can also create a desktop shortcut, keep Windows startup pointing at the installed copy, close the old copy, and reopen Sensor Readout from the installed location.
- Added: `File` > `Open Reports folder` (`Ctrl+Shift+O`) and `File` > `Open Logs folder` (`Ctrl+Shift+L`) make saved reports and logs easier to find.
- Added: Enabled plug-ins can add related support pages to the Help menu, such as vendor utility pages for Dell, Asus, HP, OMEN, Victus, and Framework laptops.
- Improved: Automatic updates handle unusual Windows temporary-folder settings more reliably.
- Improved: Saved report file names include the computer name, making reports from several machines easier to sort.
- Improved: Desktop PCs with vague Windows model names now show clearer motherboard information when Windows provides it.
- Improved: Performance/Overview shows more Windows and firmware details, including Windows edition, version, build, architecture, install date with age, boot time, BIOS mode, Secure Boot, SMBIOS version, and embedded controller version when Windows provides them.

### 2.0.0

- Added: Audio category for sound devices and endpoints, grouped by device/interface with playback and recording entries, vendor/status details, and default format details such as channels, sample rate, and bit depth where Windows exposes them.
- Added: Display category for graphics adapters and monitors, including adapter memory, resolution, refresh rate, driver details, monitor vendor, product code, serial, and manufacture date where Windows exposes them.
- Added: USB details now include connected devices, hubs, controllers, connection speed, capable speed, requested power, drive letters, safe-to-unplug status, copyable detail fields, USB network adapter MAC/OUI details, and USB storage hardware ID/OUI details where Windows exposes them.
- Added: Saved TXT and HTML reports can be opened from `File` > `Open report...` or `Ctrl+O` as static snapshots in the normal Sensor Readout tree, so another user's report can be inspected without reading the raw file manually. Use `File` > `Return to live readings` or `Ctrl+R` to return to the current computer.
- Improved: Performance/Overview now gives CPU details beyond usage, including CPU model, vendor, cores, threads, clocks, socket, architecture, instruction sets such as SSE and AVX, and clearer virtualization labels where Windows exposes them.
- Improved: The Preferences manual section is now organized by tab, with shortcut keys and clearer explanations of what each pane controls.
- Added: `Help` > `Run diagnostics...`, `Alt+F1`, and `--diagnostics [path]` create a support ZIP containing TXT and HTML reports, a debug log, timing data, a diagnostic summary, and a short fan-control test that restores the previous fan state afterward. Diagnostic ZIP names include the computer name, and temporary staging files are removed after the ZIP is created.
- Added: Diagnostics can speak progress, say "Complete." when finished, and play configurable start/completion sounds from Preferences. Command-line diagnostics can be silenced with `--diagnostics-quiet`, `--no-diagnostics-speech`, or `--no-diagnostics-sounds`.
- Added: Experimental Asus Laptop Support plug-in for opt-in tester feedback. It can read Asus ATKACPI temperature and fan duty-cycle data where available, and attempts fan control on supported models. Initial plug-in work by Jason Fayre and Claude Code; ACPI behavior is based in part on G-Helper research.
- Fixed: Tray icon rendering now falls back safely if Windows/GDI+ refuses to create a dynamic icon, preventing a repeated crash/restart loop.

### 1.6.2

- Added: If Sensor Readout crashes during normal use, it writes the crash log and tries to restart automatically, stopping after three crash restarts in a short window to avoid a loop.
- Added: Fan profiles can now disable spoken confirmation or use a custom spoken message. Use `{0}` in the message to include the profile name.
- Added: Fan profile and fan curve setup screens can show stopped or hidden fan controls, matching the Fan Controls dialog.
- Added: Experimental Dell Latitude Support plug-in for opt-in tester diagnostics on Dell systems with Dell Command | Monitor WMI data. It is disabled by default and read-only.
- Fixed: Fan profile hotkeys now refresh only live sensor rows after changing fan state, avoiding unnecessary SMART/USB refresh work before other spoken hotkeys respond.
- Fixed: Fan curves that are temporarily taken over by manual fan controls or fan profile hotkeys now resume automatically when those fans are returned to automatic/default control.
- Fixed: `Show stopped` in the Fan Controls dialog is now saved immediately even if no manual fan setting is changed.
- Added: Crash diagnostics are now written to the Logs folder even when normal logging is turned off.
- Improved: Spanish language text has been polished with help from Dreamburguer, including accents and more natural wording for keyboard shortcuts and spoken feedback.
### 1.6.1

- New: Fan profiles can now be used as toggles. Press a fan profile hotkey once to apply it, then press the same hotkey again to return those fans to automatic/default control.
- New: Spoken hotkey profiles can be imported from another Sensor Readout machine config. Readings that match the current machine are kept, while missing machine-specific readings are skipped.
- New: Update dialogs now show every release note between the installed version and the newest version, headed by the current version, new version, and version range.
- Added: Fan profile hotkey actions can play a chosen sound when the profile is applied.
- Added: The update-available dialog can play a chosen sound from Preferences.
- Added: Startup speech can now be disabled with a checkbox instead of using a blank or placeholder startup message.
- Added: Experimental HP Hardware Support plug-in for opt-in tester diagnostics on HP/OMEN/Victus systems. It is disabled by default and read-only.
- Added: `Help` > `Install Core Temp support...` explains the optional CPU fallback and can open the Core Temp website or launch a winget/Chocolatey install with user confirmation.
- Fixed: Closing a running Sensor Readout instance with `--close` now hides the notification area icon first, reducing orphaned tray icons during updates and test runs.
- Fixed: Framework Laptop setup notes now state that the bundled Framework plug-in must be enabled in Preferences before Framework Control readings can appear.
- Improved: The update dialog keeps Download and install as the first button when a ZIP package is available.

### 1.6.0

- New: Opt-in hardware plug-in system. Plug-ins live in the `Plug-Ins` folder, appear in Preferences, and are disabled until the user enables them.
- New: Plug-in ZIP import from Preferences > Plug-Ins. Imported plug-ins are validated, copied into the right folder, and left disabled for safety.
- Added: Framework Laptop support is now shipped as an optional Framework plug-in instead of being built into the main app.
- Added: `Docs\Plug-In-development.md` for developers who want to build hardware plug-ins for Sensor Readout.
- Added: Memory total now appears in Performance/Overview alongside memory used and memory available.
- Added: Reports now default to a `Reports` folder instead of the main program folder.
- Improved: GitHub self-updates back up the existing `Langs` folder before replacing shipped language files, protecting user language edits.
- Changed: OEM-specific hardware support can now be added through plug-ins, keeping the main app smaller and safer to maintain.

### 1.5.2

- Fixed: Framework Laptop systems without Framework Control installed or running no longer wait through repeated local API timeouts before normal readings appear.
- Fixed: Normal LibreHardwareMonitor, Windows, and Core Temp readings remain available on Framework systems even when the optional Framework Control API is unavailable.

### 1.5.1

- Added: Optional read-only Framework Control API support. When Framework Control is installed and running, Sensor Readout can show Framework Laptop temperature and fan RPM readings.
- Added: Battery section for laptop charge, status, capacity, health, cycle count, voltage, and power rate when Windows exposes those values.
- Added: Framework Laptop setup notes in the manual, including BIOS update guidance and the Framework Control project link.
- Fixed: On a new machine with no machine-specific config yet, copied portable/shared settings no longer silently enable Windows startup registration from another computer.

### 1.5.0

- New: USB category for connected devices, hubs, and controllers, with concise tree rows, copyable detail fields, bundled `Data\usb.ids` vendor/product lookup, and extra information such as connection speed, capable speed, requested power in mA, port, drive letters, safe-to-unplug status, USB network adapter MAC address/OUI vendor and storage hardware ID/OUI vendor where available, VID/PID, driver key, service, and Windows device IDs where Windows exposes them.
- New: Fan curves can link a writable fan control to a temperature reading with low, high, and emergency points.
- New: Fan profiles can apply several fan controls at once, including automatic/default control, and can be triggered from optional global hotkeys. New setups include empty starter profiles that users can fill with their own fans.
- Added: `Ctrl+U` opens Fan Curves directly from anywhere in the main app.
- Fixed: Fan Curves now uses friendly fan names and follows the same hidden or stopped fan filtering as Fan Controls.
- Improved: Visible reading lists defer refresh redraws briefly while menus or tree navigation are active, reducing focus stalls during background updates.
- Improved: Preferences now has a General > Updates section where automatic GitHub update checks can be set to startup, hourly, 6-hourly, 12-hourly, daily, weekly, or never.
- New: Network adapters show MAC address and OUI vendor when the bundled OUI database contains the adapter prefix.
- Added: `Data\oui.csv` is bundled as a separate MIT-licensed data file for MAC vendor lookup.
- Changed: Spoken output uses the Tolk screen-reader library, opening speech support beyond one screen reader and allowing SAPI fallback where available.
- Fixed: Launching Sensor Readout while it is already running closes the existing instance and starts the new one cleanly, while command-line report generation can still run beside the active app.
- Improved: TXT and HTML reports include USB details and other row details, making reports more useful when diagnosing hardware support.
- Changed: SMART now focuses on health, status, temperature, wear, errors, and hours, while live disk read/write rates and activity use Windows performance counters.

### 1.4.3

- Changed: Portable preferences such as language, refresh settings, temperature unit, startup/shutdown sounds, startup speech, update checks, and global hotkeys now live in `Config\Shared.json` so they can follow the app between machines.
- Changed: Hardware-specific setup such as selected tray readings, spoken hotkey reading lists, alarms, hidden readings, fan labels, fan controls, custom spoken reading labels, logging level, and Windows startup registration stays in `Config\<ComputerName>.json`. Shared preferences are no longer duplicated into the machine-specific file. Unsafe navigation keys such as Shift+Tab are rejected when assigning global hotkeys.

### 1.4.2

- Fixed: Automatic update installs now merge folder contents into existing folders instead of creating nested folders such as `Langs\Langs`, `Docs\Docs`, or `Sounds\Sounds`.
- Fixed: Sensor Readout repairs installs already affected by nested update folders on startup, moving the newer files back into the correct locations.
- Improved: The prerequisite installer now falls back from winget to Chocolatey, then to the official PawnIO.Setup GitHub download when winget is unavailable.

### 1.4.1

- Fixed: Ctrl+Alt combinations can now be captured correctly when assigning global hotkeys in Preferences.
- Added: Double-press copy actions now speak "Copied to Clipboard." through the active screen reader after the text is copied.

### 1.4.0

- New: Reading alarms can monitor numeric sensor values and notify with screen reader speech, optional WAV sounds, per-alarm cooldowns, and a flashing notification-area icon.
- New: Alarm thresholds are unit-aware. Temperature alarms can use C or F, fan alarms use RPM, rate alarms can use B/s, KB/s, MB/s, or GB/s, and size-style readings can use byte units where applicable.
- New: Startup and shutdown sounds can be chosen from the Sounds folder, with preview while choosing a sound.
- New: Speech hotkeys can copy their spoken output to the clipboard with an optional double-press gesture.
- New: Preferences now supports Delete for removing selected items, Ctrl+1 through Ctrl+6 for jumping between tabs, F2 for name/edit fields, and Enter for the main value field where applicable.
- Changed: Source code has been split into focused files for startup, models, preferences, the main form, and native/screen reader interop, making future maintenance safer.
- Added: Command-line options for starting minimized, closing a running instance, setting the logging level, and saving TXT or HTML reports.
- Fixed: Update-available release notes now display GitHub changelog lines clearly instead of running Markdown items together.

### 1.3.1

- Added: Optional Core Temp shared-memory fallback for CPU temperatures and CPU load when Core Temp is already running.
- Fixed: Systems where LibreHardwareMonitor exposes no CPU temperature can now still show CPU readings if Core Temp supports the processor.
- Added: Guidance for users with missing CPU readings: install and run Core Temp from https://www.alcpu.com/CoreTemp/ so Sensor Readout can use its shared-memory data. Fan control still depends on LibreHardwareMonitor, PawnIO, and the hardware exposing writable fan controls.

### 1.3.0

- New: Create multiple spoken hotkey profiles, each with its own key combination and ordered set of readings.
- New: Selected tray and spoken-hotkey readings show a speech preview, and spoken-hotkey readings can be renamed for shorter speech.
- New: Spoken feedback can omit device names for shorter screen reader output such as `Rx 688.4 KB/s; Tx 14.4 MB/s`.
- Improved: Repeated spoken labels are grouped for concise output, for example `CPU: 15.0%; 45.1 C`.
- Improved: Hotkey and speech controls now live together on a dedicated Hotkeys tab.
- Improved: Preferences reopens on the tab you used last during the current session.
- Improved: Startup options now live together on a dedicated Startup tab.
- New: Startup update checks can be turned off in Preferences.
- Improved: Preferences now protects plain text entry so typing profile names does not trigger unrelated UI accelerators.
- Improved: Reading selection lists in Preferences support buffered multi-character search, so typing more than one letter can find entries such as Ethernet Rx.
- New: Fan readings can show the matching control percentage next to RPM, and those percentages feed the selected-reading progress meter for screen reader feedback.
- Improved: Saved manual fan settings are restored in the background on startup, while saved automatic/default fan settings no longer slow launch.
- Fixed: Update-available dialog buttons now include keyboard accelerators.
- Changed: Shipped folders now use consistent casing: `Config`, `Logs`, `Langs`, and `Docs`, while folder lookup remains case-insensitive for existing installs.

### 1.2.1

- Fixed: Storage free/used-space readings now come from Windows logical drives, avoiding mismatched LibreHardwareMonitor space values on some SSDs.
- Improved: The selected-reading progress meter now works for any valid percentage reading, including memory available, memory used, drive free space, drive used space, CPU usage, activity percentages, and localized comma-decimal percentages.

### 1.2.0

- New: Multilingual interface support with English, German, Spanish, French, and Italian language files.
- New: Language editor in Preferences for editing translated text, creating new language files from English, and changing the NVDA startup message.
- New: HTML manuals in the `docs` folder, with `F1` opening the manual for the selected language.
- New: Temperature-unit control for Celsius or Fahrenheit.
- New: Decimal-separator control with language default, period, and comma choices.
- New: Optional global hotkeys for show/hide and speaking the current notification-area status.
- New: Configurable NVDA startup speech for minimized startup.
- New: Performance/Overview page with uptime, BIOS, system, storage, and GPU overview details.
- Added: 64-bit NVDA Controller Client files for optional NVDA speech from the portable folder.
- Added: `Options` > `Speak tray status now`.
- Added: Help > Contact and Help > Donate links. Sensor Readout remains free.
- Added: Help > Check for updates displays GitHub release notes, checks silently at startup, and can install a downloadable portable ZIP.
- Fixed: Preferences has a Close button on every tab, and Escape closes the dialog.
- Fixed: Language switching refreshes Preferences, the main window, reading labels, common screen-reader labels, and combo-box values without requiring a restart.
- Fixed: GitHub update checks use TLS 1.2 for compatibility with GitHub's current HTTPS requirements.
- Fixed: Missing notification-area selections are preserved during startup and upgrades.
- Fixed: Samsung SMART data counters are converted correctly so read/write totals display as readable GB/TB values.
- Fixed: GPU fan controls exposed by LibreHardwareMonitor stay visible even when the GPU is in fan-stop mode.
- Fixed: Fan Controls refreshes the selected fan state after manual, automatic, and all-fan actions without closing the dialog.
- Improved: Startup displays the main window while sensor refresh continues in the background.
- Improved: GPU adapter memory reporting uses Windows' 64-bit display registry value.
- Improved: Selected percentage readings expose progress value-change events for screen readers.
- Improved: Minimized or hidden operation keeps tray status fresh while reducing visible UI rebuild work.
- Improved: The main window toolbar is cleaner, with duplicate Refresh and Save report buttons removed in favour of the menu and keyboard shortcuts.
- Changed: Preferences saves edits immediately for hotkeys, tray selections, hidden items, startup options, and language choices.
- Changed: Per-computer settings live in `Config`, logs live in `Logs`, and old top-level JSON/log files are migrated automatically.
- Changed: Logging includes diagnostics for hotkeys and NVDA speech.

### 1.1.0

- Removed the combined Overview section to reduce duplicate tree navigation.
- Added Network as a first-class section.
- Moved Performance to the top of the section list and to `Ctrl+0`.
- Grouped Performance into System and Storage.
- Reworded the section list accessibility description so it is clearer that the list changes views only.
- Added Windows startup and start-minimized preferences.
- Added selected-reading progress feedback for percentage readings.
- Added ordered notification area reading selection and shorter notification tooltip labels.
- Added keyboard movement for notification area readings in Preferences.
- Added project page links to the README, Help menu, and About dialog.
- Added Help > Check for updates, backed by GitHub Releases.
- Changed About into a keyboard-friendly dialog with a Project page button.
- Pruned unused DLLs from the portable package.
- Expanded bundled component attribution in README and About.

### 1.0.0

- Initial portable release with sensor reading, report export, tray readouts, per-computer settings, hidden readings, fan labels, and direct LibreHardwareMonitor/PawnIO fan control.

## Credits

Created by Codex. Ideas by Andre Louis.

Questions and feedback can be sent through `Help` > `Contact` in the app or <https://onj.me/contact>.

Sensor Readout is free software. If you want to support Andre's work, use `Help` > `Donate` in the app or visit <https://www.paypal.me/AndreLouis>.

Sensor Readout uses or bundles components from these projects:

- [LibreHardwareMonitor](https://librehardwaremonitor.net/) and [LibreHardwareMonitor on GitHub](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), for hardware sensor access.
- [PawnIO](https://github.com/namazso/PawnIO) and [PawnIO.Setup](https://github.com/namazso/PawnIO.Setup), for low-level motherboard sensor access where supported.
- [Newtonsoft.Json](https://www.newtonsoft.com/json), for configuration file handling.
- [HidSharp](https://github.com/IntergatedCircuits/HidSharp), used by LibreHardwareMonitor for HID device access.
- [DiskInfoToolkit](https://github.com/LibreHardwareMonitor/DiskInfoToolkit), used by LibreHardwareMonitor for storage data.
- [RAMSPDToolkit](https://github.com/LibreHardwareMonitor/RAMSPDToolkit), used by LibreHardwareMonitor for memory SPD data.
- [BlackSharp.Core](https://www.nuget.org/packages/BlackSharp.Core/), used by LibreHardwareMonitor dependencies.
- [Tolk screen reader library](https://github.com/dkager/tolk), used for optional screen-reader speech output.
- [Framework Control](https://github.com/ozturkkl/framework-control), used through its optional local API when present on Framework Laptop systems.
- [G-Helper](https://github.com/seerge/g-helper), whose GPL-licensed Asus ACPI research is used in the optional experimental Asus laptop plug-in.
- [usb.ids](http://www.linux-usb.org/usb-ids.html), used under its BSD license option for USB vendor and product names.
- [MAC Address Vendor Database](https://github.com/uxmansarwar/mac-address-vendor-database), used under the MIT License for MAC/OUI vendor lookup.
- Microsoft .NET Framework and support libraries.

## License

The main Sensor Readout application is licensed under the MIT License. See `LICENSE.txt`.

Some optional bundled plug-ins or data files have their own licenses and notices. In particular, `Plug-Ins\AsusRog` contains G-Helper-derived ACPI work and ships with its own GPL notice and GPL text in that folder.
