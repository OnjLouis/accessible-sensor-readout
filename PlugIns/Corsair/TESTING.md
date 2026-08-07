# Corsair Plug-In Test Guide

The Corsair plug-in provides experimental, opt-in, read-only monitoring for supported
iCUE LINK Hub cooling devices and HXi/RMi digital power supplies. It does not expose
fan controls or send duty changes.

## Before testing

- Close Corsair iCUE. iCUE does not use the shared Corsair device guard and cannot be
  used safely alongside this plug-in.
- HWiNFO or Fan Control may continue running for monitoring. Sensor Readout uses the
  standard `Global\CorsairLinkReadWriteGuardMutex` with bounded waits.
- Enable Debug logging before collecting a diagnostic if readings are absent or stale.

## Monitoring test

1. Open Preferences > Plug-Ins and enable **Corsair iCUE Link and PSU Monitoring
   (experimental)**.
2. Supported iCUE LINK Hub hardware can provide pump and fan speeds plus liquid or
   device temperatures where the attached device reports them.
3. Supported HXi/RMi power supplies can provide fan speed, VRM and case temperatures,
   input voltage, and output power. A PSU fan speed of 0 RPM can be normal at low load.
4. Generate a normal report and a diagnostic ZIP. Corsair rows should be present in
   both when supported hardware is available.
5. Disable the plug-in. Its background worker and HID sessions should stop immediately.
   Re-enable it to confirm monitoring starts again without restarting Sensor Readout.

## Known limitations

- This release is monitoring-only. Fan control remains withheld until every partial
  write, reset, acknowledgement, and shutdown path has been validated fail-safe on
  supported hardware.
- Legacy Commander PRO/CORE, Hydro AIO, and AXi device families are not supported.
- Some hub readings are unavailable while the hub is in hardware mode or controlled by
  a program that does not expose compatible read responses.
