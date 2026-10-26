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
- Shows live fleet status and health in an operator portal, updated within 2 seconds
- Sends commands to one device, a group or the fleet: delivered at least once, executed once
- Rolls out software in waves (1% → 10% → 50% → 100%) with a health gate and automatic rollback
- Survives reconnect storms with admission control and jittered backoff

## Architecture

The **Device Gateway** holds device connections only and never touches SQL. The **Fleet API** is the control plane
(REST, operator hub, commands, rollouts, background workers). They talk through RabbitMQ (Wolverine, transactional
outbox) and Redis. See [`docs/architecture.md`](docs/architecture.md) and the decisions in [`docs/adr`](docs/adr/README.md).

## Results (measured locally)

Machine: Intel Core i5-10210U (4 cores / 8 threads), 15.8 GiB RAM, Windows 11 Pro.
Capacity spike (2026-10-26): 5,000 devices in one simulator process; also checked as two processes of 2,500.

| Target | Value | Result |
| --- | --- | --- |
| Concurrent devices | 5,000 for 15 minutes, no unexpected disconnects | Reached 5,000/5,000 by ~25 s; stayed full through a 5m 14s run with 0 reported failures. 15-minute target pending |
| Dashboard freshness | status change visible within 2 s | _pending_ |
| Command fan-out | 99% of 5,000 acknowledged within 10 s | _pending_ |
| Reconnect storm | 5,000 back online within 60 s (with vs. without admission control) | _pending_ |
| Rollout safety | bad release halted at wave 2, 100% rolled back | _pending_ |

In the D2-05 capacity spike, one simulator process reached 1,000, 2,500, and 5,000 connected devices with
heartbeats and telemetry continuing at each level. Its working set was about 114 MiB, 201 MiB, and 324 MiB,
respectively. Two 2,500-device processes also reached 5,000 combined connections with 0 reported failures;
their working sets were about 197 MiB and 190 MiB. Gateway `/health` returned 200 at 5,000 connections.
The configured fleet stops at 5,000, so this establishes a lower bound for machine capacity, not a failure limit.

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
