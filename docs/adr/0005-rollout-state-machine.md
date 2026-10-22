# ADR 0005: Rollouts as a state machine with health gates and automatic rollback

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

A bad software release can break the whole fleet. Rollouts must stop themselves and explain why.

## Decision

A rollout is a state machine (Draft, WaveRunning, GateCheck, Completed, Halted, RollingBack, RolledBack) stored in SQL with a reason for every transition. Waves: 1% / 10% / 50% / 100%. After each wave a health gate checks update failure rate (< 2%); a failure halts the rollout and sends a rollback to every updated device.

## Consequences

More code than a loop, but every decision is recorded and testable. Pause/resume and restart recovery are deferred to the full plan.

## Alternatives considered

Single 'update everything' command.
