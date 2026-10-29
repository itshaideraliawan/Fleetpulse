# ADR 0004: Commands: at-least-once delivery with bounded duplicate suppression

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Commands cross several failure boundaries: Fleet API to RabbitMQ, RabbitMQ to Gateway, Gateway to an online device, and the device's acknowledgement and result messages back again. A device can be offline, a broker delivery can be retried, and an acknowledgement can be lost after the device has already acted.

## Decision

Every command target carries a stable `CommandId`. Fleet API retries pending targets after 10 seconds using the same ID, keeps pending work for reconnecting devices, and expires commands at their deadline. The simulator keeps the most recent 128 command results per device and returns the stored result when a duplicate ID arrives. This gives an exactly-once effect for IDs retained in that bounded cache; it does not claim exactly-once transport or unbounded replay protection.

## Consequences

Delivery is at least once and command handlers must remain idempotent. A retry can be visible in broker and service metrics, while a duplicate within the cache does not repeat the device-side effect. The cache size is deliberately finite, so a very old duplicate after eviction would need a durable device-side store in a production implementation.

## Alternatives considered

- Fire-and-forget: lower latency, but it loses commands when devices disconnect or acknowledgements are dropped.
- Exactly-once transport: not realistic across independent network and process failures; stable IDs and idempotent effects provide the useful guarantee instead.
- An unbounded durable device cache: stronger replay protection, but it adds storage and lifecycle work beyond this local demo.
