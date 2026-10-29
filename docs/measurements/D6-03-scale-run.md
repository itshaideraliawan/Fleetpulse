# D6-03: 5,000-device measurements

## Reproduce

Start Docker Desktop, then run from the repository root:

```powershell
dotnet build -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ScaleProbe/Run-Isolated.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ScaleProbe/Summarize.ps1 -RunDirectory .dev-notes/scale-runs/<run> -OutputPath .dev-notes/scale-runs/<run>/summary.json
```

The default run holds 5,000 devices for 900 seconds with admission enabled, then
repeats with admission disabled. Each hold starts after all devices connect and
acknowledge the current configuration, plus a 20-second warmup. Command fan-out,
an intentional disconnect of all devices, and a bad-release rollback follow each
hold. Allow at least 35 minutes. Do not run builds or integration tests alongside
the probe.

The harness creates its own SQL Server, Redis, and RabbitMQ containers and
temporary credentials. It cleans up only its own containers and child processes.
It does not reset the existing Aspire database. SQL is provided only to Fleet
API; Gateway receives Redis and RabbitMQ references.

## Measurement boundaries

See [the probe definitions](../../tools/ScaleProbe/README.md). Freshness is source
device timestamp to operator SignalR receipt, not browser paint. Offline-expiry
notifications are excluded because their last source timestamp is deliberately
old. Steady-state and reconnect-window distributions are separate. ACKs are
observed at client send completion; server persisted receipt times are recorded
separately because completion can imply acknowledgement in the command model.

The command timer includes the first fleet command after startup, with no command
warmup. Recovery includes the control request, closure of all old sessions, new
connections, and acknowledgement of current configuration. Rollout injection
starts only after the canary's recorded health gate, using the Demo API; the
probe records the actual failing wave and confirmed restoration counts.

This is a single-laptop, single-Gateway comparison, enabled first and disabled
second, not a randomized benchmark or a production capacity limit. Clients,
services, and Docker share CPU and memory. Release services run with warning
logging and without Aspire dashboard, OTLP export, or a React browser. The host
also had three existing Aspire containers running. Numbers do not establish
end-to-end browser latency or performance with full observability enabled.

A local summary calculation also ran during the admission-on command/recovery
interval. Its host CPU load is included in those timings. No competing build or
integration test ran during either hold. This further limits causal comparison
between the two modes; the observed recovery times are not isolated limiter costs.

## Changes driven by the pilots

- Configuration retries now address only unacknowledged connections. One pending
  device previously caused a broadcast to every connected device each second.
- Gateway flushes telemetry every 250 ms. Fleet API pipelines Redis updates in
  bounded groups of 250 instead of serial round trips. Redis scripts still
  enforce timestamp ordering and idempotence.
- Fleet command creation publishes one envelope per 100 targets through the
  existing SQL transactional outbox, keeping each target's CommandId. Gateway
  dispatches at most 16 commands concurrently per envelope. Single-command retry
  and rollout delivery remain supported; duplicate execution protection remains.
- Command, ACK, and result queues use eight RabbitMQ listeners for inline
  processing. Publisher confirmations and durable Fleet API publishing remain
  enabled. Demo control no longer requests conflicting inline/durable modes for
  the same queue.
- The status broadcaster checks the shared one-second Redis push gate every
  100 ms. A timer tick just before the gate reopened previously skipped an
  entire second. The shared gate still permits at most one push per second.
- Admission rate and burst are tuned from 100/100 to 200/200. The short repaired
  pilot recovered in 52.31 seconds with admission and 23.93 seconds without.
  Admission is pacing, not evidence of faster recovery on this host.

The initial 100/100 pilot took 150.28 seconds to start and 151.09 seconds to
recover with admission, and had a 183.59-second command POST. After the changes,
the short 200/200 pilot started in 40.59 seconds and its command POST took
25.32 seconds. These pilots were short, sequential, and changed multiple factors;
they do not isolate any individual optimization or establish 15-minute stability.
The old pilot's inferred persisted receipts are not comparable to the repaired
probe's actual ACK observations. Raw pilot evidence remains local under
`.dev-notes/scale-runs/20261028-132546` and `20261028-135005`.

