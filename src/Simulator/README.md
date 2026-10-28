# Firmware simulation

The simulator executes diagnostics, firmware installs, and rollbacks through
Gateway's existing command ACK/result flow. Successful firmware commands change
the virtual device's firmware version. Repeated CommandIds return the stored
result, including after a rollback, without repeating the firmware change.

Start Aspire with a release that fails installation on Kiosk-B:

```powershell
dotnet run --project src/AppHost -- --Simulator:Devices=100 --Simulator:BadReleaseVersion=2.4.0 --Simulator:BadReleaseModel=Kiosk-B
```

For a separate simulator process, use the Gateway HTTP URL from Aspire:

```powershell
dotnet run --project src/Simulator -- --gateway http://localhost:<gateway-port> --devices 100 --bad-release-version 2.4.0 --bad-release-model Kiosk-B
```

Both injection options are required together. The model must be `Kiosk-A`,
`Kiosk-B`, or `Kiosk-C`; matching is case sensitive. Versions contain 1–40
characters. Other versions and models install successfully. Injected failures
leave the current firmware unchanged, and rollback restores the version in its
command. `--command-failure-rate` continues to affect diagnostics only.

Firmware commands run in arrival order per device and take 50–250 ms.
Expiry is checked again before changing firmware. State and the recent-result
cache survive reconnects within a process; restarting the process resets them.

Fault injection applies from process startup, including the canary wave. For the
100-device cohort numbered 1–100, the canary is Kiosk-A and Kiosk-B failures appear
in wave 2. Other cohort sizes or target selections can encounter the bad model
in their first wave. Rollout creation and wave decisions belong to Fleet API.

## Admission retry hints

The WebSocket connector collects rejected HTTP upgrade responses. On HTTP 429,
positive numeric `Retry-After` values up to one day are honored as a minimum
wait, followed by the existing full-jitter exponential backoff. Missing, invalid,
or out-of-range hints fall back to jitter alone. Other connection failures keep
the existing backoff behavior. Every new attempt gets a fresh hint, and shutdown
can cancel the wait. Gateway settings and the live on/off switch are documented
in [the Gateway guide](../Gateway/README.md).
