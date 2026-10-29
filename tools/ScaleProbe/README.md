# Local 5,000-device probe

This tool measures the five README targets against Release Gateway/Fleet API
binaries and disposable SQL Server, Redis, and RabbitMQ containers. It uses
existing project dependencies; no additional packages are required. It runs both
admission modes sequentially on the same machine and saves raw observations.

```powershell
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File tools/ScaleProbe/Run-Isolated.ps1
```

Docker Desktop must be running. The script starts hidden child processes and
containers with unique names, uses loopback ephemeral ports, and cleans up only
those resources. It does not reuse or reset an existing Aspire database. Gateway
receives only Redis/RabbitMQ references, never the SQL connection string. Container
credentials are temporary, passed through environment variables, and omitted from
artifacts. Existing parent environment values are restored.

Default hold duration is 900 seconds **per mode**, after all 5,000 devices have
connected and acknowledged settings, plus a 20-second warmup. Allow at least
35 minutes for both holds and the command/reconnect/rollout phases. A short pilot
is useful before committing to the full run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/ScaleProbe/Run-Isolated.ps1 -HoldSeconds 5
```

Short pilots never establish the 15-minute stability target. Rate and burst can
be overridden with `-PermitsPerSecond` and `-BurstCapacity`; the defaults match
Gateway's tuned 200/200 settings. Output goes to a timestamped folder under
`.dev-notes/scale-runs/`, including environment metadata, raw JSON results,
five-second process samples, and private service/probe logs.

## Definitions

- **Stability:** minimum simulator sessions, unexpected Closed events, failed
  connection attempts, Gateway session samples, and increasing heartbeat and
  telemetry counts during the timed hold. Intentional reconnect tests happen
  afterward, not during the stability interval.
- **Freshness:** operator SignalR batch arrival minus the original device
  message's sent timestamp, carried from Redis `healthSentAt`. This covers
  Gateway batching, RabbitMQ consumption, Redis, and operator broadcasting.
  It measures the feed used by the portal, **not browser paint time**. Clock
  comparison is valid here because devices and services share the host clock.
  Steady and reconnect-window samples are saved separately.
  Offline-expiry notifications are excluded from message age: their last device
  message is deliberately old under the separate 45-second offline policy.
- **Command fan-out:** actual simulator ACK-send completion minus the server's
  command creation time, correlated by target CommandId. Missing ACKs stay
  missing. Persisted receipt timestamps are saved separately; the existing
  command model can infer receipt from completion if a result arrives before its
  ACK, so those timestamps are not presented as packet-level ACK observations.
  The HTTP POST duration is also saved.
- **Recovery:** wall time from the drop POST until the simulator has observed
  all 5,000 old sessions close and both simulator/Gateway counts return to 5,000
  with current device configuration acknowledged. This includes control
  propagation and polling delay (up to 500 ms).
- **Rollout:** launch with injection off, observe the canary's recorded gate,
  then inject 2.4.0/Kiosk-B through the real Demo API. Record the actual failing
  wave and confirmed rollback counts; do not assume it must be wave 2. This is
  timed injection after the canary, not exclusion of devices from the gate.

Each mode resets only its disposable stack before starting. No benchmark phase
forces state transitions, edits target statuses, or bypasses the outbox.

## Interpreting results

Inspect `results.json` for per-mode errors before using any number. Quantiles use
nearest rank, and thresholds must be evaluated against actual counts and elapsed
time. A configured rate can improve predictability while taking longer than an
unlimited run on a machine that is not overloaded. Report either outcome.

The client, services, and Docker share one laptop. The script intentionally
disables informational logging and does not run Aspire's dashboard, OTLP export,
or a browser portal. Background host load and this isolation must accompany any
published figures. These measurements are not a production hardware limit.
