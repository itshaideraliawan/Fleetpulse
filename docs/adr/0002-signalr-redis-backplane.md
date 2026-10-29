# ADR 0002: Self-hosted SignalR over WebSockets with a Redis backplane

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

The project needs real, long-lived client connections so the demo can show presence, reconnects and command delivery. Devices may connect to different Gateway instances, so a local in-memory connection map would not be enough when a command or operator update is routed through another instance. Managed SignalR, MQTT and IoT platforms are reasonable production choices, but this project is specifically demonstrating the connection mechanics.

## Decision

Devices connect to an ASP.NET Core SignalR hub at `/hubs/device`, with the Gateway restricting that hub to WebSockets. Redis stores the current device-to-connection mapping and provides the SignalR backplane. Disconnect cleanup uses a compare-and-delete script so an old connection cannot remove a newer connection's presence record.

## Consequences

The project owns connection limits, validation, reconnect behaviour and observability. Redis becomes part of the device path and must be available before the Gateway starts. The design can fan out across Gateway instances locally, while MQTT, Azure IoT Hub and Azure SignalR Service remain production alternatives rather than hidden assumptions.

## Alternatives considered

- MQTT or Azure IoT Hub: stronger device-oriented protocols and managed operations, but outside this .NET SignalR demonstration.
- Azure SignalR Service: removes backplane operations, but hides the connection handling the project is intended to exercise.
- Raw WebSockets: less framework overhead, but would require reimplementing hub negotiation, dispatch and lifecycle plumbing.
