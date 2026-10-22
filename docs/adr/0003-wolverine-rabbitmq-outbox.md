# ADR 0003: Wolverine with RabbitMQ and the transactional outbox

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Events and commands must not be lost between saving to SQL and publishing. The repo uses only permissively licensed packages; MassTransit v9 moved to a commercial license.

## Decision

Use Wolverine (MIT) with the RabbitMQ transport and its EF Core / SQL Server transactional outbox for everything the Fleet API publishes. Handlers are idempotent.

## Consequences

One library's conventions to follow. Azure Service Bus can replace RabbitMQ later by configuration.

## Alternatives considered

MassTransit v8; NServiceBus (commercial); hand-written outbox.
