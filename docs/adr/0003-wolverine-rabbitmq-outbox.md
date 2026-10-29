# ADR 0003: Wolverine with RabbitMQ and the transactional outbox

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

The Fleet API changes SQL state and publishes commands or control messages. A process failure between those two operations would otherwise leave a durable state change with no message, or a message with no matching state. Consumers also see retries and therefore must tolerate duplicate delivery. The repository restricts dependencies to permissively licensed packages.

## Decision

Use MIT-licensed Wolverine with RabbitMQ for the message transport. Fleet API publishes use the EF Core / SQL Server durable outbox and the same database transaction as the state change. The configured command and demo-control endpoints use durable outbox delivery; Gateway publishes device batches inline with publisher confirmations because they originate from the live connection path. Message handlers are written to be idempotent.

## Consequences

The Fleet API can recover unsent messages from SQL after a restart, at the cost of outbox tables and dispatch work in the database. RabbitMQ remains a local infrastructure dependency. A future deployment can replace the transport through Wolverine configuration, but the transaction and idempotency rules still need to be preserved.

## Alternatives considered

- MassTransit v8: technically viable, but the project deliberately avoids a major-version licensing transition and keeps the Wolverine decision consistent with the current code.
- NServiceBus: mature, but commercial licensing is outside this project.
- Hand-written outbox: possible, but would duplicate transaction, dispatch, retry and broker plumbing already provided by Wolverine.
