# Accessible Sensor Readout

Accessible Sensor Readout is a Windows utility for reading hardware sensors and controlling fans through FanControl without using FanControl's main interface.

It is designed around keyboard and screen-reader use. The app shows sensor categories on the left, readings on the right, and fan controls across the top.

## What It Does

- Reads temperatures, fan RPM, storage health/SMART-style data, and selected hardware counters.
- Uses FanControl for live fan sensors and fan control changes.
- Uses bundled LibreHardwareMonitor libraries for extra hardware and storage readings.
- Lets you label fan headers with friendly names.
- Hides stopped or unpopulated motherboard fan headers by default.
- Applies manual fan percentages to one selected fan or to all visible fans.
- Resets all fan controls back to automatic/off.
- Saves TXT or HTML sensor reports.

## Prerequisites

- Windows 10 or Windows 11.
- Microsoft .NET Framework 4.8 or newer.
- FanControl installed and configured.
- Administrator rights when controlling fans.
- FanControl's hardware driver prompt, such as PawnIO, must be accepted if FanControl asks for it.

LibreHardwareMonitor is not required as a running app because this folder ships the needed library files, but it is useful for independent testing and troubleshooting.

## Official Links

- [FanControl](https://github.com/Rem0o/FanControl.Releases)
- [FanControl releases](https://github.com/Rem0o/FanControl.Releases/releases)
- [LibreHardwareMonitor](https://librehardwaremonitor.net/)
- [LibreHardwareMonitor releases](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases)
- [.NET Framework install notes](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)

## Winget Install Commands

Open Windows Terminal or PowerShell as administrator and run:

```powershell
winget install --id Microsoft.DotNet.Framework.Runtime -e --source winget
winget install --id Rem0o.FanControl -e --source winget
winget install --id LibreHardwareMonitor.LibreHardwareMonitor -e --source winget
```

You can also run `Install-Prerequisites.cmd` from this folder. It calls `Install-Prerequisites.ps1`, checks for winget, and installs the same packages.

## First Run

1. Install FanControl.
2. Start FanControl once.
3. Complete FanControl's initial setup so it can detect sensors and fan controls.
4. Accept any required hardware driver prompt.
5. Start `AccessibleSensorReadout.exe`.

Sensor Readout starts FanControl if it is not already running. When fan settings are applied, FanControl may briefly show its window; Sensor Readout asks it to close back to tray afterward.

## Keyboard Shortcuts

- `F5`: Refresh now.
- `Ctrl+S`: Save report.
- `Alt+N`: Refresh now.
- `Alt+T`: Save report.
- `Alt+L`: Save the label for the selected fan control.
- `Alt+M`: Apply the manual percentage to the selected fan control.
- `Alt+R`: Reset all fan controls to automatic/off.
- `Alt+7`: Set all visible fan controls to 75%.
- `Alt+X`: Set all visible fan controls to maximum.
- `Alt+S`: Show or hide stopped fan headers.
- `Alt+P`: Pause automatic updates.

## Fan Control Workflow

1. Choose a fan in the fan control target box.
2. Optional: type a friendly label and press `Alt+L`.
3. Type a percentage from 0 to 100.
4. Press `Alt+M` to apply it.
5. Press `Alt+R` to return all fans to automatic/off.

The all-fan buttons apply only to visible fan controls. Stopped or unpopulated motherboard headers are hidden unless `Show stopped` is enabled.

Fan labels are saved in `FanControlLabels.json` beside the executable. Labels only change the friendly names shown in Sensor Readout; they do not change FanControl's own configuration.

## Reports

Press `Alt+T` or use `Save report`.

The save dialog offers:

- TXT report.
- HTML report.

## Files

Run:

- `AccessibleSensorReadout.exe`

Configuration created by the app:

- `FanControlLabels.json`: friendly fan labels.

Useful logs/configs created outside this folder:

- FanControl generated config: `AccessibleSensorReadout-FanControl.json` in the FanControl `Configurations` folder.
- FanControl action log: `AccessibleSensorReadout-FanControl.log` in the FanControl `Configurations` folder.

## Notes

Fan control support depends on FanControl and the hardware support available on the test machine. If FanControl cannot see or control a fan, Sensor Readout cannot control it either.

## Credits

Created by Codex. Ideas by Andre Louis.

## License

This project is licensed under the MIT License. See `LICENSE.txt`.
