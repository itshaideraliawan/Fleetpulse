# ADR 0001: Separate the Device Gateway from the Fleet API

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

The device side is connection-heavy: every kiosk keeps a WebSocket open, sends heartbeats and telemetry, and may reconnect at the same time after an outage. The operator side needs predictable REST, command and rollout latency during that burst. A single process would make those workloads compete for the same resources and deployment unit.

## Decision

Run two web services with a narrow ownership boundary. The Device Gateway owns the SignalR device hub, connection admission, Redis presence and device message relay. The Fleet API owns REST, the operator hub, SQL-backed device and rollout state, and the hosted workers for telemetry, retries and rollouts. The Gateway receives only Redis and RabbitMQ references; it has no SQL connection. The services exchange device events and commands through RabbitMQ and use Redis for shared connection state and SignalR fan-out.

## Consequences

There is an extra service and a broker hop for commands. The boundary lets connection load and control-plane work scale independently, makes the no-SQL device connect path explicit, and keeps the public demo aligned with the production split. Workers remain in the Fleet API for this version, so a separate worker service is still a future option.

## Alternatives considered

- One service for everything: simpler deployment, but a reconnect storm could compete directly with operator traffic and SQL work.
- A separate Workers service: useful at larger scale, but unnecessary while the Fleet API hosts the bounded background loops.