No accepted architectural decision changed. The implementation follows
[ADR 0001](../adr/0001-separate-gateway-from-fleet-api.md),
[ADR 0003](../adr/0003-wolverine-rabbitmq-outbox.md),
[ADR 0004](../adr/0004-at-least-once-commands.md), and
[ADR 0006](../adr/0006-reconnect-storm-control.md).

## Recorded full run

Run folder: `.dev-notes/scale-runs/20261028-140429` (local raw results/logs).
The [public summary](D6-03-scale-results.json) contains quantiles, counts,
machine/container metadata, image IDs, and the SHA-256 of `results.json`.
The base is `822c7d6` plus the uncommitted D6-03 changes; .NET runtime 10.0.13.
The wrapper exited successfully and removed all its containers and processes.
The three pre-existing Aspire containers remained running.

| Observation | Admission on (200/s, burst 200) | Admission off |
| --- | --- | --- |
| Timed hold | 905.00 s | 900.47 s |
| Minimum simulator / Gateway sessions | 5,000 / 5,000 | 5,000 / 5,000 |
| Unexpected hold closures / connection failures | 0 / 0 | 0 / 0 |
| Hold samples | 179 | 179 |
| Additional heartbeats / telemetry | 299,066 / 448,658 | 298,103 / 447,820 |
| Steady online feed samples | 701,177 | 637,749 |
| Steady feed p50 / p95 / p99 / max | 0.776 / 1.385 / 1.757 / 2.376 s | 0.701 / 1.192 / 1.299 / 1.583 s |
| ACKs within 10 s | 5,000 / 5,000 (100%) | 3,800 / 5,000 (76%) |
| Actual ACK p50 / p95 / p99 / max | 7.640 / 9.034 / 9.165 / 9.213 s | 6.790 / 12.421 / 13.125 / 13.201 s |
| Missing ACK observations | 0 | 0 |
| Command POST duration | 5.726 s | 13.046 s |
| Diagnostics succeeded / failed / expired | 5,000 / 0 / 0 | 5,000 / 0 / 0 |
| All 5,000 recovered and configured | 51.653 s | 13.897 s |
| Reconnect-window feed samples | 23,015 | 3,338 |
| Reconnect-window feed p99 / max | 8.171 / 12.166 s | 5.993 / 9.471 s |
| Failing rollout wave | 2 (143/450 failures) | 2 (132/450 failures) |
| Installed / confirmed restored | 357 / 357 | 368 / 368 |
| Final rollout state | RolledBack | RolledBack |

The strict **two-second feed target missed with admission on** because its
maximum was 2.376 seconds, even though p99 was below two seconds. The feed-only
threshold passed without admission; browser rendering was not measured in either
mode. The **99%-within-ten-second command target missed without admission**;
all commands nevertheless eventually succeeded. Neither miss is rounded into a pass.

The ACKs were measured after each full hold, not immediately after service
startup. Short pilots had substantially worse command times; the full run does
not prove a ten-second cold-start command guarantee. Reconnect-window freshness
missed two seconds in both modes and has a different sample population and window
length from steady state.

Both services returned HTTP 200 from `/health` and `/alive` at 5,000 connections
during the admission-off hold. Logs also recorded optimistic concurrency conflicts
in the command retry worker and cancelled `SendDemoApplied` invocations during
disruptive phases. The worker retries on the next scan; confirmed command and
rollback results above are terminal outcomes, not merely queued requests.
These logs should not be described as an error-free run. Further concurrency
profiling and cold-start/tail-latency tuning remain follow-ups; the interactive
demo repetition is D6-04.

Final verification also exposed a Docker-heavy integration-test startup timeout
when the runner launched several SQL/broker stacks concurrently. Integration
collections are now limited to two concurrent threads in `TestAssembly.cs`;
unit-test parallelism and application concurrency are unchanged. This bounds
local test resource contention rather than increasing SQL command timeouts.
