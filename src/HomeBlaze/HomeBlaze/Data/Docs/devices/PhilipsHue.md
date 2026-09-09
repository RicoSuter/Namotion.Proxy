---
title: Philips Hue
icon: Lightbulb
---

# Philips Hue

The Philips Hue integration connects to a Hue Bridge on the local network and exposes all discovered devices as subjects in the object graph. It supports lights (including dimming, color, and color temperature), rooms and zones with grouped controls, motion sensors with presence/temperature/light level, and button devices with reactive event streams.

## Configuration

### HueBridge Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BridgeId` | string? | null | Bridge identifier from discovery |
| `AppKey` | string? | null | Authentication key from Link button registration (secret) |
| `PollingInterval` | TimeSpan | 60s | Interval of the device set reconciliation poll |
| `RetryInterval` | TimeSpan | 30s | Retry interval on connection failure |

### JSON Configuration

```json
{
  "$type": "Namotion.Devices.Philips.Hue.HueBridge",
  "bridgeId": "MY_BRIDGE_ID",
  "appKey": "MY_APP_KEY",
  "pollingInterval": "00:01:00",
  "retryInterval": "00:00:30"
}
```

## Setup

### Bridge Discovery

When `BridgeId` and `AppKey` are configured, the bridge tries each locator in turn until one reports the configured `BridgeId`: the Philips discovery endpoint, mDNS, SSDP (5-second budget each), then a local network scan (30-second budget). Each locator runs under its own timeout and a failure is kept local to it, so one locator being unavailable does not end the discovery. This matters in practice: the discovery endpoint answers 429 once it has been asked too often, and on macOS the mDNS locator can never bind port 5353 because the system resolver holds it.

### Link Button Registration

To register with a bridge:

1. Open the Hue Bridge setup component in the HomeBlaze UI
2. Press the physical **Link** button on the Hue Bridge
3. Select the discovered bridge from the list
4. The component calls the registration API to obtain an `AppKey`
5. `BridgeId` and `AppKey` are saved automatically

### Manual Configuration

If you prefer manual setup, set `BridgeId` (the bridge identifier printed on the hardware or found via the Hue app) and `AppKey` (obtained from the Hue API registration endpoint) directly in the JSON configuration file.

## Devices

### Lights (`HueLightbulb`)

Each Hue light is exposed as a `HueLightbulb` subject implementing `ILightbulb`, `IBrightnessController`, `IColorController`, `IColorTemperatureController`, and `IPowerSensor`.

**Capabilities** vary by model:

| Capability | Description |
|------------|-------------|
| On/Off | All lights support on/off control |
| Dimming | Most lights support brightness 0-100% (On/Off lights excluded) |
| Color | Color-capable models expose RGB hex color |
| Color Temperature | White ambiance and color models expose warm-to-cool range (normalized 0-1) |

**Operations:**

| Operation | Description |
|-----------|-------------|
| `TurnOnAsync` | Turn the light on |
| `TurnOffAsync` | Turn the light off |
| `SetBrightnessAsync` | Set brightness (0 = turn off, otherwise turns on and sets level) |
| `SetColorAsync` | Set RGB color by hex string |
| `SetColorTemperatureAsync` | Set color temperature (normalized 0-1 range) |

**Power consumption** is estimated per model when the light is on. Standby power is 0.5W when connected but off:

> **Note:** Power values represent the full rated wattage of each model. Actual power consumption varies with brightness level, but per-model dimming curves are not documented by Philips, so the estimate does not scale with brightness.

| Model  | Power (W) | Lumen | Socket      |
|--------|-----------|-------|-------------|
| LWA001 | 9         | 806   | E26/E27     |
| LCA008 | 13.5      | 1600  | E26/E27     |
| LCT015 | 9.5       | 808   | E26/E27     |
| LCT003 | 6.5       | 300   | GU10        |
| LCL001 | 25        | 1600  | n/a (strip) |
| LST002 | 20        | 1600  | n/a (strip) |
| LTG002 | 5         | 350   | GU10        |

See the full model table in the `HueLightbulb` source code for all supported models.

### Rooms and Zones (`HueGroup`)

Rooms and zones are exposed as `HueGroup` subjects implementing `ILightbulb`, `IBrightnessController`, `IColorController`, `IColorTemperatureController`, and `IVirtualSubject`.

**Grouped controls:**

| Operation         | Behavior                                                |
|-------------------|---------------------------------------------------------|
| On/Off            | Sent to the grouped light resource (all lights at once) |
| Brightness        | Sent to the grouped light resource                      |
| Color             | Delegated to each individual light in the group         |
| Color Temperature | Delegated to each individual light in the group         |

**Aggregation logic:**

- `Lumen` is the sum of all member lights' lumen output
- `Color` and `ColorTemperature` show the majority value only if all lights share the same value
- `Brightness` comes from the grouped light resource, falling back to majority if unavailable

Lights referenced by rooms and zones are the same `HueLightbulb` instances owned by the bridge. A single light can appear in both a room and a zone.

### Motion Sensors (`HueMotionDevice`)

