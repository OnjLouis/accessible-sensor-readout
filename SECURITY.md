# Security Policy

## Supported Versions

Security fixes target the latest published Sensor Readout Windows client, bundled plug-ins, and Sensor Readout Server. Where practical, check whether a problem still occurs in the latest release before reporting it.

## Reporting a Vulnerability

Please report suspected vulnerabilities through [GitHub private vulnerability reporting](https://github.com/OnjLouis/accessible-sensor-readout/security/advisories/new), rather than a public issue.

Include the affected component, version, platform, likely impact, and the smallest reliable reproduction. Remove passwords, server tokens, connection files, private keys, device identifiers, network addresses, and other personal information from attachments. Full diagnostic reports can contain identifying information; share only the relevant, redacted portion initially.

Reports will be assessed as availability permits. Confirmed vulnerabilities will be discussed privately until a fix or suitable mitigation is available and disclosure can be coordinated.

## Remote Monitoring and Plug-Ins

- Exported `.srconnection` files contain server access credentials. Treat them as private even though they do not contain the monitoring password.
- Monitoring passwords protect sensor data on the client. Do not include them in reports.
- Use HTTPS for internet-facing relays. Plain HTTP is intended only for localhost, private networks, or trusted VPN connections.
- Remote fan control requires explicit permission on the monitored computer. Hardware-control problems should identify the plug-in and device model without including private device identifiers.
- Plug-ins execute trusted code. Enable only plug-ins from sources you trust.

## Automated Checks

Code scanning and secret scanning provide additional checks, not a guarantee that every vulnerability or hardware issue will be detected. Dependency alerts cover dependencies GitHub can identify; bundled libraries still require review.
