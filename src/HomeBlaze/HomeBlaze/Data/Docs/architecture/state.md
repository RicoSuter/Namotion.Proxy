---
title: Implementation State
navTitle: State
position: 1
---

# HomeBlaze Implementation State

Tracks the implementation status of building blocks described in [Architecture Overview](overview.md).

## Core

| Building Block | Status | Notes |
|---|---|---|
| Subject graph + registry | Implemented | Namotion.Interceptor core |
| Property change tracking | Implemented | Including derived properties |
| Source generation | Implemented | `[InterceptorSubject]` + partial properties |

## Connectors

| Building Block | Status | Notes |
|---|---|---|
| WebSocket sync (SubjectUpdate) | Implemented | Including Welcome/Hello, structural sync |
| OPC UA client/server (core) | Implemented | `Namotion.Interceptor.OpcUa` |
| OPC UA server subject (HomeBlaze) | Implemented | `HomeBlaze.OpcUa` |
| OPC UA client subject (HomeBlaze) | Planned | Auto-discovery of remote subjects |
| MQTT client/server (core) | Implemented | `Namotion.Interceptor.Mqtt` |
| MQTT server subject (HomeBlaze) | Planned | |

## Knowledge Graph Extensions

| Building Block | Status | Notes |
|---|---|---|
| Operations (`[Operation]`/`[Query]`) | Implemented | Metadata migrated to registry attributes (`MethodMetadata`). Discoverable and invocable via registry for all interceptor applications |
| Cross-instance operation proxying (RPC) | Planned | WebSocket message types 5-6 |
| Time-series history | Implemented | InMemory and SQLite stores, cross-store merger, chart, and `get_property_history`; TimescaleDB planned |
| Document store | Planned | Documents as subjects |
| Dynamic metadata / annotations | Planned | User-created attributes stored in config JSON |

## Events and Health

| Building Block | Status | Notes |
|---|---|---|
| Message bus (`IMessageBus`) | Planned | Abstractions defined, implementation and DI wiring not yet done |
| Domain events (`IEvent`) | Planned | Abstractions defined (`DeviceEvent`, `SwitchEvent`), no subjects publishing yet |
| Commands (`ICommand`) | Planned | Abstraction defined |
| Health check interface | Planned | Uniform health reporting for any subject — see [Observability](design/observability.md) |
| Cross-instance event propagation | Planned | Events currently in-process only — see [Messages](design/messages.md) |

## AI Integration

| Building Block | Status | Notes |
|---|---|---|
| MCP server (core tools) | Implemented | `Namotion.Interceptor.Mcp` — `query`, `get_property`, `set_property`, `list_types` |
| MCP server (HomeBlaze extensions) | Implemented | Subject enrichment, type discovery, `list_methods`, `invoke_method` via `McpServerConfiguration` extension points |
| Built-in agents | Planned | Agent subjects with LLM integration. See [AI Agents plan](../../plans/ai-agents.md) |

## Platform

| Building Block | Status | Notes |
|---|---|---|
| Plugin system (build-time NuGet) | Implemented | Standard package references |
| Plugin system (runtime loading) | Planned | NuGet feed resolution at startup |
| Blazor operator UI | Implemented | Subject browser, dashboards, editors |
| Multi-instance topology | Implemented | Satellite/central via WebSocket |
| High availability (active-standby) | Planned | Failover with fencing |
| Authorization | In Progress | Graph-level access control, [PR #137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137) |
| Observability (OpenTelemetry) | Planned | |
| Observability (health subjects) | Planned | |
| Storage layer | Implemented | `IStorageContainer` + `FluentStorageContainer` (filesystem, in-memory). Shared/cloud backends planned — see [Storage](design/storage.md) |
| Deployment (UI scaling, multi-primary) | Planned | Bidirectional WebSocket sync between peer UNS instances — see [Deployment](design/deployment.md) |
| Scalability optimizations | Planned | Centralized path cache, registry indexing, Welcome compression — see [Scalability](design/scalability.md) |
| Resilience hardening | Planned | Write durability, split-brain, graceful degradation — see [Resilience](design/resilience.md) |
| Upgrade and migration | Planned | Config migration, backward compatibility, rolling upgrades — see [Upgrade and Migration](design/upgrade-and-migration.md) |
| System testing | Planned | Topology-level testing and chaos injection — see [System Testing](design/testing.md) |
| Audit trail | Planned | Change attribution |
| Alarms / events | Planned | |
