# ADR 0006: Reconnect-storm control

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

After an outage every device reconnects at once. Accepting them all immediately overloads the gateway and Redis and causes a retry spiral.

## Decision

Devices use exponential backoff with full jitter. Each gateway instance admits a fixed number of new connections per second (token bucket) and refuses the rest with a retry-after hint. A Demo Control switch disables admission control to show the difference.

## Consequences

Recovery takes tens of seconds, predictably. Limits are tuned from local measurements.

## Alternatives considered

Unlimited accepts; fixed delays without jitter.
