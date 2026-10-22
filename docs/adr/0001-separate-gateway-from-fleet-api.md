# ADR 0001: Separate the Device Gateway from the Fleet API

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Devices hold long-lived WebSocket connections and reconnect in bursts after outages. Operators need the dashboard and commands to stay responsive exactly then.

## Decision

Run a thin Device Gateway (connections, presence, message relay) separately from the Fleet API (REST, operator hub, commands, rollouts, background workers). They communicate only through RabbitMQ and Redis. The gateway never touches SQL.

## Consequences

Two services instead of one and an extra hop for commands. In return, a reconnect storm can't starve operator traffic, and each side scales on its own driver.

## Alternatives considered

One service for everything; a separate Workers service (deferred: workers run as hosted services inside the Fleet API for now).
