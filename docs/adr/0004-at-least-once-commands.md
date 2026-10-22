# ADR 0004: Commands: at-least-once delivery, exactly-once execution

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Commands go Fleet API -> RabbitMQ -> Gateway -> device over unreliable links. Messages can be lost or duplicated; devices can be offline.

## Decision

Each command target has a CommandId. Undelivered or unacknowledged targets are retried after 10 seconds with the same ID; offline devices get pending commands on reconnect; commands expire. Devices de-duplicate on CommandId and return the stored result for repeats.

## Consequences

Devices keep a small recent-command cache. Duplicates are harmless and visible in metrics.

## Alternatives considered

Fire-and-forget; trying to guarantee exactly-once delivery in the transport.
