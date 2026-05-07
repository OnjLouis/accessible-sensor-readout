# Sensor Readout

Current version: 1.3.0.

Sensor Readout is a Windows utility for reading hardware sensors and controlling supported fans with a keyboard-first, screen-reader-friendly interface.

It shows high-level categories on the left, readings grouped by device in a tree view on the right, and common commands in a standard menu bar.

Project page: [https://github.com/OnjLouis/accessible-sensor-readout](https://github.com/OnjLouis/accessible-sensor-readout)

Sensor Readout can be found [on GitHub](https://github.com/OnjLouis/accessible-sensor-readout).

## What It Does

- Reads temperatures, fan RPM, storage health, storage capacity, and selected hardware counters.
- Shows a Performance/Overview category for uptime, BIOS details, GPU details, CPU usage, memory usage, and storage read/write activity.
- Opens the main UI immediately while the first sensor refresh continues in the background.
- Shows a Network category for adapter status, IP addresses, link speed, send/receive rates, and total traffic.
- Links back to the project page from the README, Help menu, and About dialog.
- Uses bundled LibreHardwareMonitor libraries for sensor access.
- Uses the PawnIO driver for low-level motherboard sensors and fan controls where hardware support is available.
- Lets you label fan headers with friendly names.
- Hides stopped or unpopulated motherboard fan headers by default.
- Applies manual fan percentages to one selected fan or to all visible fans.
- Returns one fan or all fans to automatic/default control.
- Saves TXT or HTML sensor reports.
- Supports configurable automatic refresh.
- Defaults to a 5-second refresh interval on new configurations.
- Can run at Windows startup and start minimized to the notification area.
- Uses a per-computer configuration file in `Config`, such as `Desktop.json`, `Laptop.json`, or `Family-PC.json`, so the same folder can be shared from Dropbox or a USB stick without machines overwriting each other's settings.
- Saves preference changes as they are made, so hotkey, tray, hidden-item, and similar setup work survives crashes better.
- Writes diagnostic logs in `Logs` as `<ComputerName>.log` when logging is enabled.
- Can show temperatures in Celsius, Fahrenheit, Celsius then Fahrenheit, or Fahrenheit then Celsius.
- Converts Samsung storage data counters to readable GB/TB values when LibreHardwareMonitor exposes them as raw SMART units.
- Supports optional user-defined global hotkeys for show/hide and speaking the notification area status.
- Can speak the notification area status through NVDA using bundled 64-bit NVDA Controller Client DLLs.
- Supports simple user-editable language files in the `Langs` folder.
- Logging is off by default and can be enabled from Preferences when troubleshooting.
- Can show selected readings in the notification area tooltip.

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
- [NVDA Controller Client](https://github.com/nvaccess/nvda/tree/master/extras/controllerClient)
- [.NET Framework install notes](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)

## Winget Install Commands

Open Windows Terminal or PowerShell as administrator and run:

```powershell
winget install --id Microsoft.DotNet.Framework.Runtime -e --source winget
winget install --id namazso.PawnIO -e --source winget
```

Optional troubleshooting tool:

```powershell
winget install --id LibreHardwareMonitor.LibreHardwareMonitor -e --source winget
```

You can also run `Install-Prerequisites.cmd` from this folder. It calls `Install-Prerequisites.ps1`, asks for administrator rights if needed, checks for winget, and installs .NET Framework Runtime plus PawnIO. To also install the standalone LibreHardwareMonitor app, run:

```powershell
.\Install-Prerequisites.ps1 -IncludeLibreHardwareMonitor
```

## First Run

1. Start `Sensor Readout.exe`.
2. Accept the Windows administrator prompt.
3. If PawnIO is missing, Sensor Readout offers to run the prerequisite installer.
4. Press `F5` to refresh if values are still loading.
5. Open `Options` > `Fan controls...` or press `Ctrl+L` if you want to adjust fan control values.

If motherboard fans or controls are missing, check that PawnIO is installed and that the app was started as administrator.

You can rerun the prerequisite installer later from `Help` > `Install prerequisites...`.

## Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `F5` | Refresh now. |
| `Ctrl+S` | Save report. |
| `Ctrl+C` | Copy the selected reading or tree branch. |
| `F2` | Rename the selected fan reading, edit the selected spoken label in Preferences, or jump to the fan label field in Fan Controls. |
| `Del` | Hide the selected reading or tree branch. |
| `Ctrl+L` | Open fan controls. |
| `Ctrl+,` | Open Preferences. |
| `F1` | Open the manual. |
| `Shift+F1` | Check GitHub Releases for a newer version, check PawnIO, and offer update installation when available. |
| `Ctrl+F1` | Open the project page. |
| `Ctrl+0` to `Ctrl+4` | Show Performance, Temperatures, Fans, SMART, or Network. |
| `Esc` | Close the Fan Controls dialog. |
| `Alt+L` | Save the label for the selected fan control. |
| `Alt+M` | Apply the manual percentage to the selected fan control. |
| `Alt+A` | Return the selected fan control to automatic/default. |
| `Alt+R` | Return all fan controls to automatic/default. |
| `Alt+7` | Set all visible fan controls to 75%. |
| `Alt+X` | Set all visible fan controls to maximum. |
| `Alt+S` | Show or hide stopped fan headers. |
| `Alt+P` | Pause automatic updates. |
| `Ctrl+Right` | In Preferences, add the selected available reading to the tray order or selected spoken hotkey. |
| `Ctrl+Left` | In Preferences, remove the selected tray or spoken-hotkey reading. |
| `Ctrl+Up` / `Ctrl+Down` | In Preferences, move the selected tray or spoken-hotkey reading earlier or later. |
| `Alt+N` | In Preferences > Hotkeys, create a new spoken hotkey profile. |
| `Alt+P` | In Preferences > Hotkeys, remove the selected spoken hotkey profile. |
| `Alt+A` | In Preferences > Hotkeys, add the selected reading to the selected spoken hotkey. |
| `Alt+M` | In Preferences > Hotkeys, remove the selected reading from the selected spoken hotkey. |
| `Alt+U` / `Alt+W` | In Preferences > Hotkeys, move the selected spoken-hotkey reading up or down. |
| `Alt+R` | In Preferences > Hotkeys, rename the selected spoken label. |
| `Alt+D` | In Preferences > Hotkeys, reset the selected spoken label to default. |
| `Delete` | In Preferences > Hotkeys, reset the selected spoken label to default. |

## Preferences

Use `Options` > `Preferences` or `Ctrl+,` to configure:

- Automatic refresh on or off.
- Whether values refresh while Sensor Readout has keyboard focus.
- Refresh interval in seconds.
- Whether a notification area icon is shown.
- Whether Sensor Readout runs at Windows startup.
- Whether Sensor Readout starts minimized to the notification area.
- Up to four readings to show in the notification area tooltip, with a configurable display order.
- Hidden readings and groups.
- Logging level: Off, Error, Normal, or Debug.
- Whether spoken feedback includes device names before selected readings.

When notification area status is enabled, minimizing Sensor Readout hides it from the taskbar and Alt+Tab list. Open it again from the notification area icon. `Alt+F4` exits the app completely.

The startup option creates or removes a `Sensor Readout.lnk` shortcut in the current user's Windows Startup folder. If startup is enabled, Sensor Readout also enables start-minimized behavior so configured tray readings are available after sign-in without leaving the main window in Alt+Tab.

Notification area readings are selected from an Available readings list and moved into a Tray order list. The tray order is limited to four readings because Windows notification tooltips are short. A reading appears in only one list at a time. Use Add, Remove, Up, and Down, or `Ctrl+Right`, `Ctrl+Left`, and `Ctrl+Up` / `Ctrl+Down`, to choose exactly which readings appear first. Available readings are listed as device first, then reading name and category, such as `Ethernet - Rx: Network`, so type-ahead can jump to a device name. Sensor Readout uses shortened tray labels such as `CPU`, `GPU`, `Rx`, and `Tx`.

## Reading Sensors

The readings pane is a tree view. Sections such as Temperatures, Fans, SMART, Performance, and Network group readings by device or purpose first, then list individual readings underneath. This keeps screen readers from announcing a long device name before every value.

Use the left section list to move between broad areas. This changes the view only; it does not enable, disable, or permanently select devices.

Sensor Readout opens on Temperatures by default. There is no combined Overview section; each reading belongs to its own section to reduce duplicate tree navigation.

The Performance section summarizes live system counters and storage activity. It groups CPU and memory under System, then groups storage activity by drive.

The Network section shows each adapter under one common tree, including status, IP address, link speed, send and receive rates, and total traffic counters.

When the selected reading is a percentage, Sensor Readout also exposes it through the progress bar below the tree. This is useful visually and lets screen readers such as NVDA use their existing progress bar feedback without navigating many separate progress controls.

Use the `Edit` menu, Application key, or right-click on a reading or group to copy it, rename a fan, or hide it. Hidden items can be restored from `Options` > `Preferences` > `Hidden items`; checked items in that tab are hidden.

## Fan Control Workflow

Open fan controls from `Options` > `Fan controls...` or press `Ctrl+L`.

1. Choose a fan in the fan control target box.
2. Optional: type a friendly label and press `Alt+L`.
3. Type a percentage from 0 to 100.
4. Press `Alt+M` to apply it to the selected fan.
5. Press `Alt+A` to return the selected fan to automatic/default.
6. Press `Alt+R` to return all visible fan controls to automatic/default.

The all-fan buttons apply only to visible fan controls. Stopped or unpopulated motherboard headers are hidden unless `Show stopped` is enabled.

Fan labels are saved in the per-computer configuration file in `Config`. Labels only change friendly names shown in Sensor Readout.

## Reports

Press `Ctrl+S` or use `File` > `Save report...`.

The save dialog offers:

- TXT report.
- HTML report.

TXT reports group values by reading type and device, so long hardware names are written once as headings instead of being repeated on every line. HTML reports use formatted tables with sensor, value, and source columns.

## Files

Portable build:

- `Sensor Readout.exe`

Source:

- `src\SensorReadoutApp.cs`
- `src\SensorReadoutApp.exe.manifest`

Configuration and logging created by the app:

- `Config\<ComputerName>.json`: refresh, notification area, hidden-item, fan-label, and logging preferences.
- `Logs\<ComputerName>.log`: optional diagnostics, fan actions, hotkey registration, and NVDA speech messages, created only when logging is enabled.

Language files:

- Put `.txt` or `.ini` files in the `Langs` folder beside `Sensor Readout.exe`.
- On first run, Sensor Readout chooses a bundled language from the Windows display language when one is available; otherwise it falls back to English.
- Use simple `key=value` lines. Lines starting with `#` or `;` are comments.
- Set `language.name=Display name` so the language has a clear name in menus.
- Set `manual.file=README-xx.html` so `F1` opens the matching manual for the selected language in the user's browser. Type only a file name, not a folder path. Sensor Readout looks in the `Docs` folder first. If this entry is missing, Sensor Readout tries `<language-file-name>.md`, then falls back to `README-en.md`.
- Set `number.decimalSeparator=,` if the language should display decimal values with a comma, such as `1,1 TB`.
- Sensor Readout checks the folder every 15 seconds, so newly added or edited files appear in `Options` > `Language` without restarting.
- `Langs\English.txt` is the primary/default language file. Copy it or use the Language editor's New button to start another language.
- The decimal separator can also be changed from Preferences without editing a language file.
- Bundled manuals live in the `Docs` folder and use `README-en.html`, `README-de.html`, `README-es.html`, `README-fr.html`, and `README-it.html`.

Optional NVDA speech:

- The speak-tray-status hotkey uses NVDA's controller client when available.
- `nvdaControllerClient.dll` and `nvdaControllerClient64.dll` are bundled beside `Sensor Readout.exe` for the 64-bit app.
- If the DLL is missing or NVDA is not running, the hotkey fails safely and shows a status message when the main window is visible.
- When Sensor Readout starts minimized to the notification area, it can speak a configurable startup message through NVDA. The default comes from `speech.startupActive` in the selected language file.
- Preferences includes a simple language editor tab for editing existing language-file entries without opening a separate text editor.

## Notes

Fan support depends on LibreHardwareMonitor, PawnIO, the motherboard sensor chip, and what the BIOS exposes. Some systems may provide readings but not writable fan controls.

The app must run as administrator for motherboard Super I/O access on many systems. GPU and storage readings may still appear without elevation, but motherboard fans and controls often will not.

## Changelog

### 1.3.0

- New: Create multiple spoken hotkey profiles, each with its own key combination and ordered set of readings.
- New: Selected tray and spoken-hotkey readings show a speech preview, and spoken-hotkey readings can be renamed for shorter speech.
- New: Spoken feedback can omit device names for shorter NVDA output such as `Rx 688.4 KB/s; Tx 14.4 MB/s`.
- Improved: Repeated spoken labels are grouped for concise output, for example `CPU: 15.0%; 45.1 C`.
- Improved: Hotkey and speech controls now live together on a dedicated Hotkeys tab.
- Improved: Preferences reopens on the tab you used last during the current session.
- Improved: Startup options now live together on a dedicated Startup tab.
- New: Startup update checks can be turned off in Preferences.
- Improved: Preferences now protects plain text entry so typing profile names does not trigger unrelated UI accelerators.
- Improved: Reading selection lists in Preferences support buffered multi-character search, so typing more than one letter can find entries such as Ethernet Rx.
- New: Fan readings can show the matching control percentage next to RPM, and those percentages feed the selected-reading progress meter for NVDA feedback.
- Improved: Saved manual fan settings are restored in the background on startup, while saved automatic/default fan settings no longer slow launch.
- Fixed: Update-available dialog buttons now include keyboard accelerators.
- Changed: Shipped folders now use consistent casing: `Config`, `Logs`, `Langs`, and `Docs`, while folder lookup remains case-insensitive for existing installs.

### 1.2.1

- Fixed: Storage free/used-space readings now come from Windows logical drives, avoiding mismatched LibreHardwareMonitor space values on some SSDs.
- Improved: The selected-reading progress meter now works for any valid percentage reading, including memory available, memory used, drive free space, drive space used, CPU usage, activity percentages, and localized comma-decimal percentages.

### 1.2.0

- New: Multilingual interface support with English, German, Spanish, French, and Italian language files.
- New: Language editor in Preferences for editing translated text, creating new language files from English, and changing the NVDA startup message.
- New: HTML manuals in the `Docs` folder, with `F1` opening the manual for the selected language.
- New: Temperature-unit control for Celsius, Fahrenheit, Celsius then Fahrenheit, or Fahrenheit then Celsius.
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
- [NVDA Controller Client](https://github.com/nvaccess/nvda/tree/master/extras/controllerClient), used for optional NVDA speech output.
- Microsoft .NET Framework and support libraries.

## License

This project is licensed under the MIT License. See `LICENSE.txt`.

