---
title: Versioning and Compatibility
navTitle: Versioning
status: Implemented
---

# Versioning and Compatibility Design

## Overview

Plugins and the host must be independently upgradeable. This is achieved through stable abstractions packages with strict semver, interface-based contracts, and public API analyzers for breaking change detection.

## Abstractions Layering [Implemented]

Plugins depend on abstractions packages, never on implementation packages. The host provides implementations at runtime.

```
Namotion.Interceptor                    <- stable core (interception, attributes,
                                           registry, change tracking)

HomeBlaze.Abstractions                  <- minimal shared platform interfaces
                                           (ITitleProvider, IIconProvider, etc.)

HomeBlaze.Components.Abstractions       <- UI component contracts

HomeBlaze.Storage.Abstractions          <- document/storage contracts

As domains stabilize, extract focused packages:
  HomeBlaze.History.Abstractions        <- IHistoryStore, history query interfaces
  HomeBlaze.Sensors.Abstractions        <- ITemperatureSensor, IHumiditySensor
  HomeBlaze.Devices.Abstractions        <- ISwitchDevice, IControllable
  MyCompany.Industrial.Abstractions     <- third-party domain abstractions
```

New capabilities are added as new interfaces, not changes to existing ones. A plugin that does not implement a new interface simply does not provide that capability.

## Plugin Compatibility [Implemented]

Plugins depend on abstraction package versions, not host versions. If a plugin targets `HomeBlaze.Abstractions 1.x`, it works with any host that provides those abstractions, regardless of the host's own version.

## Breaking Change Detection [Implemented]

Public API analyzers ([PR #184](https://github.com/RicoSuter/Namotion.Interceptor/pull/184)) are used on abstractions packages to detect unintentional breaking changes. The analyzer tracks the public API surface in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` files, flagging any removals or signature changes.

## Versioning Rules [Implemented]

| Package | Policy |
|---------|--------|
| `Namotion.Interceptor` | Strict semver. Breaking changes extremely rare |
| `HomeBlaze.*.Abstractions` | Strict semver. New interfaces in minor versions, existing stable within major |
| `HomeBlaze` host | Can evolve faster. Internal changes do not affect plugins |
| Wire protocol | Deferred. Version field in Hello/Welcome for future negotiation |

## Wire Protocol Versioning [Planned]

Planned but not yet implemented. The WebSocket protocol (see [WebSocket connector](../../../../../../docs/connectors-websocket.md) and [SubjectUpdate format](../../../../../../docs/connectors-subject-updates.md)) is actively evolving (Commands/RPC, subscriptions, MessagePack). The Hello/Welcome handshake is prepared for version negotiation (version field), but no version checking or format negotiation is implemented yet. The versioning strategy will be designed once the surface stabilizes.

Requirements for the future design: rolling upgrades must be possible, `SubjectUpdate` format changes must be backward compatible or versioned, approach should support additive changes without breaking existing clients.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Plugin compatibility target | Abstraction package versions, not host versions | Decouples plugin and host release cycles |
| Abstractions packaging | Start focused, split by domain as boundaries stabilize | Three packages already exist; add more when domain contracts are clear |
| Breaking change detection | Public API analyzers on abstractions packages | Prevents accidental breaks in the plugin contract |
| Interface evolution | New interfaces for new capabilities | Existing interfaces remain stable within a major version |

## Open Questions

- Wire protocol versioning strategy (deferred until surface stabilizes)
- Plugin minimum abstraction version declaration mechanism
- Cross-major-version migration tooling
