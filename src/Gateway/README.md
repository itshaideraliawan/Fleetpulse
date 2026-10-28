# Gateway admission control

Each Gateway process has one token bucket for new device WebSocket upgrade
attempts. Admission happens before SignalR connects the device, writes Redis
presence, or delivers pending commands. There is no queue and no SQL access.
The device hub supports WebSockets only; negotiation does not consume a token.
Health checks and existing connections are unaffected.

`appsettings.json` sets `AdmissionControl.Enabled=true`, `PermitsPerSecond=100`,
and `BurstCapacity=100`. The bucket starts full, continuously refills, and never
exceeds its capacity. A lock makes acquisition atomic across concurrent requests;
elapsed time comes from a monotonic `TimeProvider`. Each replica has its own
bucket, so adding replicas increases aggregate capacity. These are starting
values; tuning and local measurements belong to D6-03.

When full, Gateway responds with HTTP **429**, an integer-seconds `Retry-After`
header, and ProblemDetails containing `retryAfterSeconds`. With the supported
minimum rate of one permit per second, the rounded hint is one second. It is an
estimate, not a reservation. The simulator waits at least this delay and adds
its full-jitter exponential backoff, avoiding synchronized retries.

## On/off switch

Start normally with `dotnet run --project src/AppHost`. Edit
`src/Gateway/appsettings.json` and save `AdmissionControl.Enabled` as `false` to
bypass admission control on that Gateway without restarting or disconnecting
devices. Set it back to `true` to enforce the bucket again. Configuration reload
can take a moment. Bypassed attempts do not consume tokens; elapsed disabled time
can refill the bucket up to its capacity. Changing rate or capacity does not
grant an extra fresh burst. Both numeric settings must be between 1 and 100000;
invalid settings fail validation, including when disabled.

Standard .NET configuration overrides also work: for a separately launched
Gateway use `AdmissionControl__Enabled=false`, or command-line
`--AdmissionControl:Enabled=false`. Higher-priority environment/command-line
settings override JSON, so remove them before using the JSON live switch.
Do not leave experiment settings modified when handing off a task.

Gateway's `FleetPulse.Gateway.DeviceMessages` meter exports
`gateway.device_connection_attempts` with `outcome=admitted|rejected`.
Admitted includes attempts while disabled, and counts upgrade attempts rather
than successful device sessions. Existing message metrics are unchanged.

## Local check

1. Start Docker Desktop and run
   `dotnet run --project src/AppHost -- --Simulator:Devices=0`.
   Wait until Gateway is healthy and copy its HTTP URL from the Aspire dashboard.
2. Save a temporary low rate/burst such as 1/1 in the Gateway configuration,
   then start a separate simulator with 20 devices:

   ```powershell
   dotnet run --project src/Simulator -- --gateway http://localhost:<gateway-port> --devices 20
   ```

3. Observe rejected attempts in Gateway metrics and simulator connection
   failures, followed by gradual connections. `/health` and `/alive` stay usable.
4. Set `Enabled` to false while clients are retrying. New upgrades bypass the
   bucket and previously connected clients remain connected. Restore true to
   resume limiting new upgrades, then restore rate/burst to 100/100.

Portal Demo Control and fleet-wide propagation of a switch are D6-02 work.
This task exposes the per-instance reloadable setting; it adds no public control
endpoint. Non-simulator clients must honor the 429 hint themselves. Malformed
attempts that reach the upgrade endpoint can consume tokens; device identity
authentication remains outside the portfolio scope.
