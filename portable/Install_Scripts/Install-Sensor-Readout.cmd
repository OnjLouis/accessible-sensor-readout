@echo off
setlocal
set "SensorReadoutExe=%~dp0..\Sensor Readout.exe"

if not exist "%SensorReadoutExe%" (
    echo Sensor Readout.exe could not be found beside the Install_Scripts folder.
    echo Extract the complete Sensor Readout package, then run this script again.
    pause
    exit /b 1
)

start "" "%SensorReadoutExe%" --install
if errorlevel 1 (
    echo Sensor Readout could not be started.
    pause
    exit /b 1
)

exit /b 0
