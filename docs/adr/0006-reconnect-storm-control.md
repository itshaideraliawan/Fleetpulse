# ADR 0006: Reconnect-storm control

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

When an outage ends, thousands of devices can attempt a new WebSocket at once. Accepting the whole burst can overload the Gateway, Redis and the host, causing failures that trigger even more retries. A useful demo needs to make that pressure visible and controllable.

## Decision

Each Gateway instance uses a token bucket before the device SignalR hub. The local configuration defaults to admission enabled at 200 permits per second with a burst of 200. Rejected handshakes receive HTTP 429, a `Retry-After` header and a structured delay hint. The simulator combines that hint with capped exponential backoff and a fresh full-jitter sample. Demo Control can turn admission on or off at runtime so the same fleet and host can be compared.

## Consequences

Admission paces each instance; it is not a global fleet-wide limiter. Recovery therefore takes tens of seconds under the local tuning, while a single Gateway or a different host may need different values. Disabling the limiter is useful for the rehearsal but is not a recommended production policy. The measured README comparison reports misses and host limitations rather than claiming that admission always makes recovery faster.

## Alternatives considered

- Unlimited accepts: fastest when the host is idle, but vulnerable to a reconnect spike and retry spiral.
- Fixed reconnect delays: simpler, but synchronized clients would continue to collide.
- A centralized limiter: could coordinate multiple Gateway instances, but would add a dependency and failure mode beyond this local per-instance control.