Motion sensors are exposed as `HueMotionDevice` subjects implementing `IPresenceSensor`, `ITemperatureSensor`, `ILightSensor`, and `IBatteryState`.

| Property       | Type     | Description                                                             |
|----------------|----------|-------------------------------------------------------------------------|
| `IsPresent`    | bool?    | Whether motion is currently detected                                    |
| `Temperature`  | decimal? | Ambient temperature in degrees Celsius (null if sensor reports invalid) |
| `Illuminance`  | decimal? | Light level in lux (null if sensor disabled)                            |
| `BatteryLevel` | decimal? | Battery level as 0-1 fraction                                           |

### Button Devices (`HueButtonDevice`)

Button devices (dimmer switches, tap switches, etc.) are exposed as `HueButtonDevice` subjects implementing `IBatteryState`, with child `HueButton` subjects for each physical button.

Each `HueButton` implements `IButtonDevice` and `IObservable<ButtonEvent>` for reactive event streaming.

**Button state mapping:**

| Hue Event       | ButtonState   |
|-----------------|---------------|
| `initial_press` | `Down`        |
| `repeat`        | `Repeat`      |
| `short_release` | `Release`     |
| `long_release`  | `LongRelease` |

**Duplicate prevention:** Button state changes are only emitted when the `ButtonChangeDate` from the Hue API changes and the new state differs from the previous state. Repeat events are deduplicated so consecutive `Repeat` states don't fire redundant events. After release events (`Release`, `LongRelease`), the state automatically resets to `None`.

**Reactive stream:** Subscribe to `IObservable<ButtonEvent>` on any `HueButton` to receive events with the button reference, state, timestamp, and device ID.

### Generic Devices (`HueDevice`)

Devices that don't match any recognized type (not a light, motion sensor, or button device) are exposed as `HueDevice` subjects implementing `IUnknownDevice`, `IConnectionState`, and `IMonitoredService`. They provide basic device metadata (product name, model ID, manufacturer, software version, certification status) and Zigbee connectivity state.

## Real-Time Updates

### Event Stream (SSE)

The Hue Bridge publishes real-time updates via Server-Sent Events (SSE). The `HueBridge` subscribes to this event stream and applies incremental updates to device subjects using JSON merge. Supported event types include `light`, `button`, `grouped_light`, `motion`, `temperature`, `light_level`, `device_power`, and `zigbee_connectivity`.

### Polling Cycle

As a backup to the event stream, a full state refresh runs every 60 seconds. This catches any updates that may have been missed by the event stream (e.g., during brief disconnections). Its eleven resource requests are issued one at a time rather than concurrently, because the bridge rate limits a burst and answers the excess with a 429 and an HTML error page that the Hue SDK cannot parse.

## Resilience

### Auto-Reconnect Behavior

The bridge runs in a reconnect loop inside `ExecuteAsync`:

1. If `BridgeId` or `AppKey` is missing, the bridge enters an error state and waits for configuration changes via `IConfigurable.ApplyConfigurationAsync`
2. On successful connection, it starts the event stream and polling loop in parallel
3. If the event stream fails, the connection is torn down and retried after `RetryInterval`. A failed poll is tolerated: the event stream carries the state changes and the poll only reconciles the device set, so the connection is kept until three consecutive polls fail
4. Configuration changes (e.g., changing `BridgeId`) signal the bridge to restart the connection loop immediately

### Failure Modes and Recovery

| Failure                     | Behavior                                                                                                  |
|-----------------------------|-----------------------------------------------------------------------------------------------------------|
| Missing configuration       | Enters error state with message "Bridge not configured. Set BridgeId and AppKey." Waits for config change |
| Bridge not found on network | Retries discovery every `RetryInterval` (default 30s)                                                     |
| Event stream disconnect     | Connection teardown triggers reconnect loop                                                               |
| Poll errors                 | Logged as warning, connection kept; torn down and retried after three consecutive failures                |
| Host shutdown               | Graceful stop via `CancellationToken`, status set to Stopped                                              |

## Known Limitations

- **Scenes** are not yet exposed. The Hue API supports activating scenes per room/zone, but this is not currently integrated.
- **Entertainment API** (streaming for light sync) is not supported.
- **Power aggregation** on rooms/zones is not available. The bridge exposes a `TotalPower` property that sums all child device power, but individual group-level aggregation is not provided.

## Troubleshooting

### Bridge not found

If the bridge is not discovered on the network:

- Verify the bridge is powered on and connected to the same network
- Check that `BridgeId` matches the bridge identifier (visible in the Hue app under Settings > Hue Bridges)
- Ensure mDNS/UPnP is not blocked by the network

### Registration failed

If Link button registration fails:

- Press the Link button within 30 seconds before attempting registration
- Ensure the bridge firmware is up to date
- Check that HTTPS connections to the bridge are not blocked

### Lights not updating

If lights show stale state:

- Check the `IsConnected` property on the bridge and individual devices
- Verify the event stream is running (bridge `Status` should be `Running`)
- Zigbee connectivity issues are reported per-device via `IConnectionState.IsConnected`
- Devices with `Status = Error` and `StatusMessage = "Device disconnected"` have lost Zigbee communication with the bridge
