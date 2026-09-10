---
title: Observability
navTitle: Observability
status: Planned
---

# Observability Design [Planned]

## Overview

HomeBlaze provides observability through two complementary mechanisms: **OpenTelemetry** for ops teams using standard monitoring infrastructure, and **health subjects** that expose system health within the knowledge graph itself.

## OpenTelemetry

.NET has built-in OpenTelemetry support. HomeBlaze exports traces, metrics, and logs via standard OTLP to any compatible backend (Prometheus, Grafana, Jaeger, etc.).

### Key Metrics

| Metric | Description |
|--------|-------------|
| Property changes per second | Throughput of the change pipeline |
| WebSocket connection state | Connected/disconnected per peer |
| Reconnection count | Stability indicator for inter-node links |
| LLM call duration | AI agent performance |
| Subject count | Knowledge graph size |
| Change queue depth | Backpressure indicator |

### Key Traces

| Trace | Description |
|-------|-------------|
| Property write path | End-to-end from consumer write to device confirmation |
| Operation invocation | Including cross-instance proxy hops |
| MCP tool call | External agent interaction |

## Health Checks

Any subject can report its health by implementing a health check interface (similar to ASP.NET Core's `IHealthCheck` pattern). This provides a uniform way to surface health across connectors, agents, storage containers, plugins, and custom domain subjects.

### Health Check Interface

A subject implementing the health check interface exposes:

| Member | Description |
|--------|-------------|
| Health status | `Healthy`, `Degraded`, or `Unhealthy` |
| Health message | Human-readable description of current state (e.g., "OPC UA: 3 monitored items failing") |

When health status transitions (e.g., Healthy→Degraded), the subject publishes a health changed event via the message bus (see [Messages](messages.md)). This enables live updates without polling.

### Existing Building Blocks

Several patterns already exist that would implement this interface:

Built-in connectors publish `IsOperational == true` only after their protocol-specific serving observation and `IsOperational == false` after a reported outage or once they stop. The value is `null` only while a connector runs before its first liveness observation; health mapping should treat that as unknown rather than as an explicit outage.

| Subject | Current Health Reporting | Maps To |
|---------|------------------------|---------|
| OPC UA client | `OpcUaClientDiagnostics`: IsOperational, OperationalChangeTime, IsReconnecting, Reconnects.TotalFailed, LastError | Unhealthy when `IsOperational == false`, Degraded when reconnecting or items failing, unknown when `IsOperational == null` |
| OPC UA server | `OpcUaServerDiagnostics`: IsOperational, OperationalChangeTime, LastError, ConsecutiveFailures | Unhealthy when `IsOperational == false` or consecutive failures, unknown when `IsOperational == null` |
| MQTT and WebSocket clients/servers | The same `ConnectorDiagnostics` base: IsOperational, OperationalChangeTime, LastError | Unhealthy when `IsOperational == false`, unknown when `IsOperational == null` |
| Storage containers | `StorageStatus` enum (Connected/Disconnected/Error) | Maps directly to health status |
| Background services | `ServiceStatus` enum (Running/Error/Unavailable) | Maps directly to health status |
| Network subjects | `IConnectionState.IsConnected` | Unhealthy when disconnected |

### Health Page

The Blazor UI provides a health page that:
1. Queries the registry for all subjects implementing the health check interface
2. Subscribes to health changed events on the message bus for live updates
3. Displays aggregated health status across the entire knowledge graph

No special aggregator subject is needed; the page queries and subscribes directly. AI agents can do the same via MCP tools (query by interface type, subscribe to events).

## Health Subjects

Each instance also exposes instance-level health as subjects in the knowledge graph: node role, property count, changes per second, uptime, last heartbeat. These are separate from per-subject health checks; they describe the health of the platform itself rather than individual subjects.

AI agents can monitor both levels using the standard MCP tools; no separate monitoring integration is needed. Operators see health information in the Blazor UI alongside business subjects.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Ops monitoring | OpenTelemetry (OTLP) | Standard, vendor-neutral, built into .NET |
| Self-monitoring | Health subjects in knowledge graph | AI agents and operators use the same tools for system and business data |
| No custom monitoring protocol | Reuse existing infrastructure | OpenTelemetry for ops, knowledge graph for self-monitoring |

## Open Questions

- Which metrics and traces are essential for v1
- Health subject schema standardization across instances
- Alerting integration (separate from alarms; see [Alarms](alarms.md))
