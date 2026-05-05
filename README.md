# Sensor Readout

Current version: 1.1.0.

Sensor Readout is a Windows utility for reading hardware sensors and controlling supported fans with a keyboard-first, screen-reader-friendly interface.

It shows high-level categories on the left, readings grouped by device in a tree view on the right, and common commands in a standard menu bar.

Project page: [https://github.com/OnjLouis/accessible-sensor-readout](https://github.com/OnjLouis/accessible-sensor-readout)

## What It Does

- Reads temperatures, fan RPM, storage health, storage capacity, and selected hardware counters.
- Shows a Performance category for CPU usage, memory usage, and storage read/write activity.
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
- Can run at Windows startup and start minimized to the notification area.
- Uses a per-computer configuration file, such as `Desktop.json`, `Laptop.json`, or `Family-PC.json`, so the same folder can be shared from Dropbox or a USB stick without machines overwriting each other's settings.
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

- `F5`: Refresh now.
- `Ctrl+S`: Save report.
- `Ctrl+C`: Copy the selected reading or tree branch.
- `F2`: Rename the selected fan reading.
- `Del`: Hide the selected reading or tree branch.
- `Ctrl+L`: Open fan controls.
- `Ctrl+,`: Preferences.
- `F1`: Open the manual.
- `Help` > `Check for updates...`: Check GitHub Releases for a newer version.
- `Help` > `Project on GitHub`: Open the project page.
- `Ctrl+0`: Show Performance.
- `Ctrl+1`: Show Temperatures.
- `Ctrl+2`: Show Fans.
- `Ctrl+3`: Show SMART.
- `Ctrl+4`: Show Network.
- `Esc`: Close the Fan Controls dialog.
- `Alt+L`: Save the label for the selected fan control.
- `Alt+M`: Apply the manual percentage to the selected fan control.
- `Alt+A`: Return the selected fan control to automatic/default.
- `Alt+R`: Return all fan controls to automatic/default.
- `Alt+7`: Set all visible fan controls to 75%.
- `Alt+X`: Set all visible fan controls to maximum.
- `Alt+S`: Show or hide stopped fan headers.
- `Alt+P`: Pause automatic updates.
- In Preferences, notification area readings:
  - `Ctrl+Right`: Add the selected available reading to the tray order.
  - `Ctrl+Left`: Remove the selected tray reading.
  - `Ctrl+Up` / `Ctrl+Down`: Move the selected tray reading earlier or later.

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

Fan labels are saved in the per-computer configuration file beside the executable. Labels only change friendly names shown in Sensor Readout.

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

Configuration created by the app:

- `<ComputerName>.json`: refresh, notification area, hidden-item, fan-label, and logging preferences.
- `SensorReadoutFanActions.log`: optional fan adjustment log, created only when logging is enabled.

## Notes

Fan support depends on LibreHardwareMonitor, PawnIO, the motherboard sensor chip, and what the BIOS exposes. Some systems may provide readings but not writable fan controls.

The app must run as administrator for motherboard Super I/O access on many systems. GPU and storage readings may still appear without elevation, but motherboard fans and controls often will not.

## Changelog

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

Sensor Readout uses or bundles components from these projects:

- [LibreHardwareMonitor](https://librehardwaremonitor.net/) and [LibreHardwareMonitor on GitHub](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), for hardware sensor access.
- [PawnIO](https://github.com/namazso/PawnIO) and [PawnIO.Setup](https://github.com/namazso/PawnIO.Setup), for low-level motherboard sensor access where supported.
- [Newtonsoft.Json](https://www.newtonsoft.com/json), for configuration file handling.
- [HidSharp](https://github.com/IntergatedCircuits/HidSharp), used by LibreHardwareMonitor for HID device access.
- [DiskInfoToolkit](https://github.com/LibreHardwareMonitor/DiskInfoToolkit), used by LibreHardwareMonitor for storage data.
- [RAMSPDToolkit](https://github.com/LibreHardwareMonitor/RAMSPDToolkit), used by LibreHardwareMonitor for memory SPD data.
- [BlackSharp.Core](https://www.nuget.org/packages/BlackSharp.Core/), used by LibreHardwareMonitor dependencies.
- Microsoft .NET Framework and support libraries.

## License

This project is licensed under the MIT License. See `LICENSE.txt`.
