# ADR 0005: Rollouts as a state machine with health gates and automatic rollback

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Firmware changes are risky because a release can be incompatible with one hardware model while appearing healthy on another. Sending a release to the whole fleet at once makes the failure expensive and leaves no clear point at which to stop. Operators also need a recorded explanation for a halt and rollback.

## Decision

Store rollout status and transition history in SQL. The state machine moves through `Draft`, `WaveRunning`, `GateCheck`, `Completed`, `Halted`, `RollingBack` and `RolledBack`, and every transition stores a bounded reason and timestamp. The planner creates cumulative 1%, 10%, 50% and 100% milestones, rounding up small cohorts so a canary exists. After each wave reaches terminal target outcomes, the health gate passes only when failures and expirations are strictly below 2%. A failed gate halts the rollout and queues rollback commands for every device confirmed to have installed the release; uncertain installs remain unresolved until a definitive result arrives.

## Consequences

The flow is more explicit than a single loop, but it is testable, queryable and safe to resume through the hosted worker's next scan. Rollback is itself command delivery and therefore follows the command ID and outbox rules. Pause/resume, restart recovery guarantees and operator authorization are outside the seven-day version.

## Alternatives considered

- One “update everything” command: simpler, but it cannot limit blast radius or identify the failing wave.
- A timer-only workflow: easy to start, but it would lose the reason and current state across restarts.
- Automatic rollback of every planned target: safer-looking, but it would send rollback work to devices that never installed the release; this implementation waits for confirmed updates.
