# ADR 0002: Self-hosted SignalR over WebSockets with a Redis backplane

- **Status:** Accepted
- **Date:** 2026-10-22

## Context

Real fleets often use MQTT (e.g. Azure IoT Hub). This project's goal is to show .NET real-time engineering, and managed SignalR is priced per ~1,000 connections, which is costly at fleet scale.

## Decision

Devices connect to an ASP.NET Core SignalR hub in the Gateway. A Redis backplane lets any gateway instance reach any device; presence (device -> connection) is stored in Redis.

## Consequences

We own connection handling, limits and reconnect behaviour, which is the point of the demo. MQTT / Azure IoT Hub and Azure SignalR Service are named in the README as production alternatives.

## Alternatives considered

MQTT broker; Azure IoT Hub; Azure SignalR Service; raw WebSockets.
