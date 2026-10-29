# FleetPulse

**Real-time device fleet control center** · .NET 10 · SignalR · RabbitMQ · Redis · React

A control center for a fleet of 5,000 simulated self-service kiosks: live presence and health, remote commands to one
device or the whole fleet, and staged software rollouts that stop and roll back on their own when health drops.
Built to show how to design systems whose clients are machines that retry, go offline and reconnect all at once.

> Status: in progress. See [`docs/execution-plan.md`](docs/execution-plan.md).
> Walkthrough video: _coming on Day 7_

## The problem

Remote device fleets (ATMs, deposit machines, payment terminals, kiosks, sensors) fail in ways ordinary web apps don't:
thousands of clients reconnect at the same moment after an outage, commands get lost or duplicated on bad networks,
and one bad software release can take down the whole fleet.

## What it does

- Connects 5,000 simulated devices over WebSockets (ASP.NET Core SignalR)
- Shows live fleet status and health through a batched operator feed
- Sends commands to one device, a group or the fleet: delivered at least once, executed once
- Rolls out software in waves (1% → 10% → 50% → 100%) with a health gate and automatic rollback
- Survives reconnect storms with admission control and jittered backoff

## Architecture

The **Device Gateway** holds device connections only and never touches SQL. The **Fleet API** is the control plane
(REST, operator hub, commands, rollouts, background workers). They talk through RabbitMQ (Wolverine, transactional
outbox) and Redis. See [`docs/architecture.md`](docs/architecture.md) and the decisions in [`docs/adr`](docs/adr/README.md).

## Results (measured locally)

Machine: Intel Core i5-10210U (4 cores / 8 threads), 15.8 GiB RAM, Windows 11 Pro.
Measured 2026-10-28: .NET 10.0.13 Release services, one simulator process, Docker 29.9.2 with 7.64 GiB RAM,
SQL Server limited to 2 GiB. Admission enabled uses 200 permits/s and burst 200; disabled runs second.

| Target | Admission enabled | Admission disabled |
| --- | --- | --- |
| 5,000 devices for 15 min, no unexpected disconnects | **Pass:** 905.00 s, 0 closures/failures | **Pass:** 900.47 s, 0 closures/failures |
| Dashboard freshness within 2 s (operator-feed proxy) | **Miss:** p99 1.76 s, max 2.38 s | Feed p99 1.30 s, max 1.58 s; browser not measured |
| 99% of 5,000 ACKs within 10 s | **Pass:** 5,000/5,000 (100%); p99 9.16 s | **Miss:** 3,800/5,000 (76%); p99 13.13 s |
| 5,000 recovered within 60 s | **Pass:** 51.65 s | **Pass:** 13.90 s |
| Halt bad release at wave 2; restore every updated device | **Pass:** wave 2; 357/357 restored | **Pass:** wave 2; 368/368 restored |

Both holds kept simulator and Gateway at 5,000 connections with heartbeats and telemetry continuing.
All 5,000 diagnostics commands eventually succeeded in each mode. ACKs are actual client send completions,
not inferred SQL receipt timestamps. Commands run after each 15-minute hold, without a command warmup.
Bad-release injection starts after the canary's recorded gate; rollback counts cover devices actually updated.

Freshness measures device timestamp to operator SignalR receipt, **not React paint time**. Reconnect-window
feed p99 was 8.17 s enabled and 5.99 s disabled, so steady-state freshness must not be assumed during storms.
The strict freshness target remains missed with admission, and command fan-out remains missed without it.

These sequential observations share one laptop with three existing Aspire containers; they do not establish
a causal admission advantage or a production capacity limit. Admission-off recovered faster on this host.
The probe excludes the Aspire dashboard, OTLP export, and a browser, and uses warning-level service logs.
See [method, pilot fixes, and limitations](docs/measurements/D6-03-scale-run.md),
[recorded results](docs/measurements/D6-03-scale-results.json), and the [reproduction tool](tools/ScaleProbe/README.md).

## Run it locally

Prerequisites: .NET 10 SDK, Docker, Node.js LTS.

```bash
dotnet run --project src/AppHost
```

Aspire starts SQL Server, Redis, RabbitMQ, the gateway, the Fleet API, the portal and the simulator, and opens its dashboard.

## Roadmap (what production would add)

- Azure deployment (Container Apps, Azure SQL, Service Bus) with infrastructure as code
- Operator login and roles (Microsoft Entra ID); short-lived device tokens
- Alerts and an audit log
- 10,000+ device load test in the cloud with a published report; monitoring dashboards
- MQTT / Azure IoT Hub for constrained devices

## Tech

.NET 10 · ASP.NET Core · SignalR · Wolverine · EF Core · SQL Server · Redis · RabbitMQ · .NET Aspire ·
OpenTelemetry · React + TypeScript · GitHub Actions

## License

MIT. All names and data are fictional.
