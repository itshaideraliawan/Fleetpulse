# Architecture

- **Device Gateway:** SignalR device hub, presence in Redis, admission control, relays messages to and from RabbitMQ. No SQL.
- **Fleet API:** REST + operator hub; Devices, Commands and Rollouts; hosted services for telemetry processing and rollout orchestration; Wolverine outbox.
- **Simulator:** virtual devices and measurements.
- **Portal:** React + TypeScript operator UI.
- **Infrastructure (containers via Aspire):** SQL Server, Redis, RabbitMQ.

```mermaid
flowchart LR
  Sim[Device Simulator] -- WebSocket --> GW[Device Gateway]
  GW <--> MQ[(RabbitMQ)]
  MQ <--> API[Fleet API + workers]
  Portal[Operator Portal] <--> API
  GW --> Redis[(Redis)]
  API --> Redis
  API --> SQL[(SQL Server)]
```
